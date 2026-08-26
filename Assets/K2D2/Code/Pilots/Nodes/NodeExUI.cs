// using System.Reflection.Emit;

using K2UI.Tabs;
using K2UI;
using KSP.Sim.Maneuver;
using KTools;
using UnityEngine.UIElements;
using K2D2.UI;

namespace K2D2.Node
{
    class NodeExUI : K2Page
    {
        NodeExPilot pilot;
        public NodeExUI(NodeExPilot pilot)
        {
            this.pilot = pilot;
            code = "node";
        }
    
        // Advanced Info is now a plain VisualElement built into label/value table rows from
        // C# (see AddInfoRow/UpdateNodeInfos below) instead of a K2UI.Console text block.
        public VisualElement node_infos_el;

        public FullStatus status_bar;

        public ToggleButton run_button, pause_button;

        // Flight Plan integration is disabled for now (not currently in use). FlightPlanCall.cs
        // itself is left fully intact - only the two call sites below are commented out - so
        // this can be reimplemented later without rewriting it.
        public FlightPlanCall call_fp;

        public override bool onInit()
        {
            node_infos_el = panel.Q<VisualElement>("node_infos");

            run_button = panel.Q<ToggleButton>("run");
            pause_button = panel.Q<ToggleButton>("pause");

            status_bar = new FullStatus(panel);

            pilot.is_running_event += is_running => run_button.Value = is_running;
            // "Give him a little life": K2's 3 grille lines cascade on/off with the autopilot
            // itself, via the same is_running_event run_button already listens to above.
            pilot.is_running_event += is_running => status_bar.avatar?.SetRunning(is_running);
            run_button.listeners +=  v =>
            {
                pilot.isRunning = v;
                run_button.label = v ? "Stop" : "Start";
            };
            pause_button.Bind(pilot.settings.pause_on_end);

            // call_fp = new FlightPlanCall(pilot);
            // call_fp.initUI(panel);

            // The old "settings" page's Turn/Warp/Burn/Experimental foldouts now live inside the
            // Advanced Info foldout on "page" instead of the separate hidden "settings"
            // VisualElement, so setupUI needs to query the whole panel to find them.
            pilot.settings.setupUI(panel);
            addSettingsResetButton("node_ex");


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

            node_infos_el.Add(row);
        }

        void UpdateNodeInfos()
        {
            node_infos_el.Clear();

            ManeuverNodeData node = null;
            if (pilot.isRunning)
                node = pilot.execute_node;
            else
                node = pilot.next_maneuver_node;

            if (node == null)
            {
                AddInfoRow("Node", "none");
                return;
            }

            var dt = GeneralTools.remainingStartTime(node);
            AddInfoRow("Node in", StrTool.DurationToString(dt) + (dt < 0 ? "  (in the past)" : ""));
            AddInfoRow("dV", $"{node.BurnRequiredDV:n2} m/s");
            AddInfoRow("Duration", StrTool.DurationToString(node.BurnDuration));
        }

        public override bool onUpdateUI()
        {
            if (!base.onUpdateUI())
                return false;

            // call_fp.updateUI();

            UpdateNodeInfos();

            return true;
        }



    }
}