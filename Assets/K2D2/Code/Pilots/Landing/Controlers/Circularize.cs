using KSP.Sim;
using KSP.Sim.Maneuver;
using KSP2FlightAssistant.MathLibrary;
using KTools;
using K2D2.UI;
using K2D2.Controller;
using K2D2.KSPService;
using K2D2.Node;
using ILogger = ReduxLib.Logging.ILogger;

namespace K2D2.Landing
{
    // Precision landing's new precondition phase, run before DeorbitBurn. DeorbitBurn's own
    // targeting math (ComputeDeorbitDeltaV in LandingTargeting.cs) treats the burn point as an
    // apsis of the current orbit - exactly true everywhere on a circular orbit, only exactly true
    // at the real apsides on an eccentric one. Rather than teaching that search to cope with an
    // arbitrary starting orbit shape, this does what a human pilot would: circularize first, then
    // deorbit - same reasoning Reese put it as "NASA doesn't go from a bad orbit straight to a
    // precision landing attempt."
    //
    // Mirrors DeorbitBurn's own structure and reasoning for not reusing NodeExPilot (its own
    // Turn/Warp/Burn instances, so driving this doesn't reset the whole Landing pilot via
    // K2D2_Plugin.ResetControllers()), and reuses DeorbitBurn's proven
    // ManeuverCreator.CreateManeuverNodeAtUT node-creation path rather than
    // ManeuverCreator.CircularizeOrbitApoapsis()'s older CreateManeuverNode_Co path - that path
    // does the same (PatchedConicsOrbit) cast that threw InvalidCastException elsewhere in this
    // codebase for the actively-flown vessel under Redux (CurrentPatchedConicsOrbit, not
    // PatchedConicsOrbit), and nothing in ManeuverCreator besides CreateManeuverNodeAtUT has
    // actually been proven to work under Redux yet (see that method's own comment). The vis-viva
    // circularize-at-apoapsis math itself IS reused as-is from CircularizeOrbitApoapsis - that
    // part was always sound, just fed by state vectors here instead of a PatchedConicsOrbit's own
    // Apoapsis/Periapsis properties (see LandingTargeting.OrbitalElementsFromStateVectors).
    //
    // New and untested in-game, same as the rest of precision landing - needs a real flight
    // before being trusted, same caveats as DeorbitBurn.
    public class Circularize : ExecuteController
    {
        public ILogger logger = ReduxLib.ReduxLib.GetLogger("K2D2.Circularize");

        public enum Mode { Turn, Warp, Burn }
        public Mode mode = Mode.Turn;

        TurnTo turn = new TurnTo();
        WarpTo warp = new WarpTo();
        BurnManeuver burn = new BurnManeuver();
        ManeuverCreator maneuver_creator = new ManeuverCreator();

        public SingleExecuteController current_executor = new SingleExecuteController();
        ManeuverNodeData node;

        public Circularize() { sub_contollers.Add(current_executor); }

        // Thresholds agreed with Reese. Skip circularizing outright if apoapsis/periapsis are
        // already within this of each other - not worth spending propellant/time closing a gap
        // this small. Refuse to even attempt circularize-then-deorbit above the altitude ceiling:
        // circularizing from something that high would still take forever and reproduce the same
        // long-coast search-convergence/calibration problems the deorbit burn already choked on
        // in testing, even with the circularize math itself being correct.
        public const double circular_tolerance_m = 1500;
        public const double max_starting_altitude_m = 100000;

        public enum OrbitCheck { AlreadyCircular, NeedsCircularizing, TooHigh, NoVessel }

        public static OrbitCheck CheckOrbit(out double apoapsisAlt_m, out double periapsisAlt_m)
        {
            apoapsisAlt_m = 0;
            periapsisAlt_m = 0;

            var current_vessel = K2D2_Plugin.Instance.current_vessel;
            if (current_vessel == null)
                return OrbitCheck.NoVessel;

            IKeplerPatch orbit = current_vessel.VesselComponent.Orbit;
            var body = orbit.referenceBody;

            double now = GeneralTools.Current_UT;
            Vector3d r = orbit.GetRelativePositionAtUTZup(now);
            Vector3d v = orbit.GetOrbitalVelocityAtUTZup(now);

            LandingTargeting.OrbitalElementsFromStateVectors(r, v, body.gravParameter,
                out _, out double apoapsisRadius, out double periapsisRadius);

            apoapsisAlt_m = apoapsisRadius - body.radius;
            periapsisAlt_m = periapsisRadius - body.radius;

            if (apoapsisAlt_m > max_starting_altitude_m || periapsisAlt_m > max_starting_altitude_m)
                return OrbitCheck.TooHigh;

            if (System.Math.Abs(apoapsisRadius - periapsisRadius) <= circular_tolerance_m)
                return OrbitCheck.AlreadyCircular;

            return OrbitCheck.NeedsCircularizing;
        }

