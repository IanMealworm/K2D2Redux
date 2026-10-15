using System;
using UnityEngine;

namespace K2D2.Lift
{
    // The Gravity Turn's throttle and steering rules. ReactiveAscent flies by them, and
    // AscentPredictor uses the very same ones to try out kick angles and hold times before the
    // kick, so what gets predicted is what gets flown.
    //
    // Phases:
    //   VerticalRise - straight up until the vertical speed reaches the kick speed.
    //   PitchKick    - tilts toward the kick angle, no further off prograde than the air allows, and
    //                  holds it until the velocity has followed. Prograde straight up has nothing to
    //                  fall over from, so the turn needs this seed.
    //   Turn         - follows prograde (zero angle of attack) and lets gravity bend the path:
    //                  surface prograde in the thick air (what the air sees), orbital prograde once
    //                  the air is thin.
    //
    // The throttle is the lowest of: max throttle, the TWR limit, the max-Q limit, the hold on the
    // time to apoapsis, and a taper as apoapsis nears its target, but never below the minimum
    // throttle. The hold works out the throttle whose upward push keeps the time to apoapsis from
    // going past its target; a lower throttle bends the path over faster. The minimum matters at
    // the end: holding the time there would ask for almost no throttle, and the rocket would level
    // off inside the air short of its target apoapsis (test flights flew level at about 60 km for
    // minutes and cooked). Held at the minimum it keeps raising the apoapsis instead.
    //
    // The nose never goes below prograde. It comes above prograde, within the angle of attack the
    // air allows, when the throttle alone can't climb as fast as the hold wants: the throttle is at
    // its limit and the time to apoapsis is still short. Without that, heavy or low-TWR rockets
    // flattened out early and flew fast and low through thick air (test flights reached 1.5 km/s
    // nearly level at 25 km). It also comes up when a part nears its temperature limit while still
    // in the air - climbing out of the air sooner is what cools it - and, if the rocket is about to
    // fall back, the TWR and max-Q limits let go as well.
    public class GravityTurnControl
    {
        public enum Phase { VerticalRise, PitchKick, Turn }

        // The kick ends once prograde is within this of the kick angle. Low-TWR rockets turn their
        // velocity slowly, so a fixed-length kick could end before anything had moved. The timeout
        // is for a rocket whose velocity never follows.
        const float KickFollowDeg = 1f;
        const float KickTimeoutS = 30f;

        // The hold closes a gap in the time to apoapsis over about this long.
        const float HoldResponseS = 10f;

        // Slow correction on top of the hold's throttle for what it doesn't model: drag, and solid
        // boosters that ignore the throttle.
        const float TrimGain = 0.002f;
        const float TrimMax = 0.2f;

        // Below this time to apoapsis the rocket is about to start falling back: the TWR and max-Q
        // limits let go.
        public const float MinTimeToApS = 10f;

        // Angle of attack allowed = this / dynamic pressure (kPa), between the min and max: a couple
        // of degrees through max-Q, where more could flip the rocket, and much more in thin air.
        const float AoaPressureProduct = 60f;
        const float AoaMinDeg = 2f;
        const float AoaMaxDeg = 35f;

        // Max-Q: full throttle up to the first fraction of the limit, down to the floor by the second.
        const float MaxQEaseStart = 0.8f;
        const float MaxQEaseEnd = 1.1f;

        // Below this air pressure (and above a quarter of the atmosphere's height), the air no longer
        // steers the rocket much, and following orbital prograde wastes less thrust turning.
        const float ThinAirKpa = 1.2f;
        const float ThinAirMinFraction = 0.25f;

        // Heat guard: from the first fraction of the hottest part's temperature limit the nose starts
        // to come up, reaching HeatGuardMaxDeg (or the air's angle-of-attack limit) by the second.
        const float HeatGuardStart = 0.8f;
        const float HeatGuardFull = 0.95f;
        const float HeatGuardMaxDeg = 15f;

        // Apoapsis taper: the throttle is limited so the rest of the climb to the target apoapsis
        // takes at least this long, instead of overshooting it at full throttle. It aims one
        // end_error_km past the finish line, or it would only creep up on it.
        const float TaperS = 5f;

