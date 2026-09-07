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
using ILogger = ReduxLib.Logging.ILogger;

namespace K2D2.Landing
{
    /// apply the wanted speed in the good direction
    public class TouchDown : ExecuteController
    {
        // Wasn't needed before RCS fine correction - the commented-out logger.LogMessage a few
        // lines down in the constructor was dead code, never an actual field. Added specifically
        // so ApplyRCSFineCorrection/ClearRCSFineCorrection below can leave a real log trail (see
        // their own comments - item 4, Reese couldn't tell if RCS ever engaged).
        public ILogger logger = ReduxLib.ReduxLib.GetLogger("K2D2.TouchDown");

        public bool gravity_compensation;
        public float max_speed = 0;

        public ClampSetting<float> touch_down_max_angle = new("land.touch_down_max_angle", 30,  0, 45);

        // Closed-loop lateral correction (precision landing): how far off pure retrograde the
        // burn direction is allowed to tilt, at full strength (i.e. anywhere in Brake, well above
        // start_touchdown_altitude - see ComputeSteeredDirection's taper). 0 disables steering
        // entirely. Unlike the arc correction below, this one never touches the vertical
        // component at all (see checkDirection/ComputeSteeredDirection), so a wide angle here
        // costs fuel efficiency, not crash safety - raised well past the old 45deg cap so Reese
        // can test how much cross-track/plane-mismatch error (his target: up to ~35km) this can
        // absorb on its own before a player genuinely needs to fix their orbital plane by hand.
        public ClampSetting<float> steering_max_angle = new("land.steering_max_angle", 40, 0, 80);

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

        // How large the actual along-track distance error (see along_track_error_m below) needs
        // to be before the arc correction is allowed to use its FULL angle cap above. Below this,
        // the commanded pitch tapers down toward 0 as the error shrinks. Added because
        // ComputeSteeredDirection used to size the pitch angle off along_cos alone - the cosine of
        // the angle between "which way the target is" and "which way we're travelling" - which is
        // just a direction, not a distance: a 600m miss that happens to sit almost dead ahead gives
        // along_cos ~1, the same as a 20km miss dead ahead, so it commanded nearly the FULL max
        // angle either way. In-game that meant small, already-in-range misses were getting shoved
        // way past the target by an overcorrection sized for a miss 10-30x bigger, which then took
        // another big correction to undo - burning way more dV than the miss ever justified
        // (Reese: 580 m/s nominal Mun landing ballooning toward 1200 m/s). Scaling the pitch by the
        // real along-track distance instead - full authority only once the miss actually reaches
        // this many meters - keeps small errors getting small (i.e. proportional) corrections.
        //
        // Default raised 2000 -> 5000 after the first real test with this fix landed consistently
        // SHORT (undershoot - came down before reaching the target). Root cause: the deorbit burn
        // plus mid-course correction typically still leave a multi-km along-track residual (one
        // real test log showed ~2.2km remaining with only 4 seconds left before impact) for
        // TouchDown's own steering to close. At the old 2000m full-scale, that residual was already
        // inside the taper band for most of the descent, so extend was easing off proportionally
        // well before it actually had time to finish the correction. 5000m kept a multi-km miss
        // at full authority through most of Brake, only tapering down once it's actually closed to
        // a few hundred meters - much closer to the real ~200m precision target.
        //
        // Then Reese's next test flipped that finding: turning this DOWN toward the floor made the
        // target error smaller, turning it UP gave a consistent ~1.5km miss. That's a real tension,
        // not a contradiction - a single linear threshold can't both (a) taper a small (~600m)
        // error down enough to avoid the original bang-bang overcorrection this setting exists to
        // fix, and (b) hold full authority long enough to close a typical multi-km post-deorbit
        // residual. Rather than keep hunting for a slider value that split that difference, fixed
        // at 50 (near the old floor) and paired with the parabolic reshaping below on
        // along_track_magnitude_frac - the curve itself, not just where it caps out, is what's
        // supposed to keep a 600m error small while still letting a multi-km one ramp up hard.
        // No longer a player-adjustable ClampSetting - slider removed from the uxml.
        const float arc_correction_full_scale_m = 50f;

