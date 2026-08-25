using K2UI;
using KTools;
// using KTools.UI;
using UnityEngine.UIElements;

namespace K2D2.Node
{
    public class NodeExSettings
    {
        public Setting<bool> show_node_infos = new Setting<bool>("node_ex.show_node_infos", true);
        public Setting<bool> auto_warp = new Setting<bool>("node_ex.auto_warp", true);

        public Setting<bool> pause_on_end = new Setting<bool>("node_ex.pause_on_end", false);

        public enum StartMode { precise, constant, half_duration }
        private static string[] StartMode_Labels = { "T0", "before", "mid-duration" };

        public EnumSetting<StartMode> start_mode = new EnumSetting<StartMode>("node_ex.start_mode", StartMode.precise);

        public Setting<float> start_before = new Setting<float>("node_ex.start_before", 1);

        public void setupUI(VisualElement root)
        {
            // Turn To
            root.Q<K2Slider>("max_angle_maneuver").Bind(TurnToSettings.max_angle_maneuver);
            root.Q<K2Slider>("max_angular_speed").Bind(TurnToSettings.max_angular_speed);

            // Warp
            root.Q<K2Toggle>("auto_warp").Bind(auto_warp);
            var warp_settings = root.Q<VisualElement>("warp_settings");    
            auto_warp.listeners += v => warp_settings.Show(v); 

            warp_settings.Q<K2Slider>("warp_speed").Bind(WarpToSettings.warp_speed);
            warp_settings.Q<IntegerField>("warp_safe_duration").Bind(WarpToSettings.warp_safe_duration);

            // Burn
            root.Q<K2Slider>("burn_adjust").Bind(BurnManeuverSettings.burn_adjust);
            root.Q<K2Slider>("max_dv_error").Bind(BurnManeuverSettings.max_dv_error);

            // Experimental - start_mode is now three individual radio-style K2Toggle rows
            // (T0/Before/Mid-duration) instead of InlineEnum's button row, so "Before" can host
            // start_before nested directly under it while selected, Parts-Manager-style.
            // InlineEnum itself is untouched here - Dock tab still uses it as-is - this wiring is
            // built straight on K2Toggle + EnumSetting instead.
            var start_mode_precise = root.Q<K2Toggle>("start_mode_precise");
            var start_mode_constant = root.Q<K2Toggle>("start_mode_constant");
            var start_mode_half_duration = root.Q<K2Toggle>("start_mode_half_duration");
            var start_mode_constant_content = root.Q<VisualElement>("start_mode_constant_content");

            var start_before_el = root.Q<K2Slider>("start_before");
            start_before_el.Bind(start_before);

            // Clicking the already-active row's toggle would otherwise turn it off and leave
            // nothing selected - snap it back on instead (SetValueWithoutNotify so this doesn't
            // re-fire its own "listeners" event) so exactly one option is always selected, like a
            // radio group.
            start_mode_precise.listeners += v => { if (v) start_mode.V = StartMode.precise; else start_mode_precise.SetValueWithoutNotify(true); };
            start_mode_constant.listeners += v => { if (v) start_mode.V = StartMode.constant; else start_mode_constant.SetValueWithoutNotify(true); };
            start_mode_half_duration.listeners += v => { if (v) start_mode.V = StartMode.half_duration; else start_mode_half_duration.SetValueWithoutNotify(true); };

            void UpdateStartModeToggles(StartMode mode)
            {
                start_mode_precise.SetValueWithoutNotify(mode == StartMode.precise);
                start_mode_constant.SetValueWithoutNotify(mode == StartMode.constant);
                start_mode_half_duration.SetValueWithoutNotify(mode == StartMode.half_duration);

                bool show_constant = mode == StartMode.constant;
                start_mode_constant_content.Show(show_constant);

                // Second attempt at start_before's missing dashed track (still broken after the
                // first attempt, per Reese's screenshot). Two changes from that first attempt:
                // 1) target the tracker VisualElement itself ("unity-tracker", the same ID
                //    K2Slider.uss's dash background is keyed to) instead of the outer K2Slider
                //    wrapper - repaint-dirtying a parent isn't guaranteed to force a specific
                //    descendant's own background image to redraw.
                // 2) schedule the repaint instead of firing it immediately: MarkDirtyRepaint()
                //    called in the same frame as Show(true) likely repaints using the stale
                //    zero-width geometry left over from display:none, since layout hasn't run yet
                //    that frame - scheduling it lets one layout pass happen first.
                // Still speculative/unconfirmed - flag to Reese as needs-testing again either way.
                if (show_constant)
                {
                    var tracker_el = start_before_el.Q<VisualElement>("unity-tracker");
                    tracker_el?.schedule.Execute(() => tracker_el.MarkDirtyRepaint());
                }
            }

            start_mode.listeners += UpdateStartModeToggles;
            UpdateStartModeToggles(start_mode.V);

            root.Q<K2Toggle>("rotate_during_burn").Bind(BurnManeuverSettings.rotate_during_burn);
        }



    

    
                           
    


    }
}

