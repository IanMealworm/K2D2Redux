using System;
using K2D2.KSPService;
using K2UI;
using KSP.Sim;
// using KTools.UI;
using UnityEngine;
using UnityEngine.UIElements;
using K2D2.UI;
using K2D2.Controller;

using KTools;

namespace K2D2.Landing
{
    /// apply the wanted speed in the good direction
    public class TouchDown : ExecuteController
    {
        public bool gravity_compensation;
        public float max_speed = 0;

        public ClampSetting<float> touch_down_max_angle = new("land.touch_down_max_angle", 30,  0, 45);

        // Closed-loop lateral correction (precision landing): how far off pure retrograde the
        // burn direction is allowed to tilt, at full strength (i.e. anywhere in Brake, well above
        // start_touchdown_altitude - see ComputeSteeredDirection's taper). Same default order of
        // magnitude as touch_down_max_angle above; 0 disables steering entirely.
        public ClampSetting<float> steering_max_angle = new("land.steering_max_angle", 20, 0, 45);

        // Along-track (arc length) correction caps - see ComputeSteeredDirection. Two separate
        // settings, not one, because the two directions carry very different risk: tilting toward
        // straight up (extending the arc on an undershoot) only ever ADDS vertical braking
        // margin, so Reese wants it allowed to go quite far (default 80°, i.e. let it get as
        // close to pure-vertical as the situation actually calls for). Tilting toward horizontal
        // (shortening the arc on an overshoot) SPENDS vertical braking margin, so it stays on a
        // much shorter leash (default 10°). Both are additionally hard-clamped in
        // ComputeSteeredDirection so neither one can ever rotate PAST straight up or PAST pure
        // horizontal, no matter how large these settings are set to.
        public ClampSetting<float> arc_extend_max_angle = new("land.arc_extend_max_angle", 80, 0, 90);
        public ClampSetting<float> arc_shorten_max_angle = new("land.arc_shorten_max_angle", 10, 0, 45);

        // How fast the commanded correction angle itself is allowed to change, in degrees/second -
        // see ComputeSteeredDirection's use of it for why (without this, the correction hunted
        // side to side instead of settling). Not exposed as a tunable setting yet - first pass.
        const float max_correction_rate_deg_per_sec = 8f;
        float smoothed_correction_deg = 0;
        float smoothed_arc_deg = 0;

        // How fast the ENGINE THROTTLE ITSELF is allowed to change, in fraction/second (5 = 0 to
        // full in 0.2s). compute_Throttle() below recomputes wanted_throttle fresh every frame
        // from delta_speed, and every time the Pause<->Brake cycle (see LandingPilot.Update())
        // restarts a burn, that jumps straight from 0 to whatever's needed - looked like the
        // throttle "blipping to 100%" in Reese's test. This doesn't touch how much total dV gets
        // used, just smooths the ramp - fast enough that it shouldn't cost any real stopping
        // distance in an actual emergency, just the sudden all-or-nothing snap.
        public ClampSetting<float> max_throttle_rate_per_sec = new("land.max_throttle_rate_per_sec", 5, 1, 20);
        float smoothed_throttle = 0;

        KSPVessel current_vessel;
        BurndV burn_dV = new BurndV();

        // float gravity_inclination = 0;
        float gravity_direction_factor = 0;
        float gravity;

        float wanted_throttle = 0;

        // Needed for precision landing's closed-loop steering below - predicted_landing_lat/lon,
        // target_latitude/longitude, and the taper reference altitude all live on LandingPilot.
        // Null when this TouchDown is used somewhere that doesn't wire a LandingPilot in (there
        // isn't one today, but keeping the null-check cheap insurance rather than assuming).
        LandingPilot landing;

        public TouchDown(LandingPilot landing = null)
        {
            this.landing = landing;
            sub_contollers.Add(burn_dV);
            // logger.LogMessage("LandingController !");
            current_vessel = K2D2_Plugin.Instance.current_vessel;
        }