        public override void Start()
        {
            finished = false;
            node = null;

            var current_vessel = K2D2_Plugin.Instance.current_vessel;
            if (current_vessel == null) { finished = true; return; }

            var check = CheckOrbit(out double apoapsisAlt_m, out double periapsisAlt_m);

            if (check == OrbitCheck.AlreadyCircular)
            {
                logger.LogInfo($"[Circularize] already close enough to circular (Ap {apoapsisAlt_m:n0}m / Pe {periapsisAlt_m:n0}m) - skipping.");
                status_line = "Orbit already circular enough - skipping circularize.";
                finished = true;
                return;
            }

            if (check == OrbitCheck.TooHigh)
            {
                logger.LogInfo($"[Circularize] starting orbit too high (Ap {apoapsisAlt_m:n0}m / Pe {periapsisAlt_m:n0}m, max {max_starting_altitude_m:n0}m).");
                status_line = $"Starting orbit too high for precision landing (Ap {apoapsisAlt_m / 1000:n0}km / Pe {periapsisAlt_m / 1000:n0}km, max {max_starting_altitude_m / 1000:n0}km) - circularize to a lower orbit first.";
                finished = true;
                return;
            }

            IKeplerPatch orbit = current_vessel.VesselComponent.Orbit;
            var body = orbit.referenceBody;

            double now = GeneralTools.Current_UT;
            Vector3d r_now = orbit.GetRelativePositionAtUTZup(now);
            Vector3d v_now = orbit.GetOrbitalVelocityAtUTZup(now);

            LandingTargeting.OrbitalElementsFromStateVectors(r_now, v_now, body.gravParameter,
                out _, out double apoapsisRadius, out double periapsisRadius);

            double period = LandingTargeting.OrbitalPeriodFromStateVectors(r_now, v_now, body.gravParameter);
            double time_to_apoapsis = LandingTargeting.TimeToNextApoapsis(r_now, v_now, body.gravParameter, period);
            double burn_UT = now + time_to_apoapsis;

            // Circularize at apoapsis: raise periapsis to meet the current apoapsis. Same vis-viva
            // math as ManeuverCreator.CircularizeOrbitApoapsis, just fed apoapsis/periapsis radii
            // computed from state vectors above instead of that method's own
            // PatchedConicsOrbit-typed orbit.Apoapsis/orbit.Periapsis.
            double v_apoapsis = VisVivaEquation.CalculateVelocity(apoapsisRadius, apoapsisRadius, periapsisRadius, body.gravParameter);
            double v_circular = VisVivaEquation.CalculateVelocity(apoapsisRadius, apoapsisRadius, apoapsisRadius, body.gravParameter);
            double deltaV = v_circular - v_apoapsis;

            logger.LogInfo($"[Circularize] now={now:n1} apoapsis={apoapsisRadius:n0} periapsis={periapsisRadius:n0} " +
                $"body={body.Name} period={period:n1}s time_to_apoapsis={time_to_apoapsis:n1}s | " +
                $"burn_UT={burn_UT:n1} (T+{burn_UT - now:n1}s) deltaV={deltaV:n2}m/s");

            maneuver_creator.Update();
            // Defensive - clear any node already on the plan (e.g. one the player created by hand,
            // or a leftover from an aborted previous run) before adding ours. AddNodeToVessel only
            // ever appends (see CreateManeuverNodeAtUT's own comment), so skipping this risks the
            // same "two nodes, vessel points at the wrong one" bug found in DeorbitBurn. Uses the
            // remove-then-create-a-frame-later helper (see ManeuverCreator.cs) rather than doing
            // both in the same call - DeorbitBurn's first in-game test showed the same-frame
            // version can leave the new node broken even though the removal itself works.
            status_line = $"Circularizing: {deltaV:n1} m/s";
            maneuver_creator.RemoveAllNodesThenCreate(burn_UT, deltaV, created_node =>
            {
                node = created_node;
                mode = Mode.Turn;
                current_executor.setController(turn);
                turn.StartManeuver(node);
            });
        }

        public override void Update()
        {
            if (finished) return;
            if (node == null) return;

            base.Update();

            if (!current_executor.finished)
                return;

            switch (mode)
            {
                case Mode.Turn:
                    mode = Mode.Warp;
                    current_executor.setController(warp);
                    // Same fix as DeorbitBurn's Turn->Warp handoff: no check_direction flag, so
                    // WarpTo's in-flight direction check (gated on a max_angle field nothing here
                    // sets) doesn't cause engage/cancel timewarp spam.
                    warp.StartManeuver(node);
                    break;
                case Mode.Warp:
                    mode = Mode.Burn;
                    current_executor.setController(burn);
                    burn.StartManeuver(node);
                    break;
                case Mode.Burn:
                    status_line = "Circularize burn complete";
                    finished = true;
                    break;
            }
        }
    }
}
