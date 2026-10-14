using System;
using KSP.Sim;
using KSP.Sim.impl;
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
    // Final ascent phase: circularizes at apoapsis with a burn K2D2 flies itself (closed-loop, like
    // Landing's AimedBurn), not by following a node.
    //
    // A KSP2 node's burn starts at the node and is simulated with its direction held fixed. An
    // ascent that ends far from orbit needs a minute or more of burn: put at apoapsis, all of it
    // happens after apoapsis, and the fixed direction tilts upward as the rocket moves round the
    // planet, so even a node followed exactly ended at Ap 146 km / Pe 47 km in testing. The first
    // node after an ascent could also be planned with out-of-date engine data (a 20,000 s burn).
    //
    // So the burn is centred on apoapsis from K2D2's own burn-time estimate, and several times a
    // second the engines point along "circular speed, level with the horizon" minus the current
    // velocity, throttling down at the end. That noses up by itself if the rocket starts to fall.
    // No node is made: one wouldn't match the burn actually flown, and nothing would read it.
    public class FinalCircularize : ExecuteController
    {
        public ILogger logger = ReduxLib.ReduxLib.GetLogger("K2D2.Lift.FinalCircularize");

        LiftSettings lift_settings = null;
        KSPVessel current_vessel;
        LiftPilot lift;

        public enum Mode { Turn, Warp, Burn }
        public Mode mode = Mode.Turn;

        readonly WarpTo warp = new WarpTo();
        readonly AtmoAttitudeHold hold = new AtmoAttitudeHold();
        readonly BurndV engines = new BurndV();

        // Lined up when the nose is within this of the burn direction; engines off while further.
        const double aligned_deg = 5;
        const double pause_beyond_deg = 15;
        // Throttle eases off over the last this-many seconds of burn.
        const double taper_s = 2;
        const double min_throttle = 0.02;
        // Done when less than this is left, or when the last few m/s stop shrinking.
        const double done_mps = 0.5;
        const double stall_below_mps = 10, stall_progress_mps = 0.5, stall_s = 3;
        // Never start the burn sooner than this, to leave time to turn.
        const double min_lead_s = 10;

        double ignition_UT;
        double planned_mps, planned_burn_s;
        double left_mps;
        double best_left_mps, best_left_UT, burn_start_UT;

        public FinalCircularize(LiftPilot lift, LiftSettings lift_settings)
        {
            current_vessel = K2D2_Plugin.Instance.current_vessel;
            this.lift = lift;
            this.lift_settings = lift_settings;
        }

        public override void Start()
        {
            base.Start();
            finished = false;
            left_mps = 0;

            if (current_vessel?.VesselComponent == null)
            {
                lift.EndLiftPilot(false, "No vessel");
                return;
            }

            current_vessel.SetThrottle(0);

            IKeplerPatch orbit = current_vessel.VesselComponent.Orbit;
            var body = orbit.referenceBody;
            double now = GeneralTools.Current_UT;
            Vector3d r_now = orbit.GetRelativePositionAtUTZup(now);
            Vector3d v_now = orbit.GetOrbitalVelocityAtUTZup(now);

            LandingTargeting.OrbitalElementsFromStateVectors(r_now, v_now, body.gravParameter,
                out _, out double apoapsisRadius, out double periapsisRadius);
            double period = LandingTargeting.OrbitalPeriodFromStateVectors(r_now, v_now, body.gravParameter);
            double apoapsis_UT = now + LandingTargeting.TimeToNextApoapsis(r_now, v_now, body.gravParameter, period);

            // Same vis-viva sum as the Node tab's Circularize at AP button.
            double v_apoapsis = VisVivaEquation.CalculateVelocity(apoapsisRadius, apoapsisRadius, periapsisRadius, body.gravParameter);
            double v_circular = VisVivaEquation.CalculateVelocity(apoapsisRadius, apoapsisRadius, apoapsisRadius, body.gravParameter);
            planned_mps = v_circular - v_apoapsis;

            // Burn time at full thrust from the engines that are lit (or could be). Ignoring the
            // fuel burned makes it a little long, so the burn starts a little early.
            engines.Compute_Thrust();
            double accel = engines.has_active_engine ? engines.active_full_dv : engines.full_dv;
            planned_burn_s = accel > 0 ? planned_mps / accel : 0;
            ignition_UT = Math.Max(apoapsis_UT - planned_burn_s / 2, now + min_lead_s);

            logger.LogInfo($"[FinalCircularize] Ap {(apoapsisRadius - body.radius) / 1000:n1}km Pe {(periapsisRadius - body.radius) / 1000:n1}km: " +
                $"{planned_mps:n0}m/s, about {planned_burn_s:n0}s at {accel:n1}m/s², ignition in {ignition_UT - now:n0}s " +
                $"(apoapsis in {apoapsis_UT - now:n0}s)");

            mode = Mode.Turn;
            status_line = "Lining up for the circularization burn";
        }

        // What's left to reach a circular orbit at this height: circular speed along the
        // horizontal part of the velocity, minus the velocity. Works in any axes.
        static Vector3d BurnLeft(Vector3d r, Vector3d v, double mu)
        {
            Vector3d up = r.normalized;
            Vector3d level = v - up * Vector3d.Dot(v, up);
            if (level.magnitude < 1e-6)
                return Vector3d.zero;
            return level.normalized * Math.Sqrt(mu / r.magnitude) - v;
        }

        public void Release()
        {
            hold.Release();
            current_vessel?.SetThrottle(0);
        }

        public override void Update()
        {
            if (finished) return;
            var vessel = current_vessel?.VesselComponent;
            if (vessel == null) return;

            double now = GeneralTools.Current_UT;
            var model = new ImpactModel(vessel.mainBody, now);
            model.StateAt(vessel.Orbit, now, out Vector3d r, out Vector3d v);
            Vector3d burn = BurnLeft(r, v, model.mu);
            left_mps = burn.magnitude;
            engines.Compute_Thrust();

            switch (mode)
            {
                case Mode.Turn:
                    current_vessel.SetThrottle(0);
                    hold.HoldDirection(burn);
                    // Moves on at ignition even if not lined up yet: the burn holds the engines
                    // off until the nose is close enough.
                    if (hold.attitude_error_deg < aligned_deg || now >= ignition_UT)
                    {
                        mode = Mode.Warp;
                        warp.Start_Ut(ignition_UT - WarpToSettings.warp_safe_duration.V);
                    }
                    status_line = $"Lining up: {hold.attitude_error_deg:n1}°";
                    break;

                case Mode.Warp:
                    current_vessel.SetThrottle(0);
                    hold.HoldDirection(burn);
                    if (now < ignition_UT - WarpToSettings.warp_safe_duration.V)
                    {
                        warp.Update();
                        status_line = warp.status_line;
                        break;
                    }
                    TimeWarpTools.SetRateIndex(0, false);
                    status_line = $"Ignition in {StrTool.DurationToString(ignition_UT - now)}";
                    if (now < ignition_UT)
                        break;
                    mode = Mode.Burn;
                    burn_start_UT = now;
                    best_left_mps = double.PositiveInfinity;
                    best_left_UT = now;
                    break;

                case Mode.Burn:
                    FlyBurn(burn, now);
                    break;
            }
        }

        void FlyBurn(Vector3d burn, double now)
        {
            if (left_mps < best_left_mps - stall_progress_mps)
            {
                best_left_mps = left_mps;
                best_left_UT = now;
            }
            if (left_mps < done_mps || (left_mps < stall_below_mps && now - best_left_UT > stall_s))
            {
                Finish(now);
                return;
            }

            hold.HoldDirection(burn);
            double accel = engines.has_active_engine ? engines.active_full_dv : 0;
            // No lit engine (a stage burned out): full throttle, so the burn carries on as soon as
            // the next one is staged.
            double throttle = hold.attitude_error_deg > pause_beyond_deg ? 0
                : accel <= 0 ? 1
                : Math.Max(min_throttle, Math.Min(1, left_mps / (accel * taper_s)));
            current_vessel.SetThrottle((float)throttle);
            status_line = $"Circularizing: {left_mps:n1} m/s to go";
        }

        void Finish(double now)
        {
            Release();
            finished = true;

            var vessel = current_vessel.VesselComponent;
            IKeplerPatch orbit = vessel.Orbit;
            var body = orbit.referenceBody;
            LandingTargeting.OrbitalElementsFromStateVectors(orbit.GetRelativePositionAtUTZup(now),
                orbit.GetOrbitalVelocityAtUTZup(now), body.gravParameter,
                out _, out double apoapsisRadius, out double periapsisRadius);
            logger.LogInfo($"[FinalCircularize] burn done after {now - burn_start_UT:n0}s (planned {planned_burn_s:n0}s), " +
                $"{left_mps:n2}m/s left | Ap {(apoapsisRadius - body.radius) / 1000:n2}km Pe {(periapsisRadius - body.radius) / 1000:n2}km");

            lift.EndLiftPilot(true, "Circularized - ascent complete");
        }

        public override void UpdateInfoRows(Action<string, string> addRow)
        {
            double now = GeneralTools.Current_UT;
            if (mode != Mode.Burn && ignition_UT > now)
                addRow("Ignition In", StrTool.DurationToString(ignition_UT - now));
            addRow("Burn Left", $"{left_mps:n1} m/s");
            if (K2D2Settings.debug_mode.V)
            {
                addRow("Planned Burn", $"{planned_mps:n0} m/s, {planned_burn_s:n0} s");
                addRow("Attitude Error", $"{hold.attitude_error_deg:n1}°");
            }
        }

        public override void updateUI(VisualElement root_el, FullStatus st)
        {
            st.Status(string.IsNullOrEmpty(status_line) ? "Circularizing..." : status_line);
        }
    }
}