        // How fast the commanded correction angle itself is allowed to change, in degrees/second -
        // see ComputeSteeredDirection's use of it for why (without this, the correction hunted
        // side to side instead of settling). Not exposed as a tunable setting yet - first pass.
        const float max_correction_rate_deg_per_sec = 8f;
        float smoothed_correction_deg = 0;
        float smoothed_arc_deg = 0;

        // World-space horizontal direction RCS should push in, as last computed by
        // ComputeSteeredDirection - captured here so ApplyRCSFineCorrection (RCS fine correction,
        // see its own comment) can reuse it instead of recomputing the same thing a second time.
        // This is the CROSS-TRACK-ONLY component of the direction toward the target (perpendicular
        // to the direction of travel), not the raw direction toward the target - per Reese, RCS's
        // job is specifically the side-to-side miss, not the along-track (arc) miss the main
        // engine's own extend/shorten correction already handles; pushing toward the raw target
        // direction would have RCS also fighting along-track, duplicating/fighting the engine's
        // own job there. See cross_track_error_m below for the matching scalar distance.
        // Reset to zero at the top of ComputeSteeredDirection and only set once it's actually
        // computed, so a stale value from a previous tick can't leak through one of that method's
        // several early-return ("nothing to steer" / degenerate) cases.
        Vector3d steered_target_horiz_dir = Vector3d.zero;

        // Cross-track (side-to-side) component of the miss, in meters - the part of the total
        // target_error_m that's perpendicular to the vessel's direction of travel, split out from
        // the along-track (ahead/behind) component the arc correction handles. RCSHandlingHeading
        // and the RCS growth-tracking bailout gate on THIS, not the raw total target_error_m -
        // per Reese, a big total miss that's mostly along-track (which the engine's own arc
        // correction is already working on) shouldn't hold RCS back from fixing whatever
        // side-to-side component already exists in parallel. Same reset discipline as
        // steered_target_horiz_dir above - 0 until ComputeSteeredDirection actually computes it.
        double cross_track_error_m = 0;

        // Along-track (ahead/behind) component of the miss, in meters - the part of the total
        // target_error_m the arc extend/shorten correction is actually working on, split out the
        // same way cross_track_error_m above splits out the sideways part. This is what
        // arc_correction_full_scale_m tapers the pitch angle against - see that setting's own
        // comment for why this replaced along_cos (a direction, not a distance) as the thing that
        // sizes the correction. Same reset discipline as cross_track_error_m - 0 until
        // ComputeSteeredDirection actually computes it.
        double along_track_error_m = 0;

        // Debug-only snapshot of the extend correction's vertical-speed safety gate (see its own
        // comment in ComputeSteeredDirection) - exposed in the info table so it can actually be
        // watched live on the next Minmus test instead of having to pull the log afterward.
        float debug_vertical_speed_up = 0;
        float debug_descent_margin_factor = 1;
        // Same idea, for the along-track proportional-scaling fix above - lets Reese watch on the
        // next test whether small misses are actually getting small corrections now.
        float debug_along_track_magnitude_frac = 0;

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
            {
                // Updated before ComputeSteeredDirection below (not after) so a bailout latched
                // this frame - see UpdateRcsGrowthTracking's own comment - is reflected in THIS
                // frame's aim_dir (RCSHandlingHeading, which it reads, checks the same latch) as
                // well as this frame's ApplyRCSFineCorrection call, instead of lagging a frame
                // behind.
                UpdateRcsGrowthTracking();
                aim_dir = ComputeSteeredDirection(HorizonUp, retro_dir.vector);
                // RCS fine correction: takes over heading correction from the engine once the
                // miss is close enough (see RCSHandlingHeading/the setting's own comment) - never
                // at all when steering itself is off.
                ApplyRCSFineCorrection(HorizonUp);
            }
            else
            {
                // Make sure a previous tick's RCS command doesn't keep firing once steering (and
                // therefore RCS fine correction) is no longer active.
                ClearRCSFineCorrection();
                ResetRcsGrowthTracking();
            }

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
            // See this field's own comment - cleared here, set below only once a real target
            // direction is actually computed, so RCS fine correction can't act on a stale value
            // from a previous tick if this call ends up taking one of the early returns below.
            steered_target_horiz_dir = Vector3d.zero;
            cross_track_error_m = 0;
            along_track_error_m = 0;

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

