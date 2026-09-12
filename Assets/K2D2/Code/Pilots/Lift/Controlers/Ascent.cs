
using K2D2.KSPService;
using KSP.Sim;
using KSP.Sim.impl;
using KTools;
using UnityEngine;
using UnityEngine.UIElements;
using K2D2.UI;
using K2D2.Controller;
using ILogger = ReduxLib.Logging.ILogger;
namespace K2D2.Lift
{
    /// <summary>
    /// rotation used for docking
    /// </summary>
    public class Ascent : ExecuteController
    {
        public ILogger logger = ReduxLib.ReduxLib.GetLogger("K2D2.Ascent");

        LiftSettings settings = null;
        LiftAscentPath ascent_path = null;

        KSPVessel current_vessel;

        public Ascent(LiftSettings lift_settings, LiftAscentPath ascent_path)
        {
            current_vessel = K2D2_Plugin.Instance.current_vessel;
            this.settings = lift_settings;
            this.ascent_path = ascent_path;
        }

        public float current_altitude_km = 0;
        public float ap_km = 0;
        float last_ap_km = 0;
        public float delta_ap_per_second;
        float wanted_elevation;

        float wanted_throttle = 0;

        float heading_correction = 0;
        float h_speed_heading = 0;

        // ROLL PROGRAM (see LiftSettings.roll_program* for the player-facing settings). Corrects
        // for whatever roll the vessel happened to spawn with on the pad - KSP doesn't guarantee a
        // rocket lands there lined up with its own design intent, and an asymmetric build (e.g.
        // off-center fins, a grid-fin cluster, a lopsided RCS quad) can make the ascent "funky" if
        // nothing ever rolls it into the orientation it was actually built for.
        //
        // FIRST VERSION of this feature drove roll with a raw current_vessel.Roll axis write, same
        // mechanism RCS fine correction uses for translation. Reese tested it and it did nothing -
        // "I was able to roll the rocket with keyboard inputs. The autopilot didnt do anything with
        // roll." Root cause, found by reading the decompiled VesselSAS.cs Reese pulled straight out
        // of his IDE: SAS.SetTargetOrientation(Vector, bool) - the only overload this codebase has
        // ever called (TouchDown.cs, AttitudePilot.cs, here, DockingTurnTo.cs) - clears SAS's
        // "persistent target rotation" flag, which leaves roll in a free/damped mode.
        // VesselSAS.ControlUpdate() writes FlightCtrlState.roll unconditionally every physics tick
        // regardless of that flag, so it was silently overwriting any raw roll-axis command the
        // instant after it was set. RCS translation (X/Y/Z) never has this problem because SAS
        // doesn't touch that channel at all, which is why the same raw-write approach works fine for
        // TouchDown's RCS fine correction but not for rotation.
        //
        // REAL FIX: SAS has a second overload, SetPersistentTargetOrientation(Vector, Rotation,
        // bool), that actually engages SAS's own roll control loop (ComputePersistentTargetRollDelta
        // / ComputePersistentTargetRollResponse in VesselSAS.cs) instead of leaving roll free/damped.
        // That loop reads local Vector3.up as "nose" and local Vector3.forward as the dorsal/roll-
        // reference axis it measures roll around - both pulled off the Rotation we hand it and
        // compared against the vessel's own current orientation - see ComputeRollTargetRotation
        // below for how that target Rotation actually gets built. This is the SAME nose convention
        // Nodes/TurnTo.cs already uses successfully for current_vessel.GetRotation() (Vector3.up) -
        // NOT the Vector3.down convention TouchDown.cs uses for that same rotation source, which is
        // specific to TouchDown's tail-first landing-burn framing and doesn't apply here.
        //
        // Still measured relative to the vessel's OWN launch orientation (captured once in Start(),
        // below), not an absolute compass/navball reading - a relative target ("roll this many
        // degrees from however you spawned") is exactly what Reese described needing, and sidesteps
        // ever having to reconcile this with TouchDown's differing nose-axis convention.
        //
        // SLOW ROLL RAMP: the first version of the SAS-driven fix handed VesselSAS the full target
        // angle the instant the roll program engaged. Reese tested it - "a very hard nudge and then
        // the rocket wouldn't stop rolling", i.e. it overshot and oscillated instead of settling.
        // VesselSAS.ComputePersistentTargetRollResponse (see VesselSAS.cs) is a simple P+D formula
        // with gain 1.0 on the error IN RADIANS and no ramping of its own - it saturates to full
        // (+/-1) roll input for any error past about 57°, so handing it a big instantaneous error
        // gives a full-power kick that easily overshoots, and the damping term alone isn't enough to
        // arrest that much built-up spin, so it overshoots back the other way too, repeatedly. Fix:
        // instead of commanding roll_program_angle_deg directly, ramp a separate runtime target
        // (ramped_roll_target_deg) toward it a few degrees at a time (UpdateRollProgram, below), so
        // the error SAS actually sees stays small.
        //
        // STOP HOLDING ONCE DONE: even with the ramp above, Reese still saw it "wouldn't stop
        // rolling" - smoother start, but never actually settling. Part of the problem was recomputing
        // and re-committing a roll target EVERY tick for the rest of the whole ascent (fixed below by
        // only actively holding a target while roll_program_started && !roll_program_done, handing
        // roll back to plain SetTargetOrientation - free/damped, same as ascent's always behaved
        // without this feature - the moment it's actually settled).
        //
        // LOCKEDMODE INSTEAD OF SetPersistentTargetOrientation: even after that, and after slowing
        // the ramp down further, it STILL wouldn't settle - and Reese found the key clue: turning the
        // K2D2 autopilot off entirely let it stop on its own ("the SRBs stop the roll when the
        // autopilot is shut off"). That means our own active commanding was what was sustaining the
        // roll, not some external disturbance we weren't fighting hard enough. Rereading VesselSAS.cs:
        // ComputePersistentTargetRollResponse (the function behind SetPersistentTargetOrientation's
        // roll axis) is a simple fixed-gain P+D formula (1.0 / 0.45) that never gets the per-vessel
        // auto-tuning (AutoTuneScalar, dynamic-pressure Ki scaling) the game applies to pitch and yaw
        // - it looks like a lightly-used fallback path, not the one the game actually leans on for
        // real attitude holding. Roll's WELL-tuned path is PidLockedRoll, the same auto-tuned PID
        // class pitch and yaw already use successfully everywhere else in this codebase - but that
        // class only ever gets exercised when SAS.lockedMode is true (VesselSAS.ControlUpdate computes
        // ALL THREE axes off a single target Rotation via GetRotationDelta()'s Euler decomposition in
        // that mode, instead of the crude roll-only formula). So the roll program now builds a FULL
        // orientation (see ComputeRollTargetRotation's RotationFromNoseAndDorsal helper - it pins both
        // the nose axis AND the roll/dorsal axis at once, which neither FromToRotation nor
        // LookRotation can do in a single call) and drives it via SAS.LockRotation + lockedMode = true
        // instead. Nose gets pinned exactly, so ascent's actual pitch/yaw steering shouldn't notice
        // the difference - only roll goes through the better-tuned control path now.
        //
        // THE ACTUAL ROOT CAUSE (found after LOCKEDMODE alone still didn't fix it) - five different
        // fixes were tried here, each patching a different layer of the same underlying approach
        // ("capture the vessel's orientation once at launch, compare it against the current
        // orientation later via Rotation.Reframed into a common frame"): capture timing
        // (launch_rotation_valid), a suspected live/mutating GetRotation() reference
        // (FreezeRotation, extracting nose/dorsal as plain Vector3d before storing), and gathering
        // diagnostics along the way. All five produced the EXACT SAME symptom: "Roll (from launch)"
        // and a raw Nose·LaunchFwd dot product both pinned at their launch-time values through a
        // 1 km to 10 km ascent with a real, large pitch/heading change (Pitch Target 90 -> 71.47,
        // Surface Heading 175.75 -> 94.79) - while a SEPARATE diagnostic (Nose·Up, comparing the
        // CURRENT nose against gravity alone, no launch reference involved) proved GetRotation()
        // itself is live and correctly updating (it moved from ~+1 to -0.7092 over that same
        // climb). So the live data is fine; something about comparing it against an EARLIER
        // snapshot, via Reframed, isn't.
        //
        // Pulling the decompiled source for Rotation.Reframed showed why:
        //   public static Rotation Reframed(Rotation rotation, ICoordinateSystem newReferenceFrame)
        //   { if (newReferenceFrame != null) return new Rotation(newReferenceFrame,
        //     newReferenceFrame.ToLocalRotation(rotation)); return rotation; }
        // It delegates to ToLocalRotation on the COORDINATE SYSTEM ITSELF, whose implementation we
        // don't have visibility into. gravityForPos's coordinate system is necessarily a
        // per-position, local-horizon-style frame (that's the only way "up" is meaningful at the
        // vessel's current location), and every other place this codebase calls Reframed
        // (TouchDown, TurnTo, DockingTurnTo) reframes something and uses it IMMEDIATELY, same tick -
        // nowhere else ever asks it to reframe a rotation captured many ticks earlier, at a
        // different position along a curved trajectory, and compare it against today. That's a
        // genuinely novel usage pattern this one feature introduced, and exactly where a hidden
        // per-position assumption inside ToLocalRotation could produce a self-consistent-looking
        // but physically wrong answer.
        //
        // Rather than chase ToLocalRotation's internals through another cycle of decompiling,
        // roll is now INTEGRATED instead of compared. VesselSAS's own decompiled source
        // (ComputePersistentTargetRollResponse) uses attitudeAngularVelocity.y directly as the
        // roll-axis angular rate, in body-local terms, with NO reframing at all - accumulated_roll_deg
        // below just sums that same .y component every tick from launch onward. This only ever
        // touches SAME-TICK data (current angular velocity), so it can't be affected by whatever
        // ToLocalRotation does with a historical rotation. The SAS target (ComputeRollTargetRotation)
        // is rebuilt the same way: instead of rotating a frozen LAUNCH dorsal reference by the full
        // ramped angle, it rotates the CURRENT dorsal reference (same-tick GetRotation(), reframed
        // and used immediately - the ONLY pattern ever empirically proven to work in this codebase)
        // by however much MORE roll is still needed (ramped target minus what's already
        // accumulated) - mathematically equivalent, but never looks at an earlier tick's rotation.
        bool roll_program_started = false;
        bool roll_program_done = false;
        double accumulated_roll_deg = 0; // total roll (deg) since launch, integrated tick-to-tick - see IntegrateRoll
        Rotation prev_roll_rotation; // previous tick's (reframed) rotation, for IntegrateRoll's tick-to-tick delta
        bool prev_roll_rotation_valid = false;
        double debug_roll_delta_deg = 0;
        double debug_roll_error_deg = 0;
        double debug_roll_rate_deg_s = 0;
        float ramped_roll_target_deg = 0; // current runtime roll target SAS is being fed, ramps toward settings.roll_program_angle_deg.V

