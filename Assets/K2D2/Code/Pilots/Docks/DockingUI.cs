using KSP.Sim.impl;
using static K2D2.Controller.Docks.DockTools;
using K2UI.Tabs;
using UnityEngine.UIElements;
using K2UI;
using K2D2.UI;

namespace K2D2.Controller.Docks
{
    // TODO : ajouter le dessin des docks ici
    class DockingUI : K2Page
    {
        public DockingUI(DockingPilot pilot)
        {
            this.pilot = pilot;
            code = "dock";
        }

        DockingPilot pilot;

        public PartComponent selected_control = null;

        private VisualElement select_group, button_bars;

        public VisualElement context;
        private ToggleButton run_button;
        private Button main_brake, rcs_final_approach, cheat;
        private K2Toggle align_dock;

        public FullStatus st;

        public override bool onInit()
        {
            select_group = panel.Q<VisualElement>("select_group");
            select_group.Show(false);

            // TODO Selection target
            // select_target_ui = SelectTargetUI(this.pilot, panel)
            // select_target_ui.onInitUI();

            context = panel.Q<VisualElement>("context");
    
            button_bars = panel.Q<VisualElement>("button_bars");
            run_button = button_bars.Q<ToggleButton>("run");
            main_brake = button_bars.Q<Button>("main_brake");
            align_dock = button_bars.Q<K2Toggle>("align_dock");
            align_dock.RegisterCallback<ChangeEvent<bool>>(is_on =>
            {
                if (is_on.newValue)
                    pilot.turnTo.StartDockAlign();
                else
                    pilot.turnTo.mode = Pilots.DockingTurnTo.Mode.Off;
            });

            cheat = button_bars.Q<Button>("cheat");
            K2D2Settings.debug_mode.listen(v=>cheat.Show(v));
            cheat.listenClick(() => onCheat());
            rcs_final_approach = button_bars.Q<Button>("rcs_final_approach");
            st = new FullStatus(panel);

            pilot.listenIsRunning(is_running =>
            {
                // Hide selection and run buttons
                // select_group.Show(!is_running);
                main_brake.Show(!is_running);
                rcs_final_approach.Show(!is_running);

                // update the main button states
                run_button.Show(is_running);
                run_button.Value = is_running;
                run_button.label = is_running ? "Stop" : "Start";
            });

            run_button.listen((v) => {
                if (!v)
                    pilot.isRunning = false;
            });

            // Same "give him a little life" touch as Node/Lift/Land: K2's 3 grille lines cascade
            // on/off with the autopilot.
            pilot.listenIsRunning(is_running => st.avatar?.SetRunning(is_running));

            main_brake.listenClick(() =>
            {
                pilot.Mode = DockingPilot.PilotMode.MainThrustKillSpeed;
            });

            rcs_final_approach.listenClick(() =>
            {
                pilot.Mode = DockingPilot.PilotMode.RCSFinalApproach;
            });

            // RCS Power now lives inside the ADVANCED foldout on "page" instead of the separate
            // "settings" VisualElement (same move as Node/Lift/Land) - panel.Q<>() searches the
            // whole TabPage regardless, so passing it in place of settings_page is enough.
            pilot.final_approach_pilot.onInitUI(panel, panel);

            addResetButton(panel.Q<Foldout>("advanced_foldout"), "dock");

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

            context.Add(row);
        }

        void updateContext()
        {
            context.Clear();
            AddInfoRow("Control", ListPart.formatComponent( pilot.current_vessel.VesselComponent, pilot.control_component ));
            AddInfoRow("Target", ListPart.formatComponent( pilot.target_vessel, pilot.target_part ));
        }

        public override bool onUpdateUI()
        {  
            if (!base.onUpdateUI())
                return false;

            st.Reset();
            // update the align_dock
            align_dock.value = pilot.turnTo.isDockAlign;

            updateContext();
            pilot.final_approach_pilot.Hide();
            if (pilot.sub_controler != null)
                pilot.sub_controler.updateUI(panel, st);
            else
                // Idle placeholder, same idea as Node/Lift/Land - K2 has something to say here
                // instead of the status readout just sitting empty before the pilot's ever run.
                st.Status("Docking autopilot not enabled");

            return true;
        }

        void onCheat()
        {
            if (pilot.target_vessel == null) return;

            if (pilot.target_vessel.Guid == pilot.current_vessel.VesselComponent.Guid)
                return;

            pilot.Game.SpaceSimulation.Lua.TeleportToRendezvous(
                pilot.current_vessel.VesselComponent.Guid,
                pilot.target_vessel.Guid,
                30,
                0, 0, 0, 0, 0);
        }

        // public bool drawShapes(DockShape shapes_drawer)
        // {
        //     // logger.LogInfo("drawShapes");
        //     if (ui_mode == UI_Mode.Select_Control)
        //     {
        //         foreach (NamedComponent part in control_parts.Parts)
        //         {
        //             Color color = settings.unselected_color;
        //             if (part.component == selected_component)
        //             {
        //                 color = settings.vessel_color;
        //             }

        //             shapes_drawer.DrawComponent(part.component, pilot.current_vessel.VesselComponent, color, true, true);
        //         }

        //         return true;
        //     }
        //     if (ui_mode == UI_Mode.Select_Dock)
        //     {
        //         foreach (NamedComponent part in pilot.docks.Parts)
        //         {
        //             Color color = settings.unselected_color;
        //             if (part.component == selected_component)
        //             {
        //                 color = settings.target_color;
        //             }

        //             shapes_drawer.DrawComponent(part.component, pilot.current_vessel.VesselComponent, color, true, true);
        //         }

        //         return true;
        //     }


        //     return false;
        // }

    }
}