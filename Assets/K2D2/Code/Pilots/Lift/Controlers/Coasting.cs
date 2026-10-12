
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
    // Coasting from engine cut-off to the edge of the atmosphere, holding surface prograde and
    // (with Coasting Warp on) warping toward the edge.
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

        double densityAtm = 0;
        double duration_to_atm = 0;
        public float current_altitude_km = 0;

        public override void Start()
        {
            base.Start();

            current_vessel.SetThrottle(0);
            TimeWarpTools.SetRateIndex(0, false);
            turn_to = new TurnTo();
            turn_to.StartProGrade(SpeedDisplayMode.Surface);
        }

        public override void updateUI(VisualElement root_el, FullStatus st)
        {
            st.Status("Coasting");

            if (!turn_to.finished)
                st.Console(turn_to.status_line);
            else if (lift_settings.coasting_warp.V)
                st.Console($"End warp : {StrTool.DurationToString(duration_to_atm)} x{TimeWarpTools.CurrentRate}");
        }

        public override void UpdateInfoRows(System.Action<string, string> addRow)
        {
            addRow("Altitude", $"{current_altitude_km:n2} km");
            addRow("Atm Density", $"{densityAtm:n2}");
        }

        public override void Update()
        {
            finished = false;

            CelestialBodyComponent mainBody = current_vessel.currentBody();
            if (lift_settings.destination_Ap_km.V < mainBody.atmosphereDepth / 1000)
            {
                lift.EndLiftPilot(false, "Warning Ap is under Atm. limit");
                return;
            }

            // atmosphereDepth is measured from sea level, so the height above terrain doesn't
            // apply here.
            double altitude_m = current_vessel.GetSeaAltitude();
            current_altitude_km = (float)(altitude_m / 1000);
            densityAtm = mainBody.GetPressure(altitude_m);

            double left_m = mainBody.atmosphereDepth - altitude_m;
            if (left_m < 0)
            {
                finished = true;
                return;
            }

            turn_to.Update();

            // With Coasting Warp off only the warp is skipped: the coast still has to reach the
            // edge before Adjust can make up what drag took off apoapsis.
            if (!lift_settings.coasting_warp.V)
                return;

            if (!turn_to.finished)
            {
                TimeWarpTools.SetRateIndex(0, false);
                return;
            }

            double vertical_speed = current_vessel.VesselVehicle.VerticalSpeed;
            duration_to_atm = vertical_speed > 0 ? left_m / vertical_speed : 0;
            TimeWarpTools.SetRateIndex(WarpToSettings.compute_wanted_warp_index(duration_to_atm), false);
        }
    }
}