            // Real along-track distance, in meters, signed the same way along_cos is (+ ahead,
            // - behind) - target_horiz_dir is a unit vector, so this is exactly the along-track
            // component of the total scalar miss (landing.target_error_m), same decomposition
            // idea as cross_track_error_m just below. This is what actually sizes the pitch
            // correction below now - see arc_correction_full_scale_m's own comment for why
            // along_cos alone (a direction, not a distance) was the bug.
            along_track_error_m = landing.target_error_m * along_cos;

            // Cross-track (side-to-side) split-out - see cross_track_error_m/steered_target_horiz_dir's
            // own comments for why RCS gates on this instead of the raw total target_error_m.
            // target_horiz_dir is a unit vector, so subtracting its along-track projection
            // (travel_dir * along_cos) leaves exactly the perpendicular component, with magnitude
            // sin(angle) - scaling that fraction by the total scalar miss distance
            // (landing.target_error_m) converts "what fraction of the miss is sideways" into an
            // actual sideways distance in meters.
            Vector3d cross_track_dir_raw = target_horiz_dir - travel_dir * along_cos;
            double cross_track_frac = cross_track_dir_raw.magnitude;
            cross_track_error_m = landing.target_error_m * cross_track_frac;
            steered_target_horiz_dir = cross_track_frac > 1e-6 ? cross_track_dir_raw / cross_track_frac : Vector3d.zero;

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

            // Reese's Minmus overshoots: "right overhead" then "goes up again" right after Brake
            // starts. Root cause traced to checkDirection() above - it's a pre-existing guard (not
            // touched this session) that goes fully idle (SetThrottle(0), no steering at all)
            // the instant the vessel's real velocity picks up ANY upward component, on the
            // assumption that just means "still climbing toward apoapsis before the natural fall
            // starts, coast and wait". That's a fine assumption for a plain retrograde burn, but
            // this extend correction actively commands thrust tilted toward straight up - on a
            // near-zero-gravity body like Minmus there's almost nothing fighting that, so a
            // sustained high-throttle burn pitched up doesn't just slow the fall like it would on
            // Mun, it can push real vertical speed past zero into an actual climb. The instant it
            // does, checkDirection() kills the engine entirely and the vessel coasts - now with no
            // thrust and no steering - on whatever velocity it had at that moment, sailing over the
            // target while it "goes up", matching exactly what was described.
            //
            // The geometric clamp above (angle_from_up) already stops the AIM direction from ever
            // pointing past straight up, but it says nothing about the vessel's ACTUAL vertical
            // speed - it's just as large mid-descent as it is a frame before tipping into a climb.
            // Gating directly on real vertical speed instead closes that gap: once descent rate
            // drops under a small margin, ease the extend correction back off before it can push
            // past zero, instead of only reacting after checkDirection() has already had to cut
            // everything to stop it.
            // Margin kept deliberately small - this only needs to catch the last stretch before
            // vertical speed actually crosses zero, not suppress extend through an entire gentle
            // low-gravity descent (Minmus can sit at a low sink rate for a long time by nature,
            // and that's exactly the undershoot case extend is supposed to help with). First-pass
            // number, not tuned against a real flight yet - if extend still feels like it's barely
            // doing anything on Minmus after this, this margin is too generous and should come down
            // further; if it's still climbing, it needs to go up instead.
            double vertical_speed_up = -Vector3d.Dot(retro_dir_vec.normalized, up_vec) * current_speed;
            const float vertical_margin_speed = 1.5f; // m/s of descent rate to fully trust the extend correction at
            float descent_margin_factor = Mathf.Clamp01((float)(-vertical_speed_up) / vertical_margin_speed);
            max_pitch_up_deg *= descent_margin_factor;