        const float RollProgramCompletionErrorDeg = 2f;   // "close enough" once error drops under this...
        const float RollProgramCompletionOmega = 3f;      // ...AND the vessel has actually stopped spinning (deg/s, roll axis only - debug_roll_rate_deg_s, not GetAngularSpeed()'s unclear units)
        // How fast ramped_roll_target_deg is allowed to move used to be a hardcoded constant here
        // (RollProgramMaxRateDegPerSec, 3 deg/s) - now player-adjustable via
        // settings.roll_program_rate_deg_s (see LiftSettings.cs), since different vessels can
        // likely tolerate different ramp speeds before SAS starts overshooting (see class-level
        // "SLOW ROLL RAMP" comment on why this is ramped at all).

        public override void Start()
        {
            base.Start();

            SASTool.setAutoPilot(AutopilotMode.StabilityAssist);
            last_ap_km = 0;
            ap_km = 0;
            delta_ap_per_second = 0;
            wanted_elevation = -90;

            accumulated_roll_deg = 0;
            prev_roll_rotation_valid = false;
            roll_program_started = false;
            roll_program_done = false;
            debug_roll_delta_deg = 0;
            debug_roll_error_deg = 0;
            ramped_roll_target_deg = 0;
        }

        // Integrates roll (rotation around the nose axis) since launch by measuring, tick-to-tick,
        // how far the dorsal reference actually rotated between the PREVIOUS tick's orientation and
        // THIS tick's - see the class-level comment for why comparing against a captured-at-launch
        // rotation didn't work. This replaced an interim version that instead integrated
        // GetAngularSpeed()'s raw .y component: that version got the DIRECTION right (after negating
        // it, since VesselSAS's own decompiled attitudeAngularVelocity.y sign didn't match this
        // file's Cross(nose,dorsal)*Sin rotation convention) but was wildly off on MAGNITUDE - a full
        // real 360 degree roll only registered as ~3.4 degrees, meaning GetAngularSpeed()'s units
        // aren't plain radians/second the way VesselSAS's internal formula implied. Rather than guess
        // at whatever unit it actually is, this measures rotation directly: the SAME Cross/Dot signed-
        // angle technique already used throughout this file (and TouchDown.ComputeSteeredDirection),
        // applied to two rotations that are only ONE TICK apart. That keeps the sign convention
        // self-consistent with ComputeRollTargetRotation's Rodrigues formula by construction (both are
        // the same right-hand-rule convention around the nose axis - no more separately-guessed sign
        // flip needed), and keeps any Reframed-into-a-moving-frame staleness (see class comment)
        // bounded to a single tick's worth of drift instead of accumulating across the whole flight.
        // Runs continuously from Start() onward (not just while the roll program is actively engaged),
        // so accumulated_roll_deg reflects real roll drift even before the roll program's trigger
        // altitude, matching UpdateRollProgram's "start the ramp from wherever the vessel actually is"
        // initialization below.
        void IntegrateRoll()
        {
            if (current_vessel.VesselComponent == null)
                return;

            Vector up_dir = current_vessel.VesselComponent.gravityForPos;
            Rotation cur_rotation = Rotation.Reframed(current_vessel.GetRotation(), up_dir.coordinateSystem);

            if (prev_roll_rotation_valid)
            {
                Vector3d nose_dir = (cur_rotation.localRotation * Vector3.up).normalized;
                Vector3d prev_dorsal = (prev_roll_rotation.localRotation * Vector3.forward).normalized;
                Vector3d cur_dorsal = (cur_rotation.localRotation * Vector3.forward).normalized;

                Vector3d prev_dorsal_flat = prev_dorsal - nose_dir * Vector3d.Dot(prev_dorsal, nose_dir);
                Vector3d cur_dorsal_flat = cur_dorsal - nose_dir * Vector3d.Dot(cur_dorsal, nose_dir);

                if (prev_dorsal_flat.magnitude > 1e-6 && cur_dorsal_flat.magnitude > 1e-6)
                {
                    prev_dorsal_flat = prev_dorsal_flat.normalized;
                    cur_dorsal_flat = cur_dorsal_flat.normalized;

                    double sign = System.Math.Sign(Vector3d.Dot(Vector3d.Cross(prev_dorsal_flat, cur_dorsal_flat), nose_dir));
                    if (sign == 0) sign = 1;

                    double delta_deg = Vector3d.Angle(prev_dorsal_flat, cur_dorsal_flat) * sign;
                    accumulated_roll_deg += delta_deg;
                    debug_roll_rate_deg_s = (Time.deltaTime > 1e-6) ? delta_deg / Time.deltaTime : 0;
                }
                // else: degenerate this tick (dorsal briefly ~parallel to nose) - skip, keep last
                // accumulated value and rate rather than risk a garbage spike.
            }

            prev_roll_rotation = cur_rotation;
            prev_roll_rotation_valid = true;
        }

