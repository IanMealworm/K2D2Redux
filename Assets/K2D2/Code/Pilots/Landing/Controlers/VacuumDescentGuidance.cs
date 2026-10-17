using System;
using K2D2.Controller;
using K2D2.KSPService;
using KSP.Sim;
using KSP.Sim.impl;
using KTools;
using UnityEngine;
using ILogger = SpaceWarp2.API.Logging.ILogger;

namespace K2D2.Landing
{
    // Powered descent for vacuum precision landing: one continuous burn from coasting flight to a
    // point just above the target, where TouchDown takes over for the final vertical drop.
    //
    // Instead of braking retrograde and tilting to push the predicted impact point around, this
    // solves for the whole burn at once. Given where the vessel is now, where it has to be at the
    // end (the "gate" above the target) and how long the burn should take, there's a simple
    // closed-form answer for the thrust that gets there using the least effort: thrust that
    // changes linearly over the burn. That answer is recomputed every frame from the vessel's
    // actual position and velocity, so errors are corrected smoothly as they appear instead of
    // building up until a separate correction kicks in.
    //
    // All the vectors here are in the body's rotating frame (body.transform.bodyFrame), where the
    // target is a fixed point. The price of that frame is two "fictitious" accelerations - Coriolis
    // and centrifugal - that get added to gravity. Both are tiny on slow-turning moons but keep the
    // math correct anywhere.
    public class VacuumDescentGuidance : ExecuteController
    {
        public ILogger logger = SpaceWarp2.API.Logging.LogManager.GetLogger("K2D2.VacuumDescentGuidance");

        // Height above the target terrain the burn delivers the vessel to, falling straight down at
        // the speed TouchDown wants at that height, so TouchDown continues the descent without a jump.
        public const double gate_height_m = 100;

        // The planned burn may need at most this fraction of full thrust at its hardest moment. The
        // rest is margin for prediction error and engines that don't deliver exactly what's asked.
        const double planned_thrust_fraction = 0.8;

        // The planned burn also never needs more than this many times the body's surface gravity.
        // The cheapest descent for a lander with far more thrust than it needs is a last-moment
        // brake very close to the ground; capping the planned braking keeps the burn longer and
        // the vessel higher while it slows, at a small fuel cost.
        const double max_planned_braking_g = 3;

        // During the burn, if staying on schedule would need more than this, the burn is stretched
        // instead of falling behind with the throttle pinned at 100%.
        const double max_thrust_fraction = 0.95;

        // The thrust formula divides by the remaining time, so in the last seconds tiny errors turn
        // into large, twitchy commands. TouchDown is better suited to that last stretch.
        const double handoff_time_to_go_s = 3;

        const double min_burn_s = 5;
        const double max_burn_s = 900;

        // The ignition search only looks at the last part of the coast. Even a weak lander kills
        // orbital speed well within this, and it keeps the search cheap during long coasts.
        const double ignition_search_window_s = 900;

        // Planned paths must stay at least this far above the target's terrain height (a sphere
        // through the target - terrain elsewhere along the way isn't known to the planner).
        const double min_path_clearance_m = gate_height_m / 2;

        // Throttle ramp, mainly so ignition eases in instead of slamming on.
        const float throttle_rate_per_s = 2;

        // Below this pointing error the full commanded throttle is used; above it throttle fades
        // out, reaching zero at TouchDown's max angle setting.
        const float full_throttle_angle_deg = 5;

        readonly LandingPilot landing;
        readonly KSPVessel current_vessel;

        // Plan made while coasting - LandingPilot schedules warp and ignition from these.
        public bool has_plan { get; private set; }
        public double ignition_UT { get; private set; }
        public double planned_burn_s { get; private set; }
        double planned_dv;
        Vector3d planned_direction;

        public bool ignited { get; private set; }
        double burn_end_UT;
        double time_to_go;

        float throttle;
        double commanded_accel;
        float pointing_error_deg;
        Vector3d thrust_direction;

        // Refreshed every frame by ReadState().
        CelestialBodyComponent body;
        ITransformFrame frame;
        double mu;
        Vector3d omega;    // body spin, rad/s, as a vector along its axis
        Vector3d position;
        Vector3d velocity; // relative to the turning surface
        double max_accel;  // full thrust / current mass

