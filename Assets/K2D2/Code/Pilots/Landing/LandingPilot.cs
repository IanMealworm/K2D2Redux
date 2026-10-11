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

        // Atmo/Vacuum profile split - see LandingSettings.cs's class comment. Two independent
        // instances, each backed by its own settings file (k2d2_landing_atmo.json /
        // k2d2_landing_vac.json). `settings` is a pass-through property, not a field, so every
        // read of settings.xxx.V throughout this file and the Landing controllers follows
        // whichever profile LandingProfile.IsAtmospheric currently says is active.
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

        // Atmospheric precision landing: deorbit toward the target, correct the entry with Redux's
        // prediction, then steer the reentry with lift, before the regular powered landing.
        public AtmoDeorbitBurn atmo_deorbit = new AtmoDeorbitBurn();
        public AtmoEntryCorrection atmo_correction = new AtmoEntryCorrection();
        // Needs 'this', so it's constructed in the constructor body.
        public AtmoEntryGuidance atmo_guidance;

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
            // start
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
                    // stop
                    if (current_vessel != null)
                        current_vessel.SetThrottle(0);
                    if (mode == Mode.AtmoReentry || mode == Mode.AtmoCorrection || mode == Mode.AtmoDeorbit)
                    {
                        atmo_guidance.Release();
                        atmo_correction.Release();
                        atmo_deorbit.Release();
                    }

                    setMode(Mode.Off);
                    _active = false;
                }
                else
                {
                    stop_reason = null;

                    // Start total burn counter
                    burn_dV.reset();

                    // reset controller to desactivate other controllers.
                    K2D2_Plugin.ResetControllers();

                    _active = true;

                    // Vacuum precision landing starts with Circularize (a no-op if the orbit's
                    // already close enough to circular, or a refusal if it's too high - see
                    // Circularize.cs), then plans and flies a real, visible deorbit node, before
                    // falling into the normal Pause->QuickWarp->...->TouchDown sequence.
                    // Atmospheric precision landing does the same with its own burns and entry
                    // guidance, as long as Redux's prediction is set up the way it needs.
                    if (vacuum_precision)
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
                        setMode(Mode.Circularize);
                    }
                    else
                        setMode(Mode.QuickWarp);
                }

                // send call backs
                base.isRunning = value;
            }
        }

        // Why the last precision landing stopped by itself, for the UI; null when it didn't.
        internal string stop_reason;

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

        // Precision landing on an airless body - the guided powered descent. The atmospheric
        // profile gets its own precision landing later; until then it always lands the regular way.
        internal bool vacuum_precision => settings.precision_landing.V && !LandingProfile.IsAtmospheric;
        internal bool atmo_precision => settings.precision_landing.V && LandingProfile.IsAtmospheric;

        // Unpowered phases, where nothing but gravity changes the trajectory.
        bool coasting => mode == Mode.Pause || mode == Mode.QuickWarp
            || mode == Mode.RotationWarp || mode == Mode.Waiting;

        // Logging throttle for compute_real_collision() below - it runs every Update() frame, so
        // logging its result unconditionally is far too much log volume. Logs immediately
        // whenever converged flips true<->false, plus a periodic heartbeat regardless.
        bool collision_search_last_converged = true;
        int collision_search_log_counter = 0;
        const int CollisionSearchHeartbeatFrames = 60;

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
            {
                // UI_Tools.Console("no vessel");
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
            // comparison log below needs it either way.
            collision_detected = compute_real_collision();

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
            else if (settings.precision_landing.V)
            {
                target_error_m = HaversineDistanceMeters(predicted_landing_lat, predicted_landing_lon,
                    settings.target_latitude.V, settings.target_longitude.V, body.radius);
            }

            speed_collision = orbit.GetOrbitalVelocityAtUTZup(adjusted_collision_UT).magnitude;
            burn_duration = (speed_collision / burn_dV.full_dv);

            // The guidance plans ignition from the coast ahead; once the burn has started (or
            // before the coast toward the surface has begun) there's nothing to plan.
            if (vacuum_precision && coasting && collision_detected)
                guidance.Plan(adjusted_collision_UT);
            else
                guidance.ClearPlan();

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
            if (!state_changed && redux_compare_log_counter % CollisionSearchHeartbeatFrames != 0)
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
            // start in 2 minutes
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
            // Coarse step for the initial forward walk below, and the starting half-step size for
            // the flip-and-refine bisection once a below-terrain sample is hit. Narrow enough
            // (with max_occurrences sized to match) that the search can't alias between two
            // different valid terrain crossings roughly one orbital period apart: right after a
            // deorbit burn, the still-coasting orbit can dip below terrain both on the current
            // pass (near) and again a full period later (far), and a too-wide forward step can
            // walk clean over a narrow near dip without ever sampling inside it. Since this search
            // restarts fresh from current_ut+120s every frame, aliasing onto the far crossing
            // instead of the near one would send the auto-warp target jumping between "burn very
            // soon" and "burn a full orbit later" from frame to frame.
            double deltaTime = 20;
            int max_occurrences = 300;
            double time = start_time;
            double terrainAltitude = 0;
            // True only once a sample actually lands within 1m of the terrain (the loop's own
            // break condition below) - false if the loop runs out of iterations without narrowing
            // that far (no below-terrain sample found within the search's ~6,000s reach, or one
            // found but the bisection didn't finish). Guards against overwriting
            // adjusted_collision_UT/target_error_m with a half-finished result - see below.
            bool converged = false;

            float radius = current_vessel.VesselComponent.SimulationObject.objVesselBehavior.BoundingSphere.radius;

            for (int i = 0; i < max_occurrences; i++)
            {
                Vector3d rel_pos_zup = orbit.GetRelativePositionAtUTZup(time);
                // Zup -> Yup: swap Y and Z before this is usable as a Position's local vector.
                Vector3d rel_pos = new Vector3d(rel_pos_zup.x, rel_pos_zup.z, rel_pos_zup.y);
                Position ps = new Position(body.SimulationObject.transform.celestialFrame, rel_pos);
                double sceneryOffset;

                body.GetAltitudeFromTerrain(ps, out terrainAltitude, out sceneryOffset);
                // terrainAltitude -= radius;

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

            // See collision_search_last_converged's own comment for why this is throttled instead
            // of unconditional.
            bool convergence_changed = converged != collision_search_last_converged;
            collision_search_last_converged = converged;
            collision_search_log_counter++;
            if (convergence_changed || collision_search_log_counter % CollisionSearchHeartbeatFrames == 0)
            {
                logger.LogInfo($"compute_real_collision: collide={collide} converged={converged} final terrainAltitude={terrainAltitude:n1} adjusted_collision_UT+{time - current_time_ut:n0}s");
            }

            if (!converged)
            {
                // The loop above ran out of iterations without landing within 1m of the terrain -
                // either no below-terrain sample was found at all, or one was found but the
                // bisection didn't finish. Either way this frame's result isn't trustworthy enough
                // to drive the warp schedule, so keep the last good prediction instead of
                // overwriting it. Only log the FIRST frame of a new not-converged streak - logging
                // every frame of a whole streak is exactly the spam this throttling avoids.
                if (convergence_changed)
                {
                    logger.LogInfo($"compute_real_collision: search did not converge this frame - keeping previous prediction " +
                        $"(adjusted_collision_UT+{adjusted_collision_UT - current_time_ut:n0}s, target_error_m={target_error_m:n1}) instead of an unreliable one.");
                }
                return collision_detected;
            }

            // Belt-and-suspenders on top of the narrower search step above, only while genuinely
            // coasting unpowered toward the deorbit impact point (Pause/QuickWarp/RotationWarp/
            // Waiting) - NOT during Circularize/DeorbitBurn/Brake/TouchDown, where the vessel's own
            // thrust deliberately changes the orbit and a jump in predicted impact time (a braking
            // burn pushing it later, say) is expected, not a bug. While coasting, nothing changes
            // the orbit, so the same physical crossing should only ever get SOONER as
            // current_time_ut advances - a jump forward by more than half an orbital period
            // almost certainly means this frame's search skipped past the real near crossing and
            // landed on a later one. Only meaningful when the orbit is actually bound (a
            // hyperbolic capture trajectory has no "next orbit" to alias onto).
            if (coasting && adjusted_collision_UT > 0)
            {
                Vector3d r_now = orbit.GetRelativePositionAtUTZup(current_time_ut);
                Vector3d v_now = orbit.GetOrbitalVelocityAtUTZup(current_time_ut);
                double period = LandingTargeting.OrbitalPeriodFromStateVectors(r_now, v_now, body.gravParameter);

                if (!double.IsNaN(period) && period > 0 && time > adjusted_collision_UT + 0.5 * period)
                {
                    logger.LogInfo($"compute_real_collision: new prediction (adjusted_collision_UT+{time - current_time_ut:n0}s) jumped more than " +
                        $"half an orbit ({period:n0}s) later than the last one (adjusted_collision_UT+{adjusted_collision_UT - current_time_ut:n0}s) - " +
                        "looks like the search locked onto a later orbit's crossing instead of the nearer one, keeping the previous prediction.");
                    return collision_detected;
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

            return collide;
        }

        // Great-circle distance between two lat/lon points on a sphere of the given radius. Used
        // for target_error_m instead of a 3D position diff. internal (not private) so
        // LandingTargeting.cs's deorbit search can score candidates on the same distance metric.
        double DistanceToTarget()
        {
            var vessel = current_vessel.VesselComponent;
            return HaversineDistanceMeters(vessel.Latitude, vessel.Longitude,
                settings.target_latitude.V, settings.target_longitude.V, vessel.mainBody.radius);
        }

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

            // Keep the Atmo/Vacuum profile switch current - single source of truth every
            // settings/TouchDown pass-through property reads. Cheap no-op most frames.
            LandingProfile.Update(current_vessel.currentBody());

            altitude = (float)current_vessel.GetApproxAltitude();

            SurfaceVelocity = current_vessel.VesselVehicle.SurfaceVelocity;
            SurfaceVelocity.Reframe(current_vessel.VesselVehicle.Up.coordinateSystem);
            current_falling_speed = (float)-SurfaceVelocity.vector.y;

            // detect collision and compute time to burn
            computeValues();

            if (!collision_detected)
            {
                // Once close enough to the ground, "no predicted collision" usually just means
                // the patched-conics bisection search in compute_real_collision() can't resolve
                // one this close anymore, not that the danger is gone - falling back straight to
                // Touch Down is correct there. But collision_detected can also blip false for a
                // single frame much higher up in the descent (the search is fragile, and an
                // active Brake burn keeps changing the coasting orbit it extrapolates from every
                // frame), so gating the fallback behind the touchdown-altitude threshold means a
                // transient false negative higher up just gets re-checked next frame, while the
                // legitimate close-to-the-ground case still falls back as intended.
                //
                // This also naturally covers the DeorbitBurn phase: while still safely in orbit
                // waiting on/flying the phasing node, altitude is far above
                // start_touchdown_altitude, so this branch does nothing until we're actually on a
                // collision course.
                if (isRunning)
                {
                    // The guided descent deliberately flattens its path toward the target, which
                    // can make an unpowered prediction stop seeing an impact - it hands over to
                    // TouchDown itself at the gate.
                    bool guided_burn = vacuum_precision && mode == Mode.Brake;
                    if (altitude < settings.start_touchdown_altitude.V && !guided_burn)
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
                if (settings.precision_landing.V)
                    logger.LogInfo($"[Landing] landed {DistanceToTarget():n0}m from the target");
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
                    setMode(Mode.Brake);
                    return;
                }
            }
            else if (mode == Mode.Brake && !vacuum_precision)
            {
                // The guided descent runs its own Brake phase and finishes itself at the gate.
                brake.gravity_compensation = true;
                brake.max_speed = 0;
                if (current_falling_speed < settings.brake_speed)
                {
                    if (atmo_precision)
                        logger.LogInfo($"[Landing] braking done at {altitude:n0}m, {SurfaceVelocity.vector.magnitude:n0}m/s, " +
                            $"{DistanceToTarget():n0}m from the target");
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
                string abort_reason = mode == Mode.Circularize ? circularize.abort_reason
                    : mode == Mode.DeorbitBurn ? deorbit_burn.abort_reason
                    : mode == Mode.AtmoDeorbit ? atmo_deorbit.abort_reason
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


    }
}
