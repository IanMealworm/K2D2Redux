using System;
using K2D2.Controller;
using K2D2.KSPService;
using KSP.Sim;
using KSP.Sim.impl;
using KTools;
using UnityEngine;
using ILogger = ReduxLib.Logging.ILogger;

namespace K2D2.Landing
{
    // The landing burn on a body with an atmosphere, from the handover all the way to touchdown:
    // one continuous burn - steered onto the target for a precision landing, or just killing the
    // sideways speed for a regular one.
    //
    // The regular braking it replaces burned at full thrust until the fall was slow, then fell
    // and braked again, and held retrograde all the way down. On the big booster that stopped
    // 3.7 km up, fell, braked again, and crept down the last kilometre - about 1,500 m/s for a
    // burn that needs about 1,000 - and with no steering it came down where the fall happened to
    // end, 0.9 km from the target. Holding retrograde near the ground, where the velocity is tiny
    // and its direction jumps around, is also what made the vessel wobble at touchdown.
    //
    // Braking: several times a second, the throttle is set to what ends the braking just above
    // the ground (AtmoBurnPlanner's fall with drag, from the vessel's state right now). Re-planning
    // all the way takes care of what a plan made once can't: the vessel getting lighter as it burns
    // (a 1,500 m/s burn takes the thrust/weight from about 4 to 6), and drag being off.
    //
    // Steering: a sideways speed toward the target that gets there by touchdown - but never faster
    // than the vessel can still stop from, with the sideways acceleration it's actually getting -
    // and a push toward that speed. The drag's own sideways part is taken off, and the thrust is
    // tilted off vertical to make the rest, as far as thrust the braking doesn't need allows.
    //
    // How much it actually gets is measured: on the test booster only 40-60% of what the tilt
    // should give (the air on its long side, and the engines' gimbals fighting the air to hold
    // the tilt, both push back). Planning as if it got all of it rushed toward the target late
    // in the burn and touched down drifting 15 m/s sideways. (Tilting off the airflow instead is
    // worse still: the airflow turns with the drift, so the vessel can never drift faster than
    // about a sixth of its speed.)
    //
    // Final descent (the last few tens of metres): nose up, sinking at the Touch Down speed
    // profile's speed, killing sideways drift first - it stops sinking while it's still drifting
    // fast - and closing in on the target at no more than a walking pace. The run ends when the
    // game says the vessel has landed (LandingPilot).
    public class AtmoLandingBurn : ExecuteController
    {
        readonly ILogger logger = ReduxLib.ReduxLib.GetLogger("K2D2.AtmoLandingBurn");

        // The braking ends this high above the ground, at the Touch Down speed for this height;
        // the final descent takes it from there.
        public const double final_height_m = 50;

        // The speed the braking ends at - the player's Touch Down speed profile at final_height_m.
        public static double FinalSpeed(LandingSettings settings) => settings.compute_limit_speed((float)final_height_m);

        const double solve_interval_s = 0.1;
        // The thrust tilts at most this far off vertical to steer, while braking and in the final
        // descent.
        const double max_braking_tilt_deg = 15;
        const double max_final_tilt_deg = 10;
        // While still drifting high in the final descent, more: the first lift-vector runs reached
        // it with 9 m/s of sideways speed (the booster lagging its attitude 7-13° in the last
        // 400 m) and touched down still sliding 5-7 m/s.
        const double max_drift_tilt_deg = 15;
        // The steering's time left never counts as less than this.
        const double min_steering_time_s = 4;
        // The push toward the wanted sideways speed closes the gap over about this long.
        const double steering_response_s = 2;
        // The approach speed is kept to what would stop in half the distance left at the sideways
        // acceleration being achieved, as a margin.
        const double stopping_margin = 0.5;
        // Final descent: closing in on the target no faster than this, and sinking slower while
        // drifting faster than drift_hold_mps - both only above drift_hold_above_m. (Stopping the
        // sink altogether made the test booster climb back up, cut the throttle, and tip over.)
        const double final_approach_mps = 2, drift_hold_mps = 3, drift_hold_above_m = 10;
        const double drift_sink_fraction = 0.3;
        // The engines never go below this much thrust (in g) in the final descent: their gimbals
        // are what keeps a long booster upright.
        const double min_final_thrust_g = 0.3;
        // How much of the expected sideways acceleration the vessel gets: measured over
        // response_window_s, blended in at response_blend; starts from what the test booster got.
        const double response_window_s = 1, response_blend = 0.3;
        const double first_efficiency = 0.5, min_efficiency = 0.2;
        // Final descent: how firmly the sink speed is held (1/s).
        const double sink_gain = 2;
        // Full throttle within this pointing error, fading to none at TouchDown's max angle.
        const double full_throttle_within_deg = 5;
        // How fast the throttle may change (fraction per second), so ignition eases in.
        const float throttle_rate_per_s = 3;
        const double log_interval_s = 2;

