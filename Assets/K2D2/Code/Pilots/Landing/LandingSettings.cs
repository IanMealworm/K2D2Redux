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

        // How much altitude clearance the correction burn (see LandingPilot.compute_startBurn)
        // should keep above start_touchdown_altitude, on top of whatever time the lateral
        // correction itself needs. Reese's complaint: corrections were starting around 4km up,
        // which felt tight against the 1500m default touchdown altitude, especially since a
        // well-aimed deorbit burn (small miss) barely pushes the lateral-correction floor out at
        // all. This is a separate, unconditional floor - it doesn't care how big the miss is, it
        // just always wants this much room above the touchdown threshold.
        //
        // Fixed at its old slider's max (8000) instead of staying a player-adjustable setting -
        // Reese always dragged this one to the top anyway, so there was never really a reason to
        // give him less than the most margin available. Was a ClampSetting (0-8000, default 3000)
        // with its own slider in the ADVANCED foldout; both are gone now.
        public const float min_correction_altitude_margin = 8000;

        // RCS fine correction (vacuum precision landing). Per Reese: the engine-steered
        // correction above can get target_error_m down close (his example: ~1km), then a "fine"
        // correction actually makes it WORSE, because closing the last bit needs the burn
        // direction to keep adjusting and the vessel can't physically turn fast enough to keep up
        // - by the time SAS gets the ship to the newly-aimed direction, the target's moved again.
        // RCS sidesteps that entirely: it translates the vessel directly, without reorienting it
        // at all, so there's no turn-rate lag to chase.
        //
        // Originally this only supplemented the engine's own heading correction rather than
        // replacing it - kept that one running too, RCS just added a sideways nudge on top. In-game
        // testing showed that was a big part of why precision landing burned so much more deltaV
        // than a normal landing: tilting the actual braking burn sideways to fix heading is an
        // inherently wasteful way to move sideways next to RCS doing it directly, and running both
        // at once didn't save anything since the engine kept paying that cost regardless. Now,
        // once active, RCS takes the heading-correction job over from the engine entirely (see
        // TouchDown.RCSHandlingHeading) - the engine still brakes and still does its own arc
        // extend/shorten correction (undershoot/overshoot), just not the left/right heading tilt -
        // and hands heading back the moment target_error_m grows past the threshold again. Off by
        // default and a player-facing toggle, since not every vessel carries RCS/monopropellant
        // for this, and it's new and untested in-game, same as the rest of precision landing.
        public Setting<bool> use_rcs_fine_correction = new("land.use_rcs_fine_correction", false);

        // How close the miss needs to be before RCS takes over heading correction - deliberately a
        // short-range "close the last bit efficiently" tool, not a replacement for the engine's own
        // much larger working range (nor for the engine's braking or arc correction, which stay
        // running throughout regardless of this threshold).
        public ClampSetting<float> rcs_fine_correction_threshold_m = new("land.rcs_fine_correction_threshold_m", 1000, 100, 5000);

        // How hard RCS pushes once active, as a fraction of full RCS authority (see TouchDown.
        // ApplyRCSFineCorrection - this scales the translation input the same way Docking's own
        // rcs_power setting scales FinalApproach's). Fades in from 0 as target_error_m approaches
        // the threshold above up to this at target_error_m = 0, rather than snapping to full
        // power the instant the threshold is crossed.
        public ClampSetting<float> rcs_fine_correction_power = new("land.rcs_fine_correction_power", 0.5f, 0.05f, 1f);

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

            // PRECISION LANDING - closed-loop steering caps, see TouchDown.ComputeSteeredDirection.
            // min_correction_altitude_margin and arc_correction_full_scale_m are no longer bound
            // here - both are fixed values now (see their own comments), sliders removed from the
            // uxml.
            root.Q<K2Slider>("max_plane_trim_dv").Bind(max_plane_trim_dv);
            root.Q<K2Slider>("steering_max_angle").Bind(pilot.brake.steering_max_angle);
            root.Q<K2Slider>("arc_extend_max_angle").Bind(pilot.brake.arc_extend_max_angle);
            root.Q<K2Slider>("arc_shorten_max_angle").Bind(pilot.brake.arc_shorten_max_angle);

            // RCS fine correction - see TouchDown.ApplyRCSFineCorrection
            root.Q<K2Toggle>("use_rcs_fine_correction").Bind(use_rcs_fine_correction);
            var rcs_fine_correction_settings = root.Q<VisualElement>("rcs_fine_correction_settings");
            use_rcs_fine_correction.listeners += v => rcs_fine_correction_settings.Show(v);
            rcs_fine_correction_settings.Q<K2Slider>("rcs_fine_correction_threshold_m").Bind(rcs_fine_correction_threshold_m);
            rcs_fine_correction_settings.Q<K2Slider>("rcs_fine_correction_power").Bind(rcs_fine_correction_power);
        }

        public float compute_limit_speed(float altitude)
        {
            // just to have understandable settings (not 0.1)
            float div = 10;
            return altitude * touch_down_ratio.V / div + touch_down_speed.V;
        }
    }
}