            debug_vertical_speed_up = (float)vertical_speed_up;
            debug_descent_margin_factor = descent_margin_factor;

            // Scale the pitch angle by how big the along-track miss actually IS, not just by
            // along_cos (which only says which way it points) - see arc_correction_full_scale_m's
            // own comment. Full authority (fraction 1) once the real distance reaches that
            // setting; below it, tapers down toward 0 as the miss shrinks toward 0.
            //
            // Squared (parabolic), not linear - per Reese. A straight-line taper gives a
            // half-scale error (e.g. 25m against a 50m full-scale) half the pitch authority, which
            // is still a lot more correction than a 25m miss actually needs. Squaring the
            // normalized ratio pulls the whole curve down below the line everywhere except the two
            // ends (0 stays 0, full-scale still reaches 1), so small errors get proportionally
            // gentler corrections than before and only the errors actually near full-scale get
            // close to full authority - a closer match to "small miss, small correction" than the
            // straight line was.
            double along_track_norm = Mathf.Clamp01(
                (float)(Math.Abs(along_track_error_m) / arc_correction_full_scale_m));
            double along_track_magnitude_frac = along_track_norm * along_track_norm;
            debug_along_track_magnitude_frac = (float)along_track_magnitude_frac;

            double pitch_target_deg = (along_cos >= 0)
                ? along_track_magnitude_frac * max_pitch_up_deg
                : -along_track_magnitude_frac * max_pitch_horizontal_deg;

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

            // Once RCS is handling the heading correction (see RCSHandlingHeading/
            // ApplyRCSFineCorrection below), the main engine yields that job entirely and the aim
            // direction goes back to pure arc-corrected (no left/right tilt) - per Reese, tilting
            // the BURN itself to fix heading is exactly what was burning extra deltaV on top of an
            // already deltaV-heavy precision landing, and RCS can nudge sideways far more cheaply.
            // The arc (extend/shorten) correction above is untouched either way - Reese still
            // wants the engine doing that part.
            //
            // SNAPPED straight to 0 here, not eased through the usual rate limiter below - first
            // pass let it relax out at the normal smoothed pace on the theory that
            // checkDirection's "wait for vessel rotation" gate would notice the mismatch and cut
            // the throttle on its own, but in-game testing showed that never actually happened:
            // the fade was gradual enough that aim_dir never got far enough ahead of the vessel's
            // actual facing to cross touch_down_max_angle, so the engine just kept burning through
            // the whole transition, still partly tilted, exactly what this was supposed to avoid.
            // Snapping instead gives checkDirection an immediate, unmistakable jump to react to -
            // aim_dir drops the entire heading tilt in one frame, which reliably exceeds
            // touch_down_max_angle against the vessel's still-tilted actual facing, so the
            // throttle actually cuts to 0 and stays there until SAS finishes rotating back to the
            // untilted (arc-only) direction, per Reese.
            bool rcsHandlingHeading = RCSHandlingHeading();
            if (rcsHandlingHeading)
                correction_deg = 0;

