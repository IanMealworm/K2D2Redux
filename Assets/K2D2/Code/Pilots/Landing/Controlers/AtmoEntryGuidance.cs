using System;
using System.Collections.Generic;
using K2D2.Controller;
using K2D2.KSPService;
using K2D2.Node;
using KSP.Modules;
using KSP.Sim;
using KSP.Sim.impl;
using KTools;
using ILogger = ReduxLib.Logging.ILogger;

namespace K2D2.Landing
{
    // Entry guidance for atmospheric precision landing: steers the vessel's lift during reentry
    // so Redux's predicted landing point stays on the target.
    //
    // The vessel flies at a fixed angle of attack, which makes lift; turning that lift around the
    // direction of travel (the bank angle) is the steering, as on Apollo or the Shuttle:
    //  - Lift pointing up keeps the vessel higher for longer, so it lands further away; pointing
    //    down brings it down sooner. The vertical part of the lift - from +1 (straight up) to -1
    //    (straight down) - controls how far along the ground track it lands.
    //  - Whatever lift isn't vertical points to one side and turns the vessel that way. Which
    //    side is chosen by the sign of the bank, flipped (a "bank reversal") whenever the target
    //    gets too far off to the other side of the path the vessel is flying now.
    //
    // Redux only predicts for the attitude being flown right now, and restarts its prediction
    // whenever that attitude changes by more than 2°. So the guidance works in steps: hold an
    // attitude, wait for a fresh prediction made after the vessel settled into it (Redux answers
    // within half a second, but turning the vessel takes a few), adjust, repeat.
    //
    // How far a change in vertical lift moves the landing point isn't known in advance (it depends
    // on the vessel, the speed and the air), so it's measured from each pair of steps (a secant
    // estimate), starting from what the probe flights saw.
    //
    // It steers until the landing burn is due, then hands over to K2D2's braking (LandingPilot's
    // Brake mode). AtmoBurnPlanner works out, with drag, when that is and how much shorter the
    // burn makes the flight; the steering aims that far beyond the target so the burn ends on it.
    public class AtmoEntryGuidance : ExecuteController
    {
        public ILogger logger = ReduxLib.ReduxLib.GetLogger("K2D2.AtmoEntryGuidance");

        // Steering goes on until it's time for the landing burn, as planned with drag by
        // AtmoBurnPlanner - low down the air is thick and cooler, so a big angle of attack makes
        // a lot of lift (the fourth test flight handed over at a fixed 10 km and the player won
        // back 8 km by hand after that). The steering also aims beyond the target by however much
        // shorter the burn will make the flight, so the burn ends on it.
        //
        // The planner's flat model only holds over the last stretch, so it's used below this.
        const double burn_planning_below_m = 35000;
        const double burn_plan_interval_s = 0.5;
        // The braking is planned to bring the fall down to TouchDown's slow speed this fraction of
        // the way down to its start height (start_touchdown_altitude) - a reserve on top of the
        // planner's own.
        const double stop_height_fraction = 0.7;
        // The overshoot is smoothed over about this long, so a noisy plan doesn't jerk the aim.
        const double shortfall_smoothing_s = 5;
        // Without a burn plan (no drag measured yet, or no thrust figure for the engines), hand
        // over here as before.
        const double unplanned_handover_m = 10000;
        // Hand over regardless below these.
        const double end_altitude_m = 2000;
        const double end_speed_mps = 150;

        // When the bank alone can't reach the target (lift already fully up, or fully down, and
        // still missing), the angle of attack goes up in steps - more lift, and more drag - up to
        // this, as far as heating allows. It comes back down once the bank has room again.
        const double max_angle_of_attack_deg = 25;
        const double aoa_boost_step_deg = 2.5;
        const double aoa_boost_interval_s = 5;
        // Smaller misses than this aren't worth more angle of attack.
        const double aoa_boost_min_miss_m = 1000;

        // Redux restarts its prediction when the held attitude moves more than this.
        const double redux_attitude_tolerance_deg = 2;

