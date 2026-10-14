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

        InfoTable lift_infos;

        public override bool onInit()
        {
            LiftSettings settings = pilot.settings;

            status_bar = new FullStatus(panel);
            panel.Q<IntegerField>("start_altitude_km").Bind(settings.start_altitude_km);
            mid_rotate_ratio = panel.Q<K2Slider>("mid_rotate_ratio").Bind(settings.mid_rotate_ratio);
            end_rotate_ratio = panel.Q<K2Slider>("end_rotate_ratio").Bind(settings.end_rotate_ratio);
            panel.Q<IntegerField>("destination_Ap_km").Bind(settings.destination_Ap_km);

            lift_infos = new InfoTable(panel.Q<VisualElement>("lift_infos"));

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
                // Follows where the pilot ended up, in case it refused or stopped straight away.
                run_button.Value = pilot.isRunning;
                run_button.label = run_button.Value ? "Stop" : "Start";
            };

            // The settings controls are inside the ADVANCED foldout on "page", so setupUI queries
            // the whole panel.
            settings.setupUI(panel);

            addResetButton(panel.Q<Foldout>("advanced_foldout"), "lift");

            return true;
        }

        void UpdateLiftInfos()
        {
            lift_infos.Begin();
            if (pilot.current_subpilot == null)
                lift_infos.Add("Status", "Not running");
            else
                pilot.current_subpilot.UpdateInfoRows(lift_infos.Add);
            lift_infos.End();
        }

        public override bool onUpdateUI()
        {
            if (!base.onUpdateUI())
                return false;

            LiftSettings settings = pilot.settings;

            // The graph only shows for Guided (see LiftSettings.ShowProfileSettings).
            if (settings.ascent_profile.V == AscentProfile.Guided)
                pilot.ascent_path.updateProfile(pilot.ascent.current_altitude_km);

            end_rotate_ratio.ValueText = $"{settings.end_rotate_altitude_km:n0} km";
            mid_rotate_ratio.ValueText = $"{settings.mid_rotate_altitude_km:n0} km";
            heading_label.text = $"{settings.heading.V:n1} °";

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