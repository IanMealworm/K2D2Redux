using KSP.Game;
using KSP.Sim.impl;
using UnityEngine;

namespace KSP2FlightAssistant.KSPService
{
    public class KSPVesselInformation
    {
        public TelemetryDataProvider TelemetryDataProvider { get; set; }
        public bool IsInitialized = false;

        //Game.ViewController.DataProvider.TelemetryDataProvider.NAVBallRotation.GetValue().z

        public KSPVesselInformation()
        {

        }

        public void Initialize(GameInstance game)
        {
            TelemetryDataProvider = game.ViewController.DataProvider.TelemetryDataProvider;

            IsInitialized = true;
        }

        public void Destroy()
        {
            TelemetryDataProvider = null;

            IsInitialized = false;
        }

        public Vector3 GetManeuverNodeVector()
        {
            // CORRECTED (this comment replaces an earlier, wrong "fix"): a prior verification pass
            // changed this from .GetValue() to .Value, based on an incomplete IL check (monodis's
            // --strings dump, which missed the real method - it truncates/misses on this large an
            // assembly). A live Unity build against the real assemblies proved that wrong: CS1061,
            // 'PropertyExternal<Vector3>' has no 'Value'. Full MethodDef-table enumeration (via
            // dnfile, which doesn't share monodis's large-assembly crash issue) confirms
            // KSP.Api.CoreTypes.PropertyExternal<T> has no Value/get_Value member at all - its actual
            // public accessor is GetValue(). Reverted to the original, correct call.
            return TelemetryDataProvider.ManeuverMarkerVector.GetValue();

        }

        /*public double GetApoapsis()
        {
            return TelemetryDataProvider.
        }*/

        public static IGGuid GetGlobalIDActiveVessel(VesselComponent vesselComponent)
        {
            return vesselComponent.SimulationObject.GlobalId;
        }



    }
}