        // Bigger bank changes are split into steps this size, so each one settles quickly.
        const double max_bank_step_deg = 30;
        // Smaller bank changes aren't worth making - each one restarts Redux's prediction and
        // stirs the attitude (the seventh test flight wobbled between 90° and 94° every step).
        const double min_bank_change_deg = 3;

        // How far (m) the landing point moves per unit of vertical lift, to start from: the probe
        // flights saw about 20 km between lift fully up and fully down.
        const double initial_range_per_lift_m = 10000;
        const double min_range_per_lift_m = 500;
        const double min_lift_change_to_measure = 0.2;
        const double range_per_lift_blend = 0.5;
        const double max_range_per_lift_m = 200000;

        // Bank reversals go by the crossrange: how far the target sits to the side of the path the
        // vessel is flying right now (Apollo's and the Shuttle's way). Redux's sideways miss can't
        // be used for that: it assumes the bank is held all the way down, so it jumps from one
        // side to the other with every reversal - 11 km either way for just 30° of bank on the
        // seventh test flight, which flipped the bank between -30° and 0° for four minutes while
        // the landing point sat 48 km long.
        //
        // The vessel leans toward the target's side until the target is more than this far off to
        // the other side. Early on there's plenty of time to turn back, so the band is wide; it
        // narrows with speed so the last reversal leaves little sideways miss.
        const double reversal_deadband_s = 1.5;
        const double min_reversal_deadband_m = 500;
        const double max_reversal_deadband_m = 4000;

        // Measured lift has to be at least this big to tell which way it points, the vessel has
        // to be flying at least this angle of attack (near 0° there's hardly any lift and what's
        // measured is noise), and this many measurements in a row have to agree clearly (within
        // lift_agree_deg of one answer or the other). The seventh test flight measured once, at
        // 0-2.5° late in the entry, got an answer 98° off - barely past "the other way" - and
        // flipped the lean, so "lift down" became lift up for the rest of the flight.
        const double min_lift_to_measure = 0.3;
        const double min_aoa_to_measure_lift_deg = 5;
        const int lift_measurements_needed = 3;
        const double lift_agree_deg = 45;
        const double accel_sample_s = 0.5;

        // Warp stops this long before reaching the atmosphere.
        const double warp_margin_s = 30;
        // How often Redux's prediction is logged on the way down to the atmosphere.
        const double coast_log_interval_s = 10;

        readonly LandingPilot landing;
        readonly AtmoAttitudeHold hold = new AtmoAttitudeHold();
        readonly EntryHeatLimit heat = new EntryHeatLimit();
        readonly KSPVessel current_vessel;

        bool in_atmosphere;

        // Where the lift should point, as a bank angle: 0 up, ±90 to either side, 180 down.
        double lift_bank_deg;
        // Which side the lift leans to (+1: positive bank). Kept separately from the bank angle so
        // a reversal survives passing through 0° on its way over (the third test flight's
        // reversals were undone at 0° every time, cycling 30° -> 0° -> 30° for minutes with the
        // landing point 11 km off to the side).
        double side_sign;
        // How far the vessel has to be tilted from that for its lift to point there. A vessel flying
        // engines-first (like the probe lander) lifts away from its tilt: 180. Checked in flight.
        double tilt_offset_deg;
        bool lift_direction_checked;
        int lift_votes_same, lift_votes_flipped;

        double range_per_lift_m;
        double last_vertical_lift = double.NaN, last_along_error = double.NaN;

        double settled_UT;
        // Each Redux result is steered on once; the next step waits for a newer one.
        double last_used_snapshot_UT;
        double angle_of_attack_deg;
        double aoa_boost_deg, last_boost_change_UT;
        double last_coast_log_UT;
        int updates;

        // Aero acceleration measurement: which way the lift points, and the drag for the burn
        // planner.
        Vector3d previous_velocity;
        double previous_sample_UT;

        readonly AtmoBurnPlanner planner = new AtmoBurnPlanner();
        AtmoBurnPlanner.Plan burn_plan;
        double last_plan_UT;
        double shortfall_m;
        bool have_shortfall;
        // Redux's latest along-track miss, and whether the steering has run out of ways to take
        // back an overshoot (lift already fully down). Only then is the burn brought forward to
        // stop short (see AtmoBurnPlanner) - while the lift can still fix it, braking early would
        // only waste fuel.
        double last_redux_along = double.NaN;
        bool overshoot_out_of_reach;
        double ground_altitude;

