using System;
using K2D2.KSPService;
using K2UI;
using KSP.Sim;
using UnityEngine;
using UnityEngine.UIElements;
using K2D2.UI;
using K2D2.Controller;

using KTools;
using ILogger = SpaceWarp2.API.Logging.ILogger;

namespace K2D2.Landing
{
    // Holds surface retrograde and throttles to keep the surface speed under max_speed, with
    // optional gravity compensation.
    public class TouchDown : ExecuteController
    {
        public ILogger logger = SpaceWarp2.API.Logging.LogManager.GetLogger("K2D2.TouchDown");

        public bool gravity_compensation;
        public float max_speed = 0;

        // Atmo/Vacuum profile split (see LandingSettings.cs's class comment): two full copies of
        // these tunables, each against its own SettingsFile, selected through the pass-through
        // properties below. Only the UI binding (SetupFullUI/SetupBasicUI) reaches into `atmo`/`vac`
        // directly, since one-time UI binding must not follow the current profile.
        class TouchDownSettings
        {
            public ClampSetting<float> touch_down_max_angle;

            // How fast the engine throttle itself is allowed to change, in fraction/second (5 = 0
            // to full in 0.2s). No player-facing slider in either profile.
            public ClampSetting<float> max_throttle_rate_per_sec;

            public TouchDownSettings(SettingsFile file)
            {
                touch_down_max_angle = new("land.touch_down_max_angle", 30, 0, 45, file);
                max_throttle_rate_per_sec = new("land.max_throttle_rate_per_sec", 5, 1, 20, file);
            }
        }

        TouchDownSettings atmo;
        TouchDownSettings vac;

        public ClampSetting<float> touch_down_max_angle =>
            LandingProfile.IsAtmospheric ? atmo.touch_down_max_angle : vac.touch_down_max_angle;
        public ClampSetting<float> max_throttle_rate_per_sec =>
            LandingProfile.IsAtmospheric ? atmo.max_throttle_rate_per_sec : vac.max_throttle_rate_per_sec;

        // Called once, ever, from LandingUI.onInit() - see the comment on TouchDownSettings above.
        public void SetupFullUI(VisualElement root)
        {
            root.Q<K2Slider>("touch_down_max_angle_vac").Bind(vac.touch_down_max_angle);
        }

        public void SetupBasicUI(VisualElement root)
        {
            root.Q<K2Slider>("touch_down_max_angle_atmo").Bind(atmo.touch_down_max_angle);
        }

        // compute_Throttle() below recomputes wanted_throttle fresh every frame from delta_speed,
        // and every time the Pause<->Brake cycle (see LandingPilot.Update()) restarts a burn, that
        // jumps straight from 0 to whatever's needed, which reads as the throttle blipping to 100%.
        // max_throttle_rate_per_sec (see TouchDownSettings above) doesn't touch how much total dV
        // gets used, just smooths the ramp - fast enough that it shouldn't cost any real stopping
        // distance in an actual emergency, just the sudden all-or-nothing snap.
        float smoothed_throttle = 0;

        KSPVessel current_vessel;
        BurndV burn_dV = new BurndV();

        // float gravity_inclination = 0;
        float gravity_direction_factor = 0;
        float gravity;

        float wanted_throttle = 0;

        public TouchDown(SettingsFile atmo_file, SettingsFile vac_file)
        {
            atmo = new TouchDownSettings(atmo_file);
            vac = new TouchDownSettings(vac_file);
            sub_contollers.Add(burn_dV);
            // logger.LogMessage("LandingController !");
            current_vessel = K2D2_Plugin.Instance.current_vessel;
        }

        // Not called by LandingPilot - Brake and TouchDown share this controller as one continuous
        // burn, so its state deliberately carries over between them.
        public override void Start()
        {
            finished = false;
            smoothed_throttle = 0;
        }

        public void computeGravityRatio()
        {
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

        float retrograde_angle;

        public bool checkDirection()
        {

            var telemetry = SASTool.getTelemetry();
            if (telemetry == null)
                return false;

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

            SASTool.setAutoPilot(AutopilotMode.Retrograde);

            Rotation vessel_rotation = current_vessel.GetRotation();

            // convert rotation to maneuver coordinates
            vessel_rotation = Rotation.Reframed(vessel_rotation, retro_dir.coordinateSystem);
            Vector3d forward_direction = (vessel_rotation.localRotation * Vector3.up).normalized;

            retrograde_angle = (float)Vector3d.Angle(retro_dir.vector, forward_direction);
            status_line = $"Waiting for Vessel rotation\nAngle = {retrograde_angle:n2}°";

            return retrograde_angle < touch_down_max_angle.V;
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

        // Numbers for the Landing tab's info table (see LandingUI.updateContext). status_line is
        // left to checkDirection's "Waiting for ..." messages.
        public override void UpdateInfoRows(System.Action<string, string> addRow)
        {
            if (K2D2Settings.debug_mode.V)
            {
                addRow("Max Speed", $"{max_speed:n2} m/s");
                addRow("Delta Speed", $"{delta_speed:n2} m/s");

                if (gravity_compensation)
                {
                    addRow("Gravity", $"{gravity:n2}");
                    addRow("Gravity Dir. Factor", $"{gravity_direction_factor:n2}");
                }

                addRow("Wanted Throttle", $"{wanted_throttle:n2}");
                addRow("Actual Throttle", $"{smoothed_throttle:n2}");
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
