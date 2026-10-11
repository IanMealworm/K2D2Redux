using System;
using KSP.Sim;
using KSP2FlightAssistant.MathLibrary;

namespace K2D2.Landing
{
    // Small orbit helpers shared across K2D2, plus the burn planning for vacuum precision landing:
    // which deorbit burn (and later, which small correction burn) makes the coast come down on
    // the target. The actual "where does this coast land" prediction is ImpactModel.
    public static class LandingTargeting
    {
        internal static double WrapDegrees180(double deg)
        {
            deg %= 360.0;
            if (deg > 180.0) deg -= 360.0;
            if (deg < -180.0) deg += 360.0;
            return deg;
        }

        // Current orbital period from a state vector pair (vis-viva -> semi-major axis ->
        // Kepler's third law).
        public static double OrbitalPeriodFromStateVectors(Vector3d r0, Vector3d v0, double gravParameter)
        {
            double r0mag = r0.magnitude;
            double v0mag = v0.magnitude;
            double sma = 1.0 / (2.0 / r0mag - v0mag * v0mag / gravParameter);
            return 2.0 * Math.PI * Math.Sqrt(sma * sma * sma / gravParameter);
        }

        // For Circularize.cs (precision landing's precondition phase - the deorbit planning
        // assumes a roughly circular starting orbit). Semi-major axis from vis-viva (same formula
        // as OrbitalPeriodFromStateVectors above), apoapsis/periapsis radii from the standard eccentricity vector
        // (e = ((v^2 - mu/r) r - (r.v) v) / mu) rather than anything engine-specific - deliberately
        // avoids IKeplerPatch/PatchedConicsOrbit member access entirely (unlike ManeuverCreator's
        // existing Circularize* methods, which read orbit.Apoapsis/orbit.Periapsis off a
        // "GetLastOrbit() as PatchedConicsOrbit" cast - the same cast pattern that throws
        // InvalidCastException for the actively-flown vessel under Redux, since its orbit is a
        // CurrentPatchedConicsOrbit, not a PatchedConicsOrbit).
        public static void OrbitalElementsFromStateVectors(Vector3d r0, Vector3d v0, double gravParameter,
            out double semiMajorAxis, out double apoapsisRadius, out double periapsisRadius)
        {
            double r0mag = r0.magnitude;
            double v0mag = v0.magnitude;
            semiMajorAxis = 1.0 / (2.0 / r0mag - v0mag * v0mag / gravParameter);

            double rDotV = Vector3d.Dot(r0, v0);
            Vector3d eccVec = ((v0mag * v0mag - gravParameter / r0mag) * r0 - rDotV * v0) / gravParameter;
            double eccentricity = eccVec.magnitude;

            apoapsisRadius = semiMajorAxis * (1.0 + eccentricity);
            periapsisRadius = semiMajorAxis * (1.0 - eccentricity);
        }

        // Not a real orbital period - a hyperbolic/unbound trajectory only ever passes periapsis
        // once, it doesn't repeat. This is just a sensible time SCALE to sweep across when
        // looking for that one passage (see TimeToNextPeriapsis's caller in NodeExPilot.
        // CreateCircularizeNode - the "Circularize at PE" button needs to work on an inbound SOI
        // capture trajectory, before any capture burn has happened). Same formula as
        // OrbitalPeriodFromStateVectors above, but taking the absolute value of the semi-major
        // axis (negative for a hyperbolic/unbound orbit - see OrbitalElementsFromStateVectors) so
        // the result stays finite and positive instead of NaN, plus a 2x safety margin since -
        // unlike a real period - there's no guarantee this "characteristic timescale" is itself
        // long enough to contain the passage being searched for.
        public static double SearchWindowFromStateVectors(Vector3d r0, Vector3d v0, double gravParameter)
        {
            double r0mag = r0.magnitude;
            double v0mag = v0.magnitude;
            double sma = 1.0 / (2.0 / r0mag - v0mag * v0mag / gravParameter);
            return 4.0 * Math.PI * Math.Sqrt(Math.Abs(sma * sma * sma) / gravParameter);
        }