        public AtmoEntryGuidance(LandingPilot landing)
        {
            this.landing = landing;
            current_vessel = K2D2_Plugin.Instance.current_vessel;
        }

        double vertical_lift => Math.Cos(lift_bank_deg * Math.PI / 180);

        public override void Start()
        {
            finished = false;
            in_atmosphere = false;
            lift_bank_deg = 90;
            side_sign = 1;
            tilt_offset_deg = 180;
            lift_direction_checked = false;
            lift_votes_same = lift_votes_flipped = 0;
            range_per_lift_m = initial_range_per_lift_m;
            last_vertical_lift = last_along_error = double.NaN;
            settled_UT = double.NaN;
            last_used_snapshot_UT = double.NaN;
            last_coast_log_UT = double.NaN;
            previous_sample_UT = double.NaN;
            angle_of_attack_deg = landing.settings.entry_angle_of_attack.V;
            aoa_boost_deg = 0;
            last_boost_change_UT = double.NaN;
            heat.Reset(max_angle_of_attack_deg);
            planner.Reset();
            burn_plan = default;
            last_plan_UT = double.NaN;
            shortfall_m = 0;
            have_shortfall = false;
            last_redux_along = double.NaN;
            overshoot_out_of_reach = false;
            updates = 0;
        }

        // Hands the attitude back to normal SAS - for when the landing is stopped mid-entry.
        public void Release() => hold.Release();

        public override void Update()
        {
            var vessel = current_vessel?.VesselComponent;
            if (vessel == null)
                return;

            double now = GeneralTools.Current_UT;
            var body = vessel.mainBody;
            double altitude = vessel.AltitudeFromRadius;

            if (!in_atmosphere)
            {
                if (altitude > body.atmosphereDepth)
                {
                    CoastToAtmosphere(vessel, now);
                    return;
                }
                in_atmosphere = true;
                TimeWarpTools.SetRateIndex(0, false);
                ground_altitude = body.SurfaceProvider.GetTerrainAltitudeFromCenter(
                    landing.settings.target_latitude.V, landing.settings.target_longitude.V) - body.radius;
                logger.LogInfo($"[EntryGuidance] entered the atmosphere at {altitude:n0}m, angle of attack {landing.settings.entry_angle_of_attack.V:n0}°");
            }

            if (!hold.UpdateAxes(out Vector3d position, out Vector3d air_velocity, out _))
                return;

            PlanBurn(vessel, position, air_velocity, altitude, now);
            // The plan is from up to burn_plan_interval_s ago, so its countdown is run on to now.
            bool burn_due = burn_plan.found ? burn_plan.handover_in_s - (now - last_plan_UT) <= 0 : altitude < unplanned_handover_m;
            if (burn_due || altitude < end_altitude_m || air_velocity.magnitude < end_speed_mps)
            {
                var last = ReduxImpactPrediction.GetFlown(vessel);
                string where = "";
                if (last.usable)
                {
                    Miss(last, body, out double along, out double across);
                    where = $", Redux lands {along:n0}m along (aiming {shortfall_m:n0}m long), {across:n0}m across";
                }
                string burn = burn_plan.found
                    ? $" | burn planned to slow to {burn_plan.stop_falling_speed:n0}m/s at {burn_plan.stop_height_m:n0}m, " +
                      (burn_plan.early ? $"brought forward {burn_plan.latest_handover_in_s:n0}s to end {burn_plan.expected_overshoot_m:n0}m past the target, " : "") +
                      $"drag k {planner.drag_k:e2}, lift/drag {planner.lift_over_drag:n2}"
                    : " | no burn plan";
                logger.LogInfo($"[EntryGuidance] done at {altitude:n0}m, {air_velocity.magnitude:n0}m/s after {updates} updates" +
                    $"{(burn_due ? ", landing burn due" : "")}{where}{burn} - handing over to the landing burn");
                hold.Release();
                finished = true;
                return;
            }

            UpdateAngleOfAttack(vessel, now);
            hold.Hold(angle_of_attack_deg, lift_bank_deg + tilt_offset_deg);
            if (double.IsNaN(settled_UT) && hold.attitude_error_deg < redux_attitude_tolerance_deg)
                settled_UT = now;

            if (MeasureAero(vessel, now, out Vector3d aero))
            {
                double drag = -Vector3d.Dot(aero, hold.flow);
                planner.MeasureDrag(body, drag, Vector3d.Dot(aero, hold.lift_up), altitude, air_velocity.magnitude, now);
                CheckLiftDirection(aero, drag);
            }

            var impact = ReduxImpactPrediction.GetFlown(vessel);
            bool fresh = impact.usable && !double.IsNaN(settled_UT) && impact.snapshot_UT >= settled_UT
                && !(impact.snapshot_UT <= last_used_snapshot_UT);
            if (fresh)
            {
                last_used_snapshot_UT = impact.snapshot_UT;
                Steer(impact, position, body, altitude, air_velocity.magnitude);
            }

            status_line = $"Entry guidance: lift {DescribeLift()}, angle of attack {angle_of_attack_deg:n1}°, attitude error {hold.attitude_error_deg:n1}°";
        }

