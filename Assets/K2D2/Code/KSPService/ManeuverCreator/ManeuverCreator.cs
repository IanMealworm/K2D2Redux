using System;
using System.Collections;
using System.Collections.Generic;
using KSP.Game;
using KSP.Map;
using KSP.Sim;
using KSP.Sim.impl;
using KSP.Sim.Maneuver;
using KSP2FlightAssistant.MathLibrary;
using UnityEngine;
using ILogger = ReduxLib.Logging.ILogger;


namespace K2D2.KSPService
{
    public class ManeuverCreator
    {
        // Fields-------------------------------------------------------------------------------------------------------

        #region fields

        private VesselComponent _vesselComponent;
        public GameInstance Game => GameManager.Instance == null ? null : GameManager.Instance.Game;

        public KSPVessel kspVessel { get; set; }

        public ILogger logger = ReduxLib.ReduxLib.GetLogger("K2D2.CircleController");

        public ManeuverCreator()
        {

        }

        public void Update()
        {
            kspVessel = K2D2_Plugin.Instance.current_vessel;
            _vesselComponent = kspVessel.GetActiveSimVessel();
        }

        #endregion

        // Functions----------------------------------------------------------------------------------------------------

        #region CicrularizeOrbit

        public double CircularizeOrbitApoapsis()
        {
            PatchedConicsOrbit orbit = GetLastOrbit() as PatchedConicsOrbit;

            if (orbit.eccentricity >= 1)
            {
                logger.LogMessage("Apoapsis Circularization not possible for hyperbolic orbits");
                return CircularizeHyperbolicOrbit();
            }

            double gravitation = orbit.ReferenceBodyConstants.StandardGravitationParameter;
            double periapsis = orbit.Periapsis;
            double apoapsis = orbit.Apoapsis;

            double circularizedVelocity = VisVivaEquation.CalculateVelocity(apoapsis, apoapsis,
                apoapsis, gravitation);

            double apoapsisVelocity = VisVivaEquation.CalculateVelocity(apoapsis,
                apoapsis,
                periapsis,
                gravitation);

            double deltaV = circularizedVelocity - apoapsisVelocity;


            Vector3d burnVector = ProgradeBurnVector(deltaV);

            // ManeuverNodeController.NodeControl.CreateManeuverNodeAtUT(burnVector, GeneralTools.Game.UniverseModel.UniverseTime + orbit.TimeToAp ,0);
            CreateManeuverNode(burnVector, 180);
            return deltaV;
        }

        public double CircularizeOrbitPeriapsis()
        {
            PatchedConicsOrbit orbit = GetLastOrbit() as PatchedConicsOrbit;
            if (orbit.eccentricity >= 1)
            {
                logger.LogMessage("Periapsis Circularization not possible for hyperbolic orbits");
                return CircularizeHyperbolicOrbit();
            }

            double periapsis = orbit.Periapsis;
            double apoapsis = orbit.Apoapsis;
            double eccentricity = orbit.eccentricity;
            logger.LogMessage($"AP: {orbit.Apoapsis} PE: {orbit.Periapsis}");
            double gravitation = orbit.ReferenceBodyConstants.StandardGravitationParameter;
            //double gravitation = _vesselComponent.Orbit.ReferenceBodyConstants.StandardGravitationParameter;
            double circularizedVelocity = VisVivaEquation.CalculateVelocity(periapsis,
                periapsis,
                periapsis,
                gravitation);

            double periapsisVelocity = VisVivaEquation.CalculateVelocity(periapsis,
                apoapsis,
                periapsis,
                gravitation);

            double deltaV = periapsisVelocity - circularizedVelocity;

            Vector3d burnVector = RetrogradeBurnVector(deltaV);
            // ManeuverNodeController.NodeControl.CreateManeuverNodeAtUT(burnVector, GeneralTools.Game.UniverseModel.UniverseTime + orbit.TimeToPe, 0);
            CreateManeuverNode(burnVector, 0);
            return deltaV;
        }

