using K2D2.KSPService;
using KSP.Sim;
using KSP.Sim.impl;
using KTools;
using UnityEngine;
using UnityEngine.UIElements;
using K2D2.UI;
using K2D2.Controller;
using K2D2.Landing;
using ILogger = ReduxLib.Logging.ILogger;

namespace K2D2.Lift
{
    // GravityTurn ascent profile (see AscentProfile.cs / LiftSettings.gravity_turn_* / Lift.uxml's
    // ascent_profile_drop) - the second, player-selectable alternative to Ascent.cs's fixed
    // altitude-based pitch curve ("Guided").
    //
    // This is an ORIGINAL implementation of the general "gravity turn" ascent technique (rise
    // vertically, apply a short pitch-over kick, then hold prograde while throttling to control
    // the climb) - a real, decades-old rocketry technique, not anyone's intellectual property.
    // It does not port, translate, or closely mirror any third-party mod's code (in particular
    // linuxgurugamer's GPL-3.0 "GravityTurn" mod, which is incompatible with K2D2Redux's own
    // CC-BY-SA 4.0 license - see NOTICE.md). Every formula and the whole state machine below were
    // written from scratch for this codebase, reusing only K2D2Redux's own existing helpers
    // (SASTool, LandingTargeting, the QuaternionD/elevation convention Ascent.cs already uses).
    //
    // Three phases, tracked by `phase` below:
    //   VerticalRise - full throttle, straight up, until vertical speed reaches
    //     gravity_turn_kick_speed_mps (a real gravity turn triggers its kick off of speed, not
    //     altitude - heavier/higher-TWR rockets reach a good kick speed at very different heights).
    //   PitchKick    - a short, fixed-duration nudge off vertical (gravity_turn_pitch_kick_deg).
    //     Necessary because prograde AT dead vertical has no horizontal component to hold onto -
    //     without this kick the vessel would just keep rising straight up forever once handed to
    //     prograde-hold.
    //   Turn         - the actual gravity turn: SAS holds prograde (letting gravity bend the
    //     trajectory over on its own, rather than commanding pitch directly), while throttle is
    //     nudged up or down to hold a target time-to-apoapsis (gravity_turn_hold_time_s). A
    //     predicted apoapsis further in the future than the target means the current trajectory
    //     is gaining altitude faster than intended, so throttle eases off (coast, let gravity turn
    //     it over); closer than the target means throttle needs to pick back up.
    //
    // Finishes under the exact same condition as Ascent.cs (remaining apoapsis within
    // end_ascent_error), so Coasting/Adjust/FinalCircularize downstream never need to know which
    // profile actually flew the ascent - see LiftPilot.ascent.
    public class ReactiveAscent : AscentControllerBase
    {
        public ILogger logger = ReduxLib.ReduxLib.GetLogger("K2D2.ReactiveAscent");

        LiftSettings settings = null;
        KSPVessel current_vessel;

        public ReactiveAscent(LiftSettings lift_settings)
        {
            current_vessel = K2D2_Plugin.Instance.current_vessel;
            this.settings = lift_settings;
        }

        enum Phase { VerticalRise, PitchKick, Turn }
        Phase phase = Phase.VerticalRise;

        // How long PitchKick holds its off-vertical target before handing off to prograde-hold -
        // long enough for SAS to actually start rotating the vessel, short enough that it's still
        // a "kick", not a second pitch program.
        const float PitchKickDurationSeconds = 3f;
        float pitch_kick_timer = 0;

        // RunTurn()'s throttle control is a proper PID loop now, not a step search or a
        // "learn and hold" snapshot - both of those turned out to be structurally unable to
        // settle, no matter how they were tuned, and here's why: time_to_apoapsis_s isn't a value
        // throttle sets directly - it behaves like a running total, an INTEGRAL of however wrong
        // the throttle currently is relative to the one value that would hold it perfectly steady
        // (call that the "equilibrium throttle"). Every previous version - the fixed step, and the
        // "detect it's settled, remember this throttle" snapshot - was ITSELF also just
        // integrating (accumulating a correction into wanted_throttle tick after tick) with
        // nothing else in the loop. Two integrators chained together (the real physics, plus a
        // controller that's also pure integration) is a textbook undamped oscillator - like a
        // mass on a spring with no friction, it overshoots, corrects, overshoots the other way,
        // forever, regardless of step size or gain. That's why every previous tuning pass just
        // moved WHERE and HOW it hunted, not whether it hunted.
        //
        // The actual missing piece is damping: a term that reacts to time_to_apoapsis_s's CURRENT
        // rate of change, not just its distance from the target - the classic PID "derivative"
        // term. It's what lets the controller feel its own momentum coming and ease off before it
        // overshoots, instead of only noticing after the fact. Combined with a small integral
        // gain (which is what actually converges wanted_throttle onto the true, unknown
        // equilibrium throttle over time, the same way it would for any "hold a level steady"
        // problem - a leaky tank, a thermostat), this is the standard, well-known fix for exactly
        // this kind of process, not anything mod-specific to look up - see RunTurn() below.