        public void computeGravityRatio()
        {
            // current_vessel.getInclination();
            Vector up_dir = current_vessel.VesselComponent.gravityForPos;
            Rotation vessel_rotation = current_vessel.GetRotation();

            // convert rotation to maneuver coordinates

            vessel_rotation = Rotation.Reframed(vessel_rotation, up_dir.coordinateSystem);
            Vector3d forward_direction = (vessel_rotation.localRotation * Vector3.down).normalized;

            var gravity_inclination = (float)Vector3d.Angle(up_dir.vector, forward_direction);
            // status_line = $"Waiting for good sas direction\nAngle = {angle:n2}°";

            gravity = (float)current_vessel.VesselComponent.graviticAcceleration.magnitude;
            gravity_direction_factor = Mathf.Cos(gravity_inclination * Mathf.Deg2Rad);
        }

        void compute_Throttle()
        {
            float min_throttle = 0;

            if (gravity_compensation)
            {
                if (gravity_direction_factor == 0)
                    min_throttle = 0;
                else
                {
                    float minimum_dv = gravity_direction_factor * gravity;
                    min_throttle = minimum_dv / burn_dV.full_dv;
                }
            }


            delta_speed = current_speed - max_speed;

            float remaining_full_burn_time = (float)(delta_speed / burn_dV.full_dv);
            wanted_throttle = Mathf.Clamp(remaining_full_burn_time + min_throttle, 0, 1);
        }

        float delta_speed = 0;

        public bool NeedBurn => delta_speed > 0;

        public bool checkSpeed()
        {
            if (delta_speed < 0)
            {
                return true;
            }

            return false;
        }

        float retrograde_angle;

        public bool checkDirection()
        {

            var telemetry = SASTool.getTelemetry();

            // check that the direction is not over max_angle
            Vector HorizonUp = telemetry.HorizonUp;
            Vector retro_dir = telemetry.SurfaceMovementRetrograde;

            retro_dir.Reframe(HorizonUp.coordinateSystem);

            var speed_vertical_angle = (float)Vector3d.Angle(retro_dir.vector, HorizonUp.vector);

            if (speed_vertical_angle > 90)
            {
                status_line = $"Waiting for speed Down\nAngle = {speed_vertical_angle:n2}°\nFree Time Warp";
                return false;
            }

            TimeWarpTools.SetRateIndex(0, false);

            // Precision landing: tilt the burn direction off pure retrograde, toward the target,
            // instead of just killing velocity in whatever direction it happens to be pointing -
            // see ComputeSteeredDirection's own comment for the reasoning and the math. Runs during
            // both Brake and TouchDown (this class is the shared executor for both, see
            // LandingPilot.setMode) - deliberately so, since Brake (high altitude/speed) is where
            // there's actually enough room to close a large miss; the taper inside
            // ComputeSteeredDirection is what keeps TouchDown itself safely close to pure vertical.
            Vector3d aim_dir = retro_dir.vector;
            bool steering = landing != null && landing.settings.precision_landing.V && steering_max_angle.V > 0;
            if (steering)
                aim_dir = ComputeSteeredDirection(HorizonUp, retro_dir.vector);

            if (steering)
            {
                var autopilot = current_vessel.Autopilot;
                autopilot.Enabled = true;
                // Guard the mode switch like SASTool.setAutoPilot does elsewhere - re-calling
                // SetMode every single tick even while already in StabilityAssist risks resetting
                // SAS's own internal state each frame instead of letting it settle onto the target.
                if (autopilot.AutopilotMode != AutopilotMode.StabilityAssist)
                    autopilot.SetMode(AutopilotMode.StabilityAssist);
                autopilot.SAS.lockedMode = false;
                autopilot.SAS.SetTargetOrientation(new Vector(HorizonUp.coordinateSystem, aim_dir), false);
            }
            else
            {
                SASTool.setAutoPilot(AutopilotMode.Retrograde);
            }

            Rotation vessel_rotation = current_vessel.GetRotation();

            // convert rotation to maneuver coordinates
            vessel_rotation = Rotation.Reframed(vessel_rotation, retro_dir.coordinateSystem);
            Vector3d forward_direction = (vessel_rotation.localRotation * Vector3.up).normalized;

            // Compare against aim_dir (the steered direction when steering, plain retrograde
            // otherwise) rather than always retro_dir - otherwise a real correction tilt would
            // read as "already aligned" against the old retrograde-only check while the vessel is
            // still rotating to catch up with what's actually commanded, and throttle would fire
            // before it's actually pointed the right way.
            retrograde_angle = (float)Vector3d.Angle(aim_dir, forward_direction);
            status_line = $"Waiting for Vessel rotation\nAngle = {retrograde_angle:n2}°";

            return retrograde_angle < touch_down_max_angle.V;
        }