        void CoastToAtmosphere(VesselComponent vessel, double now)
        {
            // Retrograde at the entry point with no lift, which is what the entry correction aimed
            // with - and what Redux then predicts (see AtmoAttitudeHold.HoldForEntry).
            hold.HoldForEntry(0, 0);
            status_line = "Entry guidance: coasting to the atmosphere";
            LogCoast(vessel, now);

            if (!landing.settings.auto_warp.V)
                return;

            var model = new ImpactModel(vessel.mainBody, now);
            model.StateAt(vessel.Orbit, now, out Vector3d r, out Vector3d v);
            double entry_radius = vessel.mainBody.radius + vessel.mainBody.atmosphereDepth;
            if (!model.Impact(r, v, now, entry_radius, out double entry_UT, out _))
                return;

            double time_left = entry_UT - now - warp_margin_s;
            TimeWarpTools.SetRateIndex(time_left > 0 ? WarpToSettings.compute_wanted_warp_index(time_left) : 0, false);
        }

        // Where Redux has the vessel coming down, every so often while nothing is being changed -
        // to see whether the prediction holds still on the way to the atmosphere.
        void LogCoast(VesselComponent vessel, double now)
        {
            if (!double.IsNaN(last_coast_log_UT) && now - last_coast_log_UT < coast_log_interval_s)
                return;
            var impact = ReduxImpactPrediction.GetFlown(vessel);
            if (!impact.usable || hold.attitude_error_deg > redux_attitude_tolerance_deg)
                return;
            last_coast_log_UT = now;
            Miss(impact, vessel.mainBody, out double along, out double across);
            logger.LogInfo($"[EntryGuidance] coasting at {vessel.AltitudeFromRadius:n0}m: Redux lands {along:n0}m along, {across:n0}m across, " +
                $"impact T+{impact.impact_UT - now:n0}s");
        }

        // Miss split into along the direction of travel (positive: lands beyond the target) and
        // across it (positive: lands toward the "side" axis that positive bank leans to).
        void Miss(ReduxImpact impact, CelestialBodyComponent body, out double along, out double across)
        {
            var settings = landing.settings;
            Vector3d target_up = body.GetRelSurfaceNVector(settings.target_latitude.V, settings.target_longitude.V);
            Vector3d predicted_up = body.GetRelSurfaceNVector(impact.latitude, impact.longitude);
            Vector3d miss = (predicted_up - target_up) * body.radius;
            Vector3d along_dir = (hold.flow - target_up * Vector3d.Dot(hold.flow, target_up)).normalized;
            Vector3d across_dir = Vector3d.Cross(along_dir, target_up);
            if (Vector3d.Dot(across_dir, hold.side) < 0)
                across_dir = -across_dir;
            along = Vector3d.Dot(miss, along_dir);
            across = Vector3d.Dot(miss, across_dir);
        }