        // time_to_apoapsis_s one tick ago, so RunTurn can compute how fast it's CURRENTLY moving -
        // the derivative term above.
        float previous_time_to_apoapsis_s = 0f;

        // The derivative term's last computed value, kept around only so UpdateInfoRows can show
        // it - not used for control itself (that reads straight off the local in RunTurn).
        float time_to_apoapsis_rate = 0f;

        // Integral gain: how hard the accumulated apoapsis-time error pulls throttle, per second
        // of error, at full horizontal (see VerticalPidScale below for early-turn scaling).
        // wanted_throttle itself IS the integral accumulator here - there's no separate state for
        // it, and clamping wanted_throttle to the throttle range every tick doubles as basic
        // anti-windup (it can never accumulate further than the clamp allows).
        const float ApoapsisTimeIntegralGain = 0.006f;

        // Derivative gain: reacts to time_to_apoapsis_s's own rate of change (not the error's -
        // equivalent here since the target doesn't move, and it avoids "derivative kick" if it
        // ever does). This is the damping term - see the class comment above for why it's the
        // actual fix, not the exact number here.
        const float ApoapsisTimeDerivativeGain = 0.05f;

        // Below this much apoapsis-time error, the integral term stops accumulating (treated as
        // zero error) - a small dead zone so once it's genuinely converged, leftover measurement
        // noise can't slowly walk the integral term around. The derivative term still applies
        // inside this band, so any real leftover motion is still damped, not ignored.
        const float TimeToApoapsisDeadbandSeconds = 2f;

        // Both gains above are scaled down by this much right at the very start of the turn (dead
        // vertical), ramping back up to full strength as the trajectory flattens toward horizontal
        // - see RunTurn's own comment. Time-to-apoapsis is at its most throttle-sensitive exactly
        // when the trajectory is most vertical, so the same gains that behave well once horizontal
        // would still overreact right after the kick.
        const float VerticalPidScale = 0.2f;

        float wanted_throttle = 0;
        public float time_to_apoapsis_s = 0;

        // Heading correction (see Ascent.cs's own computeSpeedHeading, whose decomposition this
        // mirrors) - shares LiftSettings.heading_correction with the Guided profile rather than
        // getting its own toggle, since it means the same thing in both: nudge the compass
        // heading to correct for surface velocity drifting off settings.heading.V.
        float heading_correction_deg = 0;
        float h_speed_heading_deg = 0;

        public override void Start()
        {
            base.Start();

            phase = Phase.VerticalRise;
            pitch_kick_timer = 0;
            wanted_throttle = 0;
            time_to_apoapsis_s = 0;
            previous_time_to_apoapsis_s = 0f;
            time_to_apoapsis_rate = 0f;
            heading_correction_deg = 0;
            h_speed_heading_deg = 0;
            last_ap_km = 0;
            ap_km = 0;
            delta_ap_per_second = 0;
        }

        public override void computeValues(bool compute_delta_ap_per_second)
        {
            if (current_vessel.VesselComponent == null)
                return;

            UpdateBasicTelemetry(current_vessel, compute_delta_ap_per_second);

            // Same state-vector orbit math FinalCircularize already uses (Final.cs) to predict
            // when the current trajectory reaches apoapsis - existing K2D2Redux utility, not new.
            IKeplerPatch orbit = current_vessel.VesselComponent.Orbit;
            var body = orbit.referenceBody;
            double now = GeneralTools.Current_UT;
            Vector3d r_now = orbit.GetRelativePositionAtUTZup(now);
            Vector3d v_now = orbit.GetOrbitalVelocityAtUTZup(now);
            double period = LandingTargeting.OrbitalPeriodFromStateVectors(r_now, v_now, body.gravParameter);
            float raw_time_to_apoapsis_s = (float)LandingTargeting.TimeToNextApoapsis(r_now, v_now, body.gravParameter, period);

            // Smoothed the same way UpdateBasicTelemetry already smooths delta_ap_per_second -
            // this raw prediction swings noticeably tick-to-tick (it's a ballistic projection off
            // the vessel's current, still-under-thrust trajectory, not a stable measurement), and
            // that noise was what actually made the throttle in RunTurn() below look "jumpy" once
            // its gain was raised, not the gain itself. Smoothing the input lets the gain stay
            // aggressive without amplifying tick-to-tick noise into a visible throttle jump.
            time_to_apoapsis_s = time_to_apoapsis_s <= 0
                ? raw_time_to_apoapsis_s
                : Mathf.Lerp(time_to_apoapsis_s, raw_time_to_apoapsis_s, 0.25f);
        }