            // Rate-limit how fast the COMMANDED angle itself is allowed to change, instead of
            // snapping straight to a freshly-recomputed value every frame. Confirmed in-game
            // without this: the correction visibly swayed side to side instead of settling on a
            // steady tilt - target_horiz_dir shifts a little every frame as predicted_landing_lat/
            // lon updates (partly *because* of the correction burn itself), which can flip which
            // way "closer to target" points tick to tick, and re-aiming instantly every frame
            // chases that noise instead of converging on it. Bypassed above (straight to 0, not
            // rate-limited) specifically for the "RCS just took over heading" case - see its own
            // comment for why.
            if (rcsHandlingHeading)
            {
                smoothed_correction_deg = 0;
            }
            else
            {
                float max_step = max_correction_rate_deg_per_sec * Time.deltaTime;
                smoothed_correction_deg = Mathf.MoveTowards(smoothed_correction_deg, (float)correction_deg, max_step);
            }

            if (Mathf.Abs(smoothed_correction_deg) < 0.01f)
                return pitched_dir.normalized;

            // Rodrigues' rotation formula, rotating horiz_dir2 around up_vec by the smoothed
            // correction angle. horiz_dir2 is already perpendicular to up_vec (built that way
            // above), so the along-axis term drops out.
            double rad = smoothed_correction_deg * Math.PI / 180.0;
            Vector3d rotated_horiz_dir2 = horiz_dir2 * Math.Cos(rad) + Vector3d.Cross(up_vec, horiz_dir2) * Math.Sin(rad);

            return (vertical2 + rotated_horiz_dir2 * horiz_mag2).normalized;
        }

        // RCS fine correction (item 6). Once target_error_m has closed to within
        // rcs_fine_correction_threshold_m, this drives the vessel's RCS translation inputs
        // (current_vessel.X/Y/Z) directly toward the target, taking over the heading-correction
        // job from the main engine entirely (see RCSHandlingHeading/ComputeSteeredDirection's own
        // use of it) - the engine keeps braking and keeps doing the arc extend/shorten correction
        // regardless, only the left/right heading tilt hands off to RCS. Originally this only
        // supplemented the engine's own heading correction rather than replacing it, but in-game
        // testing showed that design was a big part of why precision landing was burning so much
        // more deltaV than a normal landing: tilting the actual braking burn sideways to fix
        // heading is an inherently wasteful way to move sideways compared to RCS doing it
        // directly, and running both at once didn't save anything since the engine was still
        // paying that cost regardless of how little RCS also nudged. Unlike the engine's
        // correction, RCS doesn't need to reorient the vessel at all to act - it fires
        // sideways/vertically relative to whatever way the ship is already pointed - so it
        // doesn't have the turn-rate lag that was making the "fine" correction overshoot and make
        // target_error_m bigger instead of smaller.
        //
        // The world-to-vessel-local transform here (GetControlOwner().transform.coordinateSystem.
        // ToLocalVector) is the same one Docking's FinalApproach.RCKillSpeed() already uses
        // successfully to drive current_vessel.X/Y/Z from a world-space vector - reused as-is
        // rather than guessed at fresh, since that's the only place in this codebase this
        // particular transform has actually been proven against a real flight. Still new and
        // untested for THIS use (landing, not docking) - first pass, needs a real flight before
        // being trusted like the rest of precision landing does.
        // Tracks whether RCS was active last frame (for logging engage/disengage transitions -
        // see ApplyRCSFineCorrection/ClearRCSFineCorrection below) and throttles the
        // continuously-active log line so it doesn't spam once per frame.
        bool rcs_fine_active = false;
        int rcs_log_counter = 0;

