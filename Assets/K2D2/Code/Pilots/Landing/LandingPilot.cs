using System;
using K2D2.KSPService;
using KSP.Sim;
using KSP.Sim.impl;
using KTools;
// using KTools.UI;
using K2D2.Controller;
using K2D2.Node;
using ILogger = ReduxLib.Logging.ILogger;

namespace K2D2.Landing
{
    public class LandingPilot : Pilot
    {
        public ILogger logger = ReduxLib.ReduxLib.GetLogger("K2D2.LandingController");

        internal LandingSettings settings;

        public static LandingPilot Instance { get; set; }

        public KSPVessel current_vessel;

        public BurndV burn_dV = new BurndV();

        public WarpTo warp_to = new WarpTo();

        public TouchDown brake = new TouchDown();

        public SingleExecuteController current_executor = new SingleExecuteController();

        public LandingPilot()
        {
            settings = new LandingSettings();
            _page = new LandingUI(this);

            Instance = this;
            debug_mode_only = false;

            K2D2PilotsMgr.Instance.RegisterPilot("Land", this);

            sub_contollers.Add(burn_dV);
            sub_contollers.Add(current_executor);

            // logger.LogMessage("LandingController !");
            current_vessel = K2D2_Plugin.Instance.current_vessel;
        }


        public enum Mode
        {
            Off,
            Pause,
            QuickWarp,
            RotationWarp,
            Waiting,
            Brake,
            TouchDown
        }

        public Mode mode = Mode.Off;


        double end_pause_Ut;

        public void setMode(Mode mode)
        {
            if (mode == this.mode)
                return;

            logger.LogInfo("setMode " + mode);

            this.mode = mode;

            if (mode == Mode.Off)
            {
                TimeWarpTools.SetRateIndex(0, false);
                current_executor.setController(null);
                return;
            }
            switch (mode)
            {
                case Mode.Off:
                    current_executor.setController(null);
                    break;
                case Mode.Pause:
                    end_pause_Ut = GeneralTools.Current_UT + settings.pause_time;
                    current_vessel.SetThrottle(0);
                    break;
                case Mode.QuickWarp:
                    current_vessel.SetThrottle(0);
                    if (!settings.auto_warp.V)
                        setMode(Mode.Waiting);
                    else
                    {
                        current_executor.setController(warp_to);
                        warp_to.Start_Retrograde(startSafeWarp_UT);
                        warp_to.max_warp_index = 6;
                    }
                    break;
                case Mode.RotationWarp:
                    current_vessel.SetThrottle(0);
                    if (!settings.auto_warp.V)
                        setMode(Mode.Waiting);
                    else
                    {
                        current_executor.setController(warp_to);
                        warp_to.Start_Retrograde(startBurn_UT, true);
                        warp_to.max_warp_index = 2;
                    }
                    break;
                case Mode.Waiting:
                    current_vessel.SetThrottle(0);
                    current_executor.setController(null);
                    break;
                case Mode.Brake:
                case Mode.TouchDown:
                    current_executor.setController(brake);
                    break;
            }

            logger.LogInfo("current_pilot " + mode);
        }

        public void nextMode()
        {
            // start
            if (mode == Mode.Off)
            {
                isRunning = true;
                return;
            }

            var next = this.mode + 1;
            setMode(next);
        }

        bool _active = false;
        public override bool isRunning
        {
            get { return _active; }
            set
            {
                if (value == _active)  return;

                if (!value)
                {
                    // stop
                    if (current_vessel != null)
                        current_vessel.SetThrottle(0);

                    setMode(Mode.Off);
                    _active = false;
                }
                else
                {
                    // Start total burn counter
                    burn_dV.reset();

                    // reset controller to desactivate other controllers.
                    K2D2_Plugin.ResetControllers();

                    _active = true;
                    setMode(Mode.QuickWarp);
                }

                // send call backs
                base.isRunning = value; 
            }
        }

        public override void onReset()
        {
            isRunning = false;
        }

        internal float current_falling_speed = 0;

        internal bool collision_detected = false;

        internal double adjusted_collision_UT = 0;
        internal double startBurn_UT = 0;
        internal double startSafeWarp_UT = 0;
        internal double speed_collision;
        internal double burn_duration;

