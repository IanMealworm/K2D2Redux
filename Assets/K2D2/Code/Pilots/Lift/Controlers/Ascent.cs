
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

        // ROLL PROGRAM (see LiftSettings.roll_program* for the player-facing settings). Corrects the
        // vessel's roll relative to its OWN launch orientation - not an absolute compass/navball
        // heading - since an asymmetric build can spawn on the pad rolled away from the orientation
        // it was designed for.
        //
        // Roll is driven through SAS.LockRotation + lockedMode = true rather than
        // SetPersistentTargetOrientation: the latter's roll formula
        // (ComputePersistentTargetRollResponse in VesselSAS.cs) is a fixed-gain P+D loop with no
        // auto-tuning, while lockedMode routes all three axes through the same auto-tuned PID
        // (PidLockedRoll) already used for pitch/yaw. ComputeRollTargetRotation below builds a full
        // orientation (nose and dorsal/roll-reference pinned at once, via RotationFromNoseAndDorsal)
        // since neither FromToRotation nor LookRotation can pin two axes in a single call.
        //
        // The target ramps toward the configured angle at a limited rate (ramped_roll_target_deg,
        // settings.roll_program_rate_deg_s) instead of being commanded instantly - SAS's roll
        // response saturates on any large instantaneous error, which overshoots and oscillates. The
        // lockedMode target is only held while the program is actively ramping
        // (roll_program_started && !roll_program_done); control reverts to plain
        // SetTargetOrientation once settled, matching stock free/damped roll behavior.
        //
        // Roll traveled since launch (accumulated_roll_deg) is integrated tick-to-tick from the
        // dorsal reference's rotation delta (IntegrateRoll), rather than compared against a rotation
        // captured once at launch: Rotation.Reframed's target coordinate system is a per-position,
        // local-horizon-style frame, so reframing a rotation captured many ticks (and positions)
        // earlier and comparing it to the current one does not produce a valid delta. Reframing and
        // using the result immediately, same tick, is the pattern used elsewhere in this codebase
        // (TouchDown, TurnTo, DockingTurnTo) and the only one confirmed to work here.
        //
        // GetAngularSpeed()'s reported units are not plain radians/second, so roll rate
        // (debug_roll_rate_deg_s) is derived from the same tick-to-tick angle delta instead of that
        // API.
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
        const float RollProgramCompletionOmega = 3f;      // ...AND the vessel has stopped spinning (deg/s, roll axis only - debug_roll_rate_deg_s, not GetAngularSpeed())
        // Ramp rate is player-adjustable (settings.roll_program_rate_deg_s, see LiftSettings.cs)
        // since different vessels can tolerate different ramp speeds before SAS overshoots.

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

        // Integrates roll around the nose axis since launch by measuring, tick-to-tick, how far the
        // dorsal reference actually rotated between the previous tick's orientation and this tick's
        // (see the class comment for why comparing against a launch-time snapshot doesn't work).
        // Uses the same Cross/Dot signed-angle technique as ComputeRollTargetRotation and
        // TouchDown.ComputeSteeredDirection, applied to two rotations one tick apart, so the sign
        // convention stays consistent by construction and any Reframed staleness is bounded to a
        // single tick. Runs continuously from Start() onward, not just while the roll program is
        // engaged, so accumulated_roll_deg reflects real drift even before the program's trigger
        // altitude.
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

            // Reads Apoapsis/referenceBody through the IKeplerPatch/IOrbit interface rather than
            // casting to the concrete PatchedConicsOrbit type: Redux's ECS layer hands back
            // Redux.Ecs.Components.CurrentPatchedConicsOrbit for the actively-flown vessel, an
            // unrelated sibling class that also implements IKeplerPatch, so a hard cast throws
            // InvalidCastException for exactly the vessel this autopilot is flying.
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
                // Roll program actively ramping toward its target - lock onto a full orientation (see
                // class comment) so SAS drives roll via the auto-tuned PidLockedRoll path. Only held
                // while ramping; once roll_program_done this falls through to the plain branch below.
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

        // Builds the full orientation handed to SAS.LockRotation for the roll program: lockedMode
        // needs both axes pinned at once - local Y (SAS's "nose") at nose_target, local Z (SAS's
        // dorsal/roll-reference) at the desired roll direction. Neither FromToRotation nor
        // LookRotation can pin two axes in one call, so RotationFromNoseAndDorsal builds it directly
        // from an orthonormal basis.
        Rotation ComputeRollTargetRotation(Vector3d nose_target, Vector up)
        {
            // Same-tick reframe: GetRotation() is read and used immediately, never stored across
            // ticks (see class comment on why comparing against an earlier-tick rotation fails).
            Rotation cur_rotation = Rotation.Reframed(current_vessel.GetRotation(), up.coordinateSystem);

            // Current dorsal/roll-reference axis - Vector3.forward, matching SAS's own convention.
            // (TouchDown.cs uses Vector3.down for this same rotation source, for its tail-first
            // landing-burn framing; that doesn't apply here.)
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

            // Rotate the flattened dorsal reference around the nose axis by the remaining roll
            // needed (ramped target minus accumulated_roll_deg). Rodrigues' rotation formula, same
            // Cos/Cross/Sin pattern as TouchDown.ComputeSteeredDirection. current_dorsal_flat is
            // already perpendicular to nose_target, so the formula's parallel-component term drops
            // out.
            double remaining_roll_deg = ramped_roll_target_deg - accumulated_roll_deg;
            double roll_rad = remaining_roll_deg * (System.Math.PI / 180.0);
            Vector3d desired_dorsal_dir = current_dorsal_flat * System.Math.Cos(roll_rad)
                + Vector3d.Cross(nose_target, current_dorsal_flat) * System.Math.Sin(roll_rad);

            QuaternionD target_local_rotation = RotationFromNoseAndDorsal(nose_target, desired_dorsal_dir);
            return new Rotation(up.coordinateSystem, target_local_rotation);
        }

        // Builds a rotation whose local Y axis ("nose") points at nose_ex and local Z axis
        // (dorsal/roll-reference) points at dorsal_ez (re-orthogonalized below in case the inputs
        // aren't exactly perpendicular). Standard rotation-matrix-to-quaternion conversion
        // (Shepperd's method) rather than QuaternionD.FromToRotation/LookRotation, since neither can
        // pin two axes in one call.
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

        // Tracks whether the roll program has reached its trigger altitude, and advances the ramped
        // target toward the configured angle. Only updates state; applyDirection() actually commands
        // SAS.
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

        // Reads accumulated roll (accumulated_roll_deg, integrated in IntegrateRoll) for the LIFT
        // INFO table, and decides whether the roll program has settled.
        void UpdateRollTelemetry()
        {
            debug_roll_delta_deg = accumulated_roll_deg;

            float roll_error_deg = GeneralTools.diffAngle(settings.roll_program_angle_deg.V, (float)accumulated_roll_deg);
            debug_roll_error_deg = roll_error_deg;

            // Uses debug_roll_rate_deg_s (IntegrateRoll's tick-to-tick measurement) rather than
            // GetAngularSpeed(), whose reported units are not plain rad/s here.
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
