using System.Collections.Generic;
using K2D2.Controller;
using KSP.Sim.DeltaV;
// using KTools.UI;
using UnityEngine;
using ILogger = ReduxLib.Logging.ILogger;

namespace K2D2.KSPService
{
    public class BurndV : BaseController
    {
        public ILogger logger = ReduxLib.ReduxLib.GetLogger("K2D2.BurndV");

        KSPVessel current_vessel;

        public BurndV()
        {
            current_vessel = K2D2_Plugin.Instance.current_vessel;
        }

        public float burned_dV = 0;

        public void reset()
        {
            burned_dV = 0;
        }


        public Vector3 actual_thrust;
        public float actual_dv;

        public Vector3 full_thrust;
        public float full_dv;

        // Only the engines lit right now (staged or switched on, not flamed out). full_dv also
        // counts engines still waiting in later stages.
        public float active_full_dv;
        public bool has_active_engine => active_full_dv > 0;

        public override void FixedUpdate()
        {
            burned_dV += actual_dv * Time.fixedDeltaTime;
        }

        public override void LateUpdate()
        {
            Compute_Thrust();
        }

        // it is the way engine burning is computed in KSP
        public bool Engine_Running(DeltaVEngineInfo engine_info)
        {
            return (engine_info.Engine.EngineIgnited && engine_info.Engine.RequestedMassFlow > 0f);
        }

        float compute_full_thrust(DeltaVEngineInfo engineInfo)
        {
            var partref = engineInfo.PartInfo.PartRef;
            float staticPressureAtm = partref.StaticPressureAtm;
            if (staticPressureAtm > 0f)
            {
                return engineInfo.Engine.MaxThrustOutputAtm(runningActive: false, useThrustLimiter: true, staticPressureAtm, partref.AtmosphericTemperature, partref.AtmDensity);
            }
            else if (engineInfo.RequiresAir)
            {
                return 0f;
            }
            else
            {
                return engineInfo.Engine.MaxThrustOutputVac();
            }
        }

        public void Compute_Thrust()
        {
            if (current_vessel.VesselComponent == null) return;
            VesselDeltaVComponent delta_v = current_vessel.VesselComponent.VesselDeltaV;
            if (delta_v == null) return;

            var totalMass = current_vessel.VesselComponent.totalMass;

            actual_thrust = Vector3.zero;
            full_thrust = Vector3.zero;
            Vector3 active_full_thrust = Vector3.zero;
            List<DeltaVEngineInfo> engineInfos = delta_v.EngineInfo;
            for (int i = 0; i < engineInfos.Count; i++)
            {
                DeltaVEngineInfo engineInfo = engineInfos[i];
                if (engineInfo.Engine == null || engineInfo.PartInfo?.PartRef == null)
                    continue;

                // The delta-v list can still hold an engine for a frame after it's destroyed (in a
                // crash, say), and the game's own thrust getters then throw on its missing data.
                // That engine has no thrust to count either way.
                try
                {
                    Vector3 vector = engineInfo.Engine.ThrustDirRelativePartWorldSpace;
                    actual_thrust += vector * engineInfo.Engine.FinalThrustValue;
                    Vector3 thrust = vector * compute_full_thrust(engineInfo);
                    full_thrust += thrust;
                    if (engineInfo.Engine.IsOperational)
                        active_full_thrust += thrust;
                }
                catch (System.NullReferenceException)
                {
                }
            }

            actual_dv = (float)(actual_thrust.magnitude / totalMass);
            full_dv = (float)(full_thrust.magnitude / totalMass);
            active_full_dv = (float)(active_full_thrust.magnitude / totalMass);
        }

        // public override void onGUI()
        // {
        //     if (current_vessel.VesselComponent == null) return;
        //     VesselDeltaVComponent delta_v = current_vessel.VesselComponent.VesselDeltaV;
        //     if (delta_v == null)
        //     {
        //         UI_Tools.Error("NO VesselDeltaVComponent");
        //         return;
        //     }
        //     //List<DeltaVEngineInfo> engineInfos = delta_v.EngineInfo;

        //     var vehicle = current_vessel.VesselVehicle;
        //     if (vehicle == null) return;
        //     var mainThrottle = vehicle.mainThrottle;

        //     //UI_Tools.Console($"nb_engines {engineInfos.Count}  ");


        //     UI_Tools.Console($"mainThrottle {mainThrottle}");
        //     // UI_Tools.Console($"actual_thrust  {Tools.printVector(actual_thrust)}  ");
        //     UI_Tools.Console($"actual_dv  {actual_dv:n5}  ");


        //     // UI_Tools.Console($"full_thrust  {Tools.printVector(full_thrust)}  ");
        //     UI_Tools.Console($"full_dv  {full_dv:n5}  ");
        //     UI_Tools.Console($"burned_dV <b> {burned_dV:n5} </b>");
        //     if (GUILayout.Button("Reset"))
        //         burned_dV = 0;
        // }
    }
}