        // Time (seconds from now) until the current orbit's next apoapsis passage - needed to
        // time the Circularize burn (standard practice: circularize by raising periapsis at
        // apoapsis). Found by propagating forward with KeplerPropagator and bisecting on the sign
        // of radial velocity (r.v: positive while outbound/before apoapsis, negative once inbound/
        // past it) rather than deriving it from a closed-form Kepler's-equation true/eccentric/
        // mean-anomaly chain - more propagator calls, but far fewer places for a quadrant/sign
        // mistake to hide.
        public static double TimeToNextApoapsis(Vector3d r0, Vector3d v0, double gravParameter, double period)
        {
            const int samples = 24;
            double stepSize = period / samples;

            double prevDt = 0;
            double prevRadialVelocity = Vector3d.Dot(r0, v0);

            for (int i = 1; i <= samples; i++)
            {
                double dt = i * stepSize;
                KeplerPropagator.Propagate(r0, v0, gravParameter, dt, out Vector3d r, out Vector3d v);
                double radialVelocity = Vector3d.Dot(r, v);

                if (prevRadialVelocity > 0 && radialVelocity <= 0)
                {
                    double lo = prevDt, hi = dt;
                    for (int j = 0; j < 40; j++)
                    {
                        double mid = (lo + hi) / 2.0;
                        KeplerPropagator.Propagate(r0, v0, gravParameter, mid, out Vector3d rm, out Vector3d vm);
                        if (Vector3d.Dot(rm, vm) > 0) lo = mid; else hi = mid;
                    }
                    return (lo + hi) / 2.0;
                }

                prevDt = dt;
                prevRadialVelocity = radialVelocity;
            }

            // Radial velocity was already <= 0 from the very first sample - we're at/essentially
            // past apoapsis this instant. "Burn now" beats leaving a caller with a stale time.
            return 0;
        }

        // Mirror of TimeToNextApoapsis above, for the Node tab's "Circularize at PE" button -
        // same propagate-and-bisect approach, watching for the opposite sign flip (radial velocity
        // negative/inbound -> positive/outbound, i.e. periapsis passage).
        public static double TimeToNextPeriapsis(Vector3d r0, Vector3d v0, double gravParameter, double period)
        {
            const int samples = 24;
            double window = period;

            // Retries with a doubled window if no crossing turns up - needed for the
            // hyperbolic/unbound case (Circularize at PE during an SOI capture), where "period" is
            // really SearchWindowFromStateVectors's heuristic estimate rather than a guaranteed-
            // correct orbital period, so a single pass "not found" might just mean the window
            // undershot. A bound (elliptical) caller's real period always contains one, so this
            // loop is a no-op for that case - purely extra insurance for the hyperbolic one.
            for (int attempt = 0; attempt < 4; attempt++)
            {
                double stepSize = window / samples;

                double prevDt = 0;
                double prevRadialVelocity = Vector3d.Dot(r0, v0);

                for (int i = 1; i <= samples; i++)
                {
                    double dt = i * stepSize;
                    KeplerPropagator.Propagate(r0, v0, gravParameter, dt, out Vector3d r, out Vector3d v);
                    double radialVelocity = Vector3d.Dot(r, v);

                    if (prevRadialVelocity < 0 && radialVelocity >= 0)
                    {
                        double lo = prevDt, hi = dt;
                        for (int j = 0; j < 40; j++)
                        {
                            double mid = (lo + hi) / 2.0;
                            KeplerPropagator.Propagate(r0, v0, gravParameter, mid, out Vector3d rm, out Vector3d vm);
                            if (Vector3d.Dot(rm, vm) < 0) lo = mid; else hi = mid;
                        }
                        return (lo + hi) / 2.0;
                    }

                    prevDt = dt;
                    prevRadialVelocity = radialVelocity;
                }

                window *= 2.0;
            }

            // Radial velocity was already >= 0 from the very first sample - we're at/essentially
            // past periapsis this instant. Same reasoning as TimeToNextApoapsis's own fallback.
            return 0;
        }


        // ------------------------------------------------------------------------------------
        // Targeted burns
        // ------------------------------------------------------------------------------------

