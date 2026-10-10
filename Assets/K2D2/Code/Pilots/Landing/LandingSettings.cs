using KTools;
using UnityEngine.UIElements;
using K2UI;

namespace K2D2.Landing
{
    // Atmo/Vacuum profile split: LandingPilot constructs TWO of these (settings_atmo/
    // settings_vac), one per SettingsFile (k2d2_landing_atmo.json / k2d2_landing_vac.json), so
    // tuning one profile can never touch the other's saved values or UI. Every field keeps its
    // original key name ("land.xxx") in both files, since the files themselves are what keeps the
    // two profiles apart.
    //
    // The UI side is split into two methods: setupUI (the full binding, used for the Vacuum
    // panel) and setupBasicUI (a smaller subset for the Atmo panel: warp timing, brake timing, and
    // touchdown speed profile - the settings that are actually meaningful on an atmospheric body
    // today). Target lat/lon, the precision landing toggle and plane trim all still exist as real,
    // independently-persisted settings on the Atmo profile too - they're just not wired to a
    // visible control yet, since atmospheric precision landing isn't built.
    // TouchDown's own tunables (touch_down_max_angle etc.) are bound from TouchDown.cs's own
    // SetupFullUI/SetupBasicUI, not here.
    public class LandingSettings
    {
        public Setting<bool> auto_warp;
        public ClampSetting<float> burn_before;
        public Setting<int> rotation_warp_duration;
        // Warp with check of rotation
        public ClampSetting<float> max_rotation;

        public float brake_speed
        {
            get => 50;
        }

        public float pause_time
        {
            get => 1;
        }

        public ClampSetting<float> start_touchdown_altitude;

        public ClampSetting<float> touch_down_ratio;

        public ClampSetting<float> touch_down_speed;

        // TARGET (precision landing). Manual lat/lon entry only for now. Vacuum bodies only (see
        // LandingPilot.cs's atmosphere guard and LandingUI.cs's atmo panel note) - the Atmo
        // profile's own copy of these three exists (see class comment) but has no UI yet.
        public Setting<bool> precision_landing;
        public Setting<float> target_latitude;
        public Setting<float> target_longitude;

        // Most sideways (normal/antinormal) delta-v the deorbit burn and the mid-course correction
        // may each add to move the ground track toward the target (see LandingTargeting.PlanDeorbit).
        // Whatever sideways miss is left over, the powered descent flies out. 0 disables it.
        public ClampSetting<float> max_plane_trim_dv;

        public LandingSettings(SettingsFile file)
        {
            auto_warp = new("land.auto_warp", true, file);
            burn_before = new("land.burnBefore", 0, 0, 10, file);
            rotation_warp_duration = new("land.rotation_warp_duration", 60, file);
            max_rotation = new("land.max_rotation", 10, 5, 30, file);

            start_touchdown_altitude = new("land.touch_down_altitude", 1500, 500, 5000, file);
            touch_down_ratio = new("land.touch_down_ratio", 0.5f, 0.5f, 3, file);
            touch_down_speed = new("land.touch_down_speed", 2.5f, 0, 10, file);

            precision_landing = new("land.precision_landing", false, file);
            target_latitude = new("land.target_latitude", 0f, file);
            target_longitude = new("land.target_longitude", 0f, file);

            max_plane_trim_dv = new("land.max_plane_trim_dv", 20, 0, 200, file);
        }

        // Full binding - Vacuum panel. Element names carry a "_vac" suffix in Landing.uxml so they
        // never collide with the Atmo panel's own copies (setupBasicUI below).
        public void setupUI(VisualElement root)
        {
            // TARGET
            root.Q<K2Toggle>("precision_landing_vac").Bind(precision_landing);
            var target_settings = root.Q<VisualElement>("target_settings_vac");
            // Precision-only settings (see PRECISION LANDING below) - shown/hidden alongside
            // target_settings, since neither means anything with precision landing off.
            var precision_settings = root.Q<VisualElement>("PrecisionLanding_vac");
            // .listen(), not .listeners += - .listen() also fires once immediately with the
            // current value when registered, not just on later changes, so these panels start
            // with the correct visibility instead of whatever the uxml gave them at rest.
            precision_landing.listen(v =>
            {
                target_settings.Show(v);
                precision_settings.Show(v);
            });
            target_settings.Q<FloatField>("target_latitude_vac").Bind(target_latitude);
            target_settings.Q<FloatField>("target_longitude_vac").Bind(target_longitude);

            // WARP
            root.Q<K2Toggle>("auto_warp_vac").Bind(auto_warp);
            var warp_settings = root.Q<VisualElement>("warp_settings_vac");
            auto_warp.listen(v => warp_settings.Show(v));

            warp_settings.Q<IntegerField>("rotation_warp_duration_vac").Bind(rotation_warp_duration);
            warp_settings.Q<K2Slider>("max_rotation_vac").Bind(max_rotation);

            // BRAKE
            root.Q<K2Slider>("burn_before_vac").Bind(burn_before);

            // TOUCH DOWN (touch_down_max_angle now bound by TouchDown.SetupFullUI, not here)
            var el_touchdown_altitude = root.Q<K2Slider>("start_touchdown_altitude_vac").Bind(start_touchdown_altitude);
            start_touchdown_altitude.listen(v =>
                el_touchdown_altitude.Label = "Start TouchDown Altitude : " + StrTool.DistanceToString(v)
            );

            root.Q<K2Slider>("touch_down_ratio_vac").Bind(touch_down_ratio);
            root.Q<K2Slider>("touch_down_speed_vac").Bind(touch_down_speed);

            // PRECISION LANDING
            root.Q<K2Slider>("max_plane_trim_dv_vac").Bind(max_plane_trim_dv);
        }

        // Reduced binding - Atmo panel. Only the settings that are actually meaningful on an
        // atmospheric body today: warp timing, brake timing, and touchdown speed profile. No
        // target/precision-landing controls here (see class comment) - those fields still
        // exist and persist independently on this profile, just without a visible control until
        // atmospheric precision landing is built.
        public void setupBasicUI(VisualElement root)
        {
            root.Q<K2Toggle>("auto_warp_atmo").Bind(auto_warp);
            var warp_settings = root.Q<VisualElement>("warp_settings_atmo");
            auto_warp.listen(v => warp_settings.Show(v));

            warp_settings.Q<IntegerField>("rotation_warp_duration_atmo").Bind(rotation_warp_duration);
            warp_settings.Q<K2Slider>("max_rotation_atmo").Bind(max_rotation);

            root.Q<K2Slider>("burn_before_atmo").Bind(burn_before);

            var el_touchdown_altitude = root.Q<K2Slider>("start_touchdown_altitude_atmo").Bind(start_touchdown_altitude);
            start_touchdown_altitude.listen(v =>
                el_touchdown_altitude.Label = "Start TouchDown Altitude : " + StrTool.DistanceToString(v)
            );

            root.Q<K2Slider>("touch_down_ratio_atmo").Bind(touch_down_ratio);
            root.Q<K2Slider>("touch_down_speed_atmo").Bind(touch_down_speed);
        }

        public float compute_limit_speed(float altitude)
        {
            // just to have understandable settings (not 0.1)
            float div = 10;
            return altitude * touch_down_ratio.V / div + touch_down_speed.V;
        }
    }
}