        // Two separate corrections layered on top of pure retrograde, because a miss has two
        // genuinely different components and they need two different fixes:
        //
        //  - CROSS-TRACK (left/right of the direction of travel): fixed by rotating the burn's
        //    HEADING - which way it drifts the vessel sideways - without touching how hard it
        //    fights descent. Safe in both directions, since it never touches the vertical
        //    component at all.
        //
        //  - ALONG-TRACK (short/long, i.e. dead ahead or behind): a heading rotation can't fix
        //    this - if the target is nearly dead ahead or dead behind, the angle between "where
        //    we're heading" and "where the target is" is already near 0 or 180, so the heading
        //    correction naturally computes almost nothing to do. This is exactly what showed up
        //    in-game as "swayed side to side, not much correction was done" on a 20km+ miss -
        //    most of that miss was along-track, and the old code literally had no mechanism for
        //    it. Fixed the way Reese described it: tilt the burn's VERTICAL/HORIZONTAL split.
        //    Tilting more toward straight up leaves more of the current horizontal speed
        //    uncancelled, so the vessel coasts farther before it has to come down - extends the
        //    arc, for undershooting. Tilting more toward horizontal fights horizontal speed
        //    harder and vertical fall speed less, so it comes down sooner - shortens the arc, for
        //    overshooting.
        //
        // Both are clamped by their own max-angle setting, both of those caps are scaled down by
        // altitude the same way (full strength throughout Brake, tapering to zero across
        // TouchDown so the final approach stays close to pure vertical retrograde), and both are
        // rate-limited per frame (see max_correction_rate_deg_per_sec) for the same reason: a
        // value recomputed fresh and snapped to every frame chases noise in
        // predicted_landing_lat/lon instead of settling.
        //
        // The "shorten the arc" (overshoot, more-horizontal) direction has a much tighter cap
        // than "extend" (see arc_shorten_max_angle / arc_extend_max_angle above) - extending only
        // ever ADDS vertical braking margin, shortening SPENDS it. Both are also hard-clamped
        // below so neither can ever rotate past straight up or past pure horizontal, regardless
        // of the setting value. "Land even if it's off, not crash chasing precision" per Reese.
        //
        // Target direction is derived with a flat-Earth small-angle approximation (fine at the
        // scales here - tens of km on bodies with radii in the hundreds of km) directly in the
        // vessel's own local North/East/Up frame, and every rotation's direction is derived from
        // Dot/Cross alone (never assumes a specific handedness convention from a library
        // SignedAngle call - the vector identity behind it holds for cross products in general,
        // so this can't end up steering the wrong way regardless of convention).
        Vector3d ComputeSteeredDirection(Vector HorizonUp, Vector3d retro_dir_vec)
        {
            Vector3d up_vec = HorizonUp.vector.normalized;

            Vector3d vertical = up_vec * Vector3d.Dot(retro_dir_vec, up_vec);
            Vector3d horizontal = retro_dir_vec - vertical;

            if (horizontal.magnitude < 0.001)
                return retro_dir_vec; // already falling essentially straight down - nothing to steer

            Vector3d horiz_dir = horizontal.normalized;

            Vector north_v = current_vessel.VesselVehicle.North;
            north_v.Reframe(HorizonUp.coordinateSystem);
            // Defensively re-flatten - North should already be horizontal, but this keeps the
            // North/East basis below exactly perpendicular to up_vec regardless.
            Vector3d north_vec = north_v.vector - up_vec * Vector3d.Dot(north_v.vector, up_vec);
            if (north_vec.magnitude < 0.001)
                return retro_dir_vec; // degenerate (e.g. exactly at a pole) - don't guess, skip steering this tick
            north_vec = north_vec.normalized;
            Vector3d east_vec = Vector3d.Cross(up_vec, north_vec);

            double toRad = Math.PI / 180.0;
            double lat_rad = landing.predicted_landing_lat * toRad;
            double dLat = (landing.settings.target_latitude.V - landing.predicted_landing_lat) * toRad;
            double dLon = (landing.settings.target_longitude.V - landing.predicted_landing_lon) * toRad;

            // Flat-local direction from the predicted landing point toward the target - only the
            // ratio/direction matters here, not absolute distance (target_error_m already covers
            // "how far off", this is purely "which way").
            Vector3d target_horiz_dir = north_vec * dLat + east_vec * (dLon * Math.Cos(lat_rad));
            if (target_horiz_dir.magnitude < 1e-9)
                return retro_dir_vec; // predicted landing point is already right on the target

            target_horiz_dir = target_horiz_dir.normalized;

            float taper = Mathf.Clamp01(landing.altitude / Mathf.Max(landing.settings.start_touchdown_altitude.V, 1f));

            // --- Along-track (arc length) correction ---
            Vector3d travel_dir = -horiz_dir; // horizontal direction the vessel is actually moving
            Vector3d pitch_axis = Vector3d.Cross(up_vec, travel_dir).normalized;

            // +1 = target is dead ahead along the direction of travel (undershoot), -1 = dead
            // behind (overshoot), 0 = target is directly to the side (pure cross-track - arc
            // correction can't help there, and correctly computes ~nothing to do).
            double along_cos = Vector3d.Dot(target_horiz_dir, travel_dir);

            // Same self-consistent Dot/Cross sign trick as the heading correction below: rotating
            // retro_dir_vec by +pitch_toward_up_sign*angle (via Rodrigues, about pitch_axis) is
            // guaranteed to move it toward up_vec, regardless of Cross's handedness. retro_dir_vec
            // is perpendicular to pitch_axis by construction (it's built entirely from up_vec and
            // horiz_dir, both of which pitch_axis is perpendicular to), same as up_vec is - so the
            // triple-product argument applies here exactly as it does for the heading rotation.
            double pitch_toward_up_sign = Math.Sign(Vector3d.Dot(Vector3d.Cross(retro_dir_vec, up_vec), pitch_axis));
            if (pitch_toward_up_sign == 0)
                pitch_toward_up_sign = 1;

            // How far retro_dir_vec actually is from pure-up and from pure-horizontal right now -
            // used below to hard-clamp the rotation so it can approach either one but never
            // overshoot past it (past up_vec would start tilting back down the other side; past
            // horiz_dir would start pointing below horizontal, into the ground).
            double angle_from_up = Vector3d.Angle(retro_dir_vec, up_vec);
            double angle_from_horiz = Vector3d.Angle(retro_dir_vec, horiz_dir);

            float max_pitch_up_deg = (float)Math.Min(arc_extend_max_angle.V * taper, angle_from_up);
            float max_pitch_horizontal_deg = (float)Math.Min(arc_shorten_max_angle.V * taper, angle_from_horiz);

            double pitch_target_deg = (along_cos >= 0)
                ? along_cos * max_pitch_up_deg
                : along_cos * max_pitch_horizontal_deg;

            double signed_pitch_correction = pitch_toward_up_sign * pitch_target_deg;

            float max_pitch_step = max_correction_rate_deg_per_sec * Time.deltaTime;
            smoothed_arc_deg = Mathf.MoveTowards(smoothed_arc_deg, (float)signed_pitch_correction, max_pitch_step);

            Vector3d pitched_dir = retro_dir_vec;
            if (Mathf.Abs(smoothed_arc_deg) >= 0.01f)
            {
                double pitch_rad = smoothed_arc_deg * Math.PI / 180.0;
                pitched_dir = retro_dir_vec * Math.Cos(pitch_rad) + Vector3d.Cross(pitch_axis, retro_dir_vec) * Math.Sin(pitch_rad);
            }

            // --- Cross-track (heading) correction ---
            // Re-decompose after the pitch step above - pitching the burn changes how much
            // horizontal magnitude is even left to redirect (e.g. a hard pitch toward "up" for a
            // big undershoot leaves less horizontal component to steer with).
            Vector3d vertical2 = up_vec * Vector3d.Dot(pitched_dir, up_vec);
            Vector3d horizontal2 = pitched_dir - vertical2;
            double horiz_mag2 = horizontal2.magnitude;

            if (horiz_mag2 < 0.001)
                return pitched_dir.normalized; // pitched essentially straight up/down - nothing left to steer heading-wise

            Vector3d horiz_dir2 = horizontal2 / horiz_mag2;

            double angle_between = Vector3d.Angle(horiz_dir2, target_horiz_dir); // unsigned, 0..180

            // Sign of Dot(Cross(horiz_dir2, target_horiz_dir), up_vec) is the sign of how far to
            // rotate horiz_dir2 *toward* target_horiz_dir when the rotation itself is applied via
            // Rodrigues' formula below using Cross(up_vec, horiz_dir2) - both derive from the same
            // scalar triple product, so they're guaranteed self-consistent regardless of the
            // underlying Cross implementation's handedness.
            double rotation_sign = Math.Sign(Vector3d.Dot(Vector3d.Cross(horiz_dir2, target_horiz_dir), up_vec));
            if (rotation_sign == 0)
                rotation_sign = 1;

            float max_correction_deg = steering_max_angle.V * taper;

            double correction_deg = Math.Min(angle_between, (double)max_correction_deg) * rotation_sign;

            // Rate-limit how fast the COMMANDED angle itself is allowed to change, instead of
            // snapping straight to a freshly-recomputed value every frame. Confirmed in-game
            // without this: the correction visibly swayed side to side instead of settling on a
            // steady tilt - target_horiz_dir shifts a little every frame as predicted_landing_lat/
            // lon updates (partly *because* of the correction burn itself), which can flip which
            // way "closer to target" points tick to tick, and re-aiming instantly every frame
            // chases that noise instead of converging on it.
            float max_step = max_correction_rate_deg_per_sec * Time.deltaTime;
            smoothed_correction_deg = Mathf.MoveTowards(smoothed_correction_deg, (float)correction_deg, max_step);

            if (Mathf.Abs(smoothed_correction_deg) < 0.01f)
                return pitched_dir.normalized;

            // Rodrigues' rotation formula, rotating horiz_dir2 around up_vec by the smoothed
            // correction angle. horiz_dir2 is already perpendicular to up_vec (built that way
            // above), so the along-axis term drops out.
            double rad = smoothed_correction_deg * Math.PI / 180.0;
            Vector3d rotated_horiz_dir2 = horiz_dir2 * Math.Cos(rad) + Vector3d.Cross(up_vec, horiz_dir2) * Math.Sin(rad);

            return (vertical2 + rotated_horiz_dir2 * horiz_mag2).normalized;
        }