        // Where the coast is aimed: a point above the target, not the target itself.
        //
        // A coast that comes down right on the target leaves the powered descent no good option:
        // the vessel is still moving at nearly orbital speed when it's already low, so the burn
        // ends up being a last-moment brake a few hundred meters above the ground. Aiming the coast
        // to pass over the target at aim_height instead (it then comes down some way beyond)
        // keeps the vessel high while it brakes, and the descent drops onto the target from above.
        internal class TargetSite
        {
            public readonly Vector3d point;        // body-fixed aim point, aim_height above the target
            public readonly double radius;         // distance of the aim point from the body's center
            public readonly double ground_radius;  // distance of the target's terrain from the body's center
            public readonly double aim_height;
            readonly Vector3d up, axis1, axis2;

            // aim_height_m: how high over the target the coast is aimed; by default a few percent
            // of the body's radius (see AimHeight).
            public TargetSite(ImpactModel model, double latitude, double longitude, double aim_height_m = double.NaN)
            {
                up = model.SurfaceUp(latitude, longitude);
                ground_radius = model.TerrainRadius(latitude, longitude);
                aim_height = double.IsNaN(aim_height_m) ? AimHeight(model.body.radius) : aim_height_m;
                radius = ground_radius + aim_height;
                point = up * radius;

                Vector3d reference = Math.Abs(up.y) < 0.9 ? new Vector3d(0, 1, 0) : new Vector3d(1, 0, 0);
                axis1 = Vector3d.Cross(up, reference).normalized;
                axis2 = Vector3d.Cross(up, axis1);
            }

            // Where a body-fixed point is relative to the target, as distances along the ground in
            // two perpendicular directions - the length of (a, b) is the distance over the surface.
            // Measured as an angle around the body rather than a straight-line offset, so a point
            // on the far side of the body reads as far away instead of folding back onto the target.
            public void Offset(Vector3d body_fixed, out double a, out double b)
            {
                Vector3d d = body_fixed.normalized;
                double x = Vector3d.Dot(d, axis1), y = Vector3d.Dot(d, axis2);
                double sideways = Math.Sqrt(x * x + y * y);
                if (sideways < 1e-12)
                {
                    a = b = 0;
                    return;
                }
                double distance = radius * Math.Atan2(sideways, Vector3d.Dot(d, up));
                a = distance * x / sideways;
                b = distance * y / sideways;
            }
        }

        // A few percent of the body's radius: about 6 km on the Mun, 2 km on Minmus.
        static double AimHeight(double body_radius) => Math.Max(2000, Math.Min(10000, 0.03 * body_radius));

        internal struct TargetedBurn
        {
            public bool found;
            public double UT;
            public double prograde;     // m/s along the orbital velocity at UT
            public double normal;       // m/s along Cross(r, v) in ImpactModel's axes - not necessarily the game's "normal" sign
            public double miss_m;       // how far from the aim point the coast passes, measured along the ground
            public double impact_UT;
            public Vector3d impact;     // body-fixed
            public double clearance_m;  // lowest height above terrain on the way down, away from the impact point
        }

        // Height above terrain the coast has to keep on its way to the aim point, except within
        // clearance_skip_near_m of it. The guided descent ignites somewhere along this stretch.
        public const double required_clearance_m = 300;
        public const double clearance_skip_near_m = 3000;

        internal static Vector3d AfterBurn(Vector3d r, Vector3d v, double prograde, double normal)
        {
            return v + v.normalized * prograde + Vector3d.Cross(r, v).normalized * normal;
        }

        // Prograde delta-v (negative = retrograde) that puts periapsis at `periapsis` radius.
        static double ProgradeForPeriapsis(Vector3d r, Vector3d v, double normal, double periapsis, double mu)
        {
            double hi = 0, lo = -0.99 * v.magnitude;
            if (ImpactModel.PeriapsisRadius(r, AfterBurn(r, v, hi, normal), mu) <= periapsis)
                return 0;

            for (int i = 0; i < 50; i++)
            {
                double mid = 0.5 * (lo + hi);
                if (ImpactModel.PeriapsisRadius(r, AfterBurn(r, v, mid, normal), mu) > periapsis) hi = mid; else lo = mid;
            }
            return 0.5 * (lo + hi);
        }