        public void computeValues(bool compute_delta_ap_per_second)
        {
            if (current_vessel.VesselComponent == null)
                return;

            // FOURTEENTH follow-up fix (see NOTICE.md): the comment this replaced said the cast to the
            // concrete PatchedConicsOrbit was "VERIFIED" - that was true for orbits in general, but not
            // for the actively-flown vessel. IL/metadata inspection of Assembly-CSharp.dll showed
            // KSP.Sim.impl.PatchedConicsOrbit is NOT the only class implementing KSP.Sim.IKeplerPatch -
            // Redux.Ecs.Components.CurrentPatchedConicsOrbit also implements it, as a completely
            // unrelated sibling class (both extend System.Object directly, neither derives from the
            // other). Redux's ECS layer hands back a CurrentPatchedConicsOrbit for the vessel currently
            // being simulated/flown - exactly this vessel, exactly while the Lift autopilot is running -
            // so the hard cast below threw InvalidCastException on every single Update(), which is why
            // the Lift autopilot didn't work at all. Apoapsis and referenceBody are both members of the
            // IOrbit interface (which IKeplerPatch extends), so no concrete cast is needed - just read
            // them straight off the interface VesselComponent.Orbit already returns.
            IKeplerPatch orbit = current_vessel.VesselComponent.Orbit;
            ap_km = (float)(orbit.Apoapsis - orbit.referenceBody.radius) / 1000;
            current_altitude_km = (float)(current_vessel.GetSeaAltitude() / 1000);

            if (last_ap_km == 0)
            {
                last_ap_km = ap_km;
            }
            else
            {
                float delta_ap = ap_km - last_ap_km;
                last_ap_km = ap_km;
                if (compute_delta_ap_per_second)
                {
                    // compute delta_ap_per_second only on ascent
                    float throttle = (float)current_vessel.GetThrottle();
                    if (throttle > 0.1f && Time.deltaTime != 0)
                    {
                        float new_delta_ap_per_second = delta_ap / (Time.deltaTime * throttle);
                        delta_ap_per_second = Mathf.Lerp(delta_ap_per_second, new_delta_ap_per_second, 0.1f);
                    }
                    else
                        delta_ap_per_second = 0;
                }
            }

            wanted_elevation = ascent_path.compute_elevation(current_altitude_km);
        }