        float current_speed;

        public override void Update()
        {
            if (current_vessel == null || current_vessel.VesselVehicle == null)
                return;

            current_speed = (float)current_vessel.VesselVehicle.SurfaceSpeed;

            delta_speed = current_speed - max_speed;

            if (delta_speed > 0) // reset timewarp if it is time to burn
                TimeWarpTools.SetRateIndex(0, false);

            if (gravity_compensation)
                computeGravityRatio();

            current_vessel.SetSpeedMode(KSP.Sim.SpeedDisplayMode.Surface);

            // if (autopilot.AutopilotMode != AutopilotMode.Retrograde)
            //         autopilot.SetMode(AutopilotMode.Retrograde);
            // else


            // if (autopilot.AutopilotMode != AutopilotMode.Retrograde)
            //     autopilot.SetMode(AutopilotMode.Retrograde);
            // else
            //     if (autopilot.AutopilotMode != AutopilotMode.StabilityAssist)
            //         autopilot.SetMode(AutopilotMode.StabilityAssist);

            if (!checkDirection())
            {
                current_vessel.SetThrottle(0);
                // Keep this in sync with the real applied throttle (0) - otherwise the next good
                // frame would ramp starting from whatever smoothed_throttle was before we cut it,
                // not from the actual current 0, and briefly re-apply stale throttle.
                smoothed_throttle = 0;
                // status_line = $"Turning : {retrograde_angle:n2} °";
                return;
            }

            compute_Throttle();

            // Rate-limit the actual applied throttle instead of snapping straight to the freshly
            // computed value - see max_throttle_rate_per_sec's comment for why.
            float throttle_step = max_throttle_rate_per_sec.V * Time.deltaTime;
            smoothed_throttle = Mathf.MoveTowards(smoothed_throttle, wanted_throttle, throttle_step);

            // no stop for gravity compensation
            current_vessel.SetThrottle(smoothed_throttle);
        }