        // Target, in the same frame. target_r/target_v are the gate state the burn aims for.
        double cached_target_lat = double.NaN, cached_target_lon = double.NaN;
        CelestialBodyComponent cached_target_body;
        Vector3d target_up;
        double target_ground_radius;
        Vector3d target_r;
        Vector3d target_v;

        // Reused between frames so planning doesn't allocate.
        Vector3d[] path_r = new Vector3d[0];
        Vector3d[] path_v = new Vector3d[0];

        int log_counter = 0;
        bool last_logged_has_plan = false;
        bool terrain_warning;
        const int LogEveryFrames = 120;

        public VacuumDescentGuidance(LandingPilot landing)
        {
            this.landing = landing;
            current_vessel = K2D2_Plugin.Instance.current_vessel;
        }

        // Arms the controller for a new descent: points along the planned burn, engine off.
        public override void Start()
        {
            finished = false;
            ignited = false;
            throttle = 0;
            log_counter = 0;
        }

        public void ClearPlan()
        {
            has_plan = false;
        }

        // ---- Thrust law ----

        // Thrust acceleration over a burn, as a function of time since its start s:
        // a(s) = c0 + c1*s + c2*s^2. c0 is what to command right now.
        struct Burn
        {
            public Vector3d c0, c1, c2;
            public double duration;

            public Vector3d AccelAt(double s) => c0 + c1 * s + c2 * (s * s);
        }

        // For a burn lasting t seconds that starts in state (r, v) under a constant acceleration g
        // and must end in state (rt, vt), with the thrust at the very end exactly cancelling g.
        //
        // "ZEM" (zero-effort miss) is where the vessel would be at the end if the engine stayed
        // off, compared to where it should be; "ZEV" is the same for velocity. The three
        // coefficients come from requiring the thrust to make up both, and to end at -g. That last
        // condition is what makes the burn finish upright and hovering-while-descending at the
        // gate, instead of still braking hard sideways in its final seconds - the shape of
        // Apollo's descent guidance. It costs a few percent more delta-v than the plain
        // least-effort burn.
        static Burn SolveBurn(Vector3d r, Vector3d v, Vector3d g, Vector3d rt, Vector3d vt, double t)
        {
            Vector3d zem = rt - (r + v * t + g * (0.5 * t * t));
            Vector3d zev = vt - (v + g * t);
            Vector3d a_end = -g;
            double t2 = t * t;
            return new Burn
            {
                c0 = a_end - zev * (6 / t) + zem * (12 / t2),
                c1 = (a_end * (-t2) + zev * (5 * t) - zem * 8) * (6 / (t2 * t)),
                c2 = (a_end * t2 - zev * (4 * t) + zem * 6) * (6 / (t2 * t2)),
                duration = t,
            };
        }

        const int burn_samples = 8;

        static double PeakAccel(Burn burn)
        {
            double peak = 0;
            for (int i = 0; i <= burn_samples; i++)
                peak = Math.Max(peak, burn.AccelAt(burn.duration * i / burn_samples).magnitude);
            return peak;
        }

        // Total delta-v the burn uses (integral of |thrust acceleration|), by Simpson's rule.
        static double BurnDeltaV(Burn burn)
        {
            double sum = 0;
            for (int i = 0; i <= burn_samples; i++)
            {
                double weight = (i == 0 || i == burn_samples) ? 1 : (i % 2 == 1 ? 4 : 2);
                sum += weight * burn.AccelAt(burn.duration * i / burn_samples).magnitude;
            }
            return sum * burn.duration / (3 * burn_samples);
        }

        // A cheap check against the target's own terrain height, used while searching. The chosen
        // burn is then checked against the real terrain along its path (TerrainClear).
        bool PathClear(Vector3d r, Vector3d v, Vector3d g, Burn burn)
        {
            for (int i = 1; i < burn_samples; i++)
            {
                double s = burn.duration * i / burn_samples;
                double s2 = s * s;
                Vector3d p = r + v * s + (g + burn.c0) * (0.5 * s2) + burn.c1 * (s2 * s / 6) + burn.c2 * (s2 * s2 / 12);
                if (p.magnitude - target_ground_radius < min_path_clearance_m)
                    return false;
            }
            return true;
        }

