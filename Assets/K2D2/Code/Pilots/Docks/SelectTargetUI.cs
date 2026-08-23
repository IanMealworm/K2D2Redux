
using System.Collections.Generic;
using System.Linq;
using UnityEngine.UIElements;
using static K2D2.Controller.Docks.DockTools;
using UnityEngine;

using KSP.Sim.impl;
using KSP.Game;

using K2UI;


namespace K2D2.Controller.Docks
{
    class SelectTargetUI
    {

        DockingPilot pilot;

        public SelectTargetUI(DockingPilot pilot)
        {
            this.pilot = pilot;
        }
        private DropdownField control_from_drop, target_drop, dock_drop;

        public void onInitUI(VisualElement panel)
        {
            control_from_drop = panel.Q<DropdownField>("control_from_drop");
            control_from_drop.listenClick(buildControlList);
            control_from_drop.RegisterCallback<ChangeEvent<string>>(evt =>
            {
                Debug.LogWarning("Control Selected " + evt.newValue);
                // todo
                //selected_control = part.component;
                //pilot.current_vessel.VesselComponent.SetControlOwner(selected_control);
            });

            target_drop = panel.Q<DropdownField>("target_drop");
            target_drop.listenClick(buildTargetList);
            target_drop.RegisterCallback<ChangeEvent<string>>(evt =>
                    Debug.LogWarning("Target Selected " + evt.newValue)

        
                // todo
                // pilot.current_vessel.VesselComponent.SetTargetByID(vessel.GlobalId);
                // pilot.current_vessel.VesselComponent.ClearTarget();
            );

            dock_drop = panel.Q<DropdownField>("dock_drop");
            dock_drop.listenClick(buildDockList);
            dock_drop.RegisterCallback<ChangeEvent<string>>(evt =>
                    Debug.LogWarning("Dock Selected " + evt.newValue)

                // todo
                // if (selected_control != null)
                //     pilot.current_vessel.VesselComponent.SetTargetByID(selected_control.GlobalId);
            );
        }

        public ListPart control_parts = new ListPart();
        public void buildControlList()
        {
            control_parts.Clear();
            var main_component = pilot.control_component;
            if (main_component == null)
                return;

            control_parts = FindParts(pilot.current_vessel.VesselComponent, true, true);
            // selected_control = pilot.control_component;

            List<string> part_names = new();
            foreach (NamedComponent part in control_parts.Parts)
                // FIXED during Redux port verification: List<T> has no mutating Append() method - with
                // `using System.Linq;` in scope this silently resolved to Enumerable.Append<T>(), which
                // returns a *new* sequence instead of mutating part_names, so the list stayed empty and
                // the dropdown was always populated with nothing. Changed to List<T>.Add().
                part_names.Add(part.name);

            control_from_drop.choices = part_names;
        }


        public List<VesselComponent> target_vessels = new();
        public void buildTargetList()
        {
            target_vessels.Clear();

            var body = pilot.current_vessel.currentBody();

            // FIXED during Redux port verification: this call was duplicated on the next line
            // (harmless, but redundant) - removed the duplicate.
            var allVessels = GameManager.Instance.Game.SpaceSimulation.UniverseModel.GetAllVessels();
            allVessels.Remove(pilot.current_vessel.VesselComponent);
            allVessels.RemoveAll(v => v.IsDebris());
            allVessels.RemoveAll(v => v.mainBody != body);

            target_vessels = allVessels;

            if (target_vessels.Count < 1)
            {
                GUILayout.Label("No other vessels orbiting the planet");
            }

            List<string> vessel_names = new();
            foreach (var vessel in target_vessels)
                // FIXED during Redux port verification: same List<T>.Append() vs .Add() bug as
                // buildControlList() above - Append() is the non-mutating LINQ extension, not a list mutator.
                vessel_names.Add(vessel.Name);

            // FIXED during Redux port verification: this assigned into control_from_drop.choices
            // (apparent copy-paste from buildControlList() above), so the target dropdown never actually
            // got populated with vessel names, and the control dropdown got clobbered with vessel names
            // instead of part names whenever the target dropdown was opened. Corrected to target_drop.
            target_drop.choices = vessel_names;
        }

        public void buildDockList()
        {
            // pilot.listDocks();
            // List<string> part_names = new();
            // foreach (NamedComponent part in pilot.docks.Parts)
            //     part_names.Append(part.name);

            // dock_drop.choices = part_names;
        }

    



    }
}