        public float kick_speed_mps;
        public float kick_deg;
        public float hold_time_s;
        public float max_throttle;
        public float min_throttle;
        public float max_twr;
        public float max_q_kpa;

        public Phase phase { get; private set; } = Phase.VerticalRise;
        public float throttle { get; private set; }
        public float target_elevation_deg { get; private set; } = 90;
        public string throttle_limit { get; private set; } = "";
        public bool thin_air { get; private set; }
        public bool heat_guard { get; private set; }

        float kick_timer;
        float trim;

        public GravityTurnControl Copy() => (GravityTurnControl)MemberwiseClone();

        public void Reset()
        {
            phase = Phase.VerticalRise;
            kick_timer = 0;
            trim = 0;
            throttle = 0;
            target_elevation_deg = 90;
            throttle_limit = "";
            thin_air = false;
            heat_guard = false;
        }

        // The flight state the rules need: measured in flight, simulated by the predictor.
        public struct Inputs
        {
            public float vertical_speed;            // m/s, relative to the surface
            public float prograde_elevation_deg;    // surface velocity's pitch above the horizon
            public float orbital_prograde_elevation_deg;
            public float atmosphere_fraction;       // altitude / atmosphere height (above 1: out of it)
            public float heat_fraction;             // hottest part's temperature / its limit
            public float time_to_apoapsis_s;        // negative once past apoapsis
            public float gravity;
            public float effective_gravity;         // gravity less what horizontal speed takes off it
            public float max_accel;                 // full thrust of the lit engines / mass
            public float dynamic_pressure_kpa;
            public float remaining_ap_km;           // target apoapsis - apoapsis
            public float end_error_km;              // the ascent ends this close to the target
            public float taper_rate;                // apoapsis km/s per unit of throttle, 0 if unknown
        }

        public void Update(in Inputs i, float dt)
        {
            switch (phase)
            {
                case Phase.VerticalRise:
                    target_elevation_deg = 90;
                    throttle = ThrottleCap(i);
                    if (i.vertical_speed >= kick_speed_mps)
                    {
                        kick_timer = 0;
                        phase = Phase.PitchKick;
                    }
                    break;

                case Phase.PitchKick:
                    float kick_target = 90 - kick_deg;
                    target_elevation_deg = Mathf.Max(kick_target, i.prograde_elevation_deg - AoaLimit(i));
                    throttle = ThrottleCap(i);
                    kick_timer += dt;
                    if (i.prograde_elevation_deg <= kick_target + KickFollowDeg || kick_timer >= KickTimeoutS)
                        phase = Phase.Turn;
                    break;

                case Phase.Turn:
                    Turn(i, dt);
                    break;
            }
        }