        // Height above the real terrain along a planned burn path, away from the target (near it the
        // path is meant to come down to the gate). This needs a terrain lookup per sample, so it's
        // only done for the burn the search picked, not every candidate.
        bool TerrainClear(Vector3d r, Vector3d v, Burn burn)
        {
            const int samples = 16;
            const double skip_near_target_m = 1000;
            Vector3d g = EffectiveGravity(r, v);
            for (int i = 1; i < samples; i++)
            {
                double s = burn.duration * i / samples;
                double s2 = s * s;
                Vector3d p = r + v * s + (g + burn.c0) * (0.5 * s2) + burn.c1 * (s2 * s / 6) + burn.c2 * (s2 * s2 / 12);
                if (GroundDistanceToTarget(p) < skip_near_target_m)
                    continue;

                body.GetLatLonAltFromRadius(new Position(frame, p), out double lat, out double lon, out _);
                if (p.magnitude - body.SurfaceProvider.GetTerrainAltitudeFromCenter(lat, lon) < min_path_clearance_m)
                    return false;
            }
            return true;
        }

        bool Feasible(Vector3d r, Vector3d v, Vector3d g, double t, double accel_limit)
        {
            Burn burn = SolveBurn(r, v, g, target_r, target_v, t);
            return PeakAccel(burn) <= accel_limit && PathClear(r, v, g, burn);
        }

        // Shortest burn starting from (r, v) that stays under accel_limit and above the clearance
        // floor. Shorter burns spend less time holding the vessel up against gravity, so this is
        // also (very nearly) the cheapest one. Returns its delta-v, or infinity if none fits.
        double ShortestBurn(Vector3d r, Vector3d v, double accel_limit, double shortest, out Burn burn)
        {
            Vector3d g = EffectiveGravity(r, v);
            burn = default;

            double previous = 0;
            for (double t = shortest; t <= max_burn_s; t *= 1.15)
            {
                if (Feasible(r, v, g, t, accel_limit))
                {
                    double lo = previous > 0 ? previous : t, hi = t;
                    for (int i = 0; i < 8 && previous > 0; i++)
                    {
                        double mid = 0.5 * (lo + hi);
                        if (Feasible(r, v, g, mid, accel_limit)) hi = mid; else lo = mid;
                    }
                    burn = SolveBurn(r, v, g, target_r, target_v, hi);
                    return BurnDeltaV(burn);
                }
                previous = t;
            }
            return double.PositiveInfinity;
        }

        // Fallback when nothing fits the limits: the burn needing the least peak thrust, ignoring
        // the clearance floor. Better than not burning at all.
        double LeastThrustBurn(Vector3d r, Vector3d v)
        {
            Vector3d g = EffectiveGravity(r, v);
            double best_t = min_burn_s, best_peak = double.PositiveInfinity;
            for (double t = min_burn_s; t <= max_burn_s; t *= 1.05)
            {
                double peak = PeakAccel(SolveBurn(r, v, g, target_r, target_v, t));
                if (peak < best_peak) { best_peak = peak; best_t = t; }
            }
            return best_t;
        }

        double PlanningAccelLimit()
        {
            double surface_gravity = mu / (target_ground_radius * target_ground_radius);
            return Math.Min(planned_thrust_fraction * max_accel, max_planned_braking_g * surface_gravity);
        }

        // ---- Forces in the rotating frame ----

        Vector3d Gravity(Vector3d r)
        {
            double d = r.magnitude;
            return r * (-mu / (d * d * d));
        }

        Vector3d CoastAccel(Vector3d r, Vector3d v)
        {
            return Gravity(r) - Vector3d.Cross(omega, v) * 2 - Vector3d.Cross(omega, Vector3d.Cross(omega, r));
        }

        // The thrust law assumes one constant acceleration for the whole burn. Gravity is averaged
        // between here and the target (it gets a little stronger closer in), and Coriolis uses the
        // current velocity. Re-solving every frame absorbs what this approximation gets wrong.
        Vector3d EffectiveGravity(Vector3d r, Vector3d v)
        {
            return (Gravity(r) + Gravity(target_r)) * 0.5 - Vector3d.Cross(omega, v) * 2
                - Vector3d.Cross(omega, Vector3d.Cross(omega, r));
        }

