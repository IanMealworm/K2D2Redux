using System;
using K2D2.KSPService;
using KSP.Sim;
using KSP.Sim.impl;
using KTools;
using K2D2.Controller;
using K2D2.Node;
using ILogger = ReduxLib.Logging.ILogger;

namespace K2D2.Landing
{
    public class LandingPilot : Pilot
    {
        public ILogger logger = ReduxLib.ReduxLib.GetLogger("K2D2.LandingController");

        // One settings instance per profile (see LandingSettings.cs), each with its own file.
        // `settings` is a property, not a field, so every settings.xxx.V read here and in the
        // Landing controllers follows whichever profile LandingProfile.IsAtmospheric says is active.
        internal LandingSettings settings_atmo;
        internal LandingSettings settings_vac;
        internal LandingSettings settings => LandingProfile.IsAtmospheric ? settings_atmo : settings_vac;

        public static LandingPilot Instance { get; set; }

        public KSPVessel current_vessel;

        public BurndV burn_dV = new BurndV();

        public WarpTo warp_to = new WarpTo();

        // Speed-limited retrograde burn: the whole Brake/TouchDown descent for a regular landing,
        // and only the final vertical drop below the gate for a vacuum precision landing.
        public TouchDown brake;

        // Vacuum precision landing's powered descent, from ignition to the gate above the target
        // (see VacuumDescentGuidance.cs). Constructed in the constructor body, since it needs 'this'.
        public VacuumDescentGuidance guidance;

        // Precondition phase for precision landing, run before deorbit_burn: circularizes the
        // starting orbit if it isn't already close to circular, or refuses if it's too high.
        // deorbit_burn's own math assumes a roughly circular starting orbit.
        public Circularize circularize = new Circularize();

        // Deorbit/phasing burn phase - see DeorbitBurn.cs.
        public DeorbitBurn deorbit_burn = new DeorbitBurn();

        // Small correction burn against the real post-deorbit-burn trajectory, run right after
        // deorbit_burn and before the normal Pause -> QuickWarp -> ... sequence, so the
        // powered descent isn't left doing all the correcting close to the ground. See
        // MidCourseCorrection.cs.
        public MidCourseCorrection mid_course_correction = new MidCourseCorrection();

        // Atmospheric precision landing from orbit (off for now, see atmo_landing_from_orbit):
        // deorbit toward the target, correct the entry with Redux's prediction, then steer the
        // reentry with lift, before the regular powered landing.
        public AtmoDeorbitBurn atmo_deorbit = new AtmoDeorbitBurn();
        public AtmoEntryCorrection atmo_correction = new AtmoEntryCorrection();
        // Boost-back landing: started on a vessel that's already coming down (a recovered stage),
        // this burn replaces the deorbit and correction burns, then the entry guidance takes over.
        public BoostBackBurn boost_back = new BoostBackBurn();
        // Needs 'this', so it's constructed in the constructor body.
        public AtmoEntryGuidance atmo_guidance;
        // Atmospheric precision landing's landing burn, from the entry guidance's handover to
        // touchdown (Brake mode). Also needs 'this'.
        public AtmoLandingBurn atmo_landing_burn;
        // When the regular landing's burn starts on a body with an atmosphere.
        public AtmoLandingTiming atmo_timing;

        public SingleExecuteController current_executor = new SingleExecuteController();

