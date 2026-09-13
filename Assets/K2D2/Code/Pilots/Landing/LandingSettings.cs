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
    // today). Target lat/lon, the precision landing toggle, plane trim, and RCS fine correction
    // all still exist as real, independently-persisted settings on the Atmo profile too - they're
    // just not wired to a visible control yet, since atmospheric precision landing isn't built.
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

        // Small optional normal/antinormal component the deorbit burn is allowed to add on top of
        // its usual prograde/retrograde burn, to nudge the orbital plane a little closer to the
        // target instead of only picking the best-achievable point on the plane the player's
        // already on - see LandingTargeting.FindBestDeorbitBurn for the reasoning and limits. 0
        // disables it entirely (in-plane-only behavior).
        public ClampSetting<float> max_plane_trim_dv;

        // How much altitude clearance the correction burn (see LandingPilot.compute_startBurn)
        // keeps above start_touchdown_altitude, on top of whatever time the lateral correction
        // itself needs. This is a separate, unconditional floor - it doesn't care how big the
        // miss is, it just always wants this much room above the touchdown threshold.
        //
        // Fixed at its old slider's max (8000) instead of staying a player-adjustable setting -
        // in practice players always wanted maximum margin, so there was never a real reason to
        // offer less. Was a ClampSetting (0-8000, default 3000) with its own slider in the
        // ADVANCED foldout; both are gone now. Shared across both profiles (a plain const, not a
        // per-profile Setting), since it was never player-adjustable to begin with.
        public const float min_correction_altitude_margin = 8000;

        // RCS fine correction (vacuum precision landing). The engine-steered correction above can
        // get target_error_m down close, then a "fine" correction can actually make it WORSE,
        // because closing the last bit needs the burn direction to keep adjusting and the vessel
        // can't physically turn fast enough to keep up - by the time SAS gets the ship to the
        // newly-aimed direction, the target's moved again. RCS sidesteps that: it translates the
        // vessel directly, without reorienting it, so there's no turn-rate lag to chase.
        //
        // Once active, RCS takes the heading-correction job over from the engine entirely (see
        // TouchDown.RCSHandlingHeading) - the engine still brakes and still does its own arc
        // extend/shorten correction (undershoot/overshoot), just not the left/right heading tilt -
        // and hands heading back once target_error_m grows past the threshold again. Tilting the
        // braking burn sideways to fix heading is an inherently wasteful way to move sideways next
        // to RCS doing it directly. Off by default and a player-facing toggle, since not every
        // vessel carries RCS/monopropellant for this.
        public Setting<bool> use_rcs_fine_correction;

        // How close the miss needs to be before RCS takes over heading correction - deliberately a
        // short-range "close the last bit efficiently" tool, not a replacement for the engine's
        // own much larger working range (nor for the engine's braking or arc correction, which
        // stay running regardless of this threshold).
        public ClampSetting<float> rcs_fine_correction_threshold_m;

        // How hard RCS pushes once active, as a fraction of full RCS authority (see TouchDown.
        // ApplyRCSFineCorrection - this scales the translation input the same way Docking's own
        // rcs_power setting scales FinalApproach's). Fades in from 0 as target_error_m approaches
        // the threshold above up to this at target_error_m = 0, rather than snapping to full
        // power the instant the threshold is crossed.
        public ClampSetting<float> rcs_fine_correction_power;

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

            use_rcs_fine_correction = new("land.use_rcs_fine_correction", false, file);
            rcs_fine_correction_threshold_m = new("land.rcs_fine_correction_threshold_m", 1000, 100, 5000, file);
            rcs_fine_correction_power = new("land.rcs_fine_correction_power", 0.5f, 0.05f, 1f, file);
        }

        // Full binding - Vacuum panel. Element names carry a "_vac" suffix in Landing.uxml so they
        // never collide with the Atmo panel's own copies (setupBasicUI below).
        public void setupUI(VisualElement root)
        {
            // TARGET
            root.Q<K2Toggle>("precision_landing_vac").Bind(precision_landing);
            var target_settings = root.Q<VisualElement>("target_settings_vac");
            // Precision-only steering sliders (see PRECISION LANDING below) - shown/hidden
            // alongside target_settings, since neither means anything with precision landing off.
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

            // PRECISION LANDING - closed-loop steering caps live on TouchDown now (see its
            // SetupFullUI), min_correction_altitude_margin and arc_correction_full_scale_m are
            // fixed values (see their own comments), no sliders for either.
            root.Q<K2Slider>("max_plane_trim_dv_vac").Bind(max_plane_trim_dv);

            // RCS fine correction - see TouchDown.ApplyRCSFineCorrection
            root.Q<K2Toggle>("use_rcs_fine_correction_vac").Bind(use_rcs_fine_correction);
            var rcs_fine_correction_settings = root.Q<VisualElement>("rcs_fine_correction_settings_vac");
            use_rcs_fine_correction.listen(v => rcs_fine_correction_settings.Show(v));
            rcs_fine_correction_settings.Q<K2Slider>("rcs_fine_correction_threshold_m_vac").Bind(rcs_fine_correction_threshold_m);
            rcs_fine_correction_settings.Q<K2Slider>("rcs_fine_correction_power_vac").Bind(rcs_fine_correction_power);
        }

        // Reduced binding - Atmo panel. Only the settings that are actually meaningful on an
        // atmospheric body today: warp timing, brake timing, and touchdown speed profile. No
        // target/precision-landing/RCS controls here (see class comment) - those fields still
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