        void CoastStep(ref Vector3d r, ref Vector3d v, double h)
        {
            // Classic 4th-order Runge-Kutta: accurate enough at a few seconds per step that a whole
            // coast to the surface stays within meters of the game's own orbit.
            Vector3d k1v = CoastAccel(r, v), k1r = v;
            Vector3d k2v = CoastAccel(r + k1r * (h / 2), v + k1v * (h / 2)), k2r = v + k1v * (h / 2);
            Vector3d k3v = CoastAccel(r + k2r * (h / 2), v + k2v * (h / 2)), k3r = v + k2v * (h / 2);
            Vector3d k4v = CoastAccel(r + k3r * h, v + k3v * h), k4r = v + k3v * h;
            r += (k1r + k2r * 2 + k3r * 2 + k4r) * (h / 6);
            v += (k1v + k2v * 2 + k3v * 2 + k4v) * (h / 6);
        }

        // ---- State ----

        bool ReadState()
        {
            var vessel = current_vessel?.VesselComponent;
            if (vessel == null)
                return false;

            IKeplerPatch orbit = vessel.Orbit;
            body = orbit.referenceBody;
            frame = body.transform.bodyFrame;
            mu = body.gravParameter;
            omega = frame.ToLocalVector(body.relativeAngularVelocity);

            // Same source and Zup->Yup swap as LandingPilot.compute_real_collision: the orbit's
            // body-relative state in the non-rotating celestialFrame, re-expressed in bodyFrame.
            double now = GeneralTools.Current_UT;
            Vector3d p = orbit.GetRelativePositionAtUTZup(now);
            Vector3d u = orbit.GetOrbitalVelocityAtUTZup(now);
            var inertial = body.SimulationObject.transform.celestialFrame;
            position = frame.ToLocalPosition(new Position(inertial, new Vector3d(p.x, p.z, p.y)));
            // Velocity seen from the turning surface = inertial velocity minus the surface's own
            // motion at this point.
            velocity = frame.ToLocalVector(new Vector(inertial, new Vector3d(u.x, u.z, u.y)))
                - Vector3d.Cross(omega, position);

            max_accel = landing.burn_dV.full_dv;

            UpdateTarget();
            return true;
        }

        void UpdateTarget()
        {
            double lat = landing.settings.target_latitude.V;
            double lon = landing.settings.target_longitude.V;
            if (lat != cached_target_lat || lon != cached_target_lon || body != cached_target_body)
            {
                cached_target_lat = lat;
                cached_target_lon = lon;
                cached_target_body = body;
                target_up = body.GetRelSurfaceNVector(lat, lon);
                target_ground_radius = body.SurfaceProvider.GetTerrainAltitudeFromCenter(lat, lon);
            }

            target_r = target_up * (target_ground_radius + gate_height_m);
            target_v = target_up * -landing.settings.compute_limit_speed((float)gate_height_m);
        }

        double GroundDistanceToTarget(Vector3d r)
        {
            double angle = Math.Atan2(Vector3d.Cross(r, target_up).magnitude, Vector3d.Dot(r, target_up));
            return angle * target_ground_radius;
        }

        // ---- Planning (while coasting) ----