        readonly LandingPilot landing;
        readonly AtmoAttitudeHold hold = new AtmoAttitudeHold();
        readonly KSPVessel current_vessel;

        enum Step { Braking, Final }
        Step step;

        double last_solve_UT, last_log_UT, start_UT;
        double wanted_throttle;
        float throttle;
        Vector3d thrust_direction;
        double tilt_deg, stop_height, burn_left_s;
        // The target from the vessel, along the ground: ahead (in the direction of travel) and to
        // the side (along Cross(up, direction of travel)).
        double target_ahead, target_side, target_distance;
        // Sideways acceleration asked for (including the drag's part), and what was measured.
        Vector3d wanted_sideways, expected_sideways;
        double achieved_sideways = double.NaN;
        double efficiency;
        Vector3d response_velocity;
        double response_UT = double.NaN;

        public AtmoLandingBurn(LandingPilot landing)
        {
            this.landing = landing;
            current_vessel = K2D2_Plugin.Instance.current_vessel;
        }

        // Whether the burn steers onto the landing target; without one it only cancels the
        // sideways speed and comes straight down.
        bool steer_to_target = true;

        public void Start(bool steer_to_target)
        {
            this.steer_to_target = steer_to_target;
            Start();
        }

        public override void Start()
        {
            finished = false;
            step = Step.Braking;
            last_solve_UT = last_log_UT = double.NaN;
            start_UT = GeneralTools.Current_UT;
            wanted_throttle = 0;
            throttle = 0;
            thrust_direction = Vector3d.zero;
            response_UT = achieved_sideways = double.NaN;
            efficiency = first_efficiency;
        }

        public void Release()
        {
            hold.Release();
        }

        public override void Update()
        {
            var vessel = current_vessel?.VesselComponent;
            if (vessel == null || finished)
                return;
            double now = GeneralTools.Current_UT;
            TimeWarpTools.SetRateIndex(0, false);

            // (Its false - barely moving through the air - still fills these in, and the final
            // descent can be that slow.)
            hold.UpdateAxes(out Vector3d position, out Vector3d air_velocity, out _);
            if (position.magnitude < 1)
                return;
            Vector3d up = position.normalized;
            double v_vertical = Vector3d.Dot(air_velocity, up);
            Vector3d horizontal = air_velocity - up * v_vertical;
            // Height of the vessel's bottom above the ground (or sea) under it.
            double height = landing.altitude;
            double max_accel = landing.burn_dV.full_dv;

            if (step == Step.Braking && height < final_height_m * 1.5)
            {
                step = Step.Final;
                logger.LogInfo($"[LandingBurn] final descent from {height:n0}m, {-v_vertical:n1}m/s down, " +
                    $"{horizontal.magnitude:n1}m/s sideways" + (steer_to_target ? $", {landing.DistanceToTarget():n0}m from the target" : ""));
            }

            if (double.IsNaN(last_solve_UT) || now - last_solve_UT >= solve_interval_s)
            {
                last_solve_UT = now;
                if (step == Step.Braking)
                    SolveBraking(vessel, position, air_velocity, up, v_vertical, horizontal, height, max_accel);
                else
                    SolveFinal(vessel, position, up, v_vertical, horizontal, height, max_accel);
            }

            hold.HoldDirection(thrust_direction);
            double max_angle = Math.Max(full_throttle_within_deg + 1, landing.brake.touch_down_max_angle.V);
            double pointing = Math.Min(1, Math.Max(0, (max_angle - hold.attitude_error_deg) / (max_angle - full_throttle_within_deg)));
            throttle = Mathf.MoveTowards(throttle, (float)(wanted_throttle * pointing), throttle_rate_per_s * Time.deltaTime);
            current_vessel.SetThrottle(throttle);

            status_line = step == Step.Braking
                ? $"Landing burn: braking to stop {stop_height:n0} m up" +
                  (steer_to_target ? $", {Math.Sqrt(target_ahead * target_ahead + target_side * target_side):n0} m to the target" : "")
                : $"Landing burn: final descent, {height:n0} m up";

            if (double.IsNaN(last_log_UT) || now - last_log_UT >= log_interval_s)
            {
                last_log_UT = now;
                logger.LogInfo($"[LandingBurn] {step} at {height:n0}m, {air_velocity.magnitude:n0}m/s ({-v_vertical:n0} down, " +
                    $"{horizontal.magnitude:n0} sideways) | throttle {wanted_throttle:P0}, tilt {tilt_deg:n1}°, pointing off {hold.attitude_error_deg:n1}°" +
                    (step == Step.Braking
                        ? $" | braking ends {stop_height:n0}m up in {burn_left_s:n0}s | target {target_ahead:n0}m ahead, {target_side:n0}m to the side" +
                          $" | sideways accel wanted {wanted_sideways.magnitude:n1}m/s², expected {expected_sideways.magnitude:n1}, " +
                          $"got {achieved_sideways:n1} ({efficiency:P0} on average)"
                        : $" | target {target_distance:n0}m away"));
            }
        }