        // Prograde delta-v (negative: retrograde) at (r, v) that brings the coast into the atmosphere
        // entry_angle_deg below the horizon, measured against the air. More retrograde comes in
        // steeper. Returns the most retrograde tried if even that isn't steep enough.
        internal static double ProgradeForEntryAngle(ImpactModel model, Vector3d r, Vector3d v, double UT, double entry_angle_deg)
        {
            double hi = 0, lo = -0.99 * v.magnitude;
            for (int i = 0; i < 50; i++)
            {
                double mid = 0.5 * (lo + hi);
                if (EntryAngle(model, r, AfterBurn(r, v, mid, 0), UT, out _) < entry_angle_deg) hi = mid; else lo = mid;
            }
            return 0.5 * (lo + hi);
        }

        // Degrees below the horizon (against the air) where the coast from (r, v) at UT comes into
        // the atmosphere, and how fast through the air. 0 if it doesn't.
        internal static double EntryAngle(ImpactModel model, Vector3d r, Vector3d v, double UT, out double air_speed)
        {
            air_speed = 0;
            if (r.magnitude <= model.body.radius + model.body.atmosphereDepth
                || !model.EntryState(r, v, UT, out Vector3d entry_r, out Vector3d entry_v))
                return 0;
            Vector3d air = model.AirVelocity(entry_r, entry_v);
            air_speed = air.magnitude;
            return -Math.Asin(Math.Max(-1, Math.Min(1, Vector3d.Dot(air, entry_r.normalized) / air_speed))) * 180 / Math.PI;
        }

        // Periapsis depth below the target's ground (for PlanDeorbit) that a burn from (r, v) needs
        // for an entry this steep.
        internal static double PeriapsisDepthForEntryAngle(ImpactModel model, TargetSite site, Vector3d r, Vector3d v, double UT,
            double entry_angle_deg, out double prograde)
        {
            prograde = ProgradeForEntryAngle(model, r, v, UT, entry_angle_deg);
            return site.ground_radius - ImpactModel.PeriapsisRadius(r, AfterBurn(r, v, prograde, 0), model.mu);
        }

        // Where a burn of (prograde, normal) from (r, v) at UT makes the coast come down,
        // relative to the target.
        internal static bool Miss(ImpactModel model, TargetSite site, Vector3d r, Vector3d v, double UT,
            double prograde, double normal, out double a, out double b, out double impact_UT, out Vector3d impact)
        {
            a = b = 0;
            impact = Vector3d.zero;
            if (!model.Impact(r, AfterBurn(r, v, prograde, normal), UT, site.radius, out impact_UT, out Vector3d p))
                return false;

            impact = model.BodyFixed(p, impact_UT);
            site.Offset(impact, out a, out b);
            return true;
        }

        static TargetedBurn Finish(ImpactModel model, TargetSite site, Vector3d r, Vector3d v, double UT,
            double prograde, double normal)
        {
            var burn = new TargetedBurn { UT = UT, prograde = prograde, normal = normal };
            if (!Miss(model, site, r, v, UT, prograde, normal, out double a, out double b, out burn.impact_UT, out burn.impact))
                return burn;

            burn.found = true;
            burn.miss_m = Math.Sqrt(a * a + b * b);
            burn.clearance_m = model.MinTerrainClearance(r, AfterBurn(r, v, prograde, normal), UT, burn.impact_UT,
                burn.impact, clearance_skip_near_m, out _);
            return burn;
        }

        // A solve step that may run into the normal delta-v cap: solve for both unknowns, and if
        // the second one would go past the cap, hold it at the cap and fit the first one alone.
        // Returns the change to apply to the first unknown and the new value of the second.
        static void Step2(double a, double b, double da1, double db1, double da2, double db2,
            double second, double second_cap, out double step_first, out double new_second)
        {
            new_second = second;
            double det = da1 * db2 - da2 * db1;
            if (second_cap > 0 && Math.Abs(det) > 1e-12)
            {
                step_first = (-a * db2 + b * da2) / det;
                double step_second = (-da1 * b + db1 * a) / det;
                double wanted = second + step_second;
                if (Math.Abs(wanted) <= second_cap)
                {
                    new_second = wanted;
                    return;
                }
                new_second = Math.Max(-second_cap, Math.Min(second_cap, wanted));
            }

            // Least squares in the first unknown only.
            double norm = da1 * da1 + db1 * db1;
            step_first = norm > 1e-12 ? -(a * da1 + b * db1) / norm : 0;
        }