        public void applyDirection()
        {
            var autopilot = current_vessel.Autopilot;

            if (autopilot == null)
                return;

            // force autopilot
            autopilot.Enabled = true;

            var telemetry = SASTool.getTelemetry();
            var up = telemetry.HorizonUp;

            if (settings.heading_correction.V)
            {
                computeSpeedHeading();
            }
            else
                heading_correction = 0;


            Vector3d direction = QuaternionD.Euler(-wanted_elevation, settings.heading.V + heading_correction, 0) * Vector3d.forward;
            Vector direction_vector = new Vector(up.coordinateSystem, direction);

            autopilot.SAS.lockedMode = false;

            if (settings.roll_program.V && roll_program_started && !roll_program_done)
            {
                // Roll program actively ramping toward its target - lock onto a FULL orientation (see
                // class comment on LOCKEDMODE) so SAS drives roll via its well-tuned PidLockedRoll
                // path instead of the crude persistent-target-rotation formula. Only done here, not
                // for the whole rest of the ascent (see "STOP HOLDING ONCE DONE") - once
                // roll_program_done, this falls through to the plain branch below just like the
                // feature being off.
                Rotation target_rotation = ComputeRollTargetRotation(direction, up);
                autopilot.SAS.LockRotation(target_rotation);
                autopilot.SAS.lockedMode = true;
            }
            else
            {
                // Roll program off, hasn't reached its altitude yet, or already done - unchanged
                // stock behavior (roll left free/damped, same as ascent has always behaved without
                // this feature at all).
                autopilot.SAS.SetTargetOrientation(direction_vector, false);
            }
        }