        // Throttle: the one that ends the braking at final_height_m. Steering: tilt toward moving
        // where it ends onto the target.
        void SolveBraking(VesselComponent vessel, Vector3d position, Vector3d air_velocity, Vector3d up,
            double v_vertical, Vector3d horizontal, double height, double max_accel)
        {
            var body = vessel.mainBody;
            var planner = landing.atmo_guidance.planner;
            double ground_altitude = vessel.AltitudeFromSeaLevel - height;
            double v_horizontal = horizontal.magnitude;
            double stop_speed = FinalSpeed(landing.settings);

            double tau;
            planner.Brake(body, height, ground_altitude, v_horizontal, v_vertical, max_accel, stop_speed,
                out double h, out double x, out double t);
            if (max_accel <= 0 || h < final_height_m)
                tau = 1;
            else
            {
                // More thrust stops higher; find the throttle that stops at the wanted height.
                double lo = 0, hi = 1;
                for (int i = 0; i < 12; i++)
                {
                    double mid = (lo + hi) / 2;
                    planner.Brake(body, height, ground_altitude, v_horizontal, v_vertical, max_accel * mid, stop_speed,
                        out double h_mid, out _, out _);
                    if (h_mid >= final_height_m) hi = mid; else lo = mid;
                }
                tau = hi;
                planner.Brake(body, height, ground_altitude, v_horizontal, v_vertical, max_accel * tau, stop_speed,
                    out h, out x, out t);
            }
            wanted_throttle = tau;
            stop_height = h;
            burn_left_s = t;

            // The target's offset along the ground, and how far it is ahead / to the side.
            Vector3d along = v_horizontal > 0.5 ? horizontal / v_horizontal : Vector3d.zero;
            Vector3d to_target = ToTarget(vessel, position, up);
            target_ahead = Vector3d.Dot(to_target, along);
            target_side = Vector3d.Dot(to_target, Vector3d.Cross(up, along));

            // The braking comes first: the tilt can only use thrust the braking leaves spare, and
            // the throttle goes up to keep the braking (the upward part) as planned.
            double max_tilt = Math.Min(max_braking_tilt_deg, Math.Acos(Math.Min(1, tau)) * 180 / Math.PI);
            double vertical_thrust = Math.Max(0.1, tau) * max_accel;
            // Touchdown comes about final_height_m / final speed after the braking ends.
            double t_go = Math.Max(min_steering_time_s, t + final_height_m / Math.Max(1, stop_speed));
            double available = efficiency * vertical_thrust * Math.Tan(max_braking_tilt_deg * Math.PI / 180);
            // Whatever sideways speed it builds up has to be gone again by the end of the braking:
            // the final descent can hardly tilt. (Both test runs reached it drifting 15-18 m/s
            // after turning back toward a target 1 km away in the last seconds.)
            double can_stop_from = available * Math.Max(0, t) / 2;
            wanted_sideways = Steer(to_target, horizontal, t_go, available, can_stop_from);
            double air_speed = air_velocity.magnitude;
            Vector3d drag_sideways = air_speed > 1
                ? -horizontal / air_speed * planner.DragAccel(body, vessel.AltitudeFromSeaLevel, air_speed)
                : Vector3d.zero;
            thrust_direction = Tilt(up, wanted_sideways - drag_sideways, vertical_thrust, max_tilt);
            wanted_throttle = Math.Min(1, tau / Math.Cos(tilt_deg * Math.PI / 180));
            MeasureResponse(up, horizontal, max_accel, drag_sideways);
        }

        // The sideways push: toward a speed at the target that gets there in t_go, but no faster
        // than stops in time at `available` m/s^2 (or than max_speed).
        Vector3d Steer(Vector3d to_target, Vector3d horizontal, double t_go, double available, double max_speed)
        {
            double d = to_target.magnitude;
            Vector3d wanted_velocity = Vector3d.zero;
            if (d > 0.5)
            {
                double speed = Math.Min(max_speed, Math.Min(d / t_go, Math.Sqrt(2 * stopping_margin * Math.Max(0.1, available) * d)));
                wanted_velocity = to_target / d * speed;
            }
            return (wanted_velocity - horizontal) / steering_response_s;
        }

