using K2UI.Tabs;
using K2UI;
using UnityEngine.UIElements;
using K2D2.UI;

namespace K2D2.Lift
{
    class LiftUI : K2Page
    {
        LiftPilot pilot;
        public LiftUI(LiftPilot pilot)
        {
            this.pilot = pilot;
            code = "lift";
        }

        public FullStatus status_bar;

        K2Slider end_rotate_ratio, mid_rotate_ratio;

        Label heading_label;

        VisualElement final_grp;

        // Live LIFT INFO readout, same table pattern as NodeExUI's node_infos_el.
        VisualElement lift_infos_el;

        public override bool onInit()
        {
            LiftSettings settings = pilot.settings;

            status_bar = new FullStatus(panel);
            panel.Q<IntegerField>("start_altitude_km").Bind(settings.start_altitude_km);
            mid_rotate_ratio = panel.Q<K2Slider>("mid_rotate_ratio").Bind(settings.mid_rotate_ratio);
            end_rotate_ratio = panel.Q<K2Slider>("end_rotate_ratio").Bind(settings.end_rotate_ratio);
            panel.Q<IntegerField>("destination_Ap_km").Bind(settings.destination_Ap_km);

            lift_infos_el = panel.Q<VisualElement>("lift_infos");

            final_grp = panel.Q<VisualElement>("final_grp");

            settings.mid_rotate_ratio.listeners += v =>
            {
                if (end_rotate_ratio.value < v)
                    end_rotate_ratio.value = v;
            };

            settings.end_rotate_ratio.listeners += v =>
            {
                if (mid_rotate_ratio.value > v)
                    mid_rotate_ratio.value = v;
            };

            heading_label = panel.Q<Label>("heading_label");
            var heading = panel.Q<K2Compass>("heading").Bind(settings.heading);

            var graph = panel.Q<VisualElement>("graph");
            pilot.ascent_path.InitUI(graph);

            var max_throttle = panel.Q<K2Slider>("max_throttle").Bind(settings.max_throttle);

            var run_button = panel.Q<ToggleButton>("run");
            pilot.is_running_event += is_running => run_button.Value = is_running;
            // Same "give him a little life" touch as the Node tab: K2's 3 grille lines cascade
            // on/off with the autopilot, via the same is_running_event run_button already
            // listens to above.
            pilot.is_running_event += is_running => status_bar.avatar?.SetRunning(is_running);
            run_button.listeners +=  v =>
            {
                pilot.isRunning = v;
                run_button.label = v ? "Stop" : "Start";
            };

            // The old "settings" page's controls now live inside the ADVANCED foldout on "page"
            // instead of the separate hidden "settings" VisualElement, so setupUI needs to query
            // the whole panel to find them (same move as NodeExUI.cs).
            settings.setupUI(panel);

            // Reset button moved down into the ADVANCED foldout itself, now that the settings
            // page it used to live on is on its way out (replaced by an info button to the About
            // tab once the restyle is done).
            addResetButton(panel.Q<Foldout>("advanced_foldout"), "lift");

            return true;
        }

        void AddInfoRow(string label, string value)
        {
            var row = new VisualElement();
            row.AddToClassList("advanced-info-row");

            var label_el = new Label(label);
            label_el.AddToClassList("advanced-info-row-label");
            row.Add(label_el);

            var value_el = new Label(value);
            value_el.AddToClassList("advanced-info-row-value");
            row.Add(value_el);

            lift_infos_el.Add(row);
        }

        void UpdateLiftInfos()
        {
            lift_infos_el.Clear();

            if (pilot.current_subpilot == null)
            {
                AddInfoRow("Status", "Not running");
                return;
            }

            pilot.current_subpilot.UpdateInfoRows(AddInfoRow);
        }

        public override bool onUpdateUI()
        {
            if (!base.onUpdateUI())
                return false;

            LiftSettings settings = pilot.settings;

            pilot.ascent_path.updateProfile(pilot.ascent.current_altitude_km);

            end_rotate_ratio.ValueText = $"{settings.end_rotate_altitude_km:n0} km";
            mid_rotate_ratio.ValueText = $"{settings.mid_rotate_altitude_km:n0} km";
            heading_label.text = $"{settings.heading.V:n1} °";

            final_grp.Show(false);

            UpdateLiftInfos();

            status_bar.Reset();
            if (pilot.isRunning)
            {
                status_bar.Warning($"Status : {pilot.status}");

                if (pilot.current_subpilot != null)
                    pilot.current_subpilot.updateUI(panel, status_bar);
            }
            else
            {
                if (!string.IsNullOrEmpty(pilot.end_status))
                    status_bar.Status("Final status : " + pilot.end_status,
                        pilot.result_ok ? StatusLine.Level.Normal : StatusLine.Level.Warning);
                else
                    // Idle placeholder, same idea as Node's "No Node Created"/"Ready to execute
                    // node!" - K2 has something to say here instead of the status readout just
                    // sitting empty before the pilot's ever been run.
                    status_bar.Status("Lift autopilot not enabled");
            }

            return true;
        }



    }
}