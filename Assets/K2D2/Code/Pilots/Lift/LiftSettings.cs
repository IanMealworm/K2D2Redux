using K2UI;
using KTools;
using UnityEngine;
using UnityEngine.UIElements;

namespace K2D2.Lift
{
    public class LiftSettings
    {
        public ClampSetting<float> heading = new ("lift.heading", 90,  0 , 360);
 
        public Setting<int> start_altitude_km = new ("lift.start_altitude_km", 2);

        public ClampSetting<float> mid_rotate_ratio = new ("lift.mid_rotate_ratio", 0.2f, 0, 1);

        public ClampSetting<float> end_rotate_ratio = new ("lift.end_rotate_ratio", 0.5f, 0, 1);

        public LiftSettings()
        {
            start_altitude_km.listeners += v => update_altitudes();
            mid_rotate_ratio.listeners += v => update_altitudes();
            end_rotate_ratio.listeners += v => update_altitudes();
            destination_Ap_km.listeners += v => update_altitudes();

            update_altitudes();
        }

        public void update_altitudes()
        {
            _mid_rotate_altitude_km = Mathf.Lerp(start_altitude_km.V, destination_Ap_km.V, mid_rotate_ratio.V);
            _end_rotate_altitude_km = Mathf.Lerp(start_altitude_km.V, destination_Ap_km.V, end_rotate_ratio.V);
        }

        float _mid_rotate_altitude_km = -1;
        public float mid_rotate_altitude_km
        {
            get {
                return _mid_rotate_altitude_km;
            }
        }

        float _end_rotate_altitude_km = -1;
        public float end_rotate_altitude_km
        {
            get {
                return _end_rotate_altitude_km;
            }
        }

        public Setting<int> destination_Ap_km = new("lift.destination_Ap_km", 100);

        // ASCENT PROFILE (see AscentProfile.cs) - which ExecuteController flies the Ascent phase.
        // Bound to the ascent_profile_drop DropdownField in the Profile foldout (see setupUI
        // below); LiftPilot.ascent reads this live to pick between Ascent (Guided) and
        // ReactiveAscent (GravityTurn).
        public EnumSetting<AscentProfile> ascent_profile = new("lift.ascent_profile", AscentProfile.Guided);

        // GRAVITY TURN tunables (see ReactiveAscent.cs) - only shown/used when ascent_profile is
        // GravityTurn.

        // How fast the rocket needs to be rising (vertical speed) before it performs its pitch
        // kick and starts the actual turn - player-tunable since heavier/higher-TWR rockets reach
        // a good kick speed at very different times.
        public ClampSetting<float> gravity_turn_kick_speed_mps = new("lift.gravity_turn_kick_speed_mps", 100f, 10f, 300f);

        public ClampSetting<float> gravity_turn_pitch_kick_deg = new("lift.gravity_turn_pitch_kick_deg", 7.5f, 1f, 15f);
        public ClampSetting<float> gravity_turn_hold_time_s = new("lift.gravity_turn_hold_time_s", 45f, 10f, 120f);
        public ClampSetting<float> gravity_turn_min_throttle = new("lift.gravity_turn_min_throttle", 0.05f, 0f, 0.5f);

        // Pitch-down bias "second actuator" (see ReactiveAscent.cs's pitch_down_bias_deg) - leans
        // the nose below true prograde when throttle alone can't hold the turn's target apoapsis
        // time. Both were fixed constants at first; made player-tunable since how much lean is
        // appropriate, and how early it should start sharing the work with throttle, both depend
        // heavily on the specific vehicle (a lightly-overpowered rocket needs much less of either
        // than a heavily SRB-boosted one).
        public ClampSetting<float> gravity_turn_pitch_bias_max_deg = new("lift.gravity_turn_pitch_bias_max_deg", 25f, 0f, 45f);
        public ClampSetting<float> gravity_turn_pitch_bias_engage_margin = new("lift.gravity_turn_pitch_bias_engage_margin", 0.6f, 0f, 1f);