        // "RCS can't keep up" bailout - added after Reese's own test log showed RCS holding a
        // strong, consistent push for over a minute (gain never dropped much below 0.7, with
        // rcs_fine_correction_power maxed out) while the miss climbed the whole time anyway
        // (~180m -> 340m+). That's not a bug in the correction math, it's this particular
        // vessel's RCS just not having enough authority to win that fight once the engine's no
        // longer helping with heading - and there's no reason to keep grinding at full RCS with
        // nothing to show for it until the miss happens to cross the FULL threshold (a much
        // bigger number) before handing back to the engine. Snapshot-based (samples error every
        // RcsSnapshotIntervalSeconds rather than frame to frame) so ordinary per-frame jitter in
        // target_error_m doesn't get mistaken for a real trend.
        float rcs_error_snapshot = -1f;
        float rcs_error_snapshot_timer = 0f;
        float rcs_growing_time = 0f;
        bool rcs_bailed_out = false;
        // How long the CURRENT bailout has been in effect - see RcsBailoutMinHoldSeconds below for
        // why this exists.
        float rcs_bailout_hold_timer = 0f;

        const float RcsSnapshotIntervalSeconds = 0.5f;
        const float RcsGrowingBailoutSeconds = 2.5f;
        // Minimum growth between consecutive snapshots to count toward the "growing" streak at
        // all, in meters. Reese's own logs from a real landing showed this bailout firing 70
        // times across two landings and clearing itself again within TENS OF MILLISECONDS almost
        // every time ("I only saw the RCS fire for a split second... it never actually held on"),
        // instead of the rare last-resort catch it was meant to be. Without a noise floor, any
        // sample-to-sample increase at all - including ordinary jitter in cross_track_error_m's
        // trig-based estimate, or a real but tiny back-and-forth wobble as RCS corrects - counted
        // as "growing" and could accumulate toward the bailout. Requiring a real, meaningful
        // increase per sample (not just >0) filters that noise out up front.
        const float RcsGrowthNoiseFloorM = 5f;
        // Once bailed out, stay handed off to the engine for AT LEAST this long, regardless of how
        // quickly cross_track_error_m happens to dip back under the recovery cutoff below - see the
        // same log evidence above. The old code re-checked recovery every single frame with no
        // minimum hold at all, so a bailout triggered while already close to (or under) the
        // recovery cutoff - which was most of them, since eligibility only requires being under the
        // FULL threshold - cleared again on literally the next frame, handing heading back to RCS
        // before the engine had done anything useful with it. A real minimum hold means a bailout
        // actually accomplishes something instead of being a one-frame flicker.
        const float RcsBailoutMinHoldSeconds = 3f;
        // Once bailed out (and the minimum hold above has elapsed), stay with the engine until the
        // miss has shrunk to comfortably less than the threshold that re-admits RCS - not just
        // ticked back under it - so this doesn't just flap RCS on and off right at the edge with no
        // real recovery in between.
        const double RcsBailoutRecoverFraction = 0.5;

        // Called once per frame from checkDirection while steering is active (see its own call
        // site) - separate from RCSHandlingHeading (called from more than one place per frame)
        // so this stateful, Time.deltaTime-accumulating logic only ever runs once per tick.
        void UpdateRcsGrowthTracking()
        {
            if (landing == null || !landing.settings.use_rcs_fine_correction.V)
            {
                ResetRcsGrowthTracking();
                return;
            }

            double threshold = landing.settings.rcs_fine_correction_threshold_m.V;
            double error = cross_track_error_m;

            if (error <= 0 || error >= threshold)
            {
                // Outside the RCS-eligible band entirely (dead-on, or already past the full
                // threshold - which already hands back to the engine on its own) - nothing to
                // track either way.
                rcs_error_snapshot = -1f;
                rcs_growing_time = 0f;
                return;
            }

            if (rcs_bailed_out)
            {
                rcs_bailout_hold_timer += Time.deltaTime;
                if (rcs_bailout_hold_timer >= RcsBailoutMinHoldSeconds && error <= threshold * RcsBailoutRecoverFraction)
                {
                    logger.LogInfo($"[TouchDown] RCS bailout cleared: miss recovered to {error:n1}m " +
                        $"(threshold {threshold:n1}m) after a {rcs_bailout_hold_timer:n1}s hold - RCS can take heading back.");
                    rcs_bailed_out = false;
                    rcs_bailout_hold_timer = 0f;
                    rcs_error_snapshot = -1f;
                    rcs_growing_time = 0f;
                }
                return; // stay handed off to the engine until the minimum hold elapses AND it's recovered
            }

            rcs_error_snapshot_timer += Time.deltaTime;
            if (rcs_error_snapshot_timer < RcsSnapshotIntervalSeconds)
                return;

            if (rcs_error_snapshot >= 0f && error > rcs_error_snapshot + RcsGrowthNoiseFloorM)
            {
                rcs_growing_time += rcs_error_snapshot_timer;
                if (rcs_growing_time >= RcsGrowingBailoutSeconds)
                {
                    rcs_bailed_out = true;
                    rcs_bailout_hold_timer = 0f;
                    logger.LogInfo($"[TouchDown] RCS bailout: miss grew for {rcs_growing_time:n1}s straight " +
                        $"(now {error:n1}m of {threshold:n1}m threshold) despite RCS correcting - handing " +
                        $"heading back to the main engine for at least {RcsBailoutMinHoldSeconds:n1}s.");
                }
            }
            else
            {
                rcs_growing_time = 0f;
            }

            rcs_error_snapshot = (float)error;
            rcs_error_snapshot_timer = 0f;
        }