        public double CircularizeHyperbolicOrbit()
        {
            PatchedConicsOrbit orbit = GetLastOrbit() as PatchedConicsOrbit;
            double gravitation = orbit.ReferenceBodyConstants.StandardGravitationParameter;
            double periapsis = orbit.Periapsis;
            double orbitalEnergy = orbit.OrbitalEnergy;

            double currentVelocity = VisVivaEquation.CalculateHyperbolicVelocity(periapsis,
                gravitation,
                orbitalEnergy);

            double newPeriapsisVelocity = VisVivaEquation.CalculateVelocity(periapsis,
                periapsis,
                periapsis,
                gravitation);

            double deltaV = newPeriapsisVelocity - currentVelocity;

            Vector3d burnVector = ProgradeBurnVector(deltaV);
            CreateManeuverNode(burnVector, 0);
            return deltaV;
        }

        #endregion

        #region ChangeOrbit



        public void ChangePeriapsis(double OrbitDistance)
        {
            PatchedConicsOrbit orbit = GetLastOrbit() as PatchedConicsOrbit;


            double periapsis = orbit.Periapsis;
            double apoapsis = orbit.Apoapsis;
            double eccentricity = orbit.eccentricity;
            double gravitation = orbit.ReferenceBodyConstants.StandardGravitationParameter;

            if (eccentricity >= 1)
            {
                logger.LogMessage("Periapsis Change not possible for hyperbolic orbits");
                return;
            }

            double currentPeriapsisVelocity = VisVivaEquation.CalculateVelocity(apoapsis,
                apoapsis,
                periapsis,
                gravitation);

            double newPeriapsisVelocity = VisVivaEquation.CalculateVelocity(apoapsis,
                apoapsis,
                OrbitDistance,
                gravitation);

            double deltaV = newPeriapsisVelocity - currentPeriapsisVelocity;

            Vector3d burnVector = ProgradeBurnVector(deltaV);
            CreateManeuverNode(burnVector, 180);
        }

        public void ChangeApoapsis(double OrbitDistance)
        {
            PatchedConicsOrbit orbit = GetLastOrbit() as PatchedConicsOrbit;


            double periapsis = orbit.Periapsis;
            double apoapsis = orbit.Apoapsis;
            double eccentricity = orbit.eccentricity;
            double newEccentricity = (OrbitDistance - periapsis) / (OrbitDistance + periapsis);

            double gravitation = orbit.ReferenceBodyConstants.StandardGravitationParameter;
            double deltaV = 0;
            double currentApoapsisVelocity;

            if (eccentricity >= 1)
            {
                currentApoapsisVelocity = VisVivaEquation.CalculateHyperbolicVelocity(periapsis,
                    gravitation,
                    orbit.OrbitalEnergy);
            }
            else
            {
                currentApoapsisVelocity = VisVivaEquation.CalculateVelocity(periapsis,
                    apoapsis,
                    periapsis,
                    gravitation);
            }

            double newApoapsisVelocity = VisVivaEquation.CalculateVelocity(periapsis,
                OrbitDistance,
                periapsis,
                gravitation);

            deltaV = newApoapsisVelocity - currentApoapsisVelocity;


            Vector3d burnVector = ProgradeBurnVector(deltaV);
            CreateManeuverNode(burnVector, 0);
        }

        #endregion

        #region InterplanetaryTransfer
        public double HohmannTransfer(double UT, double OrbitDistance)
        {
            CircularizeOrbitApoapsis();
            double deltaV = 0;
            return deltaV;
        }

        #endregion

        // Internal Maneuver Services-----------------------------------------------------------------------------------

        #region Internal Maneuver Services

        private IPatchedOrbit GetLastOrbit()
        {
            List<ManeuverNodeData> patchList =
                Game.SpaceSimulation.Maneuvers.GetNodesForVessel(kspVessel.GetGlobalIDActiveVessel());

            logger.LogMessage(patchList.Count);

            if (patchList.Count == 0)
            {
                logger.LogMessage(_vesselComponent.Orbit);
                return _vesselComponent.Orbit;
            }

            logger.LogMessage(patchList[patchList.Count - 1].ManeuverTrajectoryPatch);
            IPatchedOrbit orbit = patchList[patchList.Count - 1].ManeuverTrajectoryPatch;

            return orbit;
        }

