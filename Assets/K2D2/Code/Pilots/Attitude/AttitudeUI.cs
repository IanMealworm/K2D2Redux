// using System.Reflection.Emit;
using K2UI.Tabs;
using K2UI;
using UnityEngine.UIElements;
using K2D2.UI;

namespace K2D2.Controller
{
    class AttitudeUI : K2Page
    {
        AttitudePilot pilot;
        public AttitudeUI(AttitudePilot pilot)
        {
            this.pilot = pilot;
            code = "attitude";
        }

        long repeat_delay = 500;
        long repeat_interval = 100;

        FloatField elevation_field;
        ToggleButton run_button;

        public FullStatus status_bar;


        public override bool onInit()
        {
            elevation_field = panel.Q<FloatField>("elevation_field");
            elevation_field.Bind(AttitudeSettings.elevation);
            panel.Q<RepeatButton>("elevation_n_rb").SetAction(
                () => elevation_field.value -= 1, repeat_delay, repeat_interval);

            panel.Q<RepeatButton>("elevation_p_rb").SetAction(
                () => elevation_field.value += 1, repeat_delay, repeat_interval);

            panel.Q<K2Slider>("elevation_slider").Bind(AttitudeSettings.elevation);
            var heading_label = panel.Q<Label>("heading_label");
            panel.Q<K2Compass>("heading").Bind(AttitudeSettings.heading);
            AttitudeSettings.heading.listen(v => heading_label.text = $"Heading : {v:n1}");

            run_button = panel.Q<ToggleButton>("run");

            status_bar = new FullStatus(panel);

            pilot.is_running_event += is_running => run_button.Value = is_running;
            // Same "give him a little life" touch as every other tab: K2's 3 grille lines
            // cascade on/off with the autopilot.
            pilot.is_running_event += is_running => status_bar.avatar?.SetRunning(is_running);
            run_button.listeners +=  v =>
            {
                pilot.isRunning = v;
                run_button.label = v ? "Stop" : "Start";
            };

            return true;
        }

        public override bool onUpdateUI()
        {
            if (!base.onUpdateUI())
                return false;

            status_bar.Reset();
            if (pilot.isRunning)
                status_bar.Status("Holding attitude");
            else
                // Idle placeholder, same idea as every other tab - K2 has something to say here
                // instead of the status readout just sitting empty before the pilot's ever run.
                status_bar.Status("Attitude autopilot not enabled");

            return true;
        }
    }
}