        // Points the vessel at a given elevation/heading, same QuaternionD convention Ascent.cs's
        // applyDirection already uses (elevation measured from the horizon, heading in degrees).
        // Applies the same optional heading correction Ascent.cs applies, so all three phases
        // (via this one shared method) respect the Heading Correction toggle identically.
        void PointAt(float elevation_deg)
        {
            var autopilot = current_vessel.Autopilot;
            if (autopilot == null)
                return;

            autopilot.Enabled = true;
            autopilot.SAS.lockedMode = false;

            if (settings.heading_correction.V)
                ComputeHeadingCorrection();
            else
                heading_correction_deg = 0;

            var telemetry = SASTool.getTelemetry();
            var up = telemetry.HorizonUp;

            Vector3d direction = QuaternionD.Euler(-elevation_deg, settings.heading.V + heading_correction_deg, 0) * Vector3d.forward;
            autopilot.SAS.SetTargetOrientation(new Vector(up.coordinateSystem, direction), false);
        }

        // Same decomposition as Ascent.cs's computeSpeedHeading(): finds the compass heading of
        // the horizontal component of surface velocity, then how far that is from the wanted
        // heading (settings.heading.V), clamped the same +/-45 degrees.
        void ComputeHeadingCorrection()
        {
            var Upcoords = current_vessel.VesselVehicle.Up.coordinateSystem;
            var SurfaceVelocity = Vector.Reframed(current_vessel.VesselVehicle.SurfaceVelocity, Upcoords).vector;
            var North = Vector.Reframed(current_vessel.VesselVehicle.North, Upcoords).vector;
            var Up = current_vessel.VesselVehicle.Up.vector;

            var UpSpeed = Up.normalized * Vector3d.Dot(SurfaceVelocity, Up);
            var LocalHSpeed = SurfaceVelocity - UpSpeed;

            h_speed_heading_deg = (float)-Vector3d.SignedAngle(LocalHSpeed.normalized, North, Up);

            heading_correction_deg = GeneralTools.diffAngle(settings.heading.V, h_speed_heading_deg);
            heading_correction_deg = Mathf.Clamp(heading_correction_deg, -45f, 45f);
        }

        // The Turn phase needs prograde's own elevation (pitch above the horizon) to hand to
        // PointAt above, since it no longer uses SAS's built-in AutopilotMode.Prograde (see
        // RunTurn's own comment for why). Same SurfaceVelocity/Up decomposition as
        // ComputeHeadingCorrection, just measuring the vertical angle instead of the compass one.
        float ComputeProgradeElevation()
        {
            var Upcoords = current_vessel.VesselVehicle.Up.coordinateSystem;
            Vector3d surface_velocity = Vector.Reframed(current_vessel.VesselVehicle.SurfaceVelocity, Upcoords).vector;
            Vector3d up = current_vessel.VesselVehicle.Up.vector.normalized;

            double up_speed = Vector3d.Dot(surface_velocity, up);
            Vector3d local_h_speed = surface_velocity - up * up_speed;

            // Degenerate case (no horizontal speed yet) - treat as still vertical rather than
            // calling Angle() on a near-zero vector.
            if (local_h_speed.magnitude < 1e-3)
                return up_speed >= 0 ? 90f : -90f;

            float elevation_deg = (float)Vector3d.Angle(surface_velocity, local_h_speed);
            if (up_speed < 0)
                elevation_deg = -elevation_deg;

            return elevation_deg;
        }

        void RunVerticalRise()
        {
            PointAt(90);

            wanted_throttle = settings.max_throttle.V;
            current_vessel.SetThrottle(wanted_throttle);

            // Speed-based trigger, not altitude - see the class comment above for why. Vertical
            // speed specifically (not total surface speed) since this phase is defined as
            // straight-up flight - the two are the same thing here anyway.
            float vertical_speed_mps = (float)current_vessel.VesselVehicle.VerticalSpeed;
            if (vertical_speed_mps >= settings.gravity_turn_kick_speed_mps.V)
                phase = Phase.PitchKick;
        }

        void RunPitchKick()
        {
            PointAt(90 - settings.gravity_turn_pitch_kick_deg.V);

            wanted_throttle = settings.max_throttle.V;
            current_vessel.SetThrottle(wanted_throttle);

            pitch_kick_timer += Time.deltaTime;
            if (pitch_kick_timer >= PitchKickDurationSeconds)
                phase = Phase.Turn;
        }