        // Finds when to light the engine. Every possible ignition point along the coming coast gets
        // its shortest burn and that burn's delta-v. Lighting too early wastes fuel holding the
        // vessel up for longer; too late and the only way left is to overshoot and come back (or
        // there's no way at all). The cheapest point in between is the ignition point.
        public void Plan(double impact_UT)
        {
            if (!ReadState() || max_accel <= 0)
            {
                has_plan = false;
                return;
            }

            double now = GeneralTools.Current_UT;
            double horizon = impact_UT - now;
            if (horizon <= 0)
            {
                has_plan = false;
                return;
            }

            double step = Math.Max(0.5, Math.Min(10, horizon / 600));
            int count = (int)Math.Ceiling(horizon / step) + 1;
            if (path_r.Length < count)
            {
                path_r = new Vector3d[count];
                path_v = new Vector3d[count];
            }

            Vector3d r = position, v = velocity;
            for (int i = 0; i < count; i++)
            {
                path_r[i] = r;
                path_v[i] = v;
                CoastStep(ref r, ref v, step);
            }

            double accel_limit = PlanningAccelLimit();
            int first = Math.Max(0, (int)((horizon - ignition_search_window_s) / step));
            int stride = Math.Max(1, (count - first) / 60);

            int best = -1;
            double best_dv = double.PositiveInfinity;
            Burn best_burn = default;

            void Consider(int i)
            {
                double dv = ShortestBurn(path_r[i], path_v[i], accel_limit, min_burn_s, out Burn burn);
                if (dv < best_dv)
                {
                    best = i;
                    best_dv = dv;
                    best_burn = burn;
                }
            }

            // If the cheapest burn would pass too close to the real terrain, earlier (higher)
            // ignition points are searched again, stepping further back each time. If none of
            // those clear it either, the cheapest burn is flown anyway - the terrain check only
            // samples the path, and the descent hands over to TouchDown if it gets too low.
            int last = count;
            int cheapest = -1;
            double cheapest_dv = 0;
            Burn cheapest_burn = default;
            terrain_warning = false;
            for (int attempt = 0; attempt < 6; attempt++)
            {
                best = -1;
                best_dv = double.PositiveInfinity;
                for (int i = first; i < last; i += stride)
                    Consider(i);

                // Coarse pass found roughly where; check every step around it.
                if (best >= 0 && stride > 1)
                {
                    int center = best;
                    for (int i = Math.Max(first, center - stride + 1); i < Math.Min(last, center + stride); i++)
                        if (i != center)
                            Consider(i);
                }

                if (attempt == 0)
                {
                    cheapest = best;
                    cheapest_dv = best_dv;
                    cheapest_burn = best_burn;
                }

                if (best < 0 || TerrainClear(path_r[best], path_v[best], best_burn))
                    break;

                last = best - stride * (1 << attempt);
                if (attempt == 5 || last <= first)
                {
                    best = cheapest;
                    best_dv = cheapest_dv;
                    best_burn = cheapest_burn;
                    terrain_warning = true;
                    break;
                }
            }

            has_plan = best >= 0;
            if (has_plan)
            {
                ignition_UT = now + best * step;
                planned_burn_s = best_burn.duration;
                planned_dv = best_dv;
                planned_direction = best_burn.c0.normalized;
            }

            log_counter++;
            if (has_plan != last_logged_has_plan || log_counter % LogEveryFrames == 0)
            {
                last_logged_has_plan = has_plan;
                if (has_plan && terrain_warning)
                    logger.LogWarning("[Guidance] no ignition point keeps the whole burn clear of the sampled terrain - " +
                        "flying the cheapest one");
                if (has_plan)
                    logger.LogInfo($"[Guidance] plan: ignition in {ignition_UT - now:n1}s at alt {path_r[best].magnitude - target_ground_radius:n0}m " +
                        $"range {GroundDistanceToTarget(path_r[best]):n0}m speed {path_v[best].magnitude:n1}m/s | burn {planned_burn_s:n1}s " +
                        $"dv {planned_dv:n0}m/s | max accel {max_accel:n2}m/s2");
                else
                    logger.LogInfo($"[Guidance] no burn within {PlanningAccelLimit():n2}m/s2 reaches the target from this coast " +
                        $"(max accel {max_accel:n2}m/s2) - braking will start on the regular schedule");
            }
        }

        // ---- Flying ----

        public void Ignite()
        {
            if (!ReadState())
                return;

            double now = GeneralTools.Current_UT;
            double dv = ShortestBurn(position, velocity, PlanningAccelLimit(), min_burn_s, out Burn burn);
            double duration = burn.duration;
            if (double.IsInfinity(dv))
            {
                duration = LeastThrustBurn(position, velocity);
                logger.LogWarning($"[Guidance] ignition: no burn fits within {PlanningAccelLimit():n2}m/s2 and clearance " +
                    $"- flying the gentlest one ({duration:n1}s), the vessel may not reach the target");
            }

            burn_end_UT = now + duration;
            ignited = true;
            finished = false;

            // Surface speed computed here should match the game's own readout; a mismatch of about
            // twice the surface rotation speed would mean the spin vector's sign is wrong.
            logger.LogInfo($"[Guidance] ignition: burn {duration:n1}s dv {dv:n0}m/s | alt {position.magnitude - target_ground_radius:n0}m " +
                $"range {GroundDistanceToTarget(position):n0}m | surface speed {velocity.magnitude:n2}m/s " +
                $"(game: {current_vessel.VesselVehicle.SurfaceSpeed:n2}m/s) | max accel {max_accel:n2}m/s2");
        }

        void PointAlong(Vector3d direction)
        {
            var autopilot = current_vessel.Autopilot;
            autopilot.Enabled = true;
            if (autopilot.AutopilotMode != AutopilotMode.StabilityAssist)
                autopilot.SetMode(AutopilotMode.StabilityAssist);
            autopilot.SAS.lockedMode = false;
            autopilot.SAS.SetTargetOrientation(new Vector(frame, direction), false);

            Rotation vessel_rotation = Rotation.Reframed(current_vessel.GetRotation(), frame);
            Vector3d forward = (vessel_rotation.localRotation * Vector3.up).normalized;
            pointing_error_deg = (float)Vector3d.Angle(direction, forward);
        }

