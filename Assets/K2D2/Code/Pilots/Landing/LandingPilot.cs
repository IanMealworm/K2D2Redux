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

        // Passes itself so TouchDown's closed-loop steering (see TouchDown.ComputeSteeredDirection)
        // can read predicted_landing_lat/lon, settings.target_latitude/longitude, and altitude off
        // this pilot directly rather than LandingPilot pushing a pile of individual fields into
        // TouchDown every Update() the way it already does for max_speed/gravity_compensation
        // below. Constructed in the constructor body (below), not here - 'this' isn't available
        // in an instance field initializer (CS0027, caught on the first build attempt).
        public TouchDown brake;

        // Precision landing's precondition phase, run before deorbit_burn below: circularizes
        // the starting orbit if it isn't already close enough to circular (or refuses if it's too
        // high). See Circularize.cs for why - deorbit_burn's own math assumes a roughly circular
        // starting orbit, so this makes that assumption hold instead of teaching the search to
        // cope with an arbitrary orbit shape.
        public Circularize circularize = new Circularize();

        // Precision landing's deorbit/phasing burn phase - see DeorbitBurn.cs for why this has
        // its own Turn/Warp/Burn instances instead of driving NodeExPilot.
        public DeorbitBurn deorbit_burn = new DeorbitBurn();

        public SingleExecuteController current_executor = new SingleExecuteController();

        public LandingPilot()
        {
            settings = new LandingSettings();
            brake = new TouchDown(this);
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
            // Circularize inserted right after Off (before DeorbitBurn), same reasoning as
            // DeorbitBurn's own insertion note below: nextMode() just does mode+1, so anything
            // inserted here needs to stay ahead of Pause/QuickWarp/.../TouchDown, which keep their
            // same relative ordering either way.
            Circularize,
            // DeorbitBurn = Off + 2 now (not +1) - still fine, nextMode() only cares about
            // relative order, not the exact enum values.
            DeorbitBurn,
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
                case Mode.Circularize:
                    current_executor.setController(circularize);
                    circularize.Start();
                    break;
                case Mode.DeorbitBurn:
                    current_executor.setController(deorbit_burn);
                    deorbit_burn.Start();
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

                    // Precision landing now starts with Circularize (which itself does nothing
                    // and finishes immediately if the orbit's already close enough to circular,
                    // or refuses with a status message if it's too high - see Circularize.cs),
                    // then plans and flies a real, visible deorbit/phasing node, before falling
                    // into the normal Pause->QuickWarp->...->TouchDown sequence. Everything else
                    // about that sequence is unchanged either way.
                    if (settings.precision_landing.V)
                        setMode(Mode.Circularize);
                    else
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

        // Precision landing (settings/UI side of this is in LandingSettings.cs). Predicted lat/lon
        // is computed alongside the collision check below, since it already has the terrain-
        // crossing time and position worked out. target_error_m is only meaningful once a target's
        // been set and precision_landing is on - it's not used to steer anything yet, just
        // displayed, so we can sanity-check the lat/lon math in-game before it drives the vessel.
        internal double predicted_landing_lat = 0;
        internal double predicted_landing_lon = 0;
        internal double target_error_m = 0;

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
            double burn_before = settings.burn_before.V;

            // Precision landing: TouchDown's closed-loop steering (see its ComputeSteeredDirection)
            // needs real time to act before touchdown, not just the right direction - confirmed
            // in-game, a correction attempted only in the last couple seconds before collision
            // barely moved a 20km miss, however correctly aimed. Closing a lateral error needs
            // lateral_deltaV * time_remaining >= error, so back out how much EXTRA margin (beyond
            // the efficiency-only burn_before setting) buys enough time for the maximum lateral
            // deltaV the steering angle cap allows to actually close the gap - a bigger miss earns
            // an earlier burn start (more fuel spent, more time to correct), a small one barely
            // changes anything. First-pass heuristic (treats it as one lateral kick + coast rather
            // than modeling the whole burn), not exact, but ties the "start sooner/later" question
            // directly to how far off target we actually are instead of a fixed margin.
            //
            // This used to only look at steering_max_angle (the heading/cross-track correction's
            // budget). That's what was behind the Minmus overshoot Reese kept seeing: target_error_m
            // is just a straight-line miss distance, it doesn't say whether the miss is left/right
            // (heading's job) or long/short (the arc correction's job, see TouchDown.
            // ComputeSteeredDirection) - so sizing the time margin off steering_max_angle alone was
            // assuming the generous 40 degree heading budget applied even to overshoots, when an
            // overshoot can only be fixed by the much tighter arc_shorten_max_angle (10 degrees by
            // default, deliberately conservative since tilting toward horizontal spends braking
            // margin). On low-gravity Minmus that mismatch bites hard: burn starts late because the
            // math thinks there's plenty of lateral authority, then the actual correction available
            // to shorten the arc is far smaller than assumed and the burn runs out of room before it
            // can pull the overshoot in. Using whichever of the three correction caps is smallest
            // (steering, extend, or shorten) sizes the margin off the worst case instead of the best
            // case - exactly Reese's own read on this: "if it were to start burning sooner the arc
            // would come way closer to the landing spot".
            double min_correction_angle_deg = Math.Min(brake.steering_max_angle.V,
                Math.Min(brake.arc_extend_max_angle.V, brake.arc_shorten_max_angle.V));

            if (settings.precision_landing.V && target_error_m > 0 && min_correction_angle_deg > 0)
            {
                double min_correction_angle_rad = min_correction_angle_deg * Math.PI / 180.0;
                double lateral_dv_budget = speed_collision * Math.Sin(min_correction_angle_rad);

                if (lateral_dv_budget > 0.1) // avoid a near-zero budget blowing this up
                {
                    double correction_time_needed = target_error_m / lateral_dv_budget;
                    burn_before = Math.Max(burn_before, correction_time_needed);
                }
            }

            startBurn_UT = adjusted_collision_UT - burn_duration - burn_before;
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

            // Predicted landing lat/lon, reusing the exact frame math confirmed above (same Zup->Yup
            // swap, same celestialFrame-relative Position) just handed to GetLatLonAltFromRadius -
            // a real method on CelestialBodyComponent found by inspecting the live assembly, not
            // guessed. This is the first half of precision landing: knowing where we'd actually come
            // down. target_error_m is straight-line lat/lon (haversine) against the player's chosen
            // target, not a 3D position diff - that sidesteps needing to also confirm the lat/lon ->
            // position direction (GetSurfacePosition vs GetRelSurfacePosition look like they might
            // disagree on whether body rotation is applied - see HaversineDistanceMeters below)
            // before we've had a chance to test any of this in-game.
            Vector3d final_rel_pos_zup = orbit.GetRelativePositionAtUTZup(time);
            Vector3d final_rel_pos = new Vector3d(final_rel_pos_zup.x, final_rel_pos_zup.z, final_rel_pos_zup.y);
            Position final_position = new Position(body.SimulationObject.transform.celestialFrame, final_rel_pos);
            body.GetLatLonAltFromRadius(final_position, out predicted_landing_lat, out predicted_landing_lon, out _);

            if (settings.precision_landing.V)
            {
                target_error_m = HaversineDistanceMeters(predicted_landing_lat, predicted_landing_lon,
                    settings.target_latitude.V, settings.target_longitude.V, body.radius);
            }

            return collide;
        }

        // Great-circle distance between two lat/lon points on a sphere of the given radius. Used
        // for target_error_m instead of a 3D position diff - see the comment above
        // compute_real_collision()'s use of it. internal (not private) so LandingTargeting.cs's
        // deorbit search can score candidates on the same real distance metric instead of
        // duplicating this math.
        internal static double HaversineDistanceMeters(double lat1, double lon1, double lat2, double lon2, double radius)
        {
            double toRad = Math.PI / 180.0;
            double dLat = (lat2 - lat1) * toRad;
            double dLon = (lon2 - lon1) * toRad;
            double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                + Math.Cos(lat1 * toRad) * Math.Cos(lat2 * toRad) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
            double c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
            return radius * c;
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
                //
                // Note this also naturally covers the DeorbitBurn phase: while still safely in orbit
                // waiting on/flying the phasing node, altitude is far above start_touchdown_altitude,
                // so this branch simply does nothing until we're actually on a collision course.
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
                brake.gravity_compensation = true;

                if (settings.precision_landing.V)
                {
                    // Precision landing skips the brake-to-near-stop / Pause / re-brake cycle
                    // below entirely. That cycle repeatedly zeroes throttle (Mode.Pause above
                    // does current_vessel.SetThrottle(0) outright), and TouchDown's closed-loop
                    // steering only runs while actually burning (see checkDirection) - so every
                    // Pause cycle killed the steering's authority right along with the throttle,
                    // which is what made precision landings feel "clunky" per Reese, even though
                    // the same cycle works fine for a normal (non-precision) landing. Using the
                    // exact same speed-limit profile TouchDown itself uses turns Brake and
                    // TouchDown into one continuous burn split only by an altitude threshold,
                    // instead of two different behaviors - steering stays live the whole way down.
                    brake.max_speed = settings.compute_limit_speed(altitude);

                    if (altitude < settings.start_touchdown_altitude.V)
                        setMode(Mode.TouchDown);
                }
                else
                {
                    brake.max_speed = 0;
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