        // How much of the sideways acceleration the thrust tilt (and drag) should give the vessel
        // actually gets, from the change in sideways speed.
        void MeasureResponse(Vector3d up, Vector3d horizontal, double max_accel, Vector3d drag_sideways)
        {
            Vector3d thrust = thrust_direction * (throttle * max_accel);
            expected_sideways = thrust - up * Vector3d.Dot(thrust, up) + drag_sideways;
            double now = GeneralTools.Current_UT;
            if (double.IsNaN(response_UT))
            {
                response_velocity = horizontal;
                response_UT = now;
                return;
            }
            if (now - response_UT < response_window_s)
                return;
            Vector3d accel = (horizontal - response_velocity) / (now - response_UT);
            double expected = expected_sideways.magnitude;
            achieved_sideways = expected > 1e-3 ? Vector3d.Dot(accel, expected_sideways / expected) : double.NaN;
            // Only a sizeable push says much about how well the vessel follows it.
            if (expected > 1)
                efficiency += (Math.Max(min_efficiency, Math.Min(1, achieved_sideways / expected)) - efficiency) * response_blend;
            response_velocity = horizontal;
            response_UT = now;
        }

        // Sink at the Touch Down speed for this height (or hold height while drifting fast), nose up,
        // steering as in the braking but closing in on the target only slowly.
        void SolveFinal(VesselComponent vessel, Vector3d position, Vector3d up, double v_vertical, Vector3d horizontal,
            double height, double max_accel)
        {
            double g = vessel.graviticAcceleration.magnitude;
            double wanted_sink = landing.settings.compute_limit_speed((float)Math.Max(0, height));
            bool drifting = horizontal.magnitude > drift_hold_mps && height > drift_hold_above_m;
            if (drifting)
                wanted_sink *= drift_sink_fraction;
            double max_tilt = drifting ? max_drift_tilt_deg : max_final_tilt_deg;
            double vertical_accel = g + sink_gain * (-wanted_sink - v_vertical);
            Vector3d to_target = ToTarget(vessel, position, up);
            target_distance = to_target.magnitude;
            double t_go = Math.Max(min_steering_time_s, height / Math.Max(0.5, wanted_sink));
            double available = efficiency * Math.Max(0.1, vertical_accel) * Math.Tan(max_tilt * Math.PI / 180);
            // The last few metres only kill the drift, so the legs come down without sideways speed.
            wanted_sideways = Steer(to_target, horizontal, t_go, available, height > drift_hold_above_m ? final_approach_mps : 0);
            thrust_direction = Tilt(up, wanted_sideways, Math.Max(0.1, vertical_accel), max_tilt);
            double cos_tilt = Math.Cos(tilt_deg * Math.PI / 180);
            wanted_throttle = max_accel > 0
                ? Math.Min(1, Math.Max(min_final_thrust_g * g, vertical_accel) / (max_accel * cos_tilt))
                : 0;
            MeasureResponse(up, horizontal, max_accel, Vector3d.zero);
        }

        // `main` (a unit vector) tilted toward `sideways` (an acceleration at right angles to it)
        // as far as that acceleration against `main_accel` asks, up to max_deg.
        Vector3d Tilt(Vector3d main, Vector3d sideways, double main_accel, double max_deg)
        {
            double side = sideways.magnitude;
            if (side < 1e-6)
            {
                tilt_deg = 0;
                return main;
            }
            double angle = Math.Min(max_deg * Math.PI / 180, Math.Atan2(side, main_accel));
            tilt_deg = angle * 180 / Math.PI;
            return main * Math.Cos(angle) + sideways / side * Math.Sin(angle);
        }

        // From the vessel to the target, along the ground. Zero without a target, so the steering
        // only cancels the sideways speed.
        Vector3d ToTarget(VesselComponent vessel, Vector3d position, Vector3d up)
        {
            if (!steer_to_target)
                return Vector3d.zero;
            Vector3d to_target = TargetPoint(vessel) - position;
            return to_target - up * Vector3d.Dot(to_target, up);
        }

        // The target on the ground, in the same axes as the vessel's position (AtmoAttitudeHold's).
        Vector3d TargetPoint(VesselComponent vessel)
        {
            var settings = landing.settings;
            var model = new ImpactModel(vessel.mainBody, GeneralTools.Current_UT);
            double lat = settings.target_latitude.V, lon = settings.target_longitude.V;
            return model.SurfaceUp(lat, lon) * model.TerrainRadius(lat, lon);
        }

        public override void UpdateInfoRows(Action<string, string> addRow)
        {
            if (step == Step.Braking && steer_to_target)
                addRow("To Target", $"{target_ahead:n0} m ahead, {target_side:n0} m to the side");

            // The rest is for tuning and bug reports.
            if (!K2D2Settings.debug_mode.V)
                return;
            addRow("Throttle", $"{wanted_throttle:P0}");
            addRow("Tilt", $"{tilt_deg:n1}°");
            if (step == Step.Braking)
                addRow("Braking Ends", $"{stop_height:n0} m up in {burn_left_s:n0} s");
        }
    }
}
