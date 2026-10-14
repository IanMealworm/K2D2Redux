using System.Reflection;
using KSP.Sim;
using KSP.Sim.impl;
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
    // Final ascent phase: circularizes the orbit at apoapsis, using the same state-vector orbital
    // math and node/execution pipeline as precision landing's Circularize/DeorbitBurn
    // (LandingTargeting for the orbital elements, ManeuverCreator.CreateManeuverNodeAtUT for the
    // node, Turn/Warp/Burn for execution). Unlike Landing's Circularize, there is no "close enough,
    // skip" or "too high, refuse" gating: Adjust's fine-tuning burn always leaves periapsis
    // low/suborbital, so this phase always has real circularizing work to do.
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

        // The node is created a fixed update after Start (see RemoveAllNodesThenCreate). If it
        // still isn't there after this much game time, creating it failed.
        const double node_timeout_s = 5;
        double start_UT;

        // Diagnostics for C.1 (circularization going wrong when the ascent ends far from orbit):
        // what the game's own simulation of the node's burn says, logged when the node appears and
        // every 2 s until the burn ends. _plannedWithoutDeltaV is the solver's flag for "planned
        // before the vessel's delta-v data was ready". Temporary, to be removed once C.1 is fixed.
        static readonly FieldInfo planned_without_dv_field =
            typeof(ManeuverPlanSolver).GetField("_plannedWithoutDeltaV", BindingFlags.NonPublic | BindingFlags.Instance);
        float next_trace;

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
            start_UT = GeneralTools.Current_UT;

            if (current_vessel == null)
            {
                lift.EndLiftPilot(false, "No vessel");
                return;
            }

            // Zero throttle unconditionally on entry - Adjust's own finish branches already do
            // this, but re-zeroing here guarantees a clean start regardless of which phase ran
            // before, and avoids an uncommanded burn while TurnTo reorients toward the node.
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
            // Clear any existing node before adding ours - AddNodeToVessel only appends, so a
            // player-made node or a leftover from an aborted run would otherwise leave two nodes on
            // the plan. Uses the remove-then-create-next-frame helper (see ManeuverCreator.cs):
            // removing and creating in the same frame can leave the new node broken.
            status_line = $"Circularizing: {deltaV:n1} m/s";
            maneuver_creator.RemoveAllNodesThenCreate(burn_UT, deltaV, created_node =>
            {
                node = created_node;
                next_trace = 0;
                mode = Mode.Turn;
                current_executor.setController(turn);
                turn.StartManeuver(node);
            });
        }

        public override void Update()
        {
            if (finished) return;
            if (node == null)
            {
                if (GeneralTools.Current_UT - start_UT > node_timeout_s)
                    lift.EndLiftPilot(false, "Couldn't create the circularization node");
                return;
            }

            base.Update();

            if (UnityEngine.Time.unscaledTime >= next_trace)
            {
                next_trace = UnityEngine.Time.unscaledTime + 2;
                Trace();
            }

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
                    Trace();
                    status_line = "Circularize burn complete";
                    finished = true;

                    // Auto-delete the circularize node once its burn is done - nothing else needs
                    // it, and leaving it on the plan would sit there for the rest of the flight.
                    // Uses RemoveNode, not RemoveAllNodes: this is the only node FinalCircularize
                    // ever creates.
                    maneuver_creator.Update();
                    maneuver_creator.RemoveNode(node);

                    lift.EndLiftPilot(true, "Circularized - ascent complete");
                    break;
            }
        }

        void Trace()
        {
            var solver = current_vessel.GetPlanSolver();
            if (solver == null || node == null) return;

            double now = GeneralTools.Current_UT;
            solver.GetVelocityAfterFirstManeuver(out double sim_end_UT);
            bool planned_without_dv = planned_without_dv_field != null && (bool)planned_without_dv_field.GetValue(solver);

            IKeplerPatch orbit = current_vessel.VesselComponent.Orbit;
            var body = orbit.referenceBody;
            LandingTargeting.OrbitalElementsFromStateVectors(orbit.GetRelativePositionAtUTZup(now),
                orbit.GetOrbitalVelocityAtUTZup(now), body.gravParameter,
                out _, out double apoapsisRadius, out double periapsisRadius);

            double sim_burn = sim_end_UT - node.Time;
            string early = sim_burn < node.BurnDuration - 1 ? " ENDS EARLY" : "";
            logger.LogInfo($"[FinalCircularize] trace mode={mode} node in {node.Time - now:n1}s " +
                $"node dV={node.BurnRequiredDV:n1} burn={node.BurnDuration:n1}s | game sim burns {sim_burn:n1}s{early} " +
                $"plannedWithoutDeltaV={planned_without_dv} | remaining={burn.remaining_dv:n1} " +
                $"Ap={(apoapsisRadius - body.radius) / 1000:n1}km Pe={(periapsisRadius - body.radius) / 1000:n1}km");
        }

        public override void updateUI(VisualElement root_el, FullStatus st)
        {
            st.Status(string.IsNullOrEmpty(status_line) ? "Circularizing..." : status_line);

            // See ManeuverCreator.waiting_for_map_view's own comment - the node and its burn are
            // already fully set up at this point regardless, this is purely about the map's visual
            // gizmo/conic-patch preview not being positioned correctly until the player actually
            // opens Map view at least once after this node was created.
            if (maneuver_creator.waiting_for_map_view)
                st.Console("Open Map View to finish displaying this maneuver node (the burn itself isn't affected either way).");
        }
    }
}
