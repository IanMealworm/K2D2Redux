using System;
using KSP.Sim.impl;

namespace K2D2.Landing
{
    // Plans the landing burn for atmospheric precision landing: when to light the engines so that
    // K2D2's regular braking (TouchDown in Brake mode: full thrust against the surface velocity
    // until the fall is slow) ends a little above the ground, and how much shorter that burn makes
    // the flight than the unpowered fall Redux predicts - the overshoot the entry guidance has to
    // aim for so the burn ends on the target.
    //
    // The regular landing times its burn from the airless coast, which in air is far too early:
    // the test flights lit the engines at 10-13 km and ~1,300 m/s, braked to a near stop kilometres
    // up and lost 6-7 km of range doing it.
    //
    // The model is a flat, two-dimensional fall in the plane of motion (fine for the last few tens
    // of km), with gravity, drag = k * density(height) * speed^2 against the airflow and, while
    // burning, thrust against the airflow. k (the vessel's drag per unit of air density and speed
    // squared) is measured in flight from its actual deceleration, and the body's own air density
    // by height is used - so the model only has to get the shape of the drag right, not its size.
    //
    // When the vessel is heading past the target with no steering left to bring it back, the burn
    // starts earlier instead: braking sooner kills the horizontal speed sooner, so it ends short
    // of where the latest burn would. (It can't help the other way - nothing makes the latest
    // burn go further.) Where the target is, in this model's terms, comes from Redux: it's as far
    // before the model's own unpowered landing point as Redux puts the real one past the target,
    // which cancels most of the model's own error.
    //
    // Until the burn, the fall also has the lift the vessel is flying with (its upward part, as a
    // measured fraction of the drag), because Redux's prediction - which the overshoot is added
    // to - assumes that attitude is held all the way down. The braking itself is flown pointing
    // straight into the airflow, so it has none.
    public class AtmoBurnPlanner
    {
        // The engines light this long after the handover - the vessel turns to retrograde first
        // (TouchDown only burns once it's pointing within its max angle).
        public const double turn_delay_s = 3;
        // Ignition is planned on this fraction of the engines' full thrust, as a reserve; the burn
        // itself uses all of it, so it normally stops higher than planned.
        const double planning_thrust_fraction = 0.9;
        // k is measured while the vessel flies at an angle of attack, which drags more than the
        // engines-first attitude it brakes in. Less drag during the burn means lighting earlier,
        // the safe direction to be wrong in.
        const double burn_drag_fraction = 0.8;
        const double step_s = 0.2;
        // Overshoots smaller than this are left alone.
        const double early_burn_threshold_m = 300;
        const double max_fall_s = 900;
        const double max_burn_s = 300;
        // k is smoothed over about this long; the measurement is noisy frame to frame.
        const double drag_smoothing_s = 3;

        public double drag_k { get; private set; } = double.NaN;
        // Upward lift as a fraction of the drag (negative: lift pointing down).
        public double lift_over_drag { get; private set; }
        public bool has_drag => !double.IsNaN(drag_k);

        CelestialBodyComponent table_body;
        double[] density;
        const double table_step_m = 100;
        double last_drag_UT = double.NaN;

        public void Reset()
        {
            drag_k = double.NaN;
            lift_over_drag = 0;
            last_drag_UT = double.NaN;
        }

        // Feeds one measurement of the drag deceleration (m/s^2) at a height above sea level and
        // speed through the air.
        // lift_up_accel: the upward part of the lift (m/s^2, at right angles to the airflow).
        public void MeasureDrag(CelestialBodyComponent body, double drag_accel, double lift_up_accel,
            double sea_altitude, double air_speed, double UT)
        {
            double rho = Density(body, sea_altitude);
            if (rho <= 0 || air_speed < 10 || drag_accel <= 0)
                return;
            double k = drag_accel / (rho * air_speed * air_speed);
            double ratio = lift_up_accel / drag_accel;
            if (double.IsNaN(drag_k))
            {
                drag_k = k;
                lift_over_drag = ratio;
            }
            else
            {
                double f = Math.Min(1, Math.Max(0, UT - last_drag_UT) / drag_smoothing_s);
                drag_k += (k - drag_k) * f;
                lift_over_drag += (ratio - lift_over_drag) * f;
            }
            last_drag_UT = UT;
        }

