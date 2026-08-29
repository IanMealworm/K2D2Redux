
using K2D2.KSPService;
using KSP.Sim;
using KSP.Sim.impl;
using KTools;
// using UnityEngine;
using UnityEngine.UIElements;
using K2D2.UI;
using K2D2.Controller;
using K2D2.Node;
namespace K2D2.Lift
{
    /// <summary>
    /// rotation used for docking
    /// </summary>
    public class Coasting : ExecuteController
    {
        LiftSettings lift_settings = null;

        KSPVessel current_vessel;

        LiftPilot lift;

        public Coasting(LiftPilot lift, LiftSettings lift_settings)
        {
            current_vessel = K2D2_Plugin.Instance.current_vessel;
            this.lift = lift;
            this.lift_settings = lift_settings;
        }

        TurnTo turn_to = null;

        // used during coasting
        double densityAtm = 0;
        double duration_to_atm = 0;
        public float current_altitude_km = 0;

        public override void Start()
        {
            base.Start();

            turn_to = new TurnTo();
            turn_to.StartProGrade(SpeedDisplayMode.Orbit);

            current_vessel.SetThrottle(0);
            TimeWarpTools.SetRateIndex(0, false);
            turn_to = new TurnTo();
            turn_to.StartProGrade(SpeedDisplayMode.Surface);
        }

        // Narrative feedback only here now - Altitude/Atm Density moved to UpdateInfoRows below
        // (LIFT INFO table), same split NodeExUI's controllers already use between console text
        // and the Node Info table.
        public override void updateUI(VisualElement root_el, FullStatus st)
        {
            st.Status("Coasting");

            if (!turn_to.finished)
                st.Console(turn_to.status_line);
            else
                st.Console($"End warp : {StrTool.DurationToString(duration_to_atm)} x{TimeWarpTools.CurrentRate}");
        }

        public override void UpdateInfoRows(System.Action<string, string> addRow)
        {
            addRow("Altitude", $"{current_altitude_km:n2} km");
            addRow("Atm Density", $"{densityAtm:n2}");
        }

        public override void Update()
        {
            if (!lift_settings.coasting_warp.V)
                lift.NextMode();

            current_altitude_km = (float)(current_vessel.GetSeaAltitude() / 1000);
            finished = false;

            CelestialBodyComponent mainBody = K2D2_Plugin.Instance.current_vessel.currentBody();
            var maxAtmosphereAltitude_km = (float)(mainBody.atmosphereDepth / 1000);
            if (lift_settings.destination_Ap_km.V < maxAtmosphereAltitude_km)
            {
                lift.EndLiftPilot(false, "Warning Ap is under Atm. limit");
                return;
            }

            var altitude = (float)current_vessel.GetApproxAltitude() / 1000;
            densityAtm = mainBody.GetPressure(altitude * 1000);

            // compute time to reaching altitude.
            float V_Speed = (float)current_vessel.VesselVehicle.VerticalSpeed;
            var delta_alt = maxAtmosphereAltitude_km - altitude;

            if (delta_alt < 0)
            {
                // reached
                finished = true;

                // Pause the instant we clear the atmosphere (70km on Kerbin, wherever the body's
                // own atmosphereDepth ends elsewhere) rather than waiting for Adjust to finish its
                // fine-tuning burn and Circularize to end the run (see Final.cs) - that used to be
                // the only pause point, but Adjust's real-time correction burn could eat most of
                // the runway before the player gets a chance to create their own circularization
                // node (FlightPlan auto-creation isn't hooked up yet, see Final.cs's comment).
                // Pausing here instead means the whole coast to apoapsis is available as node-
                // creation time. See LiftSettings.pause_on_final / Lift.uxml's "Pause Leaving
                // Atmosphere" toggle (moved here from the old "Final" section). Cancel warp
                // first (same order Final.cs/Adjust.cs used) - we can still be mid-warp right at
                // the instant this threshold is crossed, since warping toward it is this phase's
                // whole job just below.
                if (lift_settings.pause_on_final.V)
                {
                    TimeWarpTools.SetRateIndex(0, false);
                    TimeWarpTools.SetIsPaused(true);
                }

                return;
            }

            turn_to.Update();
            if (!turn_to.finished)
            {
                TimeWarpTools.SetRateIndex(0, false);
                return;
            }

            // warp until end
            duration_to_atm = delta_alt / V_Speed;
            var wanted_warp_index = WarpToSettings.compute_wanted_warp_index(duration_to_atm);
            TimeWarpTools.SetRateIndex(wanted_warp_index + 1, false);
        }
    }
}