        public ClampSetting<float> max_throttle = new ("lift.max_throttle", 1, 0, 1);
        public ClampSetting<float> end_ascent_pc = new ("lift.end_ascent_pc", 0.1f, 0, 1);

        public float end_ascent_error
        {
            get => destination_Ap_km.V * end_ascent_pc.V/100;
        }

        public Setting<bool> coasting_warp = new ("lift.coasting_warp", true);
        public Setting<bool> adjust = new ("lift.adjust", true);
        public ClampSetting<float> end_adjust_pc = new ("lift.end_adjust_pc", 0.01f, 0.001f, 0.5f);

        public float end_adjust_error
        {
            get => destination_Ap_km.V * end_adjust_pc.V/100;
        }

        // Default on: run straight through ascent -> atmosphere clear -> circularize with no
        // pause, since FinalCircularize (Controlers/Final.cs) now creates and flies the
        // circularize burn itself instead of waiting on a manually- or FlightPlan-created node.
        // Turning this off falls back to the old flow - Coasting.cs only actually pauses on
        // pause_on_final below when this is off.
        public Setting<bool> auto_circularize = new ("lift.auto_circularize", true);

        public Setting<bool> pause_on_final = new ("lift.pause_on_final", true);

        public Setting<bool> heading_correction = new ("lift.heading_correction", true);

        // ROLL PROGRAM (see Ascent.cs for the full reasoning). Lives in the Profile foldout
        // alongside Heading, not Advanced - it's a per-launch choice like heading, not a tuning
        // knob. Off by default; measured relative to the vessel's OWN launch orientation rather
        // than an absolute compass/navball reading (see Ascent.cs). Guided-only for now - it's
        // not wired into ReactiveAscent's prograde-hold steering (see Lift.uxml's
        // guided_profile_settings), so its controls hide when GravityTurn is selected.
        public Setting<bool> roll_program = new("lift.roll_program", false);
        public ClampSetting<float> roll_program_altitude_km = new("lift.roll_program_altitude_km", 0.1f, 0, 5);
        public ClampSetting<float> roll_program_angle_deg = new("lift.roll_program_angle_deg", 0, -180, 180);

        // How fast Ascent.cs's roll ramp (ramped_roll_target_deg) is allowed to advance toward
        // roll_program_angle_deg - player-tunable since different vessels can tolerate a faster
        // roll than others before SAS overshoots (see Ascent.cs's class comment). Default and range
        // centered on 3 deg/s.
        public ClampSetting<float> roll_program_rate_deg_s = new("lift.roll_program_rate_deg_s", 3f, 0.5f, 15f);

        Label end_ascent_alt;
        Label end_adjust_alt;

        Group adjust_group;

        void setLabels()
        {
            end_ascent_alt.text = $"End Ascent Alt. : {destination_Ap_km.V - end_ascent_error:n2} km";
            end_adjust_alt.text = $"Final Adjust Alt : {destination_Ap_km.V - end_adjust_error:n2} km";
        }