        public struct Plan
        {
            public bool found;
            // Seconds from now until the handover to the braking (which lights the engines
            // turn_delay_s later). 0: due now, or overdue.
            public double handover_in_s;
            // Height above the target's ground where the braking (planned on reduced thrust)
            // brings the fall down to the slow speed.
            public double stop_height_m;
            // How much further the unpowered fall goes than the burn (full thrust, from the
            // planned handover): the overshoot.
            public double shortfall_m;
            public double stop_falling_speed;
            // True when the handover was brought forward to stop short of an overshoot, and how far
            // past the target (along track) the braking is then expected to end.
            public bool early;
            public double expected_overshoot_m;
            // How far past the target the latest burn would end.
            public double latest_overshoot_m;
            // The latest handover, before any bringing forward.
            public double latest_handover_in_s;
        }

        struct State
        {
            public double x, h, vx, vz;   // downrange, height above the target's ground, and their speeds
        }

        // height: above the target's ground; ground_altitude: the target's ground above sea level.
        // v_horizontal, v_vertical: through the air (vertical positive up). max_accel: the
        // engines' full thrust per unit of mass. redux_along_m: how far past the target (along the
        // ground track) Redux has the unpowered fall landing - NaN to leave the burn at its latest.
        public Plan Compute(CelestialBodyComponent body, double height, double ground_altitude,
            double v_horizontal, double v_vertical, double max_accel,
            double stop_height_target, double stop_falling_speed, double redux_along_m)
        {
            var plan = new Plan { stop_falling_speed = stop_falling_speed };
            if (!has_drag || max_accel <= 0)
                return plan;
            stop_height_target = Math.Max(1, stop_height_target);

            // The unpowered fall from here, kept step by step to start the burn from.
            var fall = new System.Collections.Generic.List<State>(512);
            var s = new State { x = 0, h = height, vx = v_horizontal, vz = v_vertical };
            fall.Add(s);
            for (double t = 0; t < max_fall_s && s.h > 0; t += step_s)
            {
                Step(body, ground_altitude, ref s, 0, 1, lift_over_drag);
                fall.Add(s);
            }
            double ballistic_range = GroundCrossing(fall);

            // Later ignition stops lower. Find the last sample whose burn still stops at or above the
            // wanted height.
            double reduced = max_accel * planning_thrust_fraction;
            Burn(body, ground_altitude, fall[0], reduced, stop_falling_speed, out double first_stop, out _);
            int ignite;
            Burn(body, ground_altitude, fall[fall.Count - 1], reduced, stop_falling_speed, out double last_stop, out _);
            if (first_stop < stop_height_target)
                ignite = 0;
            else if (last_stop >= stop_height_target)
                ignite = fall.Count - 1;
            else
            {
                int lo = 0, hi = fall.Count - 1;
                while (hi - lo > 1)
                {
                    int mid = (lo + hi) / 2;
                    Burn(body, ground_altitude, fall[mid], reduced, stop_falling_speed, out double h_stop, out _);
                    if (h_stop >= stop_height_target) lo = mid; else hi = mid;
                }
                ignite = lo;
            }

            Burn(body, ground_altitude, fall[ignite], max_accel, stop_falling_speed, out _, out double powered_range);
            plan.latest_handover_in_s = ignite * step_s;
            plan.shortfall_m = ballistic_range - powered_range;

            // Overshooting: the latest burn ending past the target. Bring the burn forward to the
            // last sample whose burn ends at or short of it (an earlier burn always ends shorter).
            if (!double.IsNaN(redux_along_m))
            {
                double target_x = ballistic_range - redux_along_m;
                plan.expected_overshoot_m = plan.latest_overshoot_m = powered_range - target_x;
                if (plan.expected_overshoot_m > early_burn_threshold_m)
                {
                    Burn(body, ground_altitude, fall[0], max_accel, stop_falling_speed, out _, out double now_x);
                    int early;
                    if (now_x >= target_x)
                        early = 0;
                    else
                    {
                        int lo = 0, hi = ignite;
                        while (hi - lo > 1)
                        {
                            int mid = (lo + hi) / 2;
                            Burn(body, ground_altitude, fall[mid], max_accel, stop_falling_speed, out _, out double x);
                            if (x <= target_x) lo = mid; else hi = mid;
                        }
                        early = lo;
                    }
                    Burn(body, ground_altitude, fall[early], max_accel, stop_falling_speed, out _, out double early_x);
                    plan.expected_overshoot_m = early_x - target_x;
                    plan.early = true;
                    ignite = early;
                }
            }

            Burn(body, ground_altitude, fall[ignite], reduced, stop_falling_speed, out plan.stop_height_m, out _);
            plan.handover_in_s = ignite * step_s;
            plan.found = true;
            return plan;
        }