        // Builds the FULL orientation handed to SAS.LockRotation for the roll program - unlike the
        // old SetPersistentTargetOrientation approach (see class comment), lockedMode needs BOTH axes
        // pinned at once: local Y (VesselSAS's "nose") pointing at nose_target, and local Z (VesselSAS's
        // dorsal/roll-reference) pointing at the desired roll direction. Neither FromToRotation nor
        // LookRotation can pin two axes in one call, so RotationFromNoseAndDorsal below builds it
        // directly from an orthonormal basis instead.
        Rotation ComputeRollTargetRotation(Vector3d nose_target, Vector up)
        {
            // Same-tick reframe only - current_vessel.GetRotation() is called and used right here,
            // never stored across ticks, which is the ONLY Reframed usage pattern this codebase has
            // ever empirically proven correct (see the class-level comment on why a captured-at-an-
            // earlier-tick rotation was the problem, not this).
            Rotation cur_rotation = Rotation.Reframed(current_vessel.GetRotation(), up.coordinateSystem);

            // Current dorsal/roll-reference axis, same Vector3.forward convention SAS itself uses
            // (see class comment) - NOT the Vector3.down TouchDown.cs uses for this same
            // GetRotation() source.
            Vector3d current_dorsal = (cur_rotation.localRotation * Vector3.forward).normalized;

            // Flatten against the TARGET nose direction (not the vessel's own current nose) so the
            // result stays a valid roll reference once the vessel actually gets there.
            Vector3d current_dorsal_flat = current_dorsal - nose_target * Vector3d.Dot(current_dorsal, nose_target);
            if (current_dorsal_flat.magnitude < 1e-6)
            {
                // Degenerate (current dorsal ended up ~parallel to the target nose) - fall back to
                // an arbitrary axis perpendicular to the nose so this still produces something
                // well-defined.
                Vector3d arbitrary = new Vector3d(0, 1, 0);
                current_dorsal_flat = arbitrary - nose_target * Vector3d.Dot(arbitrary, nose_target);
                if (current_dorsal_flat.magnitude < 1e-6)
                {
                    arbitrary = new Vector3d(1, 0, 0);
                    current_dorsal_flat = arbitrary - nose_target * Vector3d.Dot(arbitrary, nose_target);
                }
            }
            current_dorsal_flat = current_dorsal_flat.normalized;

            // Rotate that flattened reference around the nose axis by however much MORE roll is
            // still needed - the ramped target minus what's already accumulated since launch
            // (accumulated_roll_deg, integrated in IntegrateRoll) - which is mathematically
            // equivalent to rotating a frozen launch reference by the full ramped angle, but only
            // ever touches THIS tick's current dorsal. Rodrigues' rotation formula, same Cos/Cross/
            // Sin pattern TouchDown.ComputeSteeredDirection already uses for its own pitch/heading
            // rotation. current_dorsal_flat is already perpendicular to nose_target by construction,
            // so the "parallel component" term of the full formula drops out.
            double remaining_roll_deg = ramped_roll_target_deg - accumulated_roll_deg;
            double roll_rad = remaining_roll_deg * (System.Math.PI / 180.0);
            Vector3d desired_dorsal_dir = current_dorsal_flat * System.Math.Cos(roll_rad)
                + Vector3d.Cross(nose_target, current_dorsal_flat) * System.Math.Sin(roll_rad);

            QuaternionD target_local_rotation = RotationFromNoseAndDorsal(nose_target, desired_dorsal_dir);
            return new Rotation(up.coordinateSystem, target_local_rotation);
        }

