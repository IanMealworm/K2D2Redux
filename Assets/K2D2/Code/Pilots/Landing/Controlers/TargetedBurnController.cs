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
    //
    // The atmospheric burns can also check the node against Redux before flying it: Redux's
    // Planned prediction follows the trajectory after the node down through the air, so the node
    // is adjusted until that lands on the target (see Refine).
    public abstract class TargetedBurnController : ExecuteController
    {
        protected readonly ILogger logger;

        enum Step { Refine, Turn, Warp, Burn }
        Step step;

        readonly TurnTo turn = new TurnTo();
        readonly WarpTo warp = new WarpTo();
        readonly BurnManeuver burn = new BurnManeuver();
        readonly AimedBurn aimed_burn = new AimedBurn();
        readonly ManeuverCreator maneuver_creator = new ManeuverCreator();
        readonly SingleExecuteController current_executor = new SingleExecuteController();
        ManeuverNodeData node;

        // Kept for the after-burn check and the refinement.
        ImpactModel model;
        LandingTargeting.TargetSite site;
        LandingTargeting.TargetedBurn current_plan;
        double normal_sign, max_normal;

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

        // Where the burn aims (see LandingTargeting.TargetSite) - the default is the point above
        // the target the vacuum descent wants.
        private protected virtual LandingTargeting.TargetSite MakeSite(ImpactModel model, LandingSettings settings) =>
            new LandingTargeting.TargetSite(model, settings.target_latitude.V, settings.target_longitude.V);

        // Planning waits until this returns true (checked every frame) - for burns planned from a
        // measurement that takes a moment to arrive.
        protected virtual bool ReadyToPlan() => true;

        // After the burn has been flown. Returning false runs the whole cycle again (wait, plan,
        // burn) instead of finishing.
        protected virtual bool BurnComplete() => true;

        // Burns that return true have their node adjusted against Redux's Planned prediction
        // before they're flown.
        protected virtual bool refine_with_redux => false;
        // What the refinement changes to move the landing point along the ground track: the burn's
        // prograde part; its timing (the same burn earlier or later lands the same way, just
        // further along); or its size, keeping its direction (for burns aimed in a direction
        // other than prograde, like the boost-back). Across the track it changes the normal part.
        protected enum RefineAlong { Prograde, Timing, Size }

        // Burns that return true are flown closed-loop (AimedBurn) once the node has turned the
        // vessel and the warp has brought it to the burn, rather than by following the node.
        protected virtual bool fly_closed_loop => false;
        // Where Redux had the refined plan landing, against the target (ground offsets) - what
        // drag does to the airless coast, which the closed-loop burn aims to leave in place.
        bool have_redux_landing;
        double redux_a, redux_b;
        protected virtual RefineAlong refine_along => RefineAlong.Prograde;
        // Whether the normal part is held to the Max Plane Trim setting.
        protected virtual bool cap_normal_to_plane_trim => true;
        // The burn has to stay at least this far ahead for the refinement to keep moving it.
        protected virtual double min_refine_lead_s => 30;

        bool waiting_to_plan;

        public override void Start()
        {
            finished = false;
            abort_reason = null;
            BeginCycle();
        }

        void BeginCycle()
        {
            node = null;
            waiting_to_plan = !ReadyToPlan();
            if (!waiting_to_plan)
                PlanAndPlaceNode();
        }

        void PlanAndPlaceNode()
        {
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
            site = MakeSite(model, settings);

            // The sideways part is computed along Cross(r, v); the game's own "normal" direction may
            // point either way relative to that, so it's checked against what SAS calls normal.
            normal_sign = GameNormalSign(model, orbit);
            max_normal = normal_sign == 0 ? 0 : cap_normal_to_plane_trim ? settings.max_plane_trim_dv.V : double.MaxValue;

            if (!Plan(model, orbit, site, max_normal, out var planned))
            {
                finished = true;
                return;
            }

            refine_round = 0;
            have_last_refine = false;
            have_redux_landing = false;
            PlaceNode(planned);
        }

        void PlaceNode(LandingTargeting.TargetedBurn planned)
        {
            current_plan = planned;
            double now = GeneralTools.Current_UT;
            var settings = LandingPilot.Instance.settings;
            double node_normal = planned.normal * normal_sign;
            double delta_v = Math.Sqrt(planned.prograde * planned.prograde + planned.normal * planned.normal + planned.radial * planned.radial);

            // The game plans a node as a burn that starts at the node's time and lasts as long as the
            // engines need (ManeuverPlanSolver.CalculateOdeManeuverNodes), and BurnManeuver flies
            // it to match that plan. The plan here is an instant burn at planned.UT, so the node
            // goes half a burn earlier to center the real burn on it.
            double max_accel = LandingPilot.Instance.burn_dV.full_dv;
            double node_UT = planned.UT - (max_accel > 0 ? 0.5 * delta_v / max_accel : 0);

            model.LatLon(planned.impact, out double aim_lat, out double aim_lon);
            logger.LogInfo($"[{label}] burn at T+{planned.UT - now:n1}s (node T+{node_UT - now:n1}s): prograde {planned.prograde:n2}m/s " +
                (planned.radial != 0 ? $"radial {planned.radial:n2}m/s " : "") +
                $"normal {node_normal:n2}m/s (cap {max_normal:n1}) | passes {site.aim_height:n0}m up over ({aim_lat:n4}, {aim_lon:n4}), " +
                $"{planned.miss_m:n0}m from the aim point over the target ({settings.target_latitude.V:n4}, {settings.target_longitude.V:n4}) " +
                $"at T+{planned.impact_UT - now:n0}s | lowest terrain clearance {planned.clearance_m:n0}m");

            status_line = $"{label}: {delta_v:n1} m/s, predicted to pass {StrTool.DistanceToString(planned.miss_m)} from the target";

            node = null;
            maneuver_creator.Update();
            maneuver_creator.RemoveAllNodesThenCreate(node_UT, planned.prograde, created_node =>
            {
                node = created_node;
                if (refine_with_redux && refine_round < max_refine_rounds)
                {
                    step = Step.Refine;
                    node_placed_UT = GeneralTools.Current_UT;
                    refine_reading.Reset();
                }
                else
                    StartTurn();
            }, node_normal, planned.radial);
        }

        void StartTurn()
        {
            step = Step.Turn;
            current_executor.setController(turn);
            turn.StartManeuver(node);
        }

        // Refinement against Redux's Planned prediction. Each round: hold the attitude the entry
        // will start in (so Redux predicts that entry - see AtmoAttitudeHold.HoldForEntry), wait for
        // a prediction made after the node was placed, measure how far it lands from the target,
        // and move the node by the change expected to cancel that. How much a change moves the
        // landing point starts from the airless coast and is then corrected by what each round
        // actually did (Broyden update). Redux answers in well under a second, so this takes
        // seconds, against minutes for a burn-and-measure correction.
        const int max_refine_rounds = 6;
        const double refine_tolerance_m = 500;
        const double max_timing_step_s = 600;
        const double max_prograde_step_mps = 50;

        // Results from before the node changed don't count; this much margin covers the game
        // updating its planned trajectory a frame or so after the node moves. (Without it, the
        // third test flight's first deorbit refinement measured some rounds against the previous
        // node and wandered between 3 and 20 km.)
        const double node_settle_s = 0.5;

        readonly AtmoAttitudeHold refine_hold = new AtmoAttitudeHold();
        readonly ReduxReading refine_reading = new ReduxReading();
        int refine_round;
        double node_placed_UT;
        // How far (m) the landing point moves along the site's two ground axes (a, b) per unit of
        // the along-track change (seconds or m/s) and per m/s of normal.
        double da_along, db_along, da_normal, db_normal;
        bool have_last_refine;
        double last_a, last_b, last_d_along, last_d_normal;

        void Refine()
        {
            var vessel = K2D2_Plugin.Instance.current_vessel?.VesselComponent;
            if (vessel == null)
                return;
            double now = GeneralTools.Current_UT;
            var settings = LandingPilot.Instance.settings;
            var model_now = new ImpactModel(vessel.mainBody, now);

            model_now.StateAt(vessel.Orbit, current_plan.UT, out Vector3d r, out Vector3d v);
            refine_hold.HoldBallistic(model_now, r,
                LandingTargeting.AfterBurn(r, v, current_plan.prograde, current_plan.normal, current_plan.radial), current_plan.UT);
            status_line = $"{label}: checking the burn against Redux's prediction (round {refine_round + 1})";
            if (!refine_reading.Update(ReduxImpactPrediction.GetPlanned(vessel), refine_hold.attitude_error_deg,
                    vessel.mainBody.radius, node_placed_UT + node_settle_s, require_steady: false))
                return;

            var impact = refine_reading.result;
            if (!impact.usable)
            {
                logger.LogInfo($"[{label}] no Redux prediction for the planned trajectory ({impact.state}) - flying the burn as planned");
                FinishRefine();
                return;
            }

            var ground = new LandingTargeting.TargetSite(model_now, settings.target_latitude.V, settings.target_longitude.V, 0);
            ground.Offset(model_now.SurfaceUp(impact.latitude, impact.longitude) * ground.radius, out double a, out double b);
            double miss = Math.Sqrt(a * a + b * b);
            redux_a = a;
            redux_b = b;
            have_redux_landing = true;
            logger.LogInfo($"[{label}] round {refine_round + 1}: Redux has the planned trajectory landing at ({impact.latitude:n4}, {impact.longitude:n4}), " +
                $"{miss:n0}m from where it should ({a:n0}, {b:n0}){(impact.approximate ? " (approximate)" : "")}");

            if (miss <= refine_tolerance_m || refine_round + 1 >= max_refine_rounds)
            {
                FinishRefine();
                return;
            }

            if (have_last_refine)
                LearnFromLastRound(a, b);
            else if (!AirlessSensitivity(model_now, vessel.Orbit))
            {
                logger.LogInfo($"[{label}] couldn't work out how the burn moves the landing point - flying it as planned");
                FinishRefine();
                return;
            }

            if (!SolveRound(a, b, out double d_along, out double d_normal))
            {
                FinishRefine();
                return;
            }

            Adjust(d_along, out double UT, out double prograde, out double radial);
            double normal = current_plan.normal + d_normal;
            if (UT < now + min_refine_lead_s)
            {
                logger.LogInfo($"[{label}] the adjusted burn would be too soon - flying it as planned");
                FinishRefine();
                return;
            }
            var adjusted = LandingTargeting.Burn(model_now, vessel.Orbit, site, UT, prograde, normal, radial);
            if (!adjusted.found)
            {
                FinishRefine();
                return;
            }

            last_a = a;
            last_b = b;
            last_d_along = d_along;
            last_d_normal = d_normal;
            have_last_refine = true;
            refine_round++;
            PlaceNode(adjusted);
        }

        // Hands the attitude back to normal SAS - for when the landing is stopped mid-refinement.
        public virtual void Release()
        {
            refine_hold.Release();
            if (current_executor.sub_controler == aimed_burn)
                aimed_burn.Release();
        }

        void FinishRefine()
        {
            refine_hold.Release();
            StartTurn();
        }

        // The burn moved along the ground track by d_along (seconds or m/s, per refine_along).
        void Adjust(double d_along, out double UT, out double prograde, out double radial)
        {
            UT = current_plan.UT;
            prograde = current_plan.prograde;
            radial = current_plan.radial;
            switch (refine_along)
            {
                case RefineAlong.Timing:
                    UT += d_along;
                    break;
                case RefineAlong.Prograde:
                    prograde += d_along;
                    break;
                case RefineAlong.Size:
                    double size = Math.Sqrt(prograde * prograde + radial * radial);
                    double scale = size > 1e-6 ? (size + d_along) / size : 1;
                    prograde *= scale;
                    radial *= scale;
                    break;
            }
        }

        bool AirlessSensitivity(ImpactModel model_now, IKeplerPatch orbit)
        {
            const double dn = 1;
            double step = refine_along == RefineAlong.Timing ? 10 : refine_along == RefineAlong.Size ? 2 : 0.5;
            double n = current_plan.normal;
            model_now.StateAt(orbit, current_plan.UT, out Vector3d r, out Vector3d v);
            if (!LandingTargeting.Miss(model_now, site, r, v, current_plan.UT, current_plan.prograde, n,
                    out double a, out double b, out _, out _, current_plan.radial)
                || !LandingTargeting.Miss(model_now, site, r, v, current_plan.UT, current_plan.prograde, n + dn,
                    out double an, out double bn, out _, out _, current_plan.radial))
                return false;

            Adjust(step, out double UT2, out double p2, out double rad2);
            model_now.StateAt(orbit, UT2, out Vector3d r2, out Vector3d v2);
            if (!LandingTargeting.Miss(model_now, site, r2, v2, UT2, p2, n, out double aa, out double ba, out _, out _, rad2))
                return false;
            da_along = (aa - a) / step;
            db_along = (ba - b) / step;
            da_normal = (an - a) / dn;
            db_normal = (bn - b) / dn;
            return true;
        }

        // Moves the sensitivities just enough to explain what the last round's change did, as
        // long as they keep turning the same way (a noisy round shouldn't flip the next step).
        void LearnFromLastRound(double a, double b)
        {
            double size2 = last_d_along * last_d_along + last_d_normal * last_d_normal;
            if (size2 < 1e-9)
                return;
            double unexplained_a = (a - last_a) - (da_along * last_d_along + da_normal * last_d_normal);
            double unexplained_b = (b - last_b) - (db_along * last_d_along + db_normal * last_d_normal);
            double aa = da_along + unexplained_a * last_d_along / size2;
            double an = da_normal + unexplained_a * last_d_normal / size2;
            double ba = db_along + unexplained_b * last_d_along / size2;
            double bn = db_normal + unexplained_b * last_d_normal / size2;
            if ((aa * bn - an * ba) * (da_along * db_normal - da_normal * db_along) <= 0)
                return;
            da_along = aa; da_normal = an; db_along = ba; db_normal = bn;
        }

        // The change that cancels a miss of (a, b), keeping the normal part within max_normal (and
        // fitting the along-track change alone, as well as it can, once that limit bites).
        bool SolveRound(double a, double b, out double d_along, out double d_normal)
        {
            d_along = d_normal = 0;
            double det = da_along * db_normal - da_normal * db_along;
            if (Math.Abs(det) > 1e-9)
            {
                d_along = (-a * db_normal + b * da_normal) / det;
                d_normal = (-b * da_along + a * db_along) / det;
            }
            double wanted_normal = current_plan.normal + d_normal;
            double capped_normal = Math.Max(-max_normal, Math.Min(max_normal, wanted_normal));
            if (Math.Abs(det) <= 1e-9 || capped_normal != wanted_normal)
            {
                d_normal = capped_normal - current_plan.normal;
                double ra = a + da_normal * d_normal, rb = b + db_normal * d_normal;
                double along2 = da_along * da_along + db_along * db_along;
                if (along2 < 1e-12)
                    return false;
                d_along = -(ra * da_along + rb * db_along) / along2;
            }
            double max_step = refine_along == RefineAlong.Timing ? max_timing_step_s : max_prograde_step_mps;
            d_along = Math.Max(-max_step, Math.Min(max_step, d_along));
            return true;
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
            if (finished)
                return;

            if (waiting_to_plan)
            {
                if (!ReadyToPlan())
                    return;
                waiting_to_plan = false;
                PlanAndPlaceNode();
            }

            if (node == null)
                return;

            if (step == Step.Refine)
            {
                Refine();
                return;
            }

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
                    if (fly_closed_loop && StartAimedBurn())
                        current_executor.setController(aimed_burn);
                    else
                    {
                        current_executor.setController(burn);
                        burn.StartManeuver(node);
                    }
                    break;
                case Step.Burn:
                    if (current_executor.sub_controler == aimed_burn && aimed_burn.could_not_solve)
                    {
                        current_executor.setController(burn);
                        burn.StartManeuver(node);
                        break;
                    }
                    maneuver_creator.RemoveAllNodes();
                    LogResult();
                    if (BurnComplete())
                        finished = true;
                    else
                        BeginCycle();
                    break;
            }
        }

        // The airless coast is aimed where the refined plan's goes, moved by however far Redux had
        // that plan landing from the target - so Redux's landing point ends up on it, across the
        // track too (the plan's normal part may have been held to the plane trim limit).
        bool StartAimedBurn()
        {
            var vessel = K2D2_Plugin.Instance.current_vessel?.VesselComponent;
            if (vessel == null)
                return false;
            var model_now = new ImpactModel(vessel.mainBody, GeneralTools.Current_UT);
            model_now.StateAt(vessel.Orbit, current_plan.UT, out Vector3d r, out Vector3d v);
            if (!LandingTargeting.Miss(model_now, site, r, v, current_plan.UT, current_plan.prograde, current_plan.normal,
                    out double plan_a, out double plan_b, out _, out _, current_plan.radial))
                return false;
            double aim_a = plan_a - (have_redux_landing ? redux_a : 0);
            double aim_b = plan_b - (have_redux_landing ? redux_b : 0);
            double size = Math.Sqrt(current_plan.prograde * current_plan.prograde + current_plan.normal * current_plan.normal
                + current_plan.radial * current_plan.radial);
            aimed_burn.Start(label, site, aim_a, aim_b, size, current_plan.normal);
            return true;
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
