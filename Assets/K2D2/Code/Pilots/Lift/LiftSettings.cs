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

        public ClampSetting<float> gravity_turn_pitch_kick_deg = new("lift.gravity_turn_pitch_kick_deg", 7.5f, 1f, 25f);
        // Below about 25 s the climb flattens out in thick air: the air pressure soars, and the
        // max-Q limit cutting the throttle can leave it falling.
        public ClampSetting<float> gravity_turn_hold_time_s = new("lift.gravity_turn_hold_time_s", 45f, 25f, 120f);

        // On: the kick angle and hold time are worked out just before the kick (see AscentPredictor),
        // and their two sliders are hidden. The slider values are still the fallback if it can't
        // make a plan.
        public Setting<bool> gravity_turn_auto = new("lift.gravity_turn_auto", true);
        // High enough that near the end the rocket keeps raising its apoapsis instead of levelling off
        // in the air (see GravityTurnControl).
        public ClampSetting<float> gravity_turn_min_throttle = new("lift.gravity_turn_min_throttle", 0.25f, 0f, 0.5f);

        // Throttle limits during the turn (see ReactiveAscent.ThrottleCap).
        public ClampSetting<float> gravity_turn_max_twr = new("lift.gravity_turn_max_twr", 2.5f, 1.2f, 5f);
        public ClampSetting<float> gravity_turn_max_q_kpa = new("lift.gravity_turn_max_q_kpa", 40f, 10f, 100f);

        // Above 0: at 0 Adjust never raises apoapsis, so the run never finishes.
        public ClampSetting<float> max_throttle = new ("lift.max_throttle", 1, 0.05f, 1);
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

        // On: the run ends with a circularization burn at apoapsis (FinalCircularize). Off: it
        // ends once apoapsis is final, optionally pausing (pause_on_final) so the player can plan
        // their own.
        public Setting<bool> auto_circularize = new ("lift.auto_circularize", true);

        public Setting<bool> pause_on_final = new ("lift.pause_on_final", false);

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

        VisualElement adjust_group;

        void setLabels()
        {
            end_ascent_alt.text = $"Ascent ends at {destination_Ap_km.V - end_ascent_error:n2} km";
            end_adjust_alt.text = $"Fine tuning ends at {destination_Ap_km.V - end_adjust_error:n2} km";
        }

        // Must match the choice in Lift.uxml's ascent_profile_drop.
        const string GravityTurnChoice = "Gravity Turn (Experimental)";

        public void setupUI(VisualElement root)
        {
            end_ascent_alt =  root.Q<Label>("end_ascent_alt");
            end_adjust_alt = root.Q<Label>("end_adjust_alt");
            adjust_group = root.Q<VisualElement>("adjust_group");

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

            ascent_profile_drop.SetValueWithoutNotify(ascent_profile.V == AscentProfile.GravityTurn ? GravityTurnChoice : "Guided");
            ShowProfileSettings(ascent_profile.V);

            ascent_profile_drop.RegisterCallback<ChangeEvent<string>>(evt =>
                ascent_profile.V = evt.newValue == GravityTurnChoice ? AscentProfile.GravityTurn : AscentProfile.Guided);
            // Also follows Reset.
            ascent_profile.listeners += profile =>
            {
                ascent_profile_drop.SetValueWithoutNotify(profile == AscentProfile.GravityTurn ? GravityTurnChoice : "Guided");
                ShowProfileSettings(profile);
            };

            gravity_turn_settings.Q<K2Slider>("gravity_turn_kick_speed_mps").Bind(gravity_turn_kick_speed_mps);
            var pitch_kick_slider = gravity_turn_settings.Q<K2Slider>("gravity_turn_pitch_kick_deg").Bind(gravity_turn_pitch_kick_deg);
            var hold_time_slider = gravity_turn_settings.Q<K2Slider>("gravity_turn_hold_time_s").Bind(gravity_turn_hold_time_s);
            // Auto picks these two itself, so the sliders only show when it's off.
            gravity_turn_auto.listen(v =>
            {
                pitch_kick_slider.Show(!v);
                hold_time_slider.Show(!v);
            });
            gravity_turn_settings.Q<K2Slider>("gravity_turn_min_throttle").Bind(gravity_turn_min_throttle);
            gravity_turn_settings.Q<K2Toggle>("gravity_turn_auto").Bind(gravity_turn_auto);
            gravity_turn_settings.Q<K2Slider>("gravity_turn_max_twr").Bind(gravity_turn_max_twr);
            gravity_turn_settings.Q<K2Slider>("gravity_turn_max_q_kpa").Bind(gravity_turn_max_q_kpa);

            root.Q<K2Toggle>("heading_correction").Bind(heading_correction);

            // bind to settings and add listener on the same line !
            root.Q<K2Slider>("end_ascent_pc").Bind(end_ascent_pc);
            end_ascent_pc.listen(v => setLabels());
            destination_Ap_km.listeners += v => setLabels();

            root.Q<K2Toggle>("coasting_warp").Bind(coasting_warp);

            // adjust is binded to setting and to show adjust group 
            root.Q<K2Toggle>("adjust").Bind(adjust);
            adjust.listen(v => adjust_group.Show(v));

            root.Q<K2Slider>("end_adjust_pc").Bind(end_adjust_pc);
            end_adjust_pc.listen(v => setLabels());

            root.Q<K2Toggle>("auto_circularize").Bind(auto_circularize);

            var pause_toggle = root.Q<K2Toggle>("pause_on_final").Bind(pause_on_final);
            auto_circularize.listen(v => pause_toggle.SetEnabled(!v));

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