        public override void Update()
        {
            if (!ReadState())
                return;

            if (!ignited)
            {
                // Waiting for ignition: turn to the planned burn direction now so the engine can
                // light at full commanded thrust instead of waiting on the turn.
                Vector3d direction = has_plan ? planned_direction : -velocity.normalized;
                PointAlong(direction);
                current_vessel.SetThrottle(0);
                status_line = $"Aligning for powered descent\nAngle = {pointing_error_deg:n1}°";
                return;
            }

            TimeWarpTools.SetRateIndex(0, false);
            current_vessel.SetSpeedMode(SpeedDisplayMode.Surface);

            double now = GeneralTools.Current_UT;
            time_to_go = burn_end_UT - now;
            bool too_low = landing.altitude < min_path_clearance_m;
            if (time_to_go <= handoff_time_to_go_s || too_low)
            {
                Vector3d horizontal = velocity - target_up * Vector3d.Dot(velocity, target_up);
                logger.LogInfo($"[Guidance] {(too_low ? "below clearance floor, handing over early" : "gate reached")}: " +
                    $"{GroundDistanceToTarget(position):n1}m from target, {position.magnitude - target_ground_radius:n1}m above target terrain, " +
                    $"vertical {Vector3d.Dot(velocity, target_up):n2}m/s horizontal {horizontal.magnitude:n2}m/s");
                finished = true;
                return;
            }

            if (max_accel <= 0)
            {
                current_vessel.SetThrottle(0);
                throttle = 0;
                status_line = "No engine thrust available";
                return;
            }

            Vector3d g = EffectiveGravity(position, velocity);
            Burn burn = SolveBurn(position, velocity, g, target_r, target_v, time_to_go);

            if (PeakAccel(burn) > max_thrust_fraction * max_accel)
            {
                double dv = ShortestBurn(position, velocity, max_thrust_fraction * max_accel, time_to_go, out Burn longer);
                if (!double.IsInfinity(dv))
                {
                    logger.LogInfo($"[Guidance] falling behind schedule, stretching the burn from {time_to_go:n1}s to {longer.duration:n1}s");
                    burn_end_UT = now + longer.duration;
                    time_to_go = longer.duration;
                    burn = longer;
                }
            }

            commanded_accel = burn.c0.magnitude;
            if (commanded_accel > 1e-3)
                thrust_direction = burn.c0 / commanded_accel;

            PointAlong(thrust_direction);

            float max_angle = landing.brake.touch_down_max_angle.V;
            float alignment = Mathf.Clamp01((max_angle - pointing_error_deg) / Mathf.Max(max_angle - full_throttle_angle_deg, 1));
            float wanted = Mathf.Clamp01((float)(commanded_accel / max_accel)) * alignment;
            throttle = Mathf.MoveTowards(throttle, wanted, throttle_rate_per_s * Time.deltaTime);
            current_vessel.SetThrottle(throttle);

            status_line = alignment < 1
                ? $"Powered descent - turning\nAngle = {pointing_error_deg:n1}°"
                : "Powered descent";

            log_counter++;
            if (log_counter % LogEveryFrames == 0)
                logger.LogInfo($"[Guidance] t_go {time_to_go:n1}s throttle {throttle:p0} accel {commanded_accel:n2}/{max_accel:n2}m/s2 " +
                    $"angle {pointing_error_deg:n1}° | {GroundDistanceToTarget(position):n0}m from target, " +
                    $"{position.magnitude - target_ground_radius:n0}m above target terrain, speed {velocity.magnitude:n1}m/s");
        }

        public override void UpdateInfoRows(Action<string, string> addRow)
        {
            if (ignited)
                addRow("Time To Go", $"{time_to_go:n1} s");
            if (body != null)
                addRow("Distance To Target", StrTool.DistanceToString(GroundDistanceToTarget(position)));

            // The rest is for tuning and bug reports.
            if (!K2D2Settings.debug_mode.V)
                return;
            if (ignited)
                addRow("Throttle", $"{throttle:p0}");
            else if (has_plan)
                addRow("Planned Burn", $"{planned_burn_s:n0} s, {planned_dv:n0} m/s");
        }
    }
}