        // Deorbit burn from a roughly circular orbit, after start_UT, within `revolutions` orbits.
        //
        // The burn lowers periapsis to `periapsis_depth_m` below the target's terrain height, so
        // the coast comes down steeply enough to clear the terrain before the target. What's left
        // to choose is WHEN to burn - that slides the landing point along the ground track - plus
        // an optional sideways (normal) part, up to max_normal, to shift the track toward the target.
        //
        // The ground track only passes close to the target on some orbits (the body turns
        // underneath), so several orbits are checked. Waiting a whole orbit has to buy a landing
        // at least `wait_penalty_m` closer to be worth it - the powered descent fixes a sideways
        // miss of a few km for very little fuel.
        internal static TargetedBurn PlanDeorbit(ImpactModel model, IKeplerPatch orbit, TargetSite site,
            double start_UT, int revolutions, double periapsis_depth_m, double max_normal)
        {
            const int samples_per_orbit = 48;
            const double wait_penalty_m = 3000;
            double periapsis = site.ground_radius - periapsis_depth_m;

            model.StateAt(orbit, start_UT, out Vector3d r0, out Vector3d v0);
            double period = model.Period(r0, v0);
            double step = period / samples_per_orbit;
            int count = revolutions * samples_per_orbit + 1;

            var distance = new double[count];
            for (int i = 0; i < count; i++)
            {
                double UT = start_UT + i * step;
                model.StateAt(orbit, UT, out Vector3d r, out Vector3d v);
                double prograde = ProgradeForPeriapsis(r, v, 0, periapsis, model.mu);
                distance[i] = Miss(model, site, r, v, UT, prograde, 0, out double a, out double b, out _, out _)
                    ? Math.Sqrt(a * a + b * b) : double.PositiveInfinity;
            }

            var best = new TargetedBurn();
            double best_score = double.PositiveInfinity;
            for (int i = 0; i < count; i++)
            {
                bool local_min = !double.IsInfinity(distance[i])
                    && (i == 0 || distance[i] <= distance[i - 1])
                    && (i == count - 1 || distance[i] <= distance[i + 1]);
                if (!local_min)
                    continue;

                TargetedBurn burn = RefineDeorbit(model, orbit, site, start_UT + i * step, step, periapsis, max_normal);
                if (!burn.found || burn.UT < start_UT)
                    continue;

                double score = burn.miss_m + wait_penalty_m * (burn.UT - start_UT) / period;
                if (burn.clearance_m < required_clearance_m)
                    score += 1e9; // only if nothing else clears the terrain
                if (score < best_score)
                {
                    best_score = score;
                    best = burn;
                }
            }
            return best;
        }

        static TargetedBurn RefineDeorbit(ImpactModel model, IKeplerPatch orbit, TargetSite site,
            double UT, double max_shift, double periapsis, double max_normal)
        {
            const double dt = 1, dn = 0.2;
            double normal = 0;
            double start = UT;

            bool Eval(double t, double n, out double a, out double b)
            {
                model.StateAt(orbit, t, out Vector3d r, out Vector3d v);
                double prograde = ProgradeForPeriapsis(r, v, n, periapsis, model.mu);
                return Miss(model, site, r, v, t, prograde, n, out a, out b, out _, out _);
            }

            for (int iteration = 0; iteration < 15; iteration++)
            {
                if (!Eval(UT, normal, out double a, out double b))
                    break;
                if (a * a + b * b < 1)
                    break;

                Eval(UT + dt, normal, out double at, out double bt);
                double an = a, bn = b;
                if (max_normal > 0)
                    Eval(UT, normal + dn, out an, out bn);

                Step2(a, b, (at - a) / dt, (bt - b) / dt, (an - a) / dn, (bn - b) / dn,
                    normal, max_normal, out double shift, out normal);
                shift = Math.Max(-max_shift, Math.Min(max_shift, shift));
                UT += shift;
                if (Math.Abs(UT - start) > 2 * max_shift)
                    break;
            }

            model.StateAt(orbit, UT, out Vector3d r1, out Vector3d v1);
            return Finish(model, site, r1, v1, UT, ProgradeForPeriapsis(r1, v1, normal, periapsis, model.mu), normal);
        }