        void Steer(ReduxImpact impact, Vector3d position, CelestialBodyComponent body, double altitude, double speed)
        {
            var settings = landing.settings;
            Vector3d target_up = body.GetRelSurfaceNVector(settings.target_latitude.V, settings.target_longitude.V);
            Miss(impact, body, out double redux_along, out double across);
            last_redux_along = redux_along;

            // Secant: the change in Redux's landing point over the change in vertical lift since
            // the last step - from the landing point itself, since the overshoot below moves with
            // the burn plan rather than with the lift.
            // More lift up always means further (the tilt offset takes care of which way the vessel
            // has to lean for that), so a measurement saying otherwise is noise - the seventh test
            // flight got a negative estimate from a small bank change and steered the wrong way for
            // the rest of the entry. Small changes are skipped, and measurements are blended in.
            double u = vertical_lift;
            if (!double.IsNaN(last_vertical_lift) && Math.Abs(u - last_vertical_lift) > min_lift_change_to_measure)
            {
                double measured = (redux_along - last_along_error) / (u - last_vertical_lift);
                if (measured >= min_range_per_lift_m)
                    range_per_lift_m += (Math.Min(measured, max_range_per_lift_m) - range_per_lift_m) * range_per_lift_blend;
            }
            last_vertical_lift = u;
            last_along_error = redux_along;

            // Aim beyond the target by however much shorter the landing burn will make the flight.
            double along = redux_along - shortfall_m;

            double raw_u = u - along / range_per_lift_m;
            double wanted_u = Math.Max(-1, Math.Min(1, raw_u));
            overshoot_out_of_reach = raw_u < -1 && along > aoa_boost_min_miss_m;
            AdjustAoaBoost(raw_u, along);

            // Lean toward the target's side of the current path, flipping only once it's clearly
            // on the other side - every reversal costs a turn during which Redux can't predict.
            // hold.side is the direction positive bank leans the lift to, at right angles to the
            // path's vertical plane.
            double crossrange = body.radius * Math.Asin(Math.Max(-1, Math.Min(1, Vector3d.Dot(target_up, hold.side))));
            double deadband = Math.Max(min_reversal_deadband_m, Math.Min(max_reversal_deadband_m, reversal_deadband_s * speed));
            if (crossrange * side_sign < -deadband)
                side_sign = -side_sign;

            double wanted_bank = side_sign * Math.Acos(wanted_u) * 180 / Math.PI;
            // The short way round: a reversal from mostly-down lift on one side to the other rolls
            // through straight down rather than all the way over the top.
            double step = Math.Max(-max_bank_step_deg, Math.Min(max_bank_step_deg,
                LandingTargeting.WrapDegrees180(wanted_bank - lift_bank_deg)));
            updates++;
            logger.LogInfo($"[EntryGuidance] alt {altitude:n0}m speed {speed:n0}m/s | Redux lands {along + shortfall_m:n0}m along " +
                $"(aim {shortfall_m:n0}m long: {along:n0}m off), {across:n0}m across | target {crossrange:n0}m to the side " +
                $"(deadband {deadband:n0}m) | vertical lift {u:n2} -> {wanted_u:n2}, bank {lift_bank_deg:n0}° -> {LandingTargeting.WrapDegrees180(lift_bank_deg + step):n0}° | " +
                $"{range_per_lift_m:n0}m per unit lift | AoA {angle_of_attack_deg:n1}°, hottest part {heat.hottest_part} at " +
                $"{heat.hottest_fraction:P0} of its limit ({heat.hottest_predicted:P0} in {EntryHeatLimit.lookahead_s:n0}s)" +
                (burn_plan.found ? $" | burn in {burn_plan.handover_in_s:n0}s" +
                    (burn_plan.early ? $" (brought forward from {burn_plan.latest_handover_in_s:n0}s: {burn_plan.latest_overshoot_m:n0}m overshoot -> {burn_plan.expected_overshoot_m:n0}m)" : "") : ""));

            if (Math.Abs(step) >= min_bank_change_deg)
            {
                lift_bank_deg = LandingTargeting.WrapDegrees180(lift_bank_deg + step);
                settled_UT = double.NaN;
            }
        }