        /// <summary>
        /// Creates a maneuver node at a given true anomaly
        /// </summary>
        /// <param name="burnVector"></param>
        /// <param name="TrueAnomaly"></param>
        private void CreateManeuverNode(Vector3d burnVector, double TrueAnomaly)
        {
            K2D2_Plugin.Instance.StartCoroutine(CreateManeuverNode_Co(burnVector, TrueAnomaly));
        }

        private IEnumerator CreateManeuverNode_Co(Vector3d burnVector, double TrueAnomaly)
        {
            // FIXED during Redux port verification: VesselComponent.Orbit is typed KSP.Sim.IKeplerPatch in
            // the current assemblies, not PatchedConicsOrbit - an explicit cast is required (every other call
            // site in this file already does this via GetLastOrbit()'s "as PatchedConicsOrbit", this one was
            // the sole outlier that would not compile as-is).
            PatchedConicsOrbit referencedOrbit = (PatchedConicsOrbit)_vesselComponent.Orbit;

            double TrueAnomalyRad = TrueAnomaly * Math.PI / 180;
            double UT = referencedOrbit.GetUTforTrueAnomaly(TrueAnomalyRad, 0);

            var SimulationObject = _vesselComponent.SimulationObject;

            // Create Node
            ManeuverNodeData nodeData = new ManeuverNodeData(SimulationObject.GlobalId, false, UT);
            referencedOrbit.PatchEndTransition = PatchTransitionType.Maneuver;
            nodeData.SetManeuverState((PatchedConicsOrbit)referencedOrbit);

            nodeData.BurnVector = burnVector;

            Game.SpaceSimulation.Maneuvers.AddNodeToVessel(nodeData);

            yield return new WaitForFixedUpdate();

            MapCore mapCore = null;
            Game.Map.TryGetMapCore(out mapCore);
            if (mapCore)
            {
                mapCore.map3D.ManeuverManager.GetNodeDataForVessels();
                mapCore.map3D.ManeuverManager.UpdatePositionForGizmo(nodeData.NodeID);
                // mapCore.map3D.ManeuverManager.UpdateAll();
                // mapCore.map3D.ManeuverManager.RemoveAll();
            }


        }

        /// <summary>
        /// New for precision landing (LandingTargeting.cs / DeorbitBurn.cs): creates a real,
        /// visible maneuver node at a specific UT with a pure prograde/retrograde burn, rather
        /// than deriving the UT from a TrueAnomaly like CreateManeuverNode_Co above does. The
        /// deorbit/phasing burn needs to happen at a UT we've already computed ourselves (the
        /// resonance search in LandingTargeting.DeltaVToShiftNodeLongitude), not one implied by
        /// a true anomaly.
        ///
        /// FIXED (first in-game test): this originally copied CreateManeuverNode_Co's
        /// referencedOrbit.PatchEndTransition / nodeData.SetManeuverState((PatchedConicsOrbit)...)
        /// dance, which hard-casts _vesselComponent.Orbit to PatchedConicsOrbit - and threw
        /// InvalidCastException every time, because under Redux the actively-flown vessel's
        /// Orbit is a Redux.Ecs.Components.CurrentPatchedConicsOrbit (an ECS-backed class that
        /// implements the same interfaces - IKeplerPatch, IPatchedOrbit, etc. - but is NOT a
        /// PatchedConicsOrbit and can't be cast to one). Confirmed via ILSpy against the live
        /// Redux assembly, not guessed.
        ///
        /// The fix, confirmed against Map3DManeuvers.OnAddManeuver() (the real code behind
        /// clicking "add node" on the map, decompiled via ILSpy): that method only calls
        /// SetManeuverState when maneuverNodeData.IsOnManeuverTrajectory is true - i.e. when
        /// you're adding a node on top of an *existing* maneuver plan segment. For a first/only
        /// node (our case - isManeuver: false in the constructor below), it's skipped entirely,
        /// same as here. What it does NOT skip, and what this was actually missing, is
        /// nodeData.InitializeTransform() right after construction - that's what
        /// ManeuverPlanComponent.UpdateNodeDetails (called from AddNode/AddNodeToVessel) needed
        /// and was null-reffing on without it. So no PatchedConicsOrbit needed at all for this
        /// case - InitializeTransform() is the missing piece, not a replacement SetManeuverState
        /// call.
        ///
        /// Unlike CreateManeuverNode_Co, this returns the created ManeuverNodeData synchronously
        /// so the caller (DeorbitBurn) can hand it straight to its own TurnTo/WarpTo/BurnManeuver
        /// instances without waiting a frame - only the map/gizmo bookkeeping is deferred to a
        /// coroutine, same as the original.
        ///
        /// Note: the rest of this class (everything above) is unused anywhere else in K2D2Redux
        /// as of this writing, so treat it as unverified rather than proven - this method is new
        /// on top of that, and needs a real in-game test same as the rest of precision landing.
        /// </summary>
        public ManeuverNodeData CreateManeuverNodeAtUT(double UT, double progradeDeltaV)
        {
            Vector3d burnVector = ProgradeBurnVector(progradeDeltaV);

            var SimulationObject = _vesselComponent.SimulationObject;

            ManeuverNodeData nodeData = new ManeuverNodeData(SimulationObject.GlobalId, false, UT);
            nodeData.InitializeTransform();
            nodeData.BurnVector = burnVector;

            // IsOnManeuverTrajectory is false here (first/only node), so per
            // Map3DManeuvers.OnAddManeuver() SetManeuverState is correctly skipped - the engine's
            // own maneuver-plan pipeline (ManeuverPlanComponent.AddNode, same path the in-game
            // "add node" UI uses) resolves ManeuverTrajectoryPatch from here.
            Game.SpaceSimulation.Maneuvers.AddNodeToVessel(nodeData);

            K2D2_Plugin.Instance.StartCoroutine(UpdateMapGizmo_Co(nodeData));

            return nodeData;
        }