        public void computeValues()
        {
            collision_detected = false;
            var current_vessel = K2D2_Plugin.Instance.current_vessel;
            if (current_vessel == null)
            {
                // UI_Tools.Console("no vessel");
                return;
            }

            // Same orbit-cast issue as Ascent.cs: VesselComponent.Orbit can be a CurrentPatchedConicsOrbit
            // for the actively-flown vessel, not just PatchedConicsOrbit, so the hard cast threw
            // InvalidCastException every Update() while landing. GetOrbitalVelocityAtUTZup is on IOrbit
            // (which IKeplerPatch extends), so no concrete cast is needed here.
            IKeplerPatch orbit = current_vessel.VesselComponent.Orbit;

            collision_detected = compute_real_collision();
            speed_collision = orbit.GetOrbitalVelocityAtUTZup(adjusted_collision_UT).magnitude;
            burn_duration = (speed_collision / burn_dV.full_dv);

            compute_startBurn();
        }

        public void compute_startBurn()
        {
            startBurn_UT = adjusted_collision_UT - burn_duration - settings.burn_before.V;
            startSafeWarp_UT = startBurn_UT - settings.rotation_warp_duration.V;
        }

        public bool compute_real_collision()
        {
            // start in 2 minutes
            double start_time = GeneralTools.Game.UniverseModel.UniverseTime + 2 * 60;
            bool collide = false;

            // FIXED (was silently broken since the original SpaceWarp1 K2D2 mod, not just this
            // Redux port - collision detection would essentially never trigger, which is also why
            // Brake would warp to a nonsense time: the scheduling math below reads
            // adjusted_collision_UT unconditionally, garbage or not).
            //
            // The old approach called orbit.GetTruePositionAtUT() (or, pre-Redux,
            // GetStateVectorsFromUT() - concrete-class-only, which is why the port moved off it, see
            // NOTICE.md) and paired the result with body.coordinateSystem to build a Position for
            // GetAltitudeFromTerrain. That never actually landed in the body's own frame - the
            // sampled "terrain altitude" for a vessel sitting at ~10km real altitude came back in the
            // hundreds of thousands to tens of millions of meters, varying with body and elapsed
            // search time, so collide never triggered.
            //
            // The real fix, found by reading KontrolSystem2's orbit/terrain code
            // (github.com/untoldwind/KontrolSystem2) rather than guessing further:
            //  1. orbit.GetRelativePositionAtUTZup(ut) returns a Vector3d already relative to the
            //     orbit's reference body - no separate reframing needed - but in "Zup" convention
            //     (Z is "up", standard orbital-mechanics axis order), not Unity's Y-up. It needs its
            //     Y/Z components swapped before use with Position/Vector. GetOrbitalVelocityAtUTZup
            //     (unchanged, a few lines below) has this exact same convention but nobody noticed
            //     because it's only ever consumed via .magnitude, which doesn't care about axis
            //     order.
            //  2. That swapped vector needs to be paired with body.SimulationObject.transform.
            //     celestialFrame, not body.coordinateSystem - confirmed against KontrolSystem2's
            //     BodyWrapper.cs, which builds every body-relative Position that way.
            // Verified in-game: real collision now detected with sane numbers on both a Mun descent
            // and a Kerbin boostback landing, and both completed a full autopilot landing end to end.
            IKeplerPatch orbit = current_vessel.VesselComponent.Orbit;
            var body = orbit.referenceBody;
            double current_time_ut = GeneralTools.Game.UniverseModel.UniverseTime;
            double deltaTime = 60; // seconds in the future
            int max_occurrences = 100;
            double time = start_time;
            double terrainAltitude = 0;

            float radius = current_vessel.VesselComponent.SimulationObject.objVesselBehavior.BoundingSphere.radius;

            for (int i = 0; i < max_occurrences; i++)
            {
                Vector3d rel_pos_zup = orbit.GetRelativePositionAtUTZup(time);
                // Zup -> Yup: swap Y and Z before this is usable as a Position's local vector (see
                // the fix note above).
                Vector3d rel_pos = new Vector3d(rel_pos_zup.x, rel_pos_zup.z, rel_pos_zup.y);
                Position ps = new Position(body.SimulationObject.transform.celestialFrame, rel_pos);
                double sceneryOffset;

                body.GetAltitudeFromTerrain(ps, out terrainAltitude, out sceneryOffset);
                // terrainAltitude -= radius;

                if (i == 0)
                {
                    logger.LogInfo($"compute_real_collision: first sample terrainAltitude={terrainAltitude:n1} at UT+{start_time - current_time_ut:n0}s");
                }

                if (terrainAltitude < 0)
                {
                    collide = true;
                    if (deltaTime > 0)
                    {
                        // dychotomy
                        deltaTime = -deltaTime / 2;
                    }
                    time += deltaTime;
                }
                else
                {
                    if (deltaTime < 0)
                    {
                        // dychotomy
                        deltaTime = -deltaTime / 2;
                    }
                    time += deltaTime;
                }

                if (Math.Abs(terrainAltitude) < 1)
                {
                    break;
                }
            }

            logger.LogInfo($"compute_real_collision: collide={collide} final terrainAltitude={terrainAltitude:n1} adjusted_collision_UT+{time - current_time_ut:n0}s");

            adjusted_collision_UT = time;
            return collide;
        }
        Vector SurfaceVelocity;
        public override void Update()
        {
            if (!page.isVisible && !isRunning) return;
            if (current_vessel == null || current_vessel.VesselVehicle == null)
                return;

            altitude = (float)current_vessel.GetApproxAltitude();

            SurfaceVelocity = current_vessel.VesselVehicle.SurfaceVelocity;
            SurfaceVelocity.Reframe(current_vessel.VesselVehicle.Up.coordinateSystem);
            current_falling_speed = (float)-SurfaceVelocity.vector.y;

            // detect collision and compute time to burn
            computeValues();

            if (!collision_detected)
            {
                // Once close enough to the ground, "no predicted collision" usually just means the
                // patched-conics bisection search in compute_real_collision() can't resolve one this
                // close in anymore (not that the danger is gone) - falling back straight to Touch Down
                // is correct there, same as before. But that same collision_detected flag can also blip
                // false for a single frame much higher up in the descent (the search is fragile, and an
                // active Brake burn keeps changing the coasting orbit it extrapolates from every frame)
                // - snapping straight to Touch Down THEN was skipping the Brake phase entirely and
                // free-falling in far too early, which matches Reese's report of the pilot suddenly
                // reporting "no collision" mid-descent and jumping to immediate touchdown. Gating the
                // fallback behind the touchdown-altitude threshold means a transient false negative
                // higher up just gets ignored and re-checked next frame, while the legitimate
                // close-to-the-ground case still falls back exactly as it did before.
                if (isRunning)
                {
                    if (altitude < settings.start_touchdown_altitude.V)
                        setMode(Mode.TouchDown);
                }
                else
                {
                    // no more collision
                    isRunning = false;
                }
            }

            if (!isRunning)
                return;

            // landing detection....
            if (altitude < 5 && current_falling_speed < 1)
            {
                //current_vessel.SetThrottle(0);
                isRunning = false;
                return;
            }
            if (mode == Mode.Pause)
            {
                if (GeneralTools.Current_UT > end_pause_Ut)
                {
                    setMode(Mode.QuickWarp);
                }
                return;
            }

            if (mode == Mode.QuickWarp)
            {
                warp_to.UT = startSafeWarp_UT;
            }
            else if (mode == Mode.RotationWarp)
            {
                warp_to.UT = startBurn_UT;
            }
            else if (mode == Mode.Waiting)
            {
                var dt = startBurn_UT - GeneralTools.Game.UniverseModel.UniverseTime;
                if (dt <= 0)
                {
                    nextMode();
                    return;
                }
            }
            else if (mode == Mode.Brake)
            {
                brake.max_speed = 0;
                brake.gravity_compensation = true;
                if (current_falling_speed < settings.brake_speed)
                {
                    // we reached the speed to stop brake
                    // check next phase
                    if (altitude < settings.start_touchdown_altitude.V)
                    {
                        setMode(Mode.TouchDown);
                    }
                    else
                    {
                        // too high altitude retry.... very worng burn time ......
                        setMode(Mode.Pause);
                    }
                    return;
                }
            }
            else if (mode == Mode.TouchDown)
            {
                TimeWarpTools.SetRateIndex(0, false);
                brake.max_speed = settings.compute_limit_speed(altitude);
                brake.gravity_compensation = true;
            }

            // call the sub controllers
            base.Update();

            if (current_executor.finished)
            {
                // auto next
                nextMode();
            }
        }

        public float altitude;


    }
}