        public LandingPilot()
        {
            // Both created in K2D2_Plugin.OnInitialized(), before any pilot is constructed.
            var atmo_file = SettingsFile.Get("land_atmo");
            var vac_file = SettingsFile.Get("land_vac");
            settings_atmo = new LandingSettings(atmo_file);
            settings_vac = new LandingSettings(vac_file);
            brake = new TouchDown(atmo_file, vac_file);
            guidance = new VacuumDescentGuidance(this);
            atmo_guidance = new AtmoEntryGuidance(this);
            atmo_landing_burn = new AtmoLandingBurn(this);
            atmo_timing = new AtmoLandingTiming(atmo_guidance.planner);
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
            // nextMode() just does mode+1, so any phase inserted here needs to stay ahead of
            // Pause/QuickWarp/.../TouchDown, which keep their relative ordering either way.
            Circularize,
            DeorbitBurn,
            MidCourseCorrection,
            // Atmospheric precision landing only - after Circularize these run instead of the
            // vacuum burns above, and go on to Pause like they do.
            AtmoDeorbit,
            AtmoCorrection,
            AtmoReentry,
            // Boost-back landing only - routed explicitly (to AtmoReentry), never reached by mode+1.
            BoostBack,
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
                case Mode.MidCourseCorrection:
                    current_executor.setController(mid_course_correction);
                    mid_course_correction.Start();
                    break;
                case Mode.AtmoDeorbit:
                    current_executor.setController(atmo_deorbit);
                    atmo_deorbit.Start();
                    break;
                case Mode.AtmoCorrection:
                    current_executor.setController(atmo_correction);
                    atmo_correction.Start();
                    break;
                case Mode.AtmoReentry:
                    current_executor.setController(atmo_guidance);
                    atmo_guidance.Start();
                    break;
                case Mode.BoostBack:
                    current_executor.setController(boost_back);
                    boost_back.Start();
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
                        warp_to.Start_Retrograde(startBurn_UT, true, settings.max_rotation.V);
                        warp_to.max_warp_index = 2;
                    }
                    break;
                case Mode.Waiting:
                    current_vessel.SetThrottle(0);
                    if (vacuum_precision)
                    {
                        // Turns the vessel to the planned burn direction while waiting.
                        current_executor.setController(guidance);
                        guidance.Start();
                    }
                    else
                        current_executor.setController(null);
                    break;
                case Mode.Brake:
                    if (vacuum_precision)
                    {
                        current_executor.setController(guidance);
                        guidance.Ignite();
                    }
                    else if (atmo_precision || atmo_regular)
                    {
                        current_executor.setController(atmo_landing_burn);
                        atmo_landing_burn.Start(steer_to_target: atmo_precision);
                    }
                    else
                        current_executor.setController(brake);
                    break;
                case Mode.TouchDown:
                    current_executor.setController(brake);
                    break;
            }

