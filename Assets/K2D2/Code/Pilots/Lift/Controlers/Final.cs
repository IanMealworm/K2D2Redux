using KSP.Sim;
using KSP.Sim.Maneuver;
using KSP2FlightAssistant.MathLibrary;
using KTools;
using UnityEngine.UIElements;
using K2D2.UI;
using K2D2.Controller;
using K2D2.Node;
using K2D2.KSPService;
using K2D2.Landing;
using ILogger = ReduxLib.Logging.ILogger;

namespace K2D2.Lift
{
    // Final ascent phase: circularize at apoapsis. Used to require the external FlightPlan mod
    // (via K2D2OtherModsInterface) to actually create the node - that integration was never
    // finished, so Start() just ended the run immediately with "Manual circularization node
    // creation needed" and left dead create_ap/create_now/run buttons behind that called into
    // stubbed-out no-ops.
    //
    // Replaced with precision landing's own proven circularize pipeline instead (LandingTargeting
    // for the state-vector orbital math, ManeuverCreator.CreateManeuverNodeAtUT for the node, the
    // same Turn/Warp/Burn execution DeorbitBurn/Circularize already use) - removes the dependency
    // on a separate, maybe-never-finished mod entirely. Unlike Landing's Circularize, there's no
    // "already close enough, skip" or "too high, refuse" gating here - Adjust's fine-tuning burn
    // leaves periapsis low/suborbital, so this phase always has real circularizing to do, and
    // there's no deorbit-search convergence concern on the ascent side that an altitude cap would
    // be protecting against.
    //
    // New and untested in-game - needs a real ascent-to-orbit flight before being trusted, same
    // as precision landing's Circularize needed before its own first successful test.
    public class FinalCircularize : ExecuteController
    {
        public ILogger logger = ReduxLib.ReduxLib.GetLogger("K2D2.Lift.FinalCircularize");

        LiftSettings lift_settings = null;
        KSPVessel current_vessel;
        LiftPilot lift;

        public enum Mode { Turn, Warp, Burn }
        public Mode mode = Mode.Turn;

        TurnTo turn = new TurnTo();
        WarpTo warp = new WarpTo();
        BurnManeuver burn = new BurnManeuver();
        ManeuverCreator maneuver_creator = new ManeuverCreator();

        public SingleExecuteController current_executor = new SingleExecuteController();
        ManeuverNodeData node;

        public FinalCircularize(LiftPilot lift, LiftSettings lift_settings)
        {
            current_vessel = K2D2_Plugin.Instance.current_vessel;
            this.lift = lift;
            this.lift_settings = lift_settings;
            sub_contollers.Add(current_executor);
        }

        public override void Start()
        {
            base.Start();
            finished = false;
            node = null;

            if (current_vessel == null)
            {
                lift.EndLiftPilot(false, "No vessel");
                return;
            }

            // Belt and suspenders on top of the fix in Adjust.cs: Adjust's own finish branches now
            // zero throttle before handing off, but zeroing again here means Circularize starts
            // clean regardless of which phase ran before it. Confirmed in-game without this: Adjust
            // left a stray throttle command active, which kept firing while TurnTo below reoriented
            // toward the circularize node - an uncommanded burn during what should've been a coast.
            current_vessel.SetThrottle(0);

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

            // Circularize at apoapsis: raise periapsis to meet the current apoapsis. Same
            // vis-viva math as Landing's Circularize.cs / ManeuverCreator.CircularizeOrbitApoapsis.
            double v_apoapsis = VisVivaEquation.CalculateVelocity(apoapsisRadius, apoapsisRadius, periapsisRadius, body.gravParameter);
            double v_circular = VisVivaEquation.CalculateVelocity(apoapsisRadius, apoapsisRadius, apoapsisRadius, body.gravParameter);
            double deltaV = v_circular - v_apoapsis;

            logger.LogInfo($"[FinalCircularize] now={now:n1} apoapsis={apoapsisRadius:n0} periapsis={periapsisRadius:n0} " +
                $"body={body.Name} period={period:n1}s time_to_apoapsis={time_to_apoapsis:n1}s | " +
                $"burn_UT={burn_UT:n1} (T+{burn_UT - now:n1}s) deltaV={deltaV:n2}m/s");

            maneuver_creator.Update();
            // Defensive - same reasoning as Landing's Circularize.cs and DeorbitBurn.cs: clear any
            // node already on the plan before adding ours, since AddNodeToVessel only ever appends.
            // Ascent doesn't create a node before this phase, but a player-made one, or a leftover
            // from an aborted previous run, would otherwise leave two nodes on the plan. Uses the
            // remove-then-create-a-frame-later helper (see ManeuverCreator.cs) - precision landing's
            // DeorbitBurn found the same-frame version can leave the new node broken even when the
            // removal itself works.
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
                    // Same timewarp-spam fix as DeorbitBurn/Landing's Circularize: no
                    // check_direction flag on StartManeuver.
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

                    // Auto-delete the circularize node once its burn is done - per Reese, nothing
                    // else needs it after this point, and leaving it on the plan meant it just sat
                    // there (harmlessly, but visibly) for the rest of the flight. Same RemoveNode
                    // helper as the Node tab's own auto-delete, not RemoveAllNodes - this is the
                    // only node FinalCircularize itself ever creates, so removing just this one is
                    // enough and doesn't assume anything about the rest of the plan.
                    maneuver_creator.Update();
                    maneuver_creator.RemoveNode(node);

                    lift.EndLiftPilot(true, "Circularized - ascent complete");
                    break;
            }
        }

        public override void updateUI(VisualElement root_el, FullStatus st)
        {
            st.Status(string.IsNullOrEmpty(status_line) ? "Circularizing..." : status_line);
        }
    }
}
