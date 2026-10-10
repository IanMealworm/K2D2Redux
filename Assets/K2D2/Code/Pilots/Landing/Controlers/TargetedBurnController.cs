using System;
using KSP.Sim;
using KSP.Sim.Maneuver;
using KSP.Sim.impl;
using KTools;
using K2D2.Controller;
using K2D2.KSPService;
using K2D2.Node;
using ILogger = ReduxLib.Logging.ILogger;

namespace K2D2.Landing
{
    // Shared base for vacuum precision landing's two orbit burns, DeorbitBurn and
    // MidCourseCorrection: plan a burn with LandingTargeting, put it on the map as a real
    // maneuver node, then turn, warp and burn.
    //
    // The node is flown with this controller's own TurnTo/WarpTo/BurnManeuver rather than through
    // NodeExPilot: starting NodeExPilot resets every pilot, which would stop Landing itself.
    public abstract class TargetedBurnController : ExecuteController
    {
        protected readonly ILogger logger;

        enum Step { Turn, Warp, Burn }
        Step step;

        readonly TurnTo turn = new TurnTo();
        readonly WarpTo warp = new WarpTo();
        readonly BurnManeuver burn = new BurnManeuver();
        readonly ManeuverCreator maneuver_creator = new ManeuverCreator();
        readonly SingleExecuteController current_executor = new SingleExecuteController();
        ManeuverNodeData node;

        // Kept for the after-burn check in Update.
        ImpactModel model;
        LandingTargeting.TargetSite site;

        protected TargetedBurnController(string logger_name)
        {
            logger = ReduxLib.ReduxLib.GetLogger(logger_name);
            sub_contollers.Add(current_executor);
        }

        // Decides the burn. Returning false means no burn is needed (or possible); set status_line
        // to say why.
        private protected abstract bool Plan(ImpactModel model, IKeplerPatch orbit, LandingTargeting.TargetSite site,
            double max_normal, out LandingTargeting.TargetedBurn planned);

        protected abstract string label { get; }

        // Set by Plan when precision landing can't go on from here; LandingPilot then stops and
        // shows this instead of carrying on toward a target it can't reach.
        public string abort_reason { get; protected set; }

        public override void Start()
        {
            finished = false;
            node = null;
            abort_reason = null;

            var vessel = K2D2_Plugin.Instance.current_vessel?.VesselComponent;
            if (vessel == null)
            {
                finished = true;
                return;
            }

            IKeplerPatch orbit = vessel.Orbit;
            double now = GeneralTools.Current_UT;
            var settings = LandingPilot.Instance.settings;
            model = new ImpactModel(orbit.referenceBody, now);
            site = new LandingTargeting.TargetSite(model, settings.target_latitude.V, settings.target_longitude.V);

            // The sideways part is computed along Cross(r, v); the game's own "normal" direction may
            // point either way relative to that, so it's checked against what SAS calls normal.
            double normal_sign = GameNormalSign(model, orbit);
            double max_normal = normal_sign == 0 ? 0 : settings.max_plane_trim_dv.V;

            if (!Plan(model, orbit, site, max_normal, out var planned))
            {
                finished = true;
                return;
            }

            double node_normal = planned.normal * normal_sign;
            double delta_v = Math.Sqrt(planned.prograde * planned.prograde + planned.normal * planned.normal);

            // The game plans a node as a burn that starts at the node's time and lasts as long as the
            // engines need (ManeuverPlanSolver.CalculateOdeManeuverNodes), and BurnManeuver flies
            // it to match that plan. The plan here is an instant burn at planned.UT, so the node
            // goes half a burn earlier to center the real burn on it.
            double max_accel = LandingPilot.Instance.burn_dV.full_dv;
            double node_UT = planned.UT - (max_accel > 0 ? 0.5 * delta_v / max_accel : 0);

            model.LatLon(planned.impact, out double aim_lat, out double aim_lon);
            logger.LogInfo($"[{label}] burn at T+{planned.UT - now:n1}s (node T+{node_UT - now:n1}s): prograde {planned.prograde:n2}m/s " +
                $"normal {node_normal:n2}m/s (cap {max_normal:n1}) | passes {site.aim_height:n0}m up over ({aim_lat:n4}, {aim_lon:n4}), " +
                $"{planned.miss_m:n0}m from the aim point over the target ({settings.target_latitude.V:n4}, {settings.target_longitude.V:n4}) " +
                $"at T+{planned.impact_UT - now:n0}s | lowest terrain clearance {planned.clearance_m:n0}m");

            status_line = $"{label}: {delta_v:n1} m/s, predicted to pass {StrTool.DistanceToString(planned.miss_m)} from the target";

            maneuver_creator.Update();
            maneuver_creator.RemoveAllNodesThenCreate(node_UT, planned.prograde, created_node =>
            {
                node = created_node;
                step = Step.Turn;
                current_executor.setController(turn);
                turn.StartManeuver(node);
            }, node_normal);
        }

        static double GameNormalSign(ImpactModel model, IKeplerPatch orbit)
        {
            var telemetry = SASTool.getTelemetry();
            if (telemetry == null)
                return 0;

            model.StateAt(orbit, model.epoch_UT, out Vector3d r, out Vector3d v);
            return Math.Sign(Vector3d.Dot(model.ToModelAxes(telemetry.OrbitMovementNormal), Vector3d.Cross(r, v)));
        }

        public override void Update()
        {
            if (finished || node == null)
                return;

            base.Update();

            if (!current_executor.finished)
                return;

            switch (step)
            {
                case Step.Turn:
                    step = Step.Warp;
                    current_executor.setController(warp);
                    warp.StartManeuver(node);
                    break;
                case Step.Warp:
                    step = Step.Burn;
                    current_executor.setController(burn);
                    burn.StartManeuver(node);
                    break;
                case Step.Burn:
                    maneuver_creator.RemoveAllNodes();
                    LogResult();
                    finished = true;
                    break;
            }
        }

        // Where the orbit comes down now that the burn is done - the difference from the plan is
        // how well the burn was flown.
        void LogResult()
        {
            var vessel = K2D2_Plugin.Instance.current_vessel?.VesselComponent;
            if (vessel == null)
                return;

            var result = LandingTargeting.Coast(new ImpactModel(model.body, GeneralTools.Current_UT), vessel.Orbit,
                site, GeneralTools.Current_UT);
            status_line = $"{label} complete";
            logger.LogInfo(result.found
                ? $"[{label}] after burn: passes {result.miss_m:n0}m from the aim point, lowest terrain clearance {result.clearance_m:n0}m"
                : $"[{label}] after burn: the orbit no longer comes down to the aim point");
        }
    }
}