        void ResetRcsGrowthTracking()
        {
            rcs_error_snapshot = -1f;
            rcs_error_snapshot_timer = 0f;
            rcs_growing_time = 0f;
            rcs_bailed_out = false;
            rcs_bailout_hold_timer = 0f;
        }

        // Single source of truth for "is RCS supposed to be handling heading correction right
        // now" - shared by ComputeSteeredDirection (to zero out the engine's own heading tilt
        // while this is true) and ApplyRCSFineCorrection below (to know it should be pushing).
        // Once target_error_m grows back past the threshold, this goes false again and the engine
        // resumes heading correction on its own - per Reese, exactly as it did before RCS existed.
        // Also false while rcs_bailed_out is latched (see UpdateRcsGrowthTracking above) - the
        // engine takes heading back early if RCS clearly isn't winning, not just once the miss
        // grows all the way past the raw threshold.
        bool RCSHandlingHeading()
        {
            if (landing == null || !landing.settings.use_rcs_fine_correction.V)
                return false;

            if (rcs_bailed_out)
                return false;

            double threshold = landing.settings.rcs_fine_correction_threshold_m.V;
            double error = cross_track_error_m;
            return error > 0 && error < threshold;
        }

        void ApplyRCSFineCorrection(Vector HorizonUp)
        {
            // steered_target_horiz_dir is zero whenever ComputeSteeredDirection had nothing to
            // steer toward this tick (see that field's own comment) - nothing for RCS to push
            // toward either in that case.
            if (!RCSHandlingHeading() || steered_target_horiz_dir.magnitude < 1e-9)
            {
                ClearRCSFineCorrection();
                return;
            }

            double threshold = landing.settings.rcs_fine_correction_threshold_m.V;
            double error = cross_track_error_m;

            // Full strength across most of the active band, only tapering down in the last
            // rcsTaperFraction of it right near target_error_m = 0 (to avoid overshoot/oscillation
            // chasing the last couple meters, the original reason this fades at all). This USED TO
            // fade linearly across the WHOLE band (strength = 1 - error/threshold) - meaning the
            // instant a miss grew back toward the threshold, the code commanded LESS RCS push, not
            // more, right when more was actually needed. That's what Reese's own test log caught:
            // RCS held on for over a minute while the miss climbed from ~180m to 340m+, because the
            // fade curve was quietly throttling itself down the whole time the miss grew. Full
            // strength now applies the moment RCS takes over and stays there unless the miss is
            // already most of the way to zero - see the separate growth-tracking bailout above for
            // the backstop if even full strength still isn't enough to hold the line.
            const float rcsTaperFraction = 0.15f;
            float normalizedError = Mathf.Clamp01((float)(error / threshold));
            float strength = normalizedError < rcsTaperFraction
                ? normalizedError / rcsTaperFraction
                : 1f;
            float gain = landing.settings.rcs_fine_correction_power.V * strength;

            Vector world_dir = new Vector(HorizonUp.coordinateSystem, steered_target_horiz_dir);

            var control_component = current_vessel.VesselComponent.GetControlOwner();
            // ToLocalVector returns a plain Vector3d here (not the coordinate-tagged Vector
            // wrapper that Reframe/etc. use) - confirmed by the CS0029 this fixed, same as
            // FinalApproach.cs's local_speed (a plain Vector3) getting assigned straight from
            // this same ToLocalVector/TransformVector chain.
            Vector3d local_dir = control_component.transform.coordinateSystem.ToLocalVector(world_dir);

            current_vessel.X = (float)local_dir.x * gain;
            current_vessel.Y = (float)local_dir.y * gain;
            current_vessel.Z = (float)local_dir.z * gain;

            // Diagnostic - added because this feature previously had zero persistent log output
            // at all (only a live debug-UI row, gated on debug_mode and only visible while
            // actively looking at the Landing tab). Reese couldn't tell whether RCS ever actually
            // engaged during his brake-burn test (item 4). Logs immediately on the OFF->ON
            // transition (so a brief engagement isn't missed) and then every ~60 frames
            // (~1s) while it stays continuously active, rather than every single frame - same
            // frame-count throttle idea as ResizeManipulator's existing pointer-move logging.
            bool justEngaged = !rcs_fine_active;
            rcs_fine_active = true;
            rcs_log_counter++;
            if (justEngaged || rcs_log_counter % 60 == 0)
            {
                logger.LogInfo($"[TouchDown] RCS fine correction ACTIVE: error={error:n1}m threshold={threshold:n1}m " +
                    $"gain={gain:n2} local_dir=({local_dir.x:n2},{local_dir.y:n2},{local_dir.z:n2}) " +
                    $"XYZ=({current_vessel.X:n2},{current_vessel.Y:n2},{current_vessel.Z:n2})");
            }
        }