        // Small correction burn at UT on an orbit that's already coming down near the target:
        // prograde/retrograde moves the landing point along the ground track, normal moves it
        // sideways (up to max_normal).
        internal static TargetedBurn PlanCorrection(ImpactModel model, IKeplerPatch orbit, TargetSite site,
            double UT, double max_normal)
        {
            const double dp = 0.05, dn = 0.2, max_step = 20;
            model.StateAt(orbit, UT, out Vector3d r, out Vector3d v);

            double prograde = 0, normal = 0;
            for (int iteration = 0; iteration < 15; iteration++)
            {
                if (!Miss(model, site, r, v, UT, prograde, normal, out double a, out double b, out _, out _))
                    break;
                if (a * a + b * b < 1)
                    break;

                Miss(model, site, r, v, UT, prograde + dp, normal, out double ap, out double bp, out _, out _);
                double an = a, bn = b;
                if (max_normal > 0)
                    Miss(model, site, r, v, UT, prograde, normal + dn, out an, out bn, out _, out _);

                Step2(a, b, (ap - a) / dp, (bp - b) / dp, (an - a) / dn, (bn - b) / dn,
                    normal, max_normal, out double step, out normal);
                prograde += Math.Max(-max_step, Math.Min(max_step, step));
            }

            return Finish(model, site, r, v, UT, prograde, normal);
        }

        // How far from the aim point the orbit passes as it is right now - Coast without the
        // terrain check, cheap enough to run every frame.
        internal static bool CoastMiss(ImpactModel model, IKeplerPatch orbit, TargetSite site, double UT, out double miss_m)
        {
            model.StateAt(orbit, UT, out Vector3d r, out Vector3d v);
            bool found = Miss(model, site, r, v, UT, 0, 0, out double a, out double b, out _, out _);
            miss_m = found ? Math.Sqrt(a * a + b * b) : 0;
            return found;
        }

        // How the point where the coast reaches the aim height moves for each m/s of prograde and
        // of normal delta-v burned at UT (same two ground directions as TargetSite.Offset). Used
        // where the miss itself comes from somewhere else - Redux's prediction with drag - and only
        // the response to a burn is taken from the airless coast.
        internal static bool BurnSensitivity(ImpactModel model, IKeplerPatch orbit, TargetSite site, double UT,
            out double da_prograde, out double db_prograde, out double da_normal, out double db_normal)
        {
            const double dp = 0.5, dn = 1;
            da_prograde = db_prograde = da_normal = db_normal = 0;
            model.StateAt(orbit, UT, out Vector3d r, out Vector3d v);
            if (!Miss(model, site, r, v, UT, 0, 0, out double a, out double b, out _, out _)
                || !Miss(model, site, r, v, UT, dp, 0, out double ap, out double bp, out _, out _)
                || !Miss(model, site, r, v, UT, 0, dn, out double an, out double bn, out _, out _))
                return false;

            da_prograde = (ap - a) / dp;
            db_prograde = (bp - b) / dp;
            da_normal = (an - a) / dn;
            db_normal = (bn - b) / dn;
            return true;
        }

        // The prograde/normal burn that moves a miss of (a, b) to zero given those sensitivities,
        // holding normal within max_normal (and fitting prograde alone if that bites).
        internal static void SolveCorrection(double a, double b, double da_prograde, double db_prograde,
            double da_normal, double db_normal, double max_normal, out double prograde, out double normal)
        {
            Step2(a, b, da_prograde, db_prograde, da_normal, db_normal, 0, max_normal, out prograde, out normal);
        }

        // A burn of (prograde, normal) at UT, with where it leaves the airless coast.
        internal static TargetedBurn Burn(ImpactModel model, IKeplerPatch orbit, TargetSite site, double UT,
            double prograde, double normal)
        {
            model.StateAt(orbit, UT, out Vector3d r, out Vector3d v);
            return Finish(model, site, r, v, UT, prograde, normal);
        }

        // Where the orbit comes down as it is right now, without any burn.
        internal static TargetedBurn Coast(ImpactModel model, IKeplerPatch orbit, TargetSite site, double UT)
        {
            model.StateAt(orbit, UT, out Vector3d r, out Vector3d v);
            return Finish(model, site, r, v, UT, 0, 0);
        }
    }
}