        // Builds a rotation whose local Y axis (VesselSAS's "nose") points exactly at nose_ex and
        // whose local Z axis (VesselSAS's dorsal/roll-reference) points exactly at dorsal_ez (assumed
        // already close to perpendicular to nose_ex - re-orthogonalized below just in case). This is
        // a standard rotation-matrix-to-quaternion conversion (Shepperd's method) built from plain
        // arithmetic only, deliberately NOT using QuaternionD.FromToRotation or LookRotation - neither
        // of those can pin two axes in a single call, which lockedMode needs (see class comment).
        static QuaternionD RotationFromNoseAndDorsal(Vector3d nose_ex, Vector3d dorsal_ez)
        {
            Vector3d ey = nose_ex.normalized;
            Vector3d ez = (dorsal_ez - ey * Vector3d.Dot(dorsal_ez, ey)).normalized;
            Vector3d ex = Vector3d.Cross(ey, ez).normalized;

            // Standard rotation-matrix-to-quaternion conversion, matrix columns = (ex, ey, ez).
            double m00 = ex.x, m10 = ex.y, m20 = ex.z;
            double m01 = ey.x, m11 = ey.y, m21 = ey.z;
            double m02 = ez.x, m12 = ez.y, m22 = ez.z;

            double trace = m00 + m11 + m22;
            double qx, qy, qz, qw;
            if (trace > 0)
            {
                double s = System.Math.Sqrt(trace + 1.0) * 2.0;
                qw = s / 4.0;
                qx = (m21 - m12) / s;
                qy = (m02 - m20) / s;
                qz = (m10 - m01) / s;
            }
            else if (m00 > m11 && m00 > m22)
            {
                double s = System.Math.Sqrt(1.0 + m00 - m11 - m22) * 2.0;
                qw = (m21 - m12) / s;
                qx = s / 4.0;
                qy = (m01 + m10) / s;
                qz = (m02 + m20) / s;
            }
            else if (m11 > m22)
            {
                double s = System.Math.Sqrt(1.0 + m11 - m00 - m22) * 2.0;
                qw = (m02 - m20) / s;
                qx = (m01 + m10) / s;
                qy = s / 4.0;
                qz = (m12 + m21) / s;
            }
            else
            {
                double s = System.Math.Sqrt(1.0 + m22 - m00 - m11) * 2.0;
                qw = (m10 - m01) / s;
                qx = (m02 + m20) / s;
                qy = (m12 + m21) / s;
                qz = s / 4.0;
            }

            return new QuaternionD(new Vector3d(qx, qy, qz), qw);
        }