        /// <summary>
        /// Removes every maneuver node currently on the vessel's plan. Needed before creating a
        /// new node via CreateManeuverNodeAtUT above - AddNodeToVessel only ever appends (see
        /// that method's own comment: IsOnManeuverTrajectory is deliberately false, "first/only
        /// node"), so calling it again on top of a node that's already there (e.g. precision
        /// landing's Circularize node, still sitting in the plan once its burn finishes and
        /// DeorbitBurn starts) adds a second node instead of replacing it - confirmed in-game:
        /// the vessel ended up turning to align with the wrong node's direction once Deorbit
        /// started. Same ManeuverPlanComponent.RemoveNodes API the old (dead, FlightPlan-
        /// dependent) Lift Final.cs used for its create_ap/create_now buttons.
        /// </summary>
        public void RemoveAllNodes()
        {
            var maneuvers_component = _vesselComponent?.SimulationObject?.FindComponent<ManeuverPlanComponent>();
            if (maneuvers_component == null)
                return;

            List<ManeuverNodeData> nodes = maneuvers_component.GetNodes();
            if (nodes == null || nodes.Count == 0)
                return;

            // FIXED (first in-game test): GetNodes() hands back the component's own live list, not
            // a copy. Passing that straight into RemoveNodes() throws "Collection was modified;
            // enumeration operation may not execute" the moment RemoveNodes tries to enumerate the
            // exact list it's removing entries from - confirmed via the log, and it's what actually
            // broke DeorbitBurn after Circularize: the exception aborted Start() right at this call,
            // before it ever got to creating the new node (the ~2 seconds of ArgumentOutOfRange
            // spam right after in the log was the game's own UI repeatedly choking on the plan this
            // left half-mutated). Passing a copy so RemoveNodes enumerates a snapshot instead.
            maneuvers_component.RemoveNodes(new List<ManeuverNodeData>(nodes));
        }