        void Turn(in Inputs i, float dt)
        {
            thin_air = i.dynamic_pressure_kpa < ThinAirKpa && i.atmosphere_fraction > ThinAirMinFraction;
            float prograde = thin_air ? i.orbital_prograde_elevation_deg : i.prograde_elevation_deg;
            float error_s = hold_time_s - i.time_to_apoapsis_s;
            bool falling = i.time_to_apoapsis_s < MinTimeToApS;

            // Upward acceleration the thrust should give: enough to cancel gravity (time to
            // apoapsis then stays put), plus or minus enough to close the gap over HoldResponseS.
            float wanted_up_accel = i.effective_gravity * (1 + error_s / HoldResponseS);

            float cap = ThrottleCap(i);
            if (falling)
            {
                cap = max_throttle;
                throttle_limit = "Falling: full throttle";
            }

            // Throttle that gives that upward push while pointing prograde.
            float sin_prograde = Mathf.Sin(prograde * Mathf.Deg2Rad);
            float hold_throttle = float.PositiveInfinity;
            if (i.max_accel > 0)
                hold_throttle = wanted_up_accel / (i.max_accel * Mathf.Max(sin_prograde, 0.02f)) + trim;

            throttle = Mathf.Clamp(Mathf.Min(hold_throttle, cap), min_throttle, cap);
            if (hold_throttle < cap)
                throttle_limit = hold_throttle <= min_throttle ? "Min throttle" : "Hold time";

            if (hold_throttle > min_throttle && hold_throttle < cap)
                trim = Mathf.Clamp(trim + TrimGain * error_s * dt, -TrimMax, TrimMax);

            float aoa_limit = AoaLimit(i);
            float target = prograde;

            // Throttle at its limit and still short of the climb the hold wants: tilt the thrust so
            // its upward part gives it.
            bool short_of_climb = hold_throttle > cap;
            if (short_of_climb && i.max_accel > 0 && throttle > 0.001f && wanted_up_accel > i.max_accel * throttle * sin_prograde)
            {
                float sin_needed = Mathf.Clamp(wanted_up_accel / (i.max_accel * throttle), -1f, 1f);
                float needed_deg = Mathf.Asin(sin_needed) * Mathf.Rad2Deg;
                target = Mathf.Max(target, Mathf.Min(needed_deg, prograde + aoa_limit));
            }

            heat_guard = i.atmosphere_fraction < 1 && i.heat_fraction > HeatGuardStart;
            if (heat_guard)
            {
                float share = Mathf.Clamp01((i.heat_fraction - HeatGuardStart) / (HeatGuardFull - HeatGuardStart));
                target = Mathf.Max(target, prograde + share * Mathf.Min(aoa_limit, HeatGuardMaxDeg));
            }
            target_elevation_deg = Mathf.Min(target, 90f);

            if (i.taper_rate > 0)
            {
                float taper_throttle = (i.remaining_ap_km + i.end_error_km) / (i.taper_rate * TaperS);
                if (taper_throttle < throttle)
                {
                    throttle = Mathf.Max(taper_throttle, 0);
                    throttle_limit = "Apoapsis";
                }
            }
        }

        static float AoaLimit(in Inputs i) =>
            Mathf.Clamp(AoaPressureProduct / Mathf.Max(i.dynamic_pressure_kpa, 0.01f), AoaMinDeg, AoaMaxDeg);

        // Max throttle, TWR limit and max-Q limit; records which one is limiting.
        float ThrottleCap(in Inputs i)
        {
            float cap = max_throttle;
            throttle_limit = "Max throttle";

            if (i.max_accel > 0)
            {
                float twr_cap = max_twr * i.gravity / i.max_accel;
                if (twr_cap < cap)
                {
                    cap = twr_cap;
                    throttle_limit = "TWR";
                }
            }

            float q_cap = Mathf.Clamp01((MaxQEaseEnd * max_q_kpa - i.dynamic_pressure_kpa) / ((MaxQEaseEnd - MaxQEaseStart) * max_q_kpa));
            if (q_cap < cap)
            {
                cap = q_cap;
                throttle_limit = "Max Q";
            }

            return Mathf.Max(cap, min_throttle);
        }

        // Seconds to the next apoapsis, or (negative) seconds since the last one, straight from the
        // orbit's shape. Going negative past apoapsis is what lets the turn see that the rocket is
        // falling, where "time to the next apoapsis" would jump to nearly a whole orbit and look like
        // far too much climb.
        public static double SignedTimeToApoapsis(Vector3d r, Vector3d v, double mu)
        {
            double r_mag = r.magnitude;
            double v2 = v.sqrMagnitude;
            double r_dot_v = Vector3d.Dot(r, v);
            double inv_a = 2 / r_mag - v2 / mu;
            // Escaping: there's no apoapsis to wait for.
            if (inv_a <= 0)
                return double.PositiveInfinity;
            double a = 1 / inv_a;

            Vector3d e_vec = ((v2 - mu / r_mag) * r - r_dot_v * v) / mu;
            double e = e_vec.magnitude;
            if (e < 1e-9)
                return 0;

            double cos_E = (1 - r_mag / a) / e;
            double sin_E = r_dot_v / (e * Math.Sqrt(mu * a));
            double E = Math.Atan2(sin_E, cos_E);
            double M = E - e * Math.Sin(E);
            double n = Math.Sqrt(mu / (a * a * a));
            // Apoapsis is at mean anomaly +/-pi: ahead while climbing (M > 0), behind while falling.
            return (M >= 0 ? Math.PI - M : -Math.PI - M) / n;
        }
    }
}
