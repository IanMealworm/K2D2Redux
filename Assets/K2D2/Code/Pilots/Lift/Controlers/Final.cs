using System.Collections.Generic;
using K2D2.KSPService;
using KSP.Sim;
using KSP.Sim.impl;
using KTools;
using KSP.Sim.Maneuver;
using UnityEngine.UIElements;
using K2UI;
using K2D2.UI;
using K2D2.Controller;
using K2D2.Node;

namespace K2D2.Lift
{
    /// <summary>
    /// rotation used for docking
    /// </summary>
    public class FinalCircularize : ExecuteController
    {
        LiftSettings lift_settings = null;

        KSPVessel current_vessel;

        LiftPilot lift;

        public FinalCircularize(LiftPilot lift, LiftSettings lift_settings)
        {
            current_vessel = K2D2_Plugin.Instance.current_vessel;
            this.lift = lift;
            this.lift_settings = lift_settings;
        }

        string status_msg = "";

        public override void Start()
        {
            base.Start();

            TimeWarpTools.SetRateIndex(0, false);
            current_vessel.SetThrottle(0);

            // The pause_on_final pause used to happen right here - but by the time Adjust's real-
            // time fine-tuning burn finishes and we reach Circularize, the vessel is already close
            // to actual apoapsis, leaving little runway to manually create the circularization
            // node. Moved to Coasting.cs instead, so it fires the moment the vessel clears the
            // atmosphere - see that file's comment.

            // Circularizing at apoapsis needs FlightPlan hooked up via K2D2OtherModsInterface, which
            // isn't wired up yet - the create_ap/create_now/run buttons in updateUI() below just call
            // stubbed-out no-ops until that's done. Previously this meant the ascent pilot just sat
            // here "running" forever showing those dead buttons (Update() below never sets finished,
            // so nothing ever advanced past this step on its own). Instead, end the run cleanly here -
            // ascent has already done everything it can do without FlightPlan. Commented out below
            // instead of removed so this is a one-line uncomment once FlightPlan integration is hooked
            // back up.
            lift.EndLiftPilot(true, "Manual circularization node creation needed");

            /* TODO: Other mods interfacing - re-enable once K2D2OtherModsInterface is hooked up
            if (!K2D2OtherModsInterface.fpLoaded)
            {
                lift.EndLiftPilot(true, "Please install FlightPlan for the final Step...");
            }
            */

            status_msg = "";
        }

        IKeplerPatch getOrbit()
        {
            if (current_vessel == null)
            {
                status_msg = "error : no vessel";
                return null;
            }

            // Same orbit-cast issue as Ascent.cs's computeValues(): the actively-flown vessel can hand
            // back a CurrentPatchedConicsOrbit instead of PatchedConicsOrbit, so casting to the concrete
            // type threw InvalidCastException. TimeToAp (used below in createApNode()) lives on
            // IKeplerPatch itself, so no cast is needed.
            IKeplerPatch orbit = current_vessel.VesselComponent.Orbit;
            if (orbit == null)
            {
                status_msg = "error : no orbit";
                return null;
            }

            return orbit;
        }

        void createApNode()
        {
            var current_time = GeneralTools.Game.UniverseModel.UniverseTime;

            var orbit = getOrbit();
            if (orbit == null)
                return;

            lift.logger.LogMessage($"Circularize TimeToAp = {orbit.TimeToAp}");
            /* TODO: Other mods interfacing
            if (!K2D2OtherModsInterface.instance.Circularize(current_time + orbit.TimeToAp, 0))
            {
                status_msg = "Error Creating Node";
            }
            */

            return;
        }

        void createNowNode()
        {
            var current_time = GeneralTools.Game.UniverseModel.UniverseTime;
            /* TODO: Other mods interfacing
            if (!K2D2OtherModsInterface.instance.Circularize(current_time + 30, 0))
            {
                status_msg = "Error Creating Node";
            }
            */

            return;
        }

        internal VisualElement final_grp;
        Button create_ap, create_now, run;

        public override void updateUI(VisualElement root_el, FullStatus st)
        {
            // Start() above now ends the lift pilot immediately instead of ever reaching this screen,
            // so none of this runs today. Commented out (not removed) so the create_ap/create_now/run
            // buttons - which need FlightPlan hooked up via K2D2OtherModsInterface to do anything - are
            // a one-line uncomment away once that integration exists.
            /*
// if (UI_Tools.BigButton("Pause"))
// {
//     TimeWarpTools.SetIsPaused(true);
// }

            if (create_ap == null)
            {
                final_grp = root_el.Q<VisualElement>("final_grp");
                create_ap = root_el.Q<Button>("create_ap");

                create_ap.listenClick(() =>
                {
                    removeAllNodes();
                    createApNode();
                });

                create_now = root_el.Q<Button>("create_now");

                create_now.listenClick(() =>
                {
                    removeAllNodes();
                    createNowNode();
                });

                run = root_el.Q<Button>("run");
                run.listenClick(() => { NodeExPilot.Instance.Start(); });
            }

            final_grp.Show(true);

            if (!string.IsNullOrEmpty(status_msg))
            {
                st.Warning(status_msg);
            }
            */
        }

        void removeAllNodes()
        {
            ManeuverPlanComponent maneuvers_component =
                current_vessel?.VesselComponent?.SimulationObject.FindComponent<ManeuverPlanComponent>();
            if (maneuvers_component == null)
            {
                lift.logger.LogWarning("no ManeuverPlanComponent");
                return;
            }

            List<ManeuverNodeData> nodes = maneuvers_component.GetNodes();
            if (nodes == null)
            {
                lift.logger.LogWarning("no ManeuverPlanComponent");
                return;
            }

            maneuvers_component.RemoveNodes(nodes);
        }

        public override void Update()
        {
        }
    }
}