using KTools;
using UnityEngine.UIElements;
using K2UI;

namespace K2D2.Landing
{
    public class LandingSettings
    {
        public Setting<bool> auto_warp = new("land.auto_warp", true);
        public ClampSetting<float> burn_before = new("land.burnBefore", 0, 0, 10);
        public Setting<int> rotation_warp_duration = new("land.rotation_warp_duration", 60);
        // Warp with check of rotation
        public ClampSetting<float> max_rotation = new("land.max_rotation", 10, 5, 30);

        public float brake_speed
        {
            get => 50;
            // get => Settings.s_settings_file.GetFloat("land.brake_speed", 20);
            // set { Settings.s_settings_file.SetFloat("land.brake_speed", value); }
        }

        public float pause_time
        {
            get => 1;
            // get => Settings.s_settings_file.GetFloat("land.brake_speed", 20);
            // set { Settings.s_settings_file.SetFloat("land.brake_speed", value); }
        }

        public ClampSetting<float> start_touchdown_altitude = new("land.touch_down_altitude", 1500, 500, 5000);

        public ClampSetting<float> touch_down_ratio = new("land.touch_down_ratio", 0.5f, 0.5f, 3);

        public ClampSetting<float> touch_down_speed = new("land.touch_down_speed", 2.5f,  0, 10);

        // TARGET (precision landing). Manual lat/lon entry only for now - this is the settings/UI
        // half of the feature. Steering toward this target, and pulling it from Redux's own
        // waypoint system instead of typing it in, both need the real Sim API for lat/lon <->
        // position confirmed against the live assembly first (same reason compute_real_collision()
        // in LandingPilot.cs went through 2 attempts before it worked - see NOTICE.md). Vacuum
        // bodies only to start (no atmosphere/drag model yet).
        public Setting<bool> precision_landing = new("land.precision_landing", false);
        public Setting<float> target_latitude = new("land.target_latitude", 0f);
        public Setting<float> target_longitude = new("land.target_longitude", 0f);

        // Small optional normal/antinormal component the deorbit burn is allowed to add on top of
        // its usual prograde/retrograde burn, to nudge the orbital plane a little closer to the
        // target instead of only ever picking the best-achievable point on the plane the player's
        // already on - see LandingTargeting.FindBestDeorbitBurn's own comment for the reasoning
        // and the honest limits. 0 disables it entirely (old in-plane-only behavior).
        // Range bumped 100 -> 200 (default left at 20) - Reese's in-game testing already pushed
        // this past the old cap (25 m/s on a Mun run) with good results ("It works great!"), so
        // the old 100 ceiling was closer to limiting than protective.
        public ClampSetting<float> max_plane_trim_dv = new("land.max_plane_trim_dv", 20, 0, 200);

        public void setupUI(LandingPilot pilot, VisualElement root)
        {
            // TARGET
            root.Q<K2Toggle>("precision_landing").Bind(precision_landing);
            var target_settings = root.Q<VisualElement>("target_settings");
            // Precision-only steering sliders (see PRECISION LANDING below) - shown/hidden
            // alongside target_settings, since neither means anything with precision landing off.
            var precision_settings = root.Q<VisualElement>("PrecisionLanding");
            precision_landing.listeners += v =>
            {
                target_settings.Show(v);
                precision_settings.Show(v);
            };
            target_settings.Q<FloatField>("target_latitude").Bind(target_latitude);
            target_settings.Q<FloatField>("target_longitude").Bind(target_longitude);

            // WARP
            root.Q<K2Toggle>("auto_warp").Bind(auto_warp);
            var warp_settings = root.Q<VisualElement>("warp_settings");    
            auto_warp.listeners += v => warp_settings.Show(v); 
  
            warp_settings.Q<IntegerField>("rotation_warp_duration").Bind(rotation_warp_duration);
            warp_settings.Q<K2Slider>("max_rotation").Bind(max_rotation);
        
            // BRAKE
            root.Q<K2Slider>("burn_before").Bind(burn_before);

            // TOUCH DOWN
            var el_touchdown_altitude = root.Q<K2Slider>("start_touchdown_altitude").Bind(start_touchdown_altitude);
            start_touchdown_altitude.listen(v =>    
                el_touchdown_altitude.Label = "Start TouchDown Altitude : " + StrTool.DistanceToString(v)
            );

            root.Q<K2Slider>("touch_down_ratio").Bind(touch_down_ratio);
            root.Q<K2Slider>("touch_down_speed").Bind(touch_down_speed);
            root.Q<K2Slider>("touch_down_max_angle").Bind(pilot.brake.touch_down_max_angle);

            // PRECISION LANDING - closed-loop steering caps, see TouchDown.ComputeSteeredDirection
            root.Q<K2Slider>("max_plane_trim_dv").Bind(max_plane_trim_dv);
            root.Q<K2Slider>("steering_max_angle").Bind(pilot.brake.steering_max_angle);
            root.Q<K2Slider>("arc_extend_max_angle").Bind(pilot.brake.arc_extend_max_angle);
            root.Q<K2Slider>("arc_shorten_max_angle").Bind(pilot.brake.arc_shorten_max_angle);
        }

        public float compute_limit_speed(float altitude)
        {
            // just to have understandable settings (not 0.1)
            float div = 10;
            return altitude * touch_down_ratio.V / div + touch_down_speed.V;
        }
    }
}
