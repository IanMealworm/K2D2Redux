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
    
        InfoTable node_infos;

        public FullStatus status_bar;

        public ToggleButton run_button, pause_button;

        // Flight Plan integration is disabled for now (not currently in use). FlightPlanCall.cs
        // itself is left fully intact - only the two call sites below are commented out - so
        // this can be reimplemented later without rewriting it.
        public FlightPlanCall call_fp;

        public override bool onInit()
        {
            node_infos = new InfoTable(panel.Q<VisualElement>("node_infos"));

            run_button = panel.Q<ToggleButton>("run");
            pause_button = panel.Q<ToggleButton>("pause");

            panel.Q<Button>("circularize_ap").listenClick(() => pilot.CreateCircularizeNode(true));
            panel.Q<Button>("circularize_pe").listenClick(() => pilot.CreateCircularizeNode(false));

            status_bar = new FullStatus(panel);

            pilot.is_running_event += is_running => run_button.Value = is_running;
            // "Give him a little life": K2's 3 grille lines cascade on/off with the autopilot
            // itself, via the same is_running_event run_button already listens to above.
            pilot.is_running_event += is_running => status_bar.avatar?.SetRunning(is_running);
            run_button.listeners +=  v =>
            {
                pilot.isRunning = v;
                // The pilot can refuse a start (no node, no engine) or stop again straight away,
                // so the button follows where it ended up rather than the click.
                run_button.Value = pilot.isRunning;
                run_button.label = run_button.Value ? "Stop" : "Start";
            };
            pause_button.Bind(pilot.settings.pause_on_end);

            // call_fp = new FlightPlanCall(pilot);
            // call_fp.initUI(panel);

            // The Turn/Warp/Burn/Experimental foldouts are inside the Advanced Info foldout on
            // "page", so setupUI queries the whole panel.
            pilot.settings.setupUI(panel);

            addResetButton(panel.Q<Foldout>("advanced_info_foldout"), "node_ex");


            return true;
        }

        void UpdateNodeInfos()
        {
            node_infos.Begin();

            ManeuverNodeData node = pilot.isRunning ? pilot.execute_node : pilot.next_maneuver_node;
            if (node == null)
                node_infos.Add("Node", "none");
            else
            {
                var dt = GeneralTools.remainingStartTime(node);
                node_infos.Add("Node in", StrTool.DurationToString(dt) + (dt < 0 ? "  (in the past)" : ""));
                node_infos.Add("dV", $"{node.BurnRequiredDV:n2} m/s");
                node_infos.Add("Duration", StrTool.DurationToString(node.BurnDuration));
            }

            node_infos.End();
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