using System;
using KSP.Game;
using KSP.Sim;
using KSP.Sim.impl;
using KSP.Sim.Maneuver;
using KSP.Sim.State;
using UnityEngine;

namespace K2D2.KSPService
{
    public class KSPVessel
    {
        public GameInstance Game => GameManager.Instance == null ? null : GameManager.Instance.Game;

        private TelemetryDataProvider telemetryDataProvider;

        public VesselComponent VesselComponent;

        public VesselVehicle VesselVehicle;

        static KSPVessel _current;

        public static KSPVessel current
        {
            get
            {
                return _current;
            }
        }

        public void Update()
        {
            _current = this;
            VesselComponent = GetActiveSimVessel();
            VesselVehicle = GetActiveSimVehicle();
            if (Game == null || Game.ViewController == null) return;
            telemetryDataProvider = this.Game.ViewController.DataProvider.TelemetryDataProvider;
        }

        //==================================================================================================================

        /// <summary>
        /// Retrieve the active vessel from the game
        /// </summary>
        /// <returns> VesselComponent</returns>
        public VesselComponent GetActiveSimVessel()
        {
            if (Game == null) return null;
            if (Game.ViewController == null) return null;
            if (!Game.ViewController.TryGetActiveSimVessel(out var vessel)) return null;
            return vessel as VesselComponent;
        }

        public VesselVehicle GetActiveSimVehicle()
        {
            if (Game == null) return null;
            if (Game.ViewController == null) return null;
            if (!Game.ViewController.TryGetActiveVehicle(out var vehicle)) return null;
            return vehicle as VesselVehicle;
        }


        public CelestialBodyComponent currentBody()
        {
            if (VesselComponent == null) return null;
            return VesselComponent.mainBody;
        }

        public void SetThrottle(float throttle)
        {
            VesselVehicle = GetActiveSimVehicle();
            // WARNING can only be called from Update not FixedUpdate
            if (VesselVehicle == null) return;

            throttle = Mathf.Clamp01(throttle);

            var update = new FlightCtrlStateIncremental
            {
                mainThrottle = throttle
            };

            VesselVehicle.AtomicSet(update);
        }

        public Vector GetAngularSpeed()
        {
            if (VesselVehicle == null) return new Vector();

            return VesselVehicle.AngularVelocity.relativeAngularVelocity;
        }

        public Rotation GetRotation()
        {
            if (VesselVehicle == null) return new Rotation();
            return VesselVehicle.VehicleTelemetry.Rotation;
        }

        public ManeuverNodeData GetNextManeuveurNode()
        {
            var maneuvers = Game.SpaceSimulation?.Maneuvers;
            if (maneuvers == null) return null;

            var current_vehicle = VesselVehicle;
            if (current_vehicle == null) return null;

            var activeNodes = maneuvers.GetNodesForVessel(current_vehicle.Guid);
            ManeuverNodeData next_node = (activeNodes.Count > 0) ? activeNodes[0] : null;
            return next_node;
        }

        public ManeuverPlanSolver GetPlanSolver()
        {
            ManeuverPlanSolver solver = null;
            solver = VesselComponent?.Orbiter?.ManeuverPlanSolver;
            return solver;
        }

        public void SetSpeedMode(SpeedDisplayMode mode)
        {
            if (VesselVehicle == null) return;
            VesselVehicle.SetSpeedDisplayMode(mode);
        }

        // Control inputs===================================================================================================



        public float X
        {
            get
            {
                if (VesselVehicle == null) return 0;
                return VesselVehicle.X;
            }
            set
            {
                if (VesselVehicle == null) return;

                value = Mathf.Clamp(value, -1, 1);

                var update = new FlightCtrlStateIncremental
                {
                    X = value
                };

                VesselVehicle.AtomicSet(update);
            }
        }
        public float Y
        {
            get
            {
                if (VesselVehicle == null) return 0;
                // Our Y is the game's Z axis.
                return VesselVehicle.Z;
            }
            set
            {
                if (VesselVehicle == null) return;
                value = Mathf.Clamp(value, -1, 1);

                var update = new FlightCtrlStateIncremental
                {
                    Z = value
                };

                VesselVehicle.AtomicSet(update);
            }
        }

        public float Z
        {
            get
            {
                if (VesselVehicle == null) return 0;
                // Our Z is the game's -Y axis.
                return -VesselVehicle.Y;
            }
            set
            {
                if (VesselVehicle == null) return;

                value = Mathf.Clamp(value, -1, 1);

                var update = new FlightCtrlStateIncremental
                {
                    Y = -value
                };

                VesselVehicle.AtomicSet(update);
            }
        }

        public float Pitch
        {
            get
            {
                if (VesselVehicle == null) return 0;

                return VesselVehicle.pitch;
            }
            set
            {
                if (VesselVehicle == null) return;

                value = Mathf.Clamp(value, -1, 1);

                var update = new FlightCtrlStateIncremental
                {
                    pitch = value
                };

                VesselVehicle.AtomicSet(update);
            }
        }

        public float Roll
        {
            get
            {
                if (VesselVehicle == null) return 0;
                return VesselVehicle.roll;
            }
            set
            {
                if (VesselVehicle == null) return;

                value = Mathf.Clamp(value, -1, 1);

                var update = new FlightCtrlStateIncremental
                {
                    roll = value
                };

                VesselVehicle.AtomicSet(update);
            }
        }

        // AutoPilot========================================================================================================


        public VesselAutopilot Autopilot => VesselComponent == null ? null : VesselComponent.Autopilot;


        // Telemetry Data===================================================================================================


        /// <summary>
        /// Retrieve the current activation value of throttle
        /// </summary>
        public double GetThrottle()
        {
            return VesselComponent.flightCtrlState.mainThrottle;
        }


        // =================================================================================================================


        // General Vessel Data===================================================================================================

        public AltimeterDisplayMode GetAltimeterDisplayMode()
        {
            // PropertyExternal<T> has GetValue(), not a Value property.
            return Game.ViewController.DataProvider.TelemetryDataProvider.AltimeterDisplayMode.GetValue();
        }


        public bool Landed()
        {
            return VesselComponent.Landed;
        }

        // Height of the vessel's bottom above the ground, roughly: the centre's height less the
        // bounding-sphere radius. 0 while landed or splashed down.
        public double GetApproxAltitude()
        {
            if (VesselComponent.Landed || VesselComponent.Splashed)
                return 0;
            var result = GetGlobalGroundAltitude() - VesselComponent.SimulationObject.objVesselBehavior.BoundingSphere.radius;
            return Math.Max(0, result);
        }

        public double GetGroundAltitude()
        {
            return telemetryDataProvider.GetAltitudeDisplayValue(AltimeterDisplayMode.GroundLevel);
        }


        public double GetSeaAltitude()
        {
            return telemetryDataProvider.GetAltitudeDisplayValue(AltimeterDisplayMode.SeaLevel);
        }

        // The lower of the sea level and ground heights. Near the space center the ground height
        // can fall back to the sea level one when the terrain or the pad isn't picked up, which
        // on the pad reads tens of metres high; low down, the vessel's own terrain height then
        // stands in for it.
        public double GetGlobalGroundAltitude()
        {
            double sea = GetSeaAltitude();
            double ground = GetGroundAltitude();
            double altitude = Math.Min(sea, ground);
            if (Math.Abs(ground - sea) < 0.5 && sea < 3000)
            {
                double from_terrain = VesselComponent.AltitudeFromTerrain;
                if (!double.IsNaN(from_terrain) && from_terrain > -50 && from_terrain < altitude)
                    altitude = from_terrain;
            }
            return altitude;
        }





        // =================================================================================================================

        public IGGuid GetGlobalIDActiveVessel()
        {
            return VesselComponent.SimulationObject.GlobalId;
        }

    }
}