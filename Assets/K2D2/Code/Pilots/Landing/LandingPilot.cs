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

        // Precision landing's third phase, run right after deorbit_burn and before the normal
        // Pause -> QuickWarp -> ... sequence - a second, small correction burn against the REAL
        // post-deorbit-burn trajectory, so the descent-phase steering isn't left doing all the
        // correcting late/close to the ground. See MidCourseCorrection.cs for the full reasoning
        // (this was Reese's own idea, in response to seeing the deorbit burn alone still commit
        // ~27-28km off target even with plane trim maxed out).
        public MidCourseCorrection mid_course_correction = new MidCourseCorrection();

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
            // Same reasoning again - inserted right after DeorbitBurn (before Pause) so it runs
            // once, right after the deorbit burn actually completes, before the normal coast
            // sequence begins. See MidCourseCorrection.cs.
            MidCourseCorrection,
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

        // Logging throttle for compute_real_collision() below - this runs every single Update()
        // frame (computeValues() calls it unconditionally), so logging its result unconditionally
        // meant over 30,000 log lines from one 10-minute test alone once the converged/skip
        // diagnostics were added to track down the "warped way too far" bug. That bug's confirmed
        // fixed now (see deltaTime's own comment on compute_real_collision), so this only needs to
        // stay loud enough to catch a NEW problem, not print every frame forever. Logs immediately
        // whenever converged flips true<->false (so a real streak of trouble is never missed) plus
        // a periodic heartbeat regardless, same throttling idea as TouchDown's RCS ACTIVE logging.
        bool collision_search_last_converged = true;
        int collision_search_log_counter = 0;
        const int CollisionSearchHeartbeatFrames = 60;

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

            // Precision landing: also make sure the burn starts with real ALTITUDE margin above
            // the touchdown phase's own threshold (start_touchdown_altitude), not just enough TIME
            // to make the lateral correction above - Reese's actual complaint: "it wants to make
            // these corrections at 4km above terrain... leaves little room especially when
            // touchdown phase defaults to start at 1.5km". The lateral floor above only sizes
            // itself off how big the miss is, so a well-aimed deorbit burn (small target_error_m)
            // could still leave the correction starting uncomfortably close to
            // start_touchdown_altitude. This floor doesn't care how big the miss is - it always
            // wants at least min_correction_altitude_margin of clearance above the touchdown
            // threshold - and Math.Max's with the lateral floor above so whichever one asks for
            // more wins.
            //
            // Deliberately uses speed_collision (the full orbital speed at the predicted impact
            // point) here, NOT the vertical-only descent rate - an earlier version divided by the
            // real vertical speed there instead, which sounds more accurate but blows up exactly
            // where this search usually lands: since targetPeriapsisRadius is only a shallow dip
            // below terrain, the predicted crossing point is often very close to the orbit's actual
            // geometric periapsis, where vertical speed is close to ZERO by definition (that's what
            // periapsis means). Dividing an altitude margin by a near-zero descent rate produced a
            // multi-hour burn_before, which pushed startBurn_UT (and therefore startSafeWarp_UT)
            // into the PAST before the QuickWarp/RotationWarp modes even got a chance to warp -
            // WarpTo.Update() just gives up instantly when its target time has already passed (see
            // its own dt<0 check), so the whole warp sequence silently no-op'd and dropped straight
            // into Brake in real time with the actual collision still ~20 minutes out. That's
            // exactly what Reese hit ("went into braking right after mid course correction and
            // didn't do any warping"). speed_collision can't collapse like that (it's nonzero
            // outside a full stop), at the cost of being a less precise "how fast will we actually
            // be falling" estimate - the same tradeoff the lateral floor above already accepts.
            if (settings.precision_landing.V && speed_collision > 0.1)
            {
                double target_start_altitude = settings.start_touchdown_altitude.V + LandingSettings.min_correction_altitude_margin;
                double altitude_lead_time = target_start_altitude / speed_collision;
                burn_before = Math.Max(burn_before, altitude_lead_time);
            }

            startBurn_UT = adjusted_collision_UT - burn_duration - burn_before;

            // Backstop: whatever combination of the floors above, never schedule the burn as
            // already overdue. This is what actually broke the warp scheduling in the bug described
            // above - WarpTo silently no-ops the instant its target time is in the past, so an
            // overshot burn_before didn't just start the burn a bit early, it skipped the warp
            // entirely and forced a real-time wait for however long was actually left. Clamping
            // here means the worst case is now "start braking immediately", not "silently stop
            // warping while still 20 minutes out".
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
            // Coarse step for the initial forward walk below, and the starting half-step size for
            // the flip-and-refine bisection once a below-terrain sample is hit. Narrowed 60 -> 20
            // (max_occurrences bumped 100 -> 300 to keep the same ~6,000s total reach at the finer
            // resolution) after a real in-game log caught this search aliasing between two
            // DIFFERENT valid terrain crossings roughly one orbital period apart: right after a
            // deorbit burn, the still-coasting orbit can dip below terrain both on the current pass
            // (near) and again a full period later (far). A 60s-wide forward step can walk clean
            // OVER a narrow near dip without ever sampling inside it - and since this whole search
            // restarts fresh from current_ut+120s every single frame, the tiny frame-to-frame shift
            // in that starting anchor meant most frames landed on the far crossing while roughly 1
            // in 15 happened to land a sample inside the narrower near one, flickering the result
            // back and forth. Everything downstream (LandingPilot.compute_startBurn, then
            // warp_to.UT) reads whatever this function finds unconditionally every frame, so that
            // flicker was sending the auto-warp target back and forth between "burn very soon" and
            // "burn a full orbit later" - and whichever one briefly won right as high-speed warp
            // kicked in could send the vessel warping straight through the real, nearer burn
            // window before the next frame's flip caught up. That's what was behind the "warped
            // waaay too far" report landing 40+km off target. A finer step makes it far less likely
            // to skip the near dip in the first place; the converged/orbit-period guards below are
            // the backstop for whatever this doesn't catch.
            double deltaTime = 20;
            int max_occurrences = 300;
            double time = start_time;
            double terrainAltitude = 0;
            // True only once a sample actually lands within 1m of the terrain (the loop's own
            // break condition below) - false if the loop instead runs out of iterations still
            // walking/bisecting without ever narrowing that far. Previously this wasn't tracked at
            // all, so a frame where the search simply ran out of budget (walked the full 300 steps
            // forward without ever finding ANY below-terrain sample, or started bisecting but didn't
            // finish) still went on to overwrite adjusted_collision_UT/target_error_m with whatever
            // half-finished result it had - see the guard below.
            bool converged = false;

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
                // The loop above ran out of iterations without ever landing within 1m of the
                // terrain - either it never found a below-terrain sample at all (legitimately no
                // collision within the search's ~6,000s reach), or it found one but didn't finish
                // narrowing in on it. Either way this frame's result isn't trustworthy enough to
                // drive the warp schedule - see the big comment on deltaTime above for exactly why
                // a half-finished/wrong-orbit search here used to send the auto-warp target
                // flickering. Keep the last good prediction instead of overwriting it. Only log the
                // FIRST frame of a new not-converged streak (convergence_changed) - a whole streak
                // logging every single frame is exactly the spam this throttling exists to avoid.
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
            // thrust is deliberately changing the orbit and a real, legitimate jump in predicted
            // impact time (a braking burn pushing it later, say) is exactly the point, not a bug.
            // While coasting, nothing is changing the orbit, so the same physical crossing should
            // only ever get SOONER as current_time_ut advances - a jump forward by more than half
            // an orbital period almost certainly means this frame's search skipped past the real
            // near crossing and landed on a later one again (see deltaTime's comment for why that
            // can still happen occasionally even at the finer step). Only meaningful when the orbit
            // is actually bound (a hyperbolic capture trajectory has no "next orbit" to alias onto).
            bool coasting_unpowered = mode == Mode.Pause || mode == Mode.QuickWarp
                || mode == Mode.RotationWarp || mode == Mode.Waiting;
            if (coasting_unpowered && adjusted_collision_UT > 0)
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