        void RunTurn()
        {
            // Prograde-hold is the actual "turn" - the nose stays on the velocity vector and
            // gravity does the rest of the pitching-over, rather than this controller commanding
            // pitch directly the way Ascent.cs's fixed curve does. This used to be
            // SASTool.setAutoPilot(AutopilotMode.Prograde) - Redux/KSP's own built-in SAS mode -
            // but that mode has no hook for nudging heading, so it couldn't support Heading
            // Correction. PointAt already knows how to point at an elevation/heading with an
            // optional heading correction baked in (VerticalRise/PitchKick already go through it),
            // so Turn now just needs prograde's own elevation each tick - see
            // ComputeProgradeElevation above - and hands that to the same shared method.
            float elevation_deg = ComputeProgradeElevation();
            PointAt(elevation_deg);

            float error = time_to_apoapsis_s - settings.gravity_turn_hold_time_s.V;

            // How fast time_to_apoapsis_s is CURRENTLY moving - the derivative term. See the class
            // comment above for why this, not just the error itself, is what actually damps the
            // loop instead of letting it oscillate forever.
            float rate = (time_to_apoapsis_s - previous_time_to_apoapsis_s) / Mathf.Max(Time.deltaTime, 0.0001f);
            previous_time_to_apoapsis_s = time_to_apoapsis_s;
            time_to_apoapsis_rate = rate;

            // Dead zone on the integral (position) term only, once genuinely close to target -
            // see TimeToApoapsisDeadbandSeconds' own comment. The derivative term below still
            // damps any leftover motion regardless of whether this zeroes it out.
            float integral_error = Mathf.Abs(error) > TimeToApoapsisDeadbandSeconds ? error : 0f;

            // Scaled down early in the turn since the same throttle change swings time_to_apoapsis
            // far more while the trajectory is still close to vertical - see VerticalPidScale's
            // own comment. 0 dead vertical, 1 once fully horizontal.
            float horizontal_factor = Mathf.Clamp01(1f - Mathf.Abs(elevation_deg) / 90f);
            float pid_scale = Mathf.Lerp(VerticalPidScale, 1f, horizontal_factor);

            // wanted_throttle IS the PID's integral accumulator - see ApoapsisTimeIntegralGain's
            // own comment for why that's deliberate, not a shortcut.
            float throttle_rate_of_change = -(ApoapsisTimeIntegralGain * integral_error + ApoapsisTimeDerivativeGain * rate) * pid_scale;
            wanted_throttle += throttle_rate_of_change * Time.deltaTime;

            if (wanted_throttle > settings.max_throttle.V)
                wanted_throttle = settings.max_throttle.V;
            else if (wanted_throttle < settings.gravity_turn_min_throttle.V)
                wanted_throttle = settings.gravity_turn_min_throttle.V;

            current_vessel.SetThrottle(wanted_throttle);
        }

        public override void Update()
        {
            finished = false;

            float remaining_Ap = settings.destination_Ap_km.V - ap_km;
            if (remaining_Ap <= settings.end_ascent_error)
            {
                current_vessel.SetThrottle(0);
                finished = true;
                return;
            }

            switch (phase)
            {
                case Phase.VerticalRise:
                    RunVerticalRise();
                    break;
                case Phase.PitchKick:
                    RunPitchKick();
                    break;
                case Phase.Turn:
                    RunTurn();
                    break;
            }
        }

        // Narrative feedback lives in UpdateInfoRows below (LIFT INFO table), same split
        // Ascent.cs uses.
        public override void updateUI(VisualElement root_el, FullStatus st)
        {
        }

        public override void UpdateInfoRows(System.Action<string, string> addRow)
        {
            addRow("Phase", phase.ToString());
            addRow("Apoapsis Alt.", $"{ap_km:n2} km");
            addRow("Altitude", $"{current_altitude_km:n2} km");
            addRow("Climb Rate", $"{delta_ap_per_second:n2} km/s");
            addRow("Time to Apoapsis", $"{time_to_apoapsis_s:n1} s (target {settings.gravity_turn_hold_time_s.V:n0} s)");
            addRow("Throttle", $"{wanted_throttle:n2}");

            // How fast time_to_apoapsis is currently moving (the PID's derivative term, see
            // ApoapsisTimeDerivativeGain's own comment) - near 0 once it's actually settled, still
            // large while it's climbing/falling toward the target. Useful for telling "still
            // converging" apart from "found it and holding" at a glance.
            addRow("Ap Time Rate", $"{time_to_apoapsis_rate:n2} s/s");

            // Same two rows Ascent.cs's own UpdateInfoRows shows under the same toggle - lets the
            // player see the correction actually working (or confirm it's doing nothing when
            // h_speed_heading_deg already matches settings.heading.V).
            if (settings.heading_correction.V)
            {
                addRow("Surface Heading", $"{h_speed_heading_deg:n2} °");
                addRow("Heading Correction", $"{heading_correction_deg:n2} °");
            }
        }
    }
}