        void AdjustAoaBoost(double raw_u, double along)
        {
            double now = GeneralTools.Current_UT;
            if (!double.IsNaN(last_boost_change_UT) && now - last_boost_change_UT < aoa_boost_interval_s)
                return;

            bool out_of_reach = Math.Abs(raw_u) > 1 && Math.Abs(along) > aoa_boost_min_miss_m;
            double nominal = landing.settings.entry_angle_of_attack.V;
            if (out_of_reach && nominal + aoa_boost_deg < max_angle_of_attack_deg)
                aoa_boost_deg = Math.Min(max_angle_of_attack_deg - nominal, aoa_boost_deg + aoa_boost_step_deg);
            else if (!out_of_reach && Math.Abs(raw_u) < 0.7 && aoa_boost_deg > 0)
                aoa_boost_deg = Math.Max(0, aoa_boost_deg - aoa_boost_step_deg);
            else
                return;
            last_boost_change_UT = now;
        }

        // The angle of attack is the setting (plus any boost while the bank is out of reach),
        // lowered while parts are heating toward their limits (tilting off the engine end exposes
        // side-mounted parts to the hot flow) and raised back once they cool.
        void UpdateAngleOfAttack(VesselComponent vessel, double now)
        {
            double wanted_aoa = Math.Min(max_angle_of_attack_deg, landing.settings.entry_angle_of_attack.V + aoa_boost_deg);
            double wanted = Math.Min(wanted_aoa, heat.Update(vessel, now, angle_of_attack_deg, max_angle_of_attack_deg));
            if (Math.Abs(wanted - angle_of_attack_deg) < 0.01)
                return;

            logger.LogInfo($"[EntryGuidance] angle of attack {angle_of_attack_deg:n1}° -> {wanted:n1}°: hottest part {heat.hottest_part} at " +
                $"{heat.hottest_fraction:P0} of its limit, {heat.hottest_predicted:P0} expected in {EntryHeatLimit.lookahead_s:n0}s");
            angle_of_attack_deg = wanted;
            // A new attitude: wait for Redux to predict it, and the lift (so the range a unit of
            // vertical lift buys) has changed size, so the last step can't be compared with the next.
            settled_UT = double.NaN;
            last_vertical_lift = double.NaN;
        }

        // The aerodynamic acceleration (everything but gravity, with the engines off), in the body
        // frame, measured from the change in velocity every accel_sample_s. True when a new
        // measurement is ready and the vessel was holding its attitude (so it belongs to that
        // attitude).
        bool MeasureAero(VesselComponent vessel, double now, out Vector3d accel)
        {
            accel = Vector3d.zero;
            IKeplerPatch orbit = vessel.Orbit;
            Vector3d u = orbit.GetOrbitalVelocityAtUTZup(now);
            if (double.IsNaN(previous_sample_UT))
            {
                previous_velocity = u;
                previous_sample_UT = now;
                return false;
            }
            double dt = now - previous_sample_UT;
            if (dt < accel_sample_s)
                return false;

            Vector3d p = orbit.GetRelativePositionAtUTZup(now);
            Vector3d gravity = p * (-vessel.mainBody.gravParameter / Math.Pow(p.magnitude, 3));
            Vector3d accel_zup = (u - previous_velocity) / dt - gravity;
            previous_velocity = u;
            previous_sample_UT = now;
            if (hold.attitude_error_deg > redux_attitude_tolerance_deg)
                return false;

            var body = vessel.mainBody;
            accel = body.transform.bodyFrame.ToLocalVector(
                new Vector(body.SimulationObject.transform.celestialFrame, new Vector3d(accel_zup.x, accel_zup.z, accel_zup.y)));
            return true;
        }