        void computeSpeedHeading()
        {
            // use Up local coordinate as reference frame
            var Upcoords = current_vessel.VesselVehicle.Up.coordinateSystem;
            var SurfaceVelocity = Vector.Reframed(current_vessel.VesselVehicle.SurfaceVelocity, Upcoords).vector;
            var North = Vector.Reframed(current_vessel.VesselVehicle.North, Upcoords).vector;
            var Up = current_vessel.VesselVehicle.Up.vector;

            var UpSpeed = Up.normalized * Vector3d.Dot(SurfaceVelocity, Up);
            var LocalHSpeed = SurfaceVelocity - UpSpeed;

       
            h_speed_heading = (float)-Vector3d.SignedAngle(LocalHSpeed.normalized, North, Up);


            heading_correction = GeneralTools.diffAngle(settings.heading.V, h_speed_heading);

            if (heading_correction > 45)
                heading_correction = 45;
            else if (heading_correction < -45)
                heading_correction = -45;
        }

        // Tracks whether the roll program has reached its trigger altitude yet, and keeps the LIFT
        // INFO telemetry rows honest. applyDirection() (above) is what actually drives roll now, via
        // SAS.SetPersistentTargetOrientation - this no longer writes any control input itself.
        void UpdateRollProgram()
        {
            if (!settings.roll_program.V)
            {
                // Toggle is off - stay ready to run again (in case it gets turned on mid-ascent)
                // rather than latching state from a stale earlier attempt.
                roll_program_started = false;
                roll_program_done = false;
                return;
            }

            bool just_engaged = false;

            if (!roll_program_started)
            {
                if (current_altitude_km < settings.roll_program_altitude_km.V)
                    return; // hasn't reached the roll program's altitude yet

                roll_program_started = true;
                just_engaged = true;
            }

            UpdateRollTelemetry();

            if (just_engaged)
            {
                // Start the ramp from wherever the vessel is ACTUALLY rolled to right now
                // (debug_roll_delta_deg, just computed above), not an assumed 0 - avoids a jump the
                // instant the roll program turns on if the vessel had already drifted a bit.
                ramped_roll_target_deg = (float)debug_roll_delta_deg;
                logger.LogInfo($"[Ascent] Roll program engaged at {current_altitude_km:n2} km, " +
                    $"currently {ramped_roll_target_deg:n1}° from launch, ramping to " +
                    $"{settings.roll_program_angle_deg.V:n1}°.");
            }

            // Advance the ramped target toward the configured angle at a limited rate, so SAS is
            // never handed a big instantaneous roll error (see class-level "SLOW ROLL RAMP" comment).
            float remaining = GeneralTools.diffAngle(settings.roll_program_angle_deg.V, ramped_roll_target_deg);
            float max_step = settings.roll_program_rate_deg_s.V * Time.deltaTime;
            if (Mathf.Abs(remaining) <= max_step)
                ramped_roll_target_deg = settings.roll_program_angle_deg.V;
            else
                ramped_roll_target_deg += Mathf.Sign(remaining) * max_step;
        }