            logger.LogInfo("current_pilot " + mode);
        }

        public void nextMode()
        {
            if (mode == Mode.Off)
            {
                isRunning = true;
                return;
            }

            var next = this.mode + 1;
            // Atmospheric precision landing swaps the vacuum burns for its own after Circularize;
            // the vacuum sequence goes straight from MidCourseCorrection to Pause. Its entry
            // guidance ends when the landing burn is due (planned with drag, see AtmoBurnPlanner),
            // so it goes straight to braking rather than through the regular landing's warps and
            // its airless burn timing.
            if (atmo_precision && mode == Mode.Circularize)
                next = Mode.AtmoDeorbit;
            else if (atmo_precision && mode == Mode.AtmoReentry)
                next = Mode.Brake;
            else if (mode == Mode.BoostBack)
                next = Mode.AtmoReentry;
            else if (!atmo_precision && next == Mode.AtmoDeorbit)
                next = Mode.Pause;
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
                    if (current_vessel != null)
                        current_vessel.SetThrottle(0);
                    if (mode == Mode.AtmoReentry || mode == Mode.AtmoCorrection || mode == Mode.AtmoDeorbit || mode == Mode.BoostBack
                        || (mode == Mode.Brake && (atmo_precision || atmo_regular)))
                        ReleaseAtmo();

                    // These phases each put their own node on the plan.
                    if (mode == Mode.Circularize || mode == Mode.DeorbitBurn || mode == Mode.MidCourseCorrection
                        || mode == Mode.AtmoDeorbit || mode == Mode.AtmoCorrection)
                    {
                        var maneuver_creator = new ManeuverCreator();
                        maneuver_creator.Update();
                        maneuver_creator.RemoveAllNodes();
                    }
                    ReleaseSasTarget();

                    setMode(Mode.Off);
                    _active = false;
                }
                else
                {
                    stop_reason = null;
                    run_precision = !touch_down_start && settings.precision_landing.V;
                    adjusted_collision_UT = 0;
                    last_k2d2_collision = false;
                    still_since_UT = double.NaN;
                    legs_deployed = false;
                    atmo_timing.Reset();

                    burn_dV.reset();

                    // Turns the other pilots off.
                    K2D2_Plugin.ResetControllers();

                    _active = true;

                    // Vacuum precision landing starts with Circularize (nothing to do if the orbit
                    // is already close to circular, refused if it's too high - see Circularize.cs),
                    // then flies a visible deorbit node before the normal
                    // Pause->QuickWarp->...->TouchDown sequence. Atmospheric precision landing
                    // needs Redux's impact prediction settings right, and for now only starts on a
                    // vessel already coming down inside the atmosphere (a boost-back - see
                    // atmo_landing_from_orbit).
                    if (touch_down_start)
                        setMode(Mode.TouchDown);
                    else if (vacuum_precision)
                        setMode(Mode.Circularize);
                    else if (atmo_precision)
                    {
                        string problem = ReduxImpactPrediction.AtmoSettingsProblem();
                        if (problem != null)
                        {
                            stop_reason = problem;
                            isRunning = false;
                            return;
                        }
                        // Already coming down (a recovered stage just after separation, say): a
                        // boost-back burn instead of circularizing and deorbiting.
                        bool from_orbit = LandsFromOrbit();
                        if (from_orbit && !atmo_landing_from_orbit)
                        {
                            stop_reason = from_orbit_unavailable;
                            isRunning = false;
                            return;
                        }
                        setMode(from_orbit ? Mode.Circularize : Mode.BoostBack);
                    }
                    else
                        setMode(Mode.QuickWarp);
                }

                base.isRunning = value;
            }
        }

        // Why the last precision landing stopped by itself, for the UI; null when it didn't.
        internal string stop_reason;

        bool touch_down_start;

        // The Touch Down button: straight to the final descent, skipping the precision setup.
        // While already running, the phase it interrupts lets go of the controls first.
        public void StartTouchDown()
        {
            if (isRunning)
            {
                ReleaseAtmo();
                setMode(Mode.TouchDown);
                return;
            }
            touch_down_start = true;
            isRunning = true;
            touch_down_start = false;
        }

        // The burns and the guided descent leave SAS turning toward their last target; after a
        // stop it holds wherever the vessel points instead. Other SAS modes the landing set
        // (retrograde for the final descent) are left as they are.
        void ReleaseSasTarget()
        {
            var autopilot = current_vessel?.Autopilot;
            if (autopilot?.SAS == null || autopilot.AutopilotMode != AutopilotMode.StabilityAssist)
                return;

            var telemetry = SASTool.getTelemetry();
            if (telemetry == null)
                return;
            var frame = telemetry.HorizonUp.coordinateSystem;
            Rotation facing = Rotation.Reframed(current_vessel.GetRotation(), frame);
            autopilot.SAS.lockedMode = false;
            autopilot.SAS.SetTargetOrientation(new Vector(frame, facing.localRotation * UnityEngine.Vector3.up), false);
        }

        void ReleaseAtmo()
        {
            atmo_guidance.Release();
            atmo_correction.Release();
            atmo_deorbit.Release();
            boost_back.Release();
            atmo_landing_burn.Release();
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

        // Whether this run is a precision landing, fixed when it starts: flipping the toggle
        // mid-run would otherwise switch the phase sequence underneath it.
        bool run_precision;
        bool precision => isRunning ? run_precision : settings.precision_landing.V;

        // Precision landing on an airless body (the guided powered descent) or an atmospheric one.
        internal bool vacuum_precision => precision && !LandingProfile.IsAtmospheric;
        internal bool atmo_precision => precision && LandingProfile.IsAtmospheric;
        // The regular landing on a body with an atmosphere: its burn is timed and flown with drag
        // (AtmoLandingTiming, AtmoLandingBurn) rather than from the airless fall.
        internal bool atmo_regular => !precision && LandingProfile.IsAtmospheric;

        // Unpowered phases, where nothing but gravity changes the trajectory.
        bool coasting => mode == Mode.Pause || mode == Mode.QuickWarp
            || mode == Mode.RotationWarp || mode == Mode.Waiting;

        // Logging throttle for compute_real_collision() below - it runs several times a second,
        // so logging its result unconditionally is far too much log volume. Logs immediately
        // whenever converged flips true<->false, plus a periodic heartbeat regardless.
        bool collision_search_last_converged = true;
        // compute_real_collision's last trusted answer, given again on frames it can't resolve.
        bool last_k2d2_collision;
        int collision_search_log_counter = 0;
        const int CollisionSearchHeartbeatFrames = 60;

        const float collision_search_interval_s = 0.25f;
        float next_collision_search, next_guidance_plan;

        // The prediction logs are for checking flights: only while a pilot is flying, or in
        // Debug Mode.
        bool log_predictions => isRunning || K2D2Settings.debug_mode.V;

        // Predicted landing point, from Redux's impact prediction when it has a usable one for
        // this vessel, otherwise from compute_real_collision (see computeValues).
        // target_error_m is only meaningful once a target's been set and precision_landing is on.
        internal double predicted_landing_lat = 0;
        internal double predicted_landing_lon = 0;
        internal double target_error_m = 0;
        internal bool using_redux_prediction = false;

        public void computeValues()
        {
            collision_detected = false;
            var current_vessel = K2D2_Plugin.Instance.current_vessel;
            if (current_vessel == null)
                return;

            // Standing on the ground (the pad, say): nothing is falling, whatever the orbit
            // through the planet's centre would say.
            if (current_vessel.VesselComponent.Landed || current_vessel.VesselComponent.Splashed)
            {
                using_redux_prediction = false;
                return;
            }

            // VesselComponent.Orbit can be a CurrentPatchedConicsOrbit for the actively-flown
            // vessel, not just PatchedConicsOrbit, so a hard cast throws InvalidCastException.
            // GetOrbitalVelocityAtUTZup is on IOrbit (which IKeplerPatch extends), so no concrete
            // cast is needed here.
            IKeplerPatch orbit = current_vessel.VesselComponent.Orbit;
            var body = orbit.referenceBody;

            // K2D2's own prediction always runs: it's the fallback whenever Redux has nothing
            // usable (its impact prediction settings off, the map focused on another vessel, a
            // calculation restarting, or the gaps it leaves late in a descent), and the
            // comparison log below needs it either way. Its search makes up to 300 terrain
            // queries, so it runs a few times a second; the impact time it finds is a UT, so it
            // stays right in between.
            if (UnityEngine.Time.unscaledTime >= next_collision_search)
            {
                next_collision_search = UnityEngine.Time.unscaledTime + collision_search_interval_s;
                collision_detected = compute_real_collision();
            }
            else
                collision_detected = last_k2d2_collision;

            // Redux's prediction accounts for drag, and it accounts for the body's rotation more
            // accurately than compute_real_collision's correction. It's an unpowered prediction,
            // the same assumption compute_real_collision makes.
            var redux = ReduxImpactPrediction.GetFlown(current_vessel.VesselComponent);
            using_redux_prediction = redux.usable;
            LogReduxComparison(redux, body);

            if (using_redux_prediction)
            {
                collision_detected = true;
                adjusted_collision_UT = redux.impact_UT;
                predicted_landing_lat = redux.latitude;
                predicted_landing_lon = redux.longitude;
            }

            // Vacuum precision landing aims the coast over the target rather than at it (see
            // LandingTargeting.TargetSite), so its error is measured against that aim point;
            // otherwise it's the ground distance from the predicted landing point to the target.
            if (vacuum_precision)
            {
                var model = new ImpactModel(body, GeneralTools.Current_UT);
                var site = new LandingTargeting.TargetSite(model, settings.target_latitude.V, settings.target_longitude.V);
                if (LandingTargeting.CoastMiss(model, orbit, site, GeneralTools.Current_UT, out double miss_m))
                    target_error_m = miss_m;
            }
            else if (precision)
            {
                target_error_m = HaversineDistanceMeters(predicted_landing_lat, predicted_landing_lon,
                    settings.target_latitude.V, settings.target_longitude.V, body.radius);
            }

            speed_collision = orbit.GetOrbitalVelocityAtUTZup(adjusted_collision_UT).magnitude;
            burn_duration = (speed_collision / burn_dV.full_dv);

            // The guidance plans ignition from the coast ahead; once the burn has started (or
            // before the coast toward the surface has begun) there's nothing to plan.
            // A few times a second is enough: nothing but gravity acts while coasting.
            if (vacuum_precision && coasting && collision_detected)
            {
                if (UnityEngine.Time.unscaledTime >= next_guidance_plan || !guidance.has_plan)
                {
                    next_guidance_plan = UnityEngine.Time.unscaledTime + collision_search_interval_s;
                    guidance.Plan(adjusted_collision_UT);
                }
            }
            else
                guidance.ClearPlan();

            if (atmo_regular && collision_detected && (coasting || !isRunning))
                atmo_timing.Update(current_vessel.VesselComponent, altitude, burn_dV.full_dv,
                    AtmoLandingBurn.FinalSpeed(settings), current_vessel.GetThrottle() == 0);

            compute_startBurn();
        }

        // Logs Redux's impact prediction next to K2D2's own (compute_real_collision's, before
        // Redux's replaces it) and which one is in use, so the two can keep being compared on real
        // flights. Same throttle as compute_real_collision's logging: immediately when Redux's
        // state changes, plus a heartbeat.
        ReduxImpactState? redux_compare_last_state = null;
        int redux_compare_log_counter = 0;

        void LogReduxComparison(ReduxImpact redux, CelestialBodyComponent body)
        {
            bool state_changed = redux.state != redux_compare_last_state;
            redux_compare_last_state = redux.state;
            redux_compare_log_counter++;
            if (!log_predictions || (!state_changed && redux_compare_log_counter % CollisionSearchHeartbeatFrames != 0))
                return;

            double now = GeneralTools.Current_UT;
            string ours = collision_detected
                ? $"lat={predicted_landing_lat:n4} lon={predicted_landing_lon:n4} T-{adjusted_collision_UT - now:n0}s"
                : "no collision";

            string source = using_redux_prediction ? "redux" : "k2d2";

            if (redux.state != ReduxImpactState.Impact)
            {
                logger.LogInfo($"[ReduxCompare] using={source} redux={redux.state} | k2d2: {ours}");
                return;
            }

            string theirs = $"lat={redux.latitude:n4} lon={redux.longitude:n4} T-{redux.impact_UT - now:n0}s " +
                $"age={redux.age:n1}s approximate={redux.approximate}";
            string delta = collision_detected
                ? $" | delta: {HaversineDistanceMeters(redux.latitude, redux.longitude, predicted_landing_lat, predicted_landing_lon, body.radius):n0}m, " +
                  $"{redux.impact_UT - adjusted_collision_UT:n1}s"
                : "";
            logger.LogInfo($"[ReduxCompare] using={source} redux: {theirs} | k2d2: {ours}{delta}");
        }

        public void compute_startBurn()
        {
            if (vacuum_precision && guidance.has_plan)
            {
                startBurn_UT = guidance.ignition_UT;
                burn_duration = guidance.planned_burn_s;
            }
            else if (atmo_regular && atmo_timing.has_plan)
            {
                // Until drag has been measured (above the air, or just into it) the airless timing
                // below stands in: it's always earlier, never too late.
                startBurn_UT = atmo_timing.start_UT;
            }
            else
            {
                startBurn_UT = adjusted_collision_UT - burn_duration - settings.burn_before.V;
            }

            // Never schedule the burn as already overdue: WarpTo silently does nothing once its
            // target time is in the past, so the worst case here is "start braking immediately",
            // not "stop warping while still minutes out".
            double now_ut = GeneralTools.Game.UniverseModel.UniverseTime;
            if (startBurn_UT < now_ut)
                startBurn_UT = now_ut;

            startSafeWarp_UT = startBurn_UT - settings.rotation_warp_duration.V;
        }

        public bool compute_real_collision()
        {
            double start_time = GeneralTools.Game.UniverseModel.UniverseTime + 2 * 60;
            bool collide = false;

            // Frame handling for the terrain check below:
            //  1. orbit.GetRelativePositionAtUTZup(ut) returns a Vector3d already relative to the
            //     orbit's reference body, but in "Zup" convention (Z is "up", standard orbital-
            //     mechanics axis order), not Unity's Y-up - its Y/Z components need swapping
            //     before use with Position/Vector. GetOrbitalVelocityAtUTZup has the same
            //     convention, but it's only ever consumed via .magnitude, which doesn't care
            //     about axis order.
            //  2. That swapped vector needs to be paired with
            //     body.SimulationObject.transform.celestialFrame, not body.coordinateSystem.
            IKeplerPatch orbit = current_vessel.VesselComponent.Orbit;
            var body = orbit.referenceBody;
            double current_time_ut = GeneralTools.Game.UniverseModel.UniverseTime;
            // Forward step of the search, and the first half-step of the bisection. Kept narrow
            // (with max_occurrences sized to match) so the walk can't step over a short dip below
            // terrain on this pass and find the crossing a full orbit later instead: right after a
            // deorbit burn the orbit can dip below terrain on both. The search restarts every
            // frame, so that would make the warp target jump between "burn soon" and "burn an
            // orbit later".
            double deltaTime = 20;
            int max_occurrences = 300;
            double time = start_time;
            double terrainAltitude = 0;
            // True once a sample lands within 1 m of the terrain. False if the loop runs out first
            // (no crossing within the search's ~6,000 s reach, or an unfinished bisection); the
            // previous prediction is then kept rather than a half-finished one.
            bool converged = false;

            for (int i = 0; i < max_occurrences; i++)
            {
                Vector3d rel_pos_zup = orbit.GetRelativePositionAtUTZup(time);
                // Zup -> Yup: swap Y and Z before this is usable as a Position's local vector.
                Vector3d rel_pos = new Vector3d(rel_pos_zup.x, rel_pos_zup.z, rel_pos_zup.y);
                Position ps = new Position(body.SimulationObject.transform.celestialFrame, rel_pos);
                double sceneryOffset;

                body.GetAltitudeFromTerrain(ps, out terrainAltitude, out sceneryOffset);

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
                    converged = true;
                    break;
                }
            }

            // Throttled, see collision_search_last_converged.
            bool convergence_changed = converged != collision_search_last_converged;
            collision_search_last_converged = converged;
            collision_search_log_counter++;
            if (log_predictions && (convergence_changed || collision_search_log_counter % CollisionSearchHeartbeatFrames == 0))
            {
                logger.LogInfo($"compute_real_collision: collide={collide} converged={converged} final terrainAltitude={terrainAltitude:n1} adjusted_collision_UT+{time - current_time_ut:n0}s");
            }

            if (!converged)
            {
                // This frame's result can't be trusted to drive the warp schedule, so the last
                // good prediction stays. Logged only on the first frame of a streak.
                if (convergence_changed && log_predictions)
                {
                    logger.LogInfo($"compute_real_collision: search did not converge this frame - keeping previous prediction " +
                        $"(adjusted_collision_UT+{adjusted_collision_UT - current_time_ut:n0}s, target_error_m={target_error_m:n1}) instead of an unreliable one.");
                }
                return last_k2d2_collision;
            }

            // While coasting unpowered nothing changes the orbit, so the same crossing can only
            // get sooner. A jump later by more than half an orbit means the search skipped the
            // near crossing and found a later one. Not checked during burns, which move the impact
            // time on purpose, or on a hyperbolic orbit, which has no later orbit to alias onto.
            if (coasting && adjusted_collision_UT > 0)
            {
                Vector3d r_now = orbit.GetRelativePositionAtUTZup(current_time_ut);
                Vector3d v_now = orbit.GetOrbitalVelocityAtUTZup(current_time_ut);
                double period = LandingTargeting.OrbitalPeriodFromStateVectors(r_now, v_now, body.gravParameter);

                if (!double.IsNaN(period) && period > 0 && time > adjusted_collision_UT + 0.5 * period)
                {
                    if (log_predictions)
                        logger.LogInfo($"compute_real_collision: new prediction (adjusted_collision_UT+{time - current_time_ut:n0}s) jumped more than " +
                            $"half an orbit ({period:n0}s) later than the last one (adjusted_collision_UT+{adjusted_collision_UT - current_time_ut:n0}s) - " +
                            "looks like the search locked onto a later orbit's crossing instead of the nearer one, keeping the previous prediction.");
                    return last_k2d2_collision;
                }
            }

            adjusted_collision_UT = time;

            // Predicted landing lat/lon, reusing the same Zup->Yup swap and celestialFrame-
            // relative Position as above, handed to GetLatLonAltFromRadius.
            Vector3d final_rel_pos_zup = orbit.GetRelativePositionAtUTZup(time);
            Vector3d final_rel_pos = new Vector3d(final_rel_pos_zup.x, final_rel_pos_zup.z, final_rel_pos_zup.y);
            Position final_position = new Position(body.SimulationObject.transform.celestialFrame, final_rel_pos);
            body.GetLatLonAltFromRadius(final_position, out predicted_landing_lat, out predicted_landing_lon, out _);

            // That lat/lon is read against the body's orientation right now, but the surface keeps
            // turning until impact, so shift the longitude back by how far the body rotates in the
            // remaining time.
            double rotation_deg = (time - current_time_ut) * 360.0 / body.rotationPeriod;
            predicted_landing_lon = LandingTargeting.WrapDegrees180(predicted_landing_lon - rotation_deg);

            last_k2d2_collision = collide;
            return collide;
        }

        // Logs Redux's stage recovery state whenever it changes, and while a recovered stage is
        // being flown, where that stage is and what it has left - the numbers the boost-back
        // landing has to work with. Every second for the first 20 s of flying the stage, then
        // every 10 s.
        string last_replay_phase;
        double replay_flying_since_UT = double.NaN, last_replay_log_UT = double.NaN;

        void LogStageRecovery()
        {
            var replay = ReduxStageRecovery.Get();
            double now = GeneralTools.Current_UT;
            bool changed = replay.phase != last_replay_phase;
            if (changed)
            {
                logger.LogInfo($"[StageRecovery] Redux stage recovery: {last_replay_phase ?? "-"} -> {replay.phase}" +
                    (replay.vessel_name != null ? $" ({replay.vessel_name}, captured at UT {replay.captured_UT:n1})" : ""));
                last_replay_phase = replay.phase;
                replay_flying_since_UT = replay.flying_stage ? now : double.NaN;
                last_replay_log_UT = double.NaN;
            }
            if (!replay.flying_stage)
                return;

            double flying_for = now - replay_flying_since_UT;
            double interval = flying_for < 20 ? 1 : 10;
            if (!double.IsNaN(last_replay_log_UT) && now - last_replay_log_UT < interval)
                return;
            last_replay_log_UT = now;

            var vessel = current_vessel?.VesselComponent;
            if (vessel == null)
            {
                logger.LogInfo("[StageRecovery] flying a recovered stage, but K2D2 has no current vessel");
                return;
            }
            var body = vessel.mainBody;
            var model = new ImpactModel(body, now);
            model.StateAt(vessel.Orbit, now, out Vector3d r, out Vector3d v);
            Vector3d air = model.AirVelocity(r, v);
            Vector3d up = r.normalized;
            double v_vertical = Vector3d.Dot(air, up);
            double v_horizontal = (air - up * v_vertical).magnitude;
            double path_angle = Math.Atan2(v_vertical, v_horizontal) * 180 / Math.PI;
            LandingTargeting.OrbitalElementsFromStateVectors(r, v, body.gravParameter, out _, out double apo, out double peri);
            double target_m = HaversineDistanceMeters(vessel.Latitude, vessel.Longitude,
                settings.target_latitude.V, settings.target_longitude.V, body.radius);
            var redux = ReduxImpactPrediction.GetFlown(vessel);
            string impact = redux.usable
                ? $"Redux lands {HaversineDistanceMeters(redux.latitude, redux.longitude, settings.target_latitude.V, settings.target_longitude.V, body.radius):n0}m from the target in {redux.impact_UT - now:n0}s"
                : $"Redux {redux.state}";
            double g = body.gravParameter / (r.magnitude * r.magnitude);
            logger.LogInfo($"[StageRecovery] {vessel.Name} T+{flying_for:n0}s on {body.Name}: {vessel.Situation}, alt {vessel.AltitudeFromSeaLevel:n0}m " +
                $"({vessel.AltitudeFromTerrain:n0}m above terrain) | air speed {air.magnitude:n0}m/s: {v_horizontal:n0} horizontal, {v_vertical:n0} vertical, " +
                $"path {path_angle:n1}° | Ap {(apo - body.radius) / 1000:n1}km, Pe {(peri - body.radius) / 1000:n1}km | " +
                $"at ({vessel.Latitude:n4}, {vessel.Longitude:n4}), target {target_m / 1000:n1}km away | {impact} | " +
                $"dV {vessel.VesselDeltaV?.TotalDeltaVVac ?? double.NaN:n0}m/s vac / {vessel.VesselDeltaV?.TotalDeltaVActual ?? double.NaN:n0}m/s actual, " +
                $"thrust/weight {burn_dV.full_dv / g:n2}, mass {vessel.totalMass:n1}t");
        }

        // Atmospheric precision landing from orbit (deorbit, entry correction, then the same entry
        // guidance and landing burn as a boost-back) is still being tested, so players only get
        // it for stages already coming down. Set to true to turn it back on.
        internal const bool atmo_landing_from_orbit = false;
        internal const string from_orbit_unavailable =
            "Atmospheric precision landing from orbit isn't available yet - for now it lands stages that are already " +
            "coming back down inside the atmosphere (boost-back landings).";

        // A landing from orbit, as opposed to a boost-back: still in orbit, or above the
        // atmosphere. A hand-made deorbit is suborbital, but until it's in the air it needs the
        // from-orbit entry, not a boost-back.
        internal bool LandsFromOrbit()
        {
            var vessel = current_vessel?.VesselComponent;
            if (vessel == null)
                return false;
            return !IsSuborbital() || vessel.AltitudeFromSeaLevel > vessel.mainBody.atmosphereDepth;
        }

        internal bool IsSuborbital()
        {
            var vessel = current_vessel?.VesselComponent;
            if (vessel == null)
                return false;
            var model = new ImpactModel(vessel.mainBody, GeneralTools.Current_UT);
            model.StateAt(vessel.Orbit, model.epoch_UT, out Vector3d r, out Vector3d v);
            return ImpactModel.PeriapsisRadius(r, v, vessel.mainBody.gravParameter) < vessel.mainBody.radius;
        }

        internal double DistanceToTarget()
        {
            var vessel = current_vessel.VesselComponent;
            return HaversineDistanceMeters(vessel.Latitude, vessel.Longitude,
                settings.target_latitude.V, settings.target_longitude.V, vessel.mainBody.radius);
        }

        // Great-circle distance between two lat/lon points on a sphere of the given radius.
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
            // Reads Redux's stage recovery through reflection, so only when it's wanted.
            if (log_predictions)
                LogStageRecovery();
            if (!page.isVisible && !isRunning) return;
            if (current_vessel == null || current_vessel.VesselVehicle == null)
                return;

            // Keeps the Atmo/Vacuum profile current for every settings/TouchDown pass-through
            // property. A cheap no-op most frames.
            LandingProfile.Update(current_vessel.currentBody());

            altitude = (float)current_vessel.GetApproxAltitude();

            SurfaceVelocity = current_vessel.VesselVehicle.SurfaceVelocity;
            SurfaceVelocity.Reframe(current_vessel.VesselVehicle.Up.coordinateSystem);
            current_falling_speed = (float)-SurfaceVelocity.vector.y;

            // detect collision and compute time to burn
            computeValues();

            if (!collision_detected)
            {
                // Close to the ground, "no collision" usually means compute_real_collision's
                // search can't resolve one this close, so falling back to Touch Down is right.
                // Higher up it can blip false for a frame (an active Brake burn keeps changing the
                // orbit it extrapolates), so the fallback waits for the touchdown altitude and a
                // blip is just re-checked next frame.
                if (isRunning)
                {
                    // Only the regular landing's own phases fall back to Touch Down. The precision
                    // phases steer or burn on purpose (an unpowered prediction can stop seeing an
                    // impact while they do) and hand over themselves.
                    bool regular_phase = coasting || (mode == Mode.Brake && !vacuum_precision && !atmo_precision && !atmo_regular);
                    if (altitude < settings.start_touchdown_altitude.V && regular_phase)
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

            // Landing detection: the game's landed flag, or standing still on the ground (see
            // UpdateStanding). Not the height reading, which can be tens of metres off near the
            // ground on a tall vessel or where the game's terrain height is wrong.
            if ((current_vessel.Landed() && SurfaceVelocity.vector.magnitude < 3) || UpdateStanding())
            {
                if (run_precision)
                    logger.LogInfo($"[Landing] landed {DistanceToTarget():n0}m from the target");
                isRunning = false;
                return;
            }
            DeployLegs();

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
                    setMode(Mode.Brake);
                    return;
                }
            }
            else if (mode == Mode.Brake && !vacuum_precision && !atmo_precision && !atmo_regular)
            {
                // The guided descents run their own Brake phase (see setMode).
                brake.gravity_compensation = true;
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
                        // Still too high: the burn timing was off, so coast and try again.
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
                string abort_reason = mode == Mode.Circularize ? circularize.abort_reason
                    : mode == Mode.DeorbitBurn ? deorbit_burn.abort_reason
                    : mode == Mode.AtmoDeorbit ? atmo_deorbit.abort_reason
                    : mode == Mode.BoostBack ? boost_back.abort_reason
                    : null;
                if (abort_reason != null)
                {
                    stop_reason = abort_reason;
                    isRunning = false;
                    return;
                }

                // auto next
                nextMode();
            }
        }

        public float altitude;

        // Landing legs (the game's Gear action group, the G key) go down once per landing: in
        // Touch Down, or once the braking burn is this low.
        const float legs_down_below_m = 1000;
        bool legs_deployed;

        void DeployLegs()
        {
            if (legs_deployed)
                return;
            bool low = mode == Mode.TouchDown || (mode == Mode.Brake && altitude < legs_down_below_m);
            if (!low)
                return;
            legs_deployed = true;

            var vessel = current_vessel.VesselComponent;
            if (vessel.GetActionGroupPartCount(KSPActionGroup.Gear) == 0)
                return;
            vessel.SetActionGroup(KSPActionGroup.Gear, true);
            logger.LogInfo($"[Landing] landing legs down at {altitude:n0}m");
        }

        // Standing on the ground while the height reading may still say tens of metres up:
        // barely moving with the engines throttled well below hovering, for long enough that in
        // the air the vessel would have fallen faster than that. The first landing from orbit came
        // down on ground the height reading put 70 m below it, the game didn't flag it as landed
        // either, and the final descent kept steering on the ground until the booster tipped.
        // Only while the engines are landing it; on very low gravity the wait gets too long to
        // tell, and the game's landed flag has to do.
        const double standing_speed_mps = 1.5, standing_thrust_g = 0.5;
        const double min_standing_s = 1.5, max_standing_s = 5;
        double still_since_UT = double.NaN;

        bool UpdateStanding()
        {
            var vessel = current_vessel.VesselComponent;
            double g = vessel.graviticAcceleration.magnitude;
            // Long enough for the unbalanced part of gravity to add twice the speed limit.
            double standing_s = Math.Max(min_standing_s, 2 * standing_speed_mps / Math.Max(1e-6, (1 - standing_thrust_g) * g));
            double thrust = current_vessel.GetThrottle() * burn_dV.full_dv;
            bool powered = mode == Mode.Brake || mode == Mode.TouchDown;
            bool still = powered && standing_s <= max_standing_s
                && SurfaceVelocity.vector.magnitude < standing_speed_mps && thrust <= standing_thrust_g * g;
            if (!still)
            {
                still_since_UT = double.NaN;
                return false;
            }
            double now = GeneralTools.Current_UT;
            if (double.IsNaN(still_since_UT))
                still_since_UT = now;
            if (now - still_since_UT < standing_s)
                return false;
            logger.LogInfo($"[Landing] standing still with the engines throttled down - on the ground (the height reading said {altitude:n0}m)");
            return true;
        }


    }
}