        // Compares where the measured lift points with where it was meant to, once there's enough
        // air to measure it, and corrects the tilt offset if the vessel lifts the other way.
        void CheckLiftDirection(Vector3d accel, double drag)
        {
            if (lift_direction_checked || angle_of_attack_deg < min_aoa_to_measure_lift_deg)
                return;

            double lift_up = Vector3d.Dot(accel, hold.lift_up);
            double lift_side = Vector3d.Dot(accel, hold.side);
            double lift = Math.Sqrt(lift_up * lift_up + lift_side * lift_side);
            if (lift < min_lift_to_measure || lift < 0.05 * drag)
                return;

            double measured_bank = Math.Atan2(lift_side, lift_up) * 180 / Math.PI;
            double difference = LandingTargeting.WrapDegrees180(measured_bank - lift_bank_deg);
            if (Math.Abs(difference) <= lift_agree_deg)
            {
                lift_votes_same++;
                lift_votes_flipped = 0;
            }
            else if (Math.Abs(difference) >= 180 - lift_agree_deg)
            {
                lift_votes_flipped++;
                lift_votes_same = 0;
            }
            else
            {
                lift_votes_same = lift_votes_flipped = 0;
                return;
            }
            if (lift_votes_same < lift_measurements_needed && lift_votes_flipped < lift_measurements_needed)
                return;

            lift_direction_checked = true;
            if (lift_votes_flipped >= lift_measurements_needed)
            {
                tilt_offset_deg = LandingTargeting.WrapDegrees180(tilt_offset_deg + 180);
                // The meaning of "vertical lift" just flipped, so the last step can't be compared
                // with the next one.
                last_vertical_lift = double.NaN;
                range_per_lift_m = Math.Abs(range_per_lift_m);
                settled_UT = double.NaN;
            }
            logger.LogInfo($"[EntryGuidance] lift measured at bank {measured_bank:n0}° while aiming it at {lift_bank_deg:n0}° " +
                $"(L/D {lift / Math.Max(drag, 1e-6):n2}) - vessel tilts {tilt_offset_deg:n0}° from where its lift points");
        }

        // Re-plans the landing burn every burn_plan_interval_s once low enough, and keeps the
        // overshoot the steering aims for.
        void PlanBurn(VesselComponent vessel, Vector3d position, Vector3d air_velocity, double altitude, double now)
        {
            if (altitude > burn_planning_below_m || !planner.has_drag)
                return;
            if (!double.IsNaN(last_plan_UT) && now - last_plan_UT < burn_plan_interval_s)
                return;
            double dt = double.IsNaN(last_plan_UT) ? 0 : now - last_plan_UT;
            last_plan_UT = now;

            Vector3d up = position.normalized;
            double v_vertical = Vector3d.Dot(air_velocity, up);
            double v_horizontal = (air_velocity - up * v_vertical).magnitude;
            var settings = landing.settings;
            burn_plan = planner.Compute(vessel.mainBody, altitude - ground_altitude, ground_altitude, v_horizontal, v_vertical,
                landing.burn_dV.full_dv, stop_height_fraction * settings.start_touchdown_altitude.V, settings.brake_speed,
                overshoot_out_of_reach ? last_redux_along : double.NaN);
            if (!burn_plan.found)
                return;

            if (!have_shortfall)
                shortfall_m = burn_plan.shortfall_m;
            else
                shortfall_m += (burn_plan.shortfall_m - shortfall_m) * Math.Min(1, dt / shortfall_smoothing_s);
            have_shortfall = true;
        }

        string DescribeLift()
        {
            double u = vertical_lift;
            string vertical = u > 0.3 ? "up" : u < -0.3 ? "down" : "level";
            return $"{vertical} (bank {lift_bank_deg:n0}°)";
        }

        public override void UpdateInfoRows(Action<string, string> addRow)
        {
            if (!in_atmosphere)
                return;
            addRow("Lift Bank", $"{lift_bank_deg:n0}°");
            addRow("Vertical Lift", $"{vertical_lift:n2}");
            addRow("Angle of Attack", $"{angle_of_attack_deg:n1}°");
            addRow("Attitude Error", $"{hold.attitude_error_deg:n1}°");
            addRow("Hottest Part", $"{heat.hottest_fraction:P0} of max");
        }
    }

