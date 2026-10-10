using System;
using KSP.Api;
using KSP.Sim;
using KSP.Sim.impl;
using KSP2FlightAssistant.MathLibrary;

namespace K2D2.Landing
{
    // Where a coasting vessel would meet the ground, for an airless body.
    //
    // With no atmosphere, a coast is exactly a Kepler orbit, so instead of stepping through time
    // this jumps straight to any moment with KeplerPropagator. The only other ingredient is the
    // body turning underneath: a point on the surface keeps the same coordinates in the body's
    // rotating frame (body.transform.bodyFrame), so every position is converted into that frame
    // for the moment it happens - which also makes lat/lon and terrain lookups straightforward.
    //
    // All vectors are in bodyFrame's axes as they are at `epoch_UT` (when this model was created).
    // "Inertial" vectors don't turn with the body; "body-fixed" ones are where that point is on
    // the surface.
    internal class ImpactModel
    {
        public readonly CelestialBodyComponent body;
        public readonly double mu;
        public readonly double epoch_UT;
        readonly ITransformFrame frame;
        readonly ICoordinateSystem inertial_frame;
        readonly Vector3d spin_axis;
        readonly double spin_rate; // rad/s

        public ImpactModel(CelestialBodyComponent body, double now_UT)
        {
            this.body = body;
            mu = body.gravParameter;
            epoch_UT = now_UT;
            frame = body.transform.bodyFrame;
            inertial_frame = body.SimulationObject.transform.celestialFrame;

            Vector3d omega = frame.ToLocalVector(body.relativeAngularVelocity);
            spin_rate = omega.magnitude;
            spin_axis = spin_rate > 0 ? omega / spin_rate : Vector3d.zero;
        }

        // The orbit's state at UT. Same Zup->Yup swap and celestialFrame pairing as
        // LandingPilot.compute_real_collision.
        public void StateAt(IKeplerPatch orbit, double UT, out Vector3d r, out Vector3d v)
        {
            Vector3d p = orbit.GetRelativePositionAtUTZup(UT);
            Vector3d u = orbit.GetOrbitalVelocityAtUTZup(UT);
            r = frame.ToLocalPosition(new Position(inertial_frame, new Vector3d(p.x, p.z, p.y)));
            v = frame.ToLocalVector(new Vector(inertial_frame, new Vector3d(u.x, u.z, u.y)));
        }

        // A game-provided direction (any frame) in this model's axes.
        public Vector3d ToModelAxes(Vector direction) => frame.ToLocalVector(direction);

        // Where an inertial position at UT sits on the turning body.
        public Vector3d BodyFixed(Vector3d inertial, double UT)
        {
            if (spin_rate == 0)
                return inertial;

            // Rotating backwards by however far the body has turned since epoch_UT (Rodrigues'
            // rotation formula about the spin axis).
            double angle = -spin_rate * (UT - epoch_UT);
            double c = Math.Cos(angle), s = Math.Sin(angle);
            return inertial * c + Vector3d.Cross(spin_axis, inertial) * s
                + spin_axis * (Vector3d.Dot(spin_axis, inertial) * (1 - c));
        }

        public void LatLon(Vector3d body_fixed, out double latitude, out double longitude)
        {
            body.GetLatLonAltFromRadius(new Position(frame, body_fixed), out latitude, out longitude, out _);
        }

        public Vector3d SurfaceUp(double latitude, double longitude) => body.GetRelSurfaceNVector(latitude, longitude);

        public double TerrainRadius(double latitude, double longitude) =>
            body.SurfaceProvider.GetTerrainAltitudeFromCenter(latitude, longitude);

        public double TerrainRadius(Vector3d body_fixed)
        {
            LatLon(body_fixed, out double latitude, out double longitude);
            return TerrainRadius(latitude, longitude);
        }

        public double Period(Vector3d r, Vector3d v) => LandingTargeting.OrbitalPeriodFromStateVectors(r, v, mu);

        public static double PeriapsisRadius(Vector3d r, Vector3d v, double mu)
        {
            LandingTargeting.OrbitalElementsFromStateVectors(r, v, mu, out _, out _, out double periapsis);
            return periapsis;
        }

        // First moment after UT that the coast from (r, v) comes down through `radius` (a sphere
        // around the body's center), searching up to one orbit ahead.
        public bool Impact(Vector3d r, Vector3d v, double UT, double radius,
            out double impact_UT, out Vector3d impact_inertial)
        {
            impact_UT = 0;
            impact_inertial = Vector3d.zero;

            if (PeriapsisRadius(r, v, mu) > radius)
                return false;

            double period = Period(r, v);
            double horizon = double.IsNaN(period) ? 3600 : period;
            double step = Math.Min(horizon / 90, 30);

            double previous = 0;
            for (double t = step; t <= horizon + step; t += step)
            {
                KeplerPropagator.Propagate(r, v, mu, t, out Vector3d p, out _);
                if (p.magnitude > radius)
                {
                    previous = t;
                    continue;
                }

                double lo = previous, hi = t;
                for (int i = 0; i < 40; i++)
                {
                    double mid = 0.5 * (lo + hi);
                    KeplerPropagator.Propagate(r, v, mu, mid, out Vector3d m, out _);
                    if (m.magnitude > radius) lo = mid; else hi = mid;
                }

                KeplerPropagator.Propagate(r, v, mu, hi, out impact_inertial, out _);
                impact_UT = UT + hi;
                return true;
            }
            return false;
        }

        // Lowest height above the real terrain along the coast from (r, v) at UT until end_UT,
        // skipping the part within skip_near_m of `end_point` (the coast is meant to reach the
        // ground there). Only the last part of the coast is sampled - before that the vessel is
        // still at orbital height.
        public double MinTerrainClearance(Vector3d r, Vector3d v, double UT, double end_UT,
            Vector3d end_point_body_fixed, double skip_near_m, out Vector3d lowest_point)
        {
            const int samples = 120;
            const double sampled_fraction = 0.35;

            double clearance = double.PositiveInfinity;
            lowest_point = Vector3d.zero;
            double duration = end_UT - UT;
            double start = duration * (1 - sampled_fraction);

            for (int i = 0; i <= samples; i++)
            {
                double t = start + (duration - start) * i / samples;
                KeplerPropagator.Propagate(r, v, mu, t, out Vector3d p, out _);
                Vector3d ground = BodyFixed(p, UT + t);
                if (SurfaceDistance(ground, end_point_body_fixed) < skip_near_m)
                    continue;

                double height = ground.magnitude - TerrainRadius(ground);
                if (height < clearance)
                {
                    clearance = height;
                    lowest_point = ground;
                }
            }
            return clearance;
        }

        // Distance along the surface between the points under two positions.
        public double SurfaceDistance(Vector3d a, Vector3d b)
        {
            double angle = Math.Atan2(Vector3d.Cross(a, b).magnitude, Vector3d.Dot(a, b));
            return angle * body.radius;
        }
    }
}