        // Purely informational - reads how far the vessel has actually rolled from its launch
        // orientation (accumulated_roll_deg, integrated continuously in IntegrateRoll - see the
        // class-level comment for why this replaced comparing a captured launch Rotation against
        // the current one) for the LIFT INFO table, and decides whether the roll program has
        // actually settled.
        void UpdateRollTelemetry()
        {
            debug_roll_delta_deg = accumulated_roll_deg;

            float roll_error_deg = GeneralTools.diffAngle(settings.roll_program_angle_deg.V, (float)accumulated_roll_deg);
            debug_roll_error_deg = roll_error_deg;

            // Was current_vessel.GetAngularSpeed().vector.magnitude < RollProgramCompletionOmega
            // (threshold in assumed rad/s) - now that IntegrateRoll's ~105x calibration mismatch has
            // shown GetAngularSpeed()'s units aren't plain rad/s, that check couldn't be trusted
            // either (it would likely have been satisfied even while genuinely still spinning at a
            // real few degrees/second, since the API under-reports by the same ~100x). Uses
            // debug_roll_rate_deg_s instead - the SAME tick-to-tick measurement IntegrateRoll already
            // computes and that this file's own math trusts for everything else.
            roll_program_done = Mathf.Abs(roll_error_deg) < RollProgramCompletionErrorDeg && Mathf.Abs((float)debug_roll_rate_deg_s) < RollProgramCompletionOmega;
        }

        // Ascent has no narrative feedback of its own beyond LiftUI's generic "Status : Ascent"
        // headline - all its telemetry moved to UpdateInfoRows below (LIFT INFO table) instead of
        // scrolling through the console text.
        public override void updateUI(VisualElement root_el, FullStatus st)
        {
        }

        public override void UpdateInfoRows(System.Action<string, string> addRow)
        {
            addRow("Apoapsis Alt.", $"{ap_km:n2} km");
            addRow("Altitude", $"{current_altitude_km:n2} km");
            addRow("Climb Rate", $"{delta_ap_per_second:n2} km/s");
            addRow("Pitch Target", $"{wanted_elevation:n2} °");
            addRow("Throttle", $"{wanted_throttle:n2}");

            if (settings.heading_correction.V)
            {
                addRow("Surface Heading", $"{h_speed_heading:n2} °");
                addRow("Heading Correction", $"{heading_correction:n2} °");
            }

            if (settings.roll_program.V)
            {
                string roll_status = roll_program_done ? "Done" : (roll_program_started ? "Active" : "Waiting for Altitude");
                addRow("Roll Program", roll_status);
                addRow("Roll (from launch)", $"{debug_roll_delta_deg:n1} °");
                addRow("Roll Error", $"{debug_roll_error_deg:n1} °");
                addRow("Roll Dbg: RollRate", $"{debug_roll_rate_deg_s:n2} °/s");
            }
        }

        public override void Update()
        {
            IntegrateRoll();
            UpdateRollProgram();
            applyDirection();
            finished = false;
            float remaining_Ap = settings.destination_Ap_km.V - ap_km;
            if (remaining_Ap <= settings.end_ascent_error)
            {
                finished = true;
                return;
            }
            else
            {
                if (delta_ap_per_second <= 0)
                {
                    wanted_throttle = settings.max_throttle.V;
                }
                else
                {
                    wanted_throttle = remaining_Ap / delta_ap_per_second;
                    if (wanted_throttle > settings.max_throttle.V)
                        wanted_throttle = settings.max_throttle.V;
                }

                current_vessel.SetThrottle(wanted_throttle);
            }
        }


    }
}