        void ClearRCSFineCorrection()
        {
            // Only worth a log line on the ON->OFF transition, not every frame RCS just happens
            // to be off (which is most of them, most of every flight, whenever the toggle's off).
            if (rcs_fine_active)
                logger.LogInfo("[TouchDown] RCS fine correction OFF");
            rcs_fine_active = false;
            rcs_log_counter = 0;

            current_vessel.X = 0;
            current_vessel.Y = 0;
            current_vessel.Z = 0;
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
                    addRow("Along-Track Error", $"{along_track_error_m:n1} m");
                    addRow("Arc Authority", $"{debug_along_track_magnitude_frac:n2}");
                    // Watch this pair on Minmus specifically - Vertical Speed should stay negative
                    // (descending) through Brake; if it's creeping toward/past 0, Extend Margin
                    // should already be sliding toward 0 well before it gets there. If Vertical
                    // Speed goes positive anyway, the margin (currently 1.5 m/s) is too small.
                    addRow("Vertical Speed (up+)", $"{debug_vertical_speed_up:n2} m/s");
                    addRow("Extend Margin", $"{debug_descent_margin_factor:n2}");

                    if (landing.settings.use_rcs_fine_correction.V)
                    {
                        bool active = cross_track_error_m > 0
                            && cross_track_error_m < landing.settings.rcs_fine_correction_threshold_m.V;
                        addRow("RCS Fine Correction", active ? "Active" : "Idle (out of range)");
                        addRow("Cross-Track Error", $"{cross_track_error_m:n1} m");
                        if (active)
                            addRow("RCS Input (X/Y/Z)", $"{current_vessel.X:n2} / {current_vessel.Y:n2} / {current_vessel.Z:n2}");
                    }
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