        // Numeric telemetry for the Landing tab's own on-page info table (see LandingUI.cs's
        // updateContext) - status_line stays narrative-only now (checkDirection's "Waiting for
        // ..." messages) instead of being overwritten every tick with this Max Speed readout.
        public override void UpdateInfoRows(System.Action<string, string> addRow)
        {
            addRow("Max Speed", $"{max_speed:n2} m/s");
            addRow("Delta Speed", $"{delta_speed:n2} m/s");

            if (K2D2Settings.debug_mode.V)
            {
                if (gravity_compensation)
                {
                    addRow("Gravity", $"{gravity:n2}");
                    addRow("Gravity Dir. Factor", $"{gravity_direction_factor:n2}");
                }

                addRow("Wanted Throttle", $"{wanted_throttle:n2}");
                addRow("Actual Throttle", $"{smoothed_throttle:n2}");

                if (landing != null && landing.settings.precision_landing.V)
                {
                    addRow("Heading Correction", $"{smoothed_correction_deg:n2}°");
                    addRow("Arc Correction", $"{smoothed_arc_deg:n2}°");
                }
            }
        }

        public override void updateUI(VisualElement el, FullStatus st)
        {
            // need to burn ?

            string txt = $" Max speed : {max_speed:n2} !!";
            txt += $"\n delta speed  : {delta_speed:n2}  m/s";    

            var level = delta_speed > 0 ? StatusLine.Level.Warning : StatusLine.Level.Normal;
            st.Status(txt, level );
       
            if (burn_dV.burned_dV > 0)
                st.Console($"dV consumed : {burn_dV.burned_dV:n2} m/s");

            if (K2D2Settings.debug_mode.V)
            {
                if (gravity_compensation)
                {
                    st.Console($"gravity : {gravity:n2}");
                    st.Console($"gravity_direction_factor : {gravity_direction_factor:n2}");   
                }

                st.Console($"wanted_throttle : {wanted_throttle:n2}");
            }
        }


    }
}