        /// <summary>
        /// Removes every node on the plan, then creates a fresh one via CreateManeuverNodeAtUT -
        /// but a fixed update later, not in the same call. First in-game test of the remove-then-
        /// create sequence (precision landing's Circularize handing off to DeorbitBurn): the old
        /// node visibly disappeared (RemoveAllNodes worked), but the new deorbit node never
        /// actually showed up or did anything - the game "half saw" the removal. Calling
        /// CreateManeuverNodeAtUT immediately afterward, in the same frame as RemoveNodes, is the
        /// one thing that changed versus DeorbitBurn's first (working, node-count-zero) test, so
        /// that's the leading suspect - same reasoning as why AddNodeToVessel's own gizmo/map
        /// update below already waits a WaitForFixedUpdate rather than touching the map layer in
        /// the same frame it adds a node. This is the fix to try first; logs the node count right
        /// after creation so a next test confirms it either way if this isn't the whole story.
        /// </summary>
        public void RemoveAllNodesThenCreate(double UT, double progradeDeltaV, System.Action<ManeuverNodeData> onCreated)
        {
            RemoveAllNodes();
            K2D2_Plugin.Instance.StartCoroutine(RemoveAllNodesThenCreate_Co(UT, progradeDeltaV, onCreated));
        }

        private IEnumerator RemoveAllNodesThenCreate_Co(double UT, double progradeDeltaV, System.Action<ManeuverNodeData> onCreated)
        {
            yield return new WaitForFixedUpdate();

            var nodeData = CreateManeuverNodeAtUT(UT, progradeDeltaV);

            var maneuvers_component = _vesselComponent?.SimulationObject?.FindComponent<ManeuverPlanComponent>();
            int count_after = maneuvers_component?.GetNodes()?.Count ?? -1;
            logger.LogInfo($"[ManeuverCreator] RemoveAllNodesThenCreate: created node {nodeData?.NodeID} at UT={UT:n1} " +
                $"deltaV={progradeDeltaV:n2} - {count_after} node(s) now on the plan.");

            onCreated?.Invoke(nodeData);
        }

        private IEnumerator UpdateMapGizmo_Co(ManeuverNodeData nodeData)
        {
            yield return new WaitForFixedUpdate();

            MapCore mapCore = null;
            Game.Map.TryGetMapCore(out mapCore);
            if (mapCore)
            {
                mapCore.map3D.ManeuverManager.GetNodeDataForVessels();
                mapCore.map3D.ManeuverManager.UpdatePositionForGizmo(nodeData.NodeID);
            }
        }




        private VesselComponent activeVessel
        {
            get
            {

                return KSPVessel.current.VesselComponent;
            }
        }



        #endregion

        // Logging------------------------------------------------------------------------------------------------------

        #region Logging

        public void Log(ILogger logger, string message)
        {
            logger.LogMessage(message);
        }

        public void LogOrbit()
        {
            logger.LogMessage("================= Orbit Log =================");
            PatchedConicsOrbit orbit = GetLastOrbit() as PatchedConicsOrbit;
            logger.LogMessage($"AP: {orbit.Apoapsis} PE: {orbit.Periapsis}");
            logger.LogMessage($"SemiMajorAxis: {orbit.semiMajorAxis} SemiMinorAxis: {orbit.SemiMinorAxis}");
            logger.LogMessage($"Eccentricity: {orbit.eccentricity} ");
            logger.LogMessage($"Inclination: {orbit.inclination} ArgumentOfPeriapsis: {orbit.argumentOfPeriapsis}");
            logger.LogMessage("epoch: " + orbit.epoch);
            logger.LogMessage("referenceBody: " + orbit.referenceBody);
        }

        #endregion

        // Special Burning Vectors--------------------------------------------------------------------------------------

        #region Special Burning Vectors

        /// <summary>
        /// Burn Vector for a Prograde Maneuver(0,0,1 )* magnitude
        /// </summary>
        /// <param name="magnitude"></param>
        /// <returns>Prograde Burn Vector3d </returns>
        public Vector3d ProgradeBurnVector(double magnitude)
        {
            return new Vector3d(0, 0, magnitude);
            ;
        }

        /// <summary>
        /// Burn Vector for a Retrograde Maneuver(0,0,-1) * magnitude
        /// </summary>
        /// <param name="magnitude"></param>
        /// <returns>Retrograde Burn Vector3d</returns>
        public Vector3d RetrogradeBurnVector(double magnitude)
        {
            return new Vector3d(0, 0, -magnitude);
        }

