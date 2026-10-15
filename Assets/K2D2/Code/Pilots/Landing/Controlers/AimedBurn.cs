using System;
using K2D2.Controller;
using K2D2.KSPService;
using KSP.Sim.impl;
using KTools;
using ILogger = ReduxLib.Logging.ILogger;

namespace K2D2.Landing
{
    // A planned burn flown closed-loop, the way the boost-back is (see BoostBackBurn): several
    // times a second, from where the vessel is right now, the burn that brings the airless coast
    // through the aim point is worked out again - against retrograde, plus a sideways part - and
    // the engines point along what's left of it, throttling down at the end.
    //
    // For the atmospheric deorbit burn: a steep entry from orbit takes a burn of 1-2 km/s, a
    // minute or more of engine time. Flown as a node, the first steep test came out 11 km from
    // where it was planned to go, finishing 29 s before the air - too late for any correction. The
    // aim point here already includes what Redux said drag would do to the plan, so the burn ends
    // with Redux's landing point where the plan put it, and the sideways part isn't held to the
    // plane trim limit.
    public class AimedBurn : ExecuteController
    {
        readonly ILogger logger = ReduxLib.ReduxLib.GetLogger("K2D2.AimedBurn");

        const double solve_interval_s = 0.2;
        // Below this much left, the burn is solved directly (see BoostBackBurn.SolveDirect).
        const double direct_solve_below_mps = 50;
        // Engines off while pointing further off than this (the node's turn already lined up).
        const double pause_beyond_deg = 15;
        const double taper_s = 2;
        const double min_throttle = 0.02;
        // Done when less than this is left, or when the last few m/s stop shrinking (see
        // BoostBackBurn).
        const double done_mps = 0.5;
        const double stall_below_mps = 10, stall_progress_mps = 0.5, stall_s = 3;

        readonly AtmoAttitudeHold hold = new AtmoAttitudeHold();
        readonly KSPVessel current_vessel;

        string label;
        LandingTargeting.TargetSite site;
        double aim_a, aim_b;
        double last_solve_UT, start_UT;
        double best_left_mps, best_left_UT;
        Vector3d burn;
        double planned_mps;
        double start_size, start_side;

        // Set when no burn reaching the aim point could be found before any of it was flown; the
        // caller then flies its node instead.
        public bool could_not_solve { get; private set; }

        public AimedBurn()
        {
            current_vessel = K2D2_Plugin.Instance.current_vessel;
        }

        // site: the aim site the burn was planned against. (aim_a, aim_b): where on it the airless
        // coast should pass (ground offsets along its axes). planned: the burn's planned size, and
        // its normal part (along Cross(r, v)) to start the solving from.
        internal void Start(string label, LandingTargeting.TargetSite site, double aim_a, double aim_b,
            double planned_mps, double planned_normal_mps)
        {
            this.label = label;
            this.site = site;
            this.aim_a = aim_a;
            this.aim_b = aim_b;
            this.planned_mps = planned_mps;
            start_size = planned_mps;
            start_side = planned_normal_mps;
            Start();
        }

        public override void Start()
        {
            finished = false;
            could_not_solve = false;
            last_solve_UT = double.NaN;
            start_UT = GeneralTools.Current_UT;
            best_left_mps = double.PositiveInfinity;
            best_left_UT = start_UT;
            burn = Vector3d.zero;
            logger.LogInfo($"[{label}] flying the burn closed-loop: about {planned_mps:n0}m/s, aiming the airless coast " +
                $"({aim_a:n0}, {aim_b:n0})m from the aim point");
        }

        public void Release()
        {
            hold.Release();
            current_vessel?.SetThrottle(0);
        }

        public override void Update()
        {
            if (finished)
                return;
            var vessel = current_vessel?.VesselComponent;
            if (vessel == null)
                return;
            double now = GeneralTools.Current_UT;

            if (double.IsNaN(last_solve_UT) || now - last_solve_UT >= solve_interval_s)
            {
                last_solve_UT = now;
                if (!Solve(vessel, now))
                {
                    if (burn.magnitude == 0)
                    {
                        logger.LogInfo($"[{label}] no burn found that brings the coast through the aim point - flying the node instead");
                        could_not_solve = true;
                        finished = true;
                        return;
                    }
                    Finish(vessel, now, "lost the solution");
                    return;
                }
            }

            double left = burn.magnitude;
            if (left < best_left_mps - stall_progress_mps)
            {
                best_left_mps = left;
                best_left_UT = now;
            }
            if (left < done_mps || (left < stall_below_mps && now - best_left_UT > stall_s))
            {
                Finish(vessel, now, null);
                return;
            }

            hold.HoldDirection(burn);
            double max_accel = LandingPilot.Instance.burn_dV.full_dv;
            double throttle = hold.attitude_error_deg > pause_beyond_deg || max_accel <= 0 ? 0
                : Math.Max(min_throttle, Math.Min(1, left / (max_accel * taper_s)));
            current_vessel.SetThrottle((float)throttle);
            status_line = $"{label}: {left:n0} m/s to go";
        }

        bool Solve(VesselComponent vessel, double now)
        {
            var model = new ImpactModel(vessel.mainBody, now);
            model.StateAt(vessel.Orbit, now, out Vector3d r, out Vector3d v);
            Vector3d dv;
            bool solved;
            if (burn.magnitude > 0 && burn.magnitude < direct_solve_below_mps)
                solved = BoostBackBurn.SolveDirect(model, site, r, v, now, out dv, aim_a, aim_b);
            else
            {
                Vector3d retrograde = -v.normalized;
                Vector3d side = Vector3d.Cross(r, v).normalized;
                solved = BoostBackBurn.Size(model, site, r, v, now, retrograde, side, start_size, out dv,
                    start_side, aim_a, aim_b);
                if (solved)
                {
                    start_size = Vector3d.Dot(dv, retrograde);
                    start_side = Vector3d.Dot(dv, side);
                }
            }
            if (solved)
                burn = dv;
            return solved;
        }

        void Finish(VesselComponent vessel, double now, string why)
        {
            current_vessel.SetThrottle(0);
            hold.Release();
            var model = new ImpactModel(vessel.mainBody, now);
            model.StateAt(vessel.Orbit, now, out Vector3d r, out Vector3d v);
            string airless = BoostBackBurn.Miss(model, site, r, v, now, Vector3d.zero, out double a, out double b, aim_a, aim_b)
                ? $"{Math.Sqrt(a * a + b * b):n0}m" : "-";
            logger.LogInfo($"[{label}] closed-loop burn done after {now - start_UT:n0}s{(why != null ? $" ({why})" : "")}, " +
                $"{burn.magnitude:n1}m/s left | airless coast passes {airless} from where it was aimed");
            status_line = $"{label} complete";
            finished = true;
        }

        public override void UpdateInfoRows(Action<string, string> addRow)
        {
            addRow("Burn Left", $"{burn.magnitude:n1} m/s");
            if (K2D2Settings.debug_mode.V)
                addRow("Attitude Error", $"{hold.attitude_error_deg:n1}°");
        }
    }
}