        // The braking from `start`: a coast while the vessel turns, then thrust against the airflow
        // until the fall is down to stop_falling_speed (or the ground comes first).
        void Burn(CelestialBodyComponent body, double ground_altitude, State start, double thrust,
            double stop_falling_speed, out double stop_height, out double stop_x)
        {
            var s = start;
            double t = 0;
            for (; t < turn_delay_s && s.h > 0; t += step_s)
                Step(body, ground_altitude, ref s, 0, burn_drag_fraction, 0);
            for (; t < max_burn_s && s.h > 0 && s.vz < -stop_falling_speed; t += step_s)
                Step(body, ground_altitude, ref s, thrust, burn_drag_fraction, 0);
            // Still falling fast when the time ran out counts as not stopping at all.
            stop_height = s.vz < -stop_falling_speed ? 0 : Math.Max(0, s.h);
            stop_x = s.x;
        }

        // Midpoint (second-order) step.
        void Step(CelestialBodyComponent body, double ground_altitude, ref State s, double thrust, double drag_fraction,
            double lift_ratio)
        {
            Acceleration(body, ground_altitude, s, thrust, drag_fraction, lift_ratio, out double ax, out double az);
            var m = new State
            {
                x = s.x + s.vx * step_s / 2,
                h = s.h + s.vz * step_s / 2,
                vx = s.vx + ax * step_s / 2,
                vz = s.vz + az * step_s / 2,
            };
            Acceleration(body, ground_altitude, m, thrust, drag_fraction, lift_ratio, out ax, out az);
            s.x += m.vx * step_s;
            s.h += m.vz * step_s;
            s.vx += ax * step_s;
            s.vz += az * step_s;
        }

        void Acceleration(CelestialBodyComponent body, double ground_altitude, State s, double thrust, double drag_fraction,
            double lift_ratio, out double ax, out double az)
        {
            double sea_altitude = s.h + ground_altitude;
            double r = body.radius + sea_altitude;
            double g = body.gravParameter / (r * r);
            double v = Math.Sqrt(s.vx * s.vx + s.vz * s.vz);
            double drag_per_speed = drag_fraction * drag_k * Density(body, sea_altitude) * v;
            double thrust_per_speed = v > 1e-3 ? thrust / v : 0;
            ax = -(drag_per_speed + thrust_per_speed) * s.vx;
            // Gravity, less the curve of the ground falling away under horizontal speed.
            az = -g + s.vx * s.vx / r - (drag_per_speed + thrust_per_speed) * s.vz;
            // Lift: at right angles to the airflow, on the upper side (velocity turned +90°).
            double lift_per_speed = lift_ratio * drag_per_speed;
            ax += -lift_per_speed * s.vz;
            az += lift_per_speed * s.vx;
        }

        // Where the fall crosses the ground, between the last two samples.
        static double GroundCrossing(System.Collections.Generic.List<State> fall)
        {
            var b = fall[fall.Count - 1];
            if (fall.Count < 2 || b.h > 0)
                return b.x;
            var a = fall[fall.Count - 2];
            double f = a.h / Math.Max(1e-9, a.h - b.h);
            return a.x + (b.x - a.x) * f;
        }

        // The body's air density at a height above sea level, from a table built on first use.
        double Density(CelestialBodyComponent body, double sea_altitude)
        {
            if (body != table_body || density == null)
            {
                table_body = body;
                int n = (int)(Math.Max(0, body.atmosphereDepth) / table_step_m) + 2;
                density = new double[n];
                for (int i = 0; i < n; i++)
                {
                    double altitude = i * table_step_m;
                    density[i] = altitude >= body.atmosphereDepth ? 0
                        : body.GetDensity(body.GetPressure(altitude), body.GetTemperature(altitude));
                }
            }
            double index = Math.Max(0, sea_altitude) / table_step_m;
            int i0 = (int)index;
            if (i0 >= density.Length - 1)
                return 0;
            double f = index - i0;
            return density[i0] + (density[i0 + 1] - density[i0]) * f;
        }
    }
}