        /// <summary>
        /// Burn Vector for a Normal Maneuver(0,1,0) * magnitude
        /// </summary>
        /// <param name="magnitude"></param>
        /// <returns>Normal Burn Vector3d</returns>
        public Vector3d NormalBurnVector(double magnitude)
        {
            return new Vector3d(0, magnitude, 0);
            ;
        }

        /// <summary>
        /// Burn Vector for a AntiNormal Maneuver(0,-1,0) * magnitude
        /// </summary>
        /// <param name="magnitude"></param>
        /// <returns>Anti Normal Burn Vector3d </returns>
        public Vector3d AntiNormalBurnVector(double magnitude)
        {
            return new Vector3d(0, -magnitude, 0);
            ;
        }

        /// <summary>
        /// Radial Out Burn Vector(1,0,0) * magnitude
        /// </summary>
        /// <param name="magnitude"></param>
        /// <returns>Radial Out Burn Vector3d</returns>
        public Vector3d RadialOutBurnVector(double magnitude)
        {
            return new Vector3d(magnitude, 0, 0);
            ;
        }

        /// <summary>
        /// Radial In Burn Vector(-1,0,0) * magnitude
        /// </summary>
        /// <param name="magnitude"></param>
        /// <returns>Radial In Burn Vector3d</returns>
        public Vector3d RadialInBurnVector(double magnitude)
        {
            return new Vector3d(-magnitude, 0, 0);
        }

        #endregion

        // Custom Functions---------------------------------------------------------------------------------------------

        #region Custom Functions

        public bool IsApoapsisFirst()
        {
            PatchedConicsOrbit orbit = GetLastOrbit() as PatchedConicsOrbit;
            double timePE = orbit.GetUTforTrueAnomaly(0, 0);
            double timeAP = orbit.GetUTforTrueAnomaly(Math.PI, 0);
            return timePE > timeAP;
        }

        public bool IsOrbitElliptic()
        {
            PatchedConicsOrbit orbit = GetLastOrbit() as PatchedConicsOrbit;
            return orbit.eccentricity < 1;
        }

        public double AddRadiusOfBody(double radius)
        {
            PatchedConicsOrbit orbit = GetLastOrbit() as PatchedConicsOrbit;
            return radius + orbit.ReferenceBodyConstants.Radius;
        }

        public double GetCurrentPeriapsis()
        {
            PatchedConicsOrbit orbit = GetLastOrbit() as PatchedConicsOrbit;
            return orbit.Periapsis;
        }

        public double GetCurrentApoapsis()
        {
            PatchedConicsOrbit orbit = GetLastOrbit() as PatchedConicsOrbit;
            return orbit.Apoapsis;
        }

        public Vector3d GetOrbitalVelocityAtUT(double UT)
        {
            double inclination = _vesselComponent.Orbit.inclination;
            double longitudeOfAscendingNode = _vesselComponent.Orbit.longitudeOfAscendingNode;
            Vector3d
                normalVector =
                    _vesselComponent.Orbit
                        .GetRelativeOrbitNormal(); //GetOrbitalNormalVector(UT, inclination, longitudeOfAscendingNode);
            // VERIFIED during Redux port verification: ReferenceBodyConstants exists only on the concrete
            // PatchedConicsOrbit class, not on the IKeplerPatch interface VesselComponent.Orbit is typed
            // as - needs an explicit cast, unlike inclination/eccentricity/semiMajorAxis/etc. above which
            // are genuinely on the interface and compile fine as-is.
            Vector3d velocity = GetOrbitalPerifocalVelocityVector(UT, _vesselComponent.Orbit.eccentricity,
                _vesselComponent.Orbit.semiMajorAxis, _vesselComponent.Orbit.meanAnomalyAtEpoch,
                ((PatchedConicsOrbit)_vesselComponent.Orbit).ReferenceBodyConstants.StandardGravitationParameter);

            Vector3d orbitalVelocity = Vector3d.Cross(normalVector, velocity);
            return orbitalVelocity;
        }

