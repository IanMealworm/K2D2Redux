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
    // Precision landing's precondition phase, run before the deorbit burn. The deorbit planning
    // (LandingTargeting.PlanDeorbit) assumes a roughly circular starting orbit, so this
    // circularizes first rather than teaching that search to cope with any orbit shape.
    //
    // Flies its own Turn/Warp/Burn instances rather than NodeExPilot, so driving this doesn't reset
    // the whole Landing pilot via K2D2_Plugin.ResetControllers(). The node is made with
    // ManeuverCreator.CreateManeuverNodeAtUT, not CircularizeOrbitApoapsis(): that one casts the
    // orbit to PatchedConicsOrbit, which throws InvalidCastException for the active vessel under
    // Redux (its orbit is a CurrentPatchedConicsOrbit). The vis-viva math is the same, fed by state
    // vectors (see LandingTargeting.OrbitalElementsFromStateVectors).
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

        // Skip circularizing outright if apoapsis/periapsis are already within this of each other
        // - not worth spending propellant/time closing a gap this small. Refuse to even attempt
        // circularize-then-deorbit above the altitude ceiling: circularizing from something that
        // high would still take a long time and reproduce the same long-coast search-convergence/
        // calibration problems the deorbit burn runs into at high altitude.
        public const double circular_tolerance_m = 1500;
        public const double max_starting_altitude_m = 100000;

        // Suborbital: periapsis below the surface, so the vessel is already coming down. The
        // circularize burn would go at the next apoapsis, which may come after hitting the
        // ground - adding a node there made the game throw.
        public enum OrbitCheck { AlreadyCircular, NeedsCircularizing, TooHigh, Suborbital, NoVessel }

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

            if (periapsisAlt_m < 0)
                return OrbitCheck.Suborbital;

            if (System.Math.Abs(apoapsisRadius - periapsisRadius) <= circular_tolerance_m)
                return OrbitCheck.AlreadyCircular;

            return OrbitCheck.NeedsCircularizing;
        }

        // Set when precision landing can't start from this orbit; LandingPilot then stops and
        // shows it (same as TargetedBurnController.abort_reason).
        public string abort_reason { get; private set; }

        public override void Start()
        {
            finished = false;
            node = null;
            abort_reason = null;

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
                abort_reason = $"Starting orbit too high for precision landing (Ap {apoapsisAlt_m / 1000:n0}km / Pe {periapsisAlt_m / 1000:n0}km, max {max_starting_altitude_m / 1000:n0}km) - circularize to a lower orbit first.";
                status_line = abort_reason;
                finished = true;
                return;
            }

            if (check == OrbitCheck.Suborbital)
            {
                logger.LogInfo($"[Circularize] already coming down (Pe {periapsisAlt_m:n0}m) - precision landing needs to start from orbit.");
                abort_reason = "Already coming down - precision landing needs to start from a stable orbit.";
                status_line = abort_reason;
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

            // Circularize at apoapsis: raise periapsis to meet the current apoapsis.
            double v_apoapsis = VisVivaEquation.CalculateVelocity(apoapsisRadius, apoapsisRadius, periapsisRadius, body.gravParameter);
            double v_circular = VisVivaEquation.CalculateVelocity(apoapsisRadius, apoapsisRadius, apoapsisRadius, body.gravParameter);
            double deltaV = v_circular - v_apoapsis;

            logger.LogInfo($"[Circularize] now={now:n1} apoapsis={apoapsisRadius:n0} periapsis={periapsisRadius:n0} " +
                $"body={body.Name} period={period:n1}s time_to_apoapsis={time_to_apoapsis:n1}s | " +
                $"burn_UT={burn_UT:n1} (T+{burn_UT - now:n1}s) deltaV={deltaV:n2}m/s");

            maneuver_creator.Update();
            // Clear any node already on the plan (e.g. one the player created by hand, or a
            // leftover from an aborted previous run) before adding ours. AddNodeToVessel only ever
            // appends (see CreateManeuverNodeAtUT's own comment), so skipping this risks two
            // nodes on the plan with the vessel pointed at the wrong one. Uses the
            // remove-then-create-a-frame-later helper (see ManeuverCreator.cs) rather than doing
            // both in the same call - removing and creating in the same frame can leave the new
            // node broken even though the removal itself works.
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
                    // No check_direction flag: WarpTo's in-flight direction check depends on a
                    // max_angle nothing here sets, and would start and cancel timewarp over and over.
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