    // Upper limit on the entry angle of attack from part temperatures.
    //
    // Each part's temperature is extrapolated a little ahead from how fast it's rising; once any
    // part is expected to get near its maximum the limit steps down, and once everything has
    // stayed comfortably cool for a while it steps back up. The steps are coarse on purpose: every
    // change of attitude makes Redux start its prediction over.
    public class EntryHeatLimit
    {
        // (20 s forecast the eighth test flight's parts at 90-116% while they were really at 40-80%
        // and still well short of where they'd level off.)
        public const double lookahead_s = 10;
        const double check_interval_s = 1;
        // Fractions of a part's maximum temperature. (At 0.8/0.6 the fourth test flight sat at 0°
        // from 24 km down with the hottest part at 79% and cooling, where the player could raise
        // it by hand without trouble.)
        const double hot = 0.9;
        const double cool = 0.82;
        const double step_deg = 2.5;
        const double raise_wait_s = 10;

        // How fast each part is heating is smoothed over about this long: from one second to the
        // next it jumps around enough to fake a part heading past its limit (the sixth test flight
        // stepped the angle of attack down and back up a dozen times on reaction wheel readings
        // like that).
        const double rate_smoothing_s = 3;

        readonly Dictionary<PartComponent, double> previous_temperature = new Dictionary<PartComponent, double>();
        readonly Dictionary<PartComponent, double> heating_rate = new Dictionary<PartComponent, double>();
        double limit_deg;
        double last_check_UT, last_change_UT;

        public double hottest_fraction { get; private set; }
        public double hottest_predicted { get; private set; }
        public string hottest_part { get; private set; } = "-";

        public void Reset(double max_deg)
        {
            limit_deg = max_deg;
            previous_temperature.Clear();
            heating_rate.Clear();
            last_check_UT = last_change_UT = double.NaN;
            hottest_fraction = hottest_predicted = 0;
            hottest_part = "-";
        }

        // Returns the current limit. current_deg: the angle of attack being flown, which a hot
        // part steps down from.
        public double Update(VesselComponent vessel, double now, double current_deg, double max_deg)
        {
            limit_deg = Math.Min(limit_deg, max_deg);
            if (!double.IsNaN(last_check_UT) && now - last_check_UT < check_interval_s)
                return limit_deg;
            double dt = double.IsNaN(last_check_UT) ? 0 : now - last_check_UT;
            last_check_UT = now;
            if (double.IsNaN(last_change_UT))
                last_change_UT = now;

            hottest_fraction = hottest_predicted = 0;
            foreach (PartComponent part in vessel.SimulationObject.PartOwner.Parts)
            {
                // Heat shields are built to face the hot flow and lowering the angle of attack only
                // turns them further into it, so they don't count. (The second test flight dropped
                // straight to 0° on a heat shield heating up fast but ending at 61% of its limit.)
                if (part.TryGetModule<PartComponentModule_Heatshield>(out _))
                    continue;
                var thermal = part.ThermalData;
                if (thermal.MaxTemp <= 0)
                    continue;
                double temperature = thermal.Temperature;
                double rate = 0;
                if (dt > 0 && previous_temperature.TryGetValue(part, out double before))
                {
                    double measured = (temperature - before) / dt;
                    rate = heating_rate.TryGetValue(part, out double smoothed)
                        ? smoothed + (measured - smoothed) * Math.Min(1, dt / rate_smoothing_s)
                        : measured;
                    heating_rate[part] = rate;
                }
                previous_temperature[part] = temperature;

                double predicted = (temperature + Math.Max(0, rate) * lookahead_s) / thermal.MaxTemp;
                if (predicted > hottest_predicted)
                {
                    hottest_predicted = predicted;
                    hottest_fraction = temperature / thermal.MaxTemp;
                    hottest_part = part.PartName;
                }
            }

            if (hottest_predicted > hot && Math.Min(limit_deg, current_deg) > 0)
            {
                limit_deg = Math.Max(0, Math.Min(limit_deg, current_deg) - step_deg);
                last_change_UT = now;
            }
            else if (hottest_predicted < cool && limit_deg < max_deg && now - last_change_UT > raise_wait_s)
            {
                // A step above what's being flown (the limit may be well above it already).
                limit_deg = Math.Min(max_deg, Math.Max(limit_deg, current_deg) + step_deg);
                last_change_UT = now;
            }
            return limit_deg;
        }
    }
}
