using System;
using K2D2.KSPService;
using K2D2.Landing;
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
    // Gravity Turn ascent profile (see AscentProfile.cs), the alternative to Ascent.cs's fixed
    // altitude-based pitch curve ("Guided").
    //
    // An original implementation of the general gravity turn technique (rise vertically, kick
    // over, then follow prograde while throttling to shape the climb). It does not port or mirror
    // other mods' code, in particular linuxgurugamer's GPL-3.0 "GravityTurn" mod, whose license is
    // incompatible with K2D2Redux's CC-BY-SA 4.0 (see NOTICE.md).
    //
    // The throttle and steering rules live in GravityTurnControl. This class feeds them the
    // vessel's state and applies what they ask for. With Auto Kick & Hold on, it also measures the
    // rocket's drag on the way up and, when the kick speed is reached, has AscentPredictor try the
    // kick angles and hold times and pick the cheapest to orbit; the sliders are the fallback.
    //
    // Finishes under the same condition as Ascent.cs (remaining apoapsis within end_ascent_error),
    // so Coasting/Adjust/FinalCircularize don't need to know which profile flew the ascent.
    public class ReactiveAscent : AscentControllerBase
    {
        public ILogger logger = ReduxLib.ReduxLib.GetLogger("K2D2.ReactiveAscent");

        LiftSettings settings = null;
        KSPVessel current_vessel;
        readonly BurndV engines = new BurndV();
        readonly GravityTurnControl control = new GravityTurnControl();

        public ReactiveAscent(LiftSettings lift_settings)
        {
            current_vessel = K2D2_Plugin.Instance.current_vessel;
            this.settings = lift_settings;
        }

        // Candidates tried per frame while planning (each is one or two short test flights). All
        // of them together take tens of milliseconds, so they're spread out to avoid a stutter;
        // the rocket keeps rising straight up for the fraction of a second that takes.
        const int PlanBudgetPerFrame = 2;

        enum Plan { Waiting, Planning, Auto, Sliders }
        Plan plan = Plan.Sliders;
        string plan_note = "";
        AscentPredictor predictor;
        float auto_kick_deg, auto_hold_s;

        public float time_to_apoapsis_s = 0;
        bool time_to_apoapsis_known = false;

        GravityTurnControl.Inputs inputs;
        float twr = 0;
        bool landed = false;

        // Hottest part relative to its own temperature limit, checked a few times a second.
        const float HeatCheckS = 0.25f;
        float next_heat_check_s;
        float heat_fraction;
        string hottest_part = "";

        // Climb rate last measured with enough throttle to measure it (delta_ap_per_second drops to
        // 0 below 10% throttle, which would switch the taper off right at the end).
        float taper_rate = 0;

        float heading_correction_deg = 0;
        float h_speed_heading_deg = 0;

        // Drag measured during the vertical rise: drag force / (density * airspeed^2), averaged with
        // more weight where the air pushes harder (and the measurement is less noisy).
        ImpactModel drag_model;
        Vector3d previous_velocity;
        double previous_UT = double.NaN;
        float previous_thrust_accel;
        double drag_area_sum, drag_weight;
        const double DragSampleS = 0.25;

        // Flight log every few seconds, to compare with the predicted path the plan logs.
        const float LogIntervalS = 5;
        float flight_time_s, next_log_s, plan_time_s;
        bool finish_logged;

        public override void Start()
        {
            base.Start();
            flight_time_s = next_log_s = plan_time_s = 0;
            finish_logged = false;

            control.Reset();
            plan = settings.gravity_turn_auto.V ? Plan.Waiting : Plan.Sliders;
            plan_note = plan == Plan.Sliders ? "sliders" : "";
            predictor = null;
            time_to_apoapsis_known = false;
            taper_rate = 0;
            heading_correction_deg = 0;
            h_speed_heading_deg = 0;
            last_ap_km = 0;
            ap_km = 0;
            delta_ap_per_second = 0;
            drag_model = null;
            previous_UT = double.NaN;
            drag_area_sum = drag_weight = 0;
        }

        public override void computeValues(bool compute_delta_ap_per_second)
        {
            var vessel = current_vessel.VesselComponent;
            if (vessel == null)
                return;

            UpdateBasicTelemetry(current_vessel, compute_delta_ap_per_second);
            ApplySettings();
            if (delta_ap_per_second > 0)
                taper_rate = delta_ap_per_second;

            IKeplerPatch orbit = vessel.Orbit;
            var body = orbit.referenceBody;
            double now = GeneralTools.Current_UT;
            Vector3d r = orbit.GetRelativePositionAtUTZup(now);
            Vector3d v = orbit.GetOrbitalVelocityAtUTZup(now);

            float raw_time_to_apoapsis_s = (float)GravityTurnControl.SignedTimeToApoapsis(r, v, body.gravParameter);
            // Lightly smoothed: under thrust the prediction moves a little every frame.
            time_to_apoapsis_s = time_to_apoapsis_known
                ? Mathf.Lerp(time_to_apoapsis_s, raw_time_to_apoapsis_s, 0.25f)
                : raw_time_to_apoapsis_s;
            time_to_apoapsis_known = true;

            double r_mag = r.magnitude;
            Vector3d up = r / r_mag;
            double v_up = Vector3d.Dot(v, up);
            double v_h = (v - up * v_up).magnitude;
            float gravity = (float)(body.gravParameter / (r_mag * r_mag));

            engines.Compute_Thrust();
            twr = gravity > 0 ? engines.active_full_dv / gravity : 0;
            landed = vessel.Landed;

            inputs = new GravityTurnControl.Inputs
            {
                vertical_speed = (float)current_vessel.VesselVehicle.VerticalSpeed,
                prograde_elevation_deg = ComputeProgradeElevation(),
                orbital_prograde_elevation_deg = (float)(Math.Atan2(v_up, v_h) * 180 / Math.PI),
                atmosphere_fraction = body.hasAtmosphere ? (float)(vessel.AltitudeFromSeaLevel / body.atmosphereDepth) : 2,
                heat_fraction = HeatFraction(vessel),
                time_to_apoapsis_s = time_to_apoapsis_s,
                gravity = gravity,
                // Gravity less what horizontal speed takes off it; the floor keeps the hold sane
                // close to orbital speed.
                effective_gravity = Mathf.Max((float)(gravity - v_h * v_h / r_mag), 0.05f * gravity),
                max_accel = engines.active_full_dv,
                dynamic_pressure_kpa = (float)vessel.DynamicPressure_kPa,
                remaining_ap_km = settings.destination_Ap_km.V - ap_km,
                end_error_km = settings.end_ascent_error,
                taper_rate = taper_rate,
            };
        }

        float HeatFraction(VesselComponent vessel)
        {
            if (Time.unscaledTime < next_heat_check_s)
                return heat_fraction;
            next_heat_check_s = Time.unscaledTime + HeatCheckS;

            heat_fraction = 0;
            var parts = vessel.SimulationObject?.PartOwner?.Parts;
            if (parts == null)
                return heat_fraction;
            foreach (var part in parts)
            {
                if (part == null || part.MaxTemp <= 0)
                    continue;
                float fraction = (float)(part.Temperature / part.MaxTemp);
                if (fraction > heat_fraction)
                {
                    heat_fraction = fraction;
                    hottest_part = part.PartName;
                }
            }
            return heat_fraction;
        }

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
            if (telemetry == null)
                return;
            var up = telemetry.HorizonUp;

            Vector3d direction = QuaternionD.Euler(-elevation_deg, settings.heading.V + heading_correction_deg, 0) * Vector3d.forward;
            autopilot.SAS.SetTargetOrientation(new Vector(up.coordinateSystem, direction), false);
        }

        // Same decomposition as Ascent.cs's computeSpeedHeading().
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
            heading_correction_deg = Mathf.Clamp(heading_correction_deg, -45f, 45f) * HeadingCorrectionWeight(LocalHSpeed.magnitude);
        }

        // Prograde's pitch above the horizon, from the surface velocity (what the air sees).
        float ComputeProgradeElevation()
        {
            var Upcoords = current_vessel.VesselVehicle.Up.coordinateSystem;
            Vector3d surface_velocity = Vector.Reframed(current_vessel.VesselVehicle.SurfaceVelocity, Upcoords).vector;
            Vector3d up = current_vessel.VesselVehicle.Up.vector.normalized;

            double up_speed = Vector3d.Dot(surface_velocity, up);
            Vector3d local_h_speed = surface_velocity - up * up_speed;

            // No horizontal speed yet: straight up (or down).
            if (local_h_speed.magnitude < 1e-3)
                return up_speed >= 0 ? 90f : -90f;

            float elevation_deg = (float)Vector3d.Angle(surface_velocity, local_h_speed);
            if (up_speed < 0)
                elevation_deg = -elevation_deg;

            return elevation_deg;
        }

        // Settings are read every frame so slider changes still apply mid-flight; the kick angle and
        // hold time come from the plan once there is one.
        void ApplySettings()
        {
            control.kick_speed_mps = settings.gravity_turn_kick_speed_mps.V;
            control.kick_deg = plan == Plan.Auto ? auto_kick_deg : settings.gravity_turn_pitch_kick_deg.V;
            control.hold_time_s = plan == Plan.Auto ? auto_hold_s : settings.gravity_turn_hold_time_s.V;
            control.max_throttle = settings.max_throttle.V;
            control.min_throttle = settings.gravity_turn_min_throttle.V;
            control.max_twr = settings.gravity_turn_max_twr.V;
            control.max_q_kpa = settings.gravity_turn_max_q_kpa.V;
        }

        public override void Update()
        {
            finished = false;

            flight_time_s += Time.deltaTime;

            float remaining_Ap = settings.destination_Ap_km.V - ap_km;
            if (remaining_Ap <= settings.end_ascent_error)
            {
                current_vessel.SetThrottle(0);
                finished = true;
                if (!finish_logged)
                {
                    finish_logged = true;
                    var orbit = current_vessel.VesselComponent.Orbit;
                    logger.LogInfo($"[GravityTurn] Ascent done at +{flight_time_s:n0}s: altitude {current_altitude_km:n1} km, " +
                        $"apoapsis {ap_km:n1} km, periapsis {(orbit.Periapsis - orbit.referenceBody.radius) / 1000:n1} km");
                }
                return;
            }

            if (control.phase == GravityTurnControl.Phase.VerticalRise)
            {
                MeasureDrag();
                if (inputs.vertical_speed >= control.kick_speed_mps && UpdatePlan())
                {
                    // Still planning: keep going straight up.
                    PointAt(90);
                    current_vessel.SetThrottle(control.throttle);
                    return;
                }
            }

            control.Update(inputs, Time.deltaTime);
            PointAt(control.target_elevation_deg);
            current_vessel.SetThrottle(control.throttle);
            LogFlight();
        }

        void LogFlight()
        {
            if (flight_time_s < next_log_s)
                return;
            next_log_s = flight_time_s + LogIntervalS;
            var vessel = current_vessel.VesselComponent;
            logger.LogInfo($"[GravityTurn] +{flight_time_s:n0}s {control.phase} {current_altitude_km:n1}km " +
                $"{current_vessel.VesselVehicle.SurfaceSpeed:n0}m/s vs {inputs.vertical_speed:n0} pro {inputs.prograde_elevation_deg:n1}° " +
                $"target {control.target_elevation_deg:n1}° thr {control.throttle:n2} ({control.throttle_limit}) " +
                $"tAp {time_to_apoapsis_s:n1}s ap {ap_km:n1}km q {inputs.dynamic_pressure_kpa:n1} twr {twr:n2} mass {vessel.totalMass:n2}t " +
                $"heat {inputs.heat_fraction:p0} {hottest_part}{(control.heat_guard ? " GUARD" : "")}{(control.thin_air ? " orbital" : "")}");
        }

        void MeasureDrag()
        {
            var vessel = current_vessel.VesselComponent;
            var body = vessel.mainBody;
            if (!body.hasAtmosphere)
                return;

            double now = GeneralTools.Current_UT;
            if (drag_model == null || drag_model.body != body)
            {
                drag_model = new ImpactModel(body, now);
                previous_UT = double.NaN;
            }
            drag_model.StateAt(vessel.Orbit, now, out Vector3d r, out Vector3d v);
            float thrust_accel = engines.actual_dv;

            if (double.IsNaN(previous_UT))
            {
                previous_velocity = v;
                previous_UT = now;
                previous_thrust_accel = thrust_accel;
                return;
            }
            double dt = now - previous_UT;
            if (dt < DragSampleS)
                return;

            // Acceleration from everything but gravity, along the airflow. Going straight up, the
            // thrust points the same way, so what's missing from it is the drag.
            Vector3d gravity = r * (-body.gravParameter / Math.Pow(r.magnitude, 3));
            Vector3d accel = (v - previous_velocity) / dt - gravity;
            Vector3d air = drag_model.AirVelocity(r, v);
            double air_speed = air.magnitude;
            double altitude = vessel.AltitudeFromSeaLevel;
            double rho = altitude < body.atmosphereDepth
                ? body.GetDensity(body.GetPressure(altitude), body.GetTemperature(altitude))
                : 0;

            if (air_speed > 30 && rho > 0)
            {
                double drag_accel = (thrust_accel + previous_thrust_accel) / 2 - Vector3d.Dot(accel, air) / air_speed;
                if (drag_accel > 0)
                {
                    double weight = rho * air_speed * air_speed;
                    drag_area_sum += drag_accel * vessel.totalMass / (rho * air_speed * air_speed) * weight;
                    drag_weight += weight;
                }
            }

            previous_velocity = v;
            previous_UT = now;
            previous_thrust_accel = thrust_accel;
        }

        // Runs the auto plan at the kick speed. True while it's still working.
        bool UpdatePlan()
        {
            if (plan == Plan.Waiting)
            {
                var vessel = current_vessel.VesselComponent;
                var body = vessel.mainBody;
                if (body.hasAtmosphere && drag_weight <= 0)
                {
                    UseSliders("no drag measurement - raise Kick Speed");
                    return false;
                }

                double now = GeneralTools.Current_UT;
                var model = new ImpactModel(body, now);
                model.StateAt(vessel.Orbit, now, out Vector3d r, out Vector3d v);
                Vector3d air = model.AirVelocity(r, v);
                Vector3d up = r.normalized;
                double vertical = Vector3d.Dot(air, up);
                Vector3d spin = v - air;
                Vector3d spin_horizontal = spin - up * Vector3d.Dot(spin, up);

                var start = new AscentPredictor.Start
                {
                    radius = r.magnitude,
                    vertical_speed = vertical,
                    horizontal_speed = (air - up * vertical).magnitude,
                    // The surface moves east; only its part along the heading is in the climb's plane.
                    rotation_speed = spin_horizontal.magnitude * Math.Sin(settings.heading.V * Math.PI / 180),
                    mass = vessel.totalMass,
                    full_thrust = engines.active_full_dv * vessel.totalMass,
                    drag_area = drag_weight > 0 ? drag_area_sum / drag_weight : 0,
                };
                predictor = new AscentPredictor(vessel, start, control, settings.destination_Ap_km.V, settings.end_ascent_error);
                plan_time_s = flight_time_s;
                logger.LogInfo($"[GravityTurn] Planning at +{flight_time_s:n0}s: {current_altitude_km:n2} km, vertical {start.vertical_speed:n0} m/s, " +
                    $"spin {start.rotation_speed:n0} m/s, mass {start.mass:n2} t, thrust {start.full_thrust:n0} kN, drag area {start.drag_area:g3}");
                plan = Plan.Planning;
                plan_note = "planning...";
            }

            if (plan != Plan.Planning)
                return false;

            predictor.Continue(PlanBudgetPerFrame);
            if (!predictor.done)
                return true;

            if (predictor.found)
            {
                auto_kick_deg = predictor.best_kick_deg;
                auto_hold_s = predictor.best_hold_s;
                plan = Plan.Auto;
                plan_note = "auto";
                logger.LogInfo($"[GravityTurn] Auto plan at +{plan_time_s:n0}s: kick {auto_kick_deg:n1}°, hold {auto_hold_s:n0} s, " +
                    $"predicted {predictor.best_delta_v:n0} m/s to orbit (drag area {drag_area_sum / Math.Max(drag_weight, 1e-12):g3})");
                logger.LogInfo($"[GravityTurn] Stages: {predictor.StageSummary()}");
                logger.LogInfo($"[GravityTurn] Game stages:{AscentPredictor.RawStages(current_vessel.VesselComponent)}");
                var trace = new System.Text.StringBuilder();
                predictor.Fly(auto_kick_deg, auto_hold_s, 1, trace);
                logger.LogInfo($"[GravityTurn] Predicted path from the plan:{trace}");
            }
            else
                UseSliders(predictor.failure);
            predictor = null;
            return false;
        }

        void UseSliders(string why)
        {
            plan = Plan.Sliders;
            plan_note = "sliders: " + why;
            logger.LogInfo($"[GravityTurn] Auto plan not used ({why}); flying the sliders: kick " +
                $"{settings.gravity_turn_pitch_kick_deg.V:n1}°, hold {settings.gravity_turn_hold_time_s.V:n0} s");
            var vessel = current_vessel.VesselComponent;
            if (vessel != null)
                logger.LogInfo($"[GravityTurn] Game stages:{AscentPredictor.RawStages(vessel)}");
        }

        public override void updateUI(VisualElement root_el, FullStatus st)
        {
            if (plan == Plan.Planning)
            {
                st.Console("Planning the turn...");
                return;
            }
            if (control.phase != GravityTurnControl.Phase.VerticalRise || !landed)
                return;

            if (inputs.max_accel <= 0)
                st.Console("No engine running yet - stage to lift off.");
            else if (twr < 1)
                st.Console($"TWR {twr:n2}: not enough thrust to lift off.");
        }

        public override void UpdateInfoRows(System.Action<string, string> addRow)
        {
            addRow("Phase", control.phase.ToString());
            addRow("Kick / Hold", $"{control.kick_deg:n1} ° / {control.hold_time_s:n0} s" + (plan_note != "" ? $" ({plan_note})" : ""));
            addRow("Apoapsis Alt.", $"{ap_km:n2} km");
            addRow("Time to Apoapsis", $"{time_to_apoapsis_s:n1} s");
            addRow("Throttle", $"{control.throttle:n2} ({control.throttle_limit})");

            // The rest is for tuning and bug reports.
            if (!K2D2Settings.debug_mode.V)
                return;

            addRow("Altitude", $"{current_altitude_km:n2} km");
            addRow("Angle of Attack", $"{control.target_elevation_deg - inputs.prograde_elevation_deg:n1} °");
            addRow("TWR", $"{twr:n2}");
            addRow("Dynamic Pressure", $"{inputs.dynamic_pressure_kpa:n1} kPa");
            addRow("Prograde", control.thin_air ? "orbital (thin air)" : "surface");
            addRow("Hottest Part", $"{inputs.heat_fraction:p0} of its limit" + (control.heat_guard ? " - nose up to cool" : ""));

            if (settings.heading_correction.V)
            {
                addRow("Surface Heading", $"{h_speed_heading_deg:n2} °");
                addRow("Heading Correction", $"{heading_correction_deg:n2} °");
            }
        }
    }
}