        public void setupUI(VisualElement root)
        {
            end_ascent_alt =  root.Q<Label>("end_ascent_alt");
            end_adjust_alt = root.Q<Label>("end_adjust_alt");
            adjust_group = root.Q<K2UI.Group>("adjust_group");

            // ASCENT PROFILE - a real DropdownField. The oversized height seen in-game was a
            // K2UI.uss bug (.unity-popup-field__input's height: 100% resolving against this
            // Foldout's flex-grow: 1 container, not something specific to this field - see
            // K2UI.uss's own comment), now fixed at the shared stylesheet level.
            // guided_profile_settings holds every control that's Guided-only (rotate ratios, 90°
            // alt, and the roll program - see its own comment above); gravity_turn_settings holds
            // GravityTurn's own tunables. Both live side by side in the Profile foldout and only
            // one shows at a time.
            var ascent_profile_drop = root.Q<DropdownField>("ascent_profile_drop");
            var guided_profile_settings = root.Q<VisualElement>("guided_profile_settings");
            var gravity_turn_settings = root.Q<VisualElement>("gravity_turn_settings");
            var graph = root.Q<VisualElement>("graph");

            void ShowProfileSettings(AscentProfile profile)
            {
                guided_profile_settings.Show(profile == AscentProfile.Guided);
                gravity_turn_settings.Show(profile == AscentProfile.GravityTurn);
                // The ascent path graph only ever plots Guided's fixed pitch curve
                // (LiftAscentPath.compute_elevation) - under Gravity Turn the live green altitude
                // marker doesn't track that curve at all (the throttle-modulated trajectory has
                // nothing to do with it), so it just reads as constant, meaningless motion.
                // Hidden here rather than shown-but-wrong until the graph learns to plot Gravity
                // Turn's own trajectory.
                graph.Show(profile == AscentProfile.Guided);
            }

            ascent_profile_drop.SetValueWithoutNotify(ascent_profile.V == AscentProfile.GravityTurn ? "Gravity Turn" : "Guided");
            ShowProfileSettings(ascent_profile.V);

            ascent_profile_drop.RegisterCallback<ChangeEvent<string>>(evt =>
            {
                var profile = evt.newValue == "Gravity Turn" ? AscentProfile.GravityTurn : AscentProfile.Guided;
                ascent_profile.V = profile;
                ShowProfileSettings(profile);
            });

            gravity_turn_settings.Q<K2Slider>("gravity_turn_kick_speed_mps").Bind(gravity_turn_kick_speed_mps);
            gravity_turn_settings.Q<K2Slider>("gravity_turn_pitch_kick_deg").Bind(gravity_turn_pitch_kick_deg);
            gravity_turn_settings.Q<K2Slider>("gravity_turn_hold_time_s").Bind(gravity_turn_hold_time_s);
            gravity_turn_settings.Q<K2Slider>("gravity_turn_min_throttle").Bind(gravity_turn_min_throttle);
            gravity_turn_settings.Q<K2Slider>("gravity_turn_pitch_bias_max_deg").Bind(gravity_turn_pitch_bias_max_deg);
            gravity_turn_settings.Q<K2Slider>("gravity_turn_pitch_bias_engage_margin").Bind(gravity_turn_pitch_bias_engage_margin);

            root.Q<K2Toggle>("heading_correction").Bind(heading_correction);

            // bind to settings and add listener on the same line !
            root.Q<K2Slider>("end_ascent_pc").Bind(end_ascent_pc);
            end_ascent_pc.listen(v => setLabels());

            root.Q<K2Toggle>("coasting_warp").Bind(coasting_warp);

            // adjust is binded to setting and to show adjust group 
            root.Q<K2Toggle>("adjust").Bind(adjust);
            adjust.listen(v => adjust_group.Show(v));

            root.Q<K2Slider>("end_adjust_pc").Bind(end_adjust_pc);
            end_adjust_pc.listen(v => setLabels());

            root.Q<K2Toggle>("auto_circularize").Bind(auto_circularize);

            root.Q<K2Toggle>("pause_on_final").Bind(pause_on_final);

            // ROLL PROGRAM - .listen(), not .listeners += (see LandingSettings.setupUI's comment on
            // precision_landing for why that distinction matters): this defaults to off, so a plain
            // .listeners += would leave roll_program_settings visible on a fresh window open until
            // the toggle was flipped once.
            root.Q<K2Toggle>("roll_program").Bind(roll_program);
            var roll_program_settings = root.Q<VisualElement>("roll_program_settings");
            roll_program.listen(v => roll_program_settings.Show(v));
            roll_program_settings.Q<K2Slider>("roll_program_altitude_km").Bind(roll_program_altitude_km);
            roll_program_settings.Q<K2Slider>("roll_program_angle_deg").Bind(roll_program_angle_deg);
            roll_program_settings.Q<K2Slider>("roll_program_rate_deg_s").Bind(roll_program_rate_deg_s);
        }
    }
}