        /// <summary>
        /// Returns the orbital normal vector in the ECI frame
        /// Use VesselComponent.Orbit.GetRelativeOrbitNormal() instead of this function if you can
        /// </summary>
        /// <param name="UT"></param>
        /// <param name="inclination"></param>
        /// <param name="longitudeOfAscendingNode"></param>
        /// <returns></returns>
        public Vector3d GetOrbitalNormalVector(double UT, double inclination, double longitudeOfAscendingNode)
        {
            // Calculate the normal vector components in the perifocal frame
            double nx = Math.Cos(inclination) * Math.Cos(longitudeOfAscendingNode);
            double ny = Math.Cos(inclination) * Math.Sin(longitudeOfAscendingNode);
            double nz = Math.Sin(inclination);

            // Convert the normal vector from the perifocal frame to the ECI frame
            double cosRAAN = Math.Cos(longitudeOfAscendingNode);
            double sinRAAN = Math.Sin(longitudeOfAscendingNode);
            double cosI = Math.Cos(inclination);
            double sinI = Math.Sin(inclination);
            double cosTA = Math.Cos(UT);
            double sinTA = Math.Sin(UT);

            double ex = cosRAAN * cosTA - sinRAAN * sinTA * cosI;
            double ey = sinRAAN * cosTA + cosRAAN * sinTA * cosI;
            double ez = sinTA * sinI;

            return new Vector3d(ex, ey, ez);
        }

        /// <summary>
        /// Returns the orbital velocity vector in the ECI frame (Earth-centered inertial)
        /// </summary>
        /// <param name="semiMajorAxis"></param>
        /// <param name="eccentricity"></param>
        /// <param name="trueAnomaly"></param>
        /// <param name="inclination"></param>
        /// <param name="longitudeOfAscendingNode"></param>
        /// <returns></returns>
        public Vector3d GetOrbitalPerifocalVelocityVector(double semiMajorAxis, double eccentricity, double trueAnomaly,
            double inclination, double longitudeOfAscendingNode)
        {
            // Calculate the magnitude of the velocity vector
            double r = semiMajorAxis * (1 - eccentricity * eccentricity) / (1 + eccentricity * Math.Cos(trueAnomaly));
            // VERIFIED during Redux port verification: same ReferenceBodyConstants cast fix as above.
            double gravitation = ((PatchedConicsOrbit)_vesselComponent.Orbit).ReferenceBodyConstants.StandardGravitationParameter;
            // Calculate the magnitude of the velocity vector
            double v = Math.Sqrt(gravitation * (2 / r - 1 / semiMajorAxis));

            // Calculate the velocity vector components in the perifocal frame
            double vx = v * Math.Sin(trueAnomaly);
            double vy = v * (Math.Cos(trueAnomaly) + eccentricity);
            double vz = 0;

            // Convert the velocity vector from the perifocal frame to the ECI frame
            double cosRAAN = Math.Cos(longitudeOfAscendingNode);
            double sinRAAN = Math.Sin(longitudeOfAscendingNode);
            double cosArgPeriapsis = Math.Cos(_vesselComponent.Orbit.argumentOfPeriapsis);
            double sinArgPeriapsis = Math.Sin(_vesselComponent.Orbit.argumentOfPeriapsis);
            double cosInclination = Math.Cos(inclination);
            double sinInclination = Math.Sin(inclination);

            double x = cosRAAN * cosArgPeriapsis - sinRAAN * sinArgPeriapsis * cosInclination;
            double y = sinRAAN * cosArgPeriapsis + cosRAAN * sinArgPeriapsis * cosInclination;
            double z = sinArgPeriapsis * sinInclination;

            Vector3d perifocalVelocity = new Vector3d(vx, vy, vz);
            QuaternionD rotation = new QuaternionD(
                -sinRAAN * cosArgPeriapsis - cosRAAN * sinArgPeriapsis * cosInclination,
                cosRAAN * cosArgPeriapsis - sinRAAN * sinArgPeriapsis * cosInclination,
                sinRAAN * sinInclination,
                sinRAAN * cosArgPeriapsis * cosInclination + cosRAAN * sinArgPeriapsis);

            Vector3d velocityVector = rotation * perifocalVelocity;
            return velocityVector;
        }

        #endregion

        // Currently Not Implemented Functions--------------------------------------------------------------------------

        #region Unimplemented Functions

        public void deleteAllManeuvers()
        {
            throw new NotImplementedException();
        }

        #endregion

    }
}
