using System;
using System.Collections.Generic;
using System.Text;
using KSP.Sim.DeltaV;
using KSP.Sim.impl;

namespace K2D2.Lift
{
    // Picks the Gravity Turn's kick angle and hold time just before the kick. It flies the rest of
    // the ascent ahead of time for each combination, by the same rules ReactiveAscent flies
    // (GravityTurnControl), and keeps the one that reaches the target apoapsis on the least fuel,
    // counting the circularization still needed afterwards.
    //
    // The flight is simplified: the rocket is a point moving in the plane of the climb, with the
    // drag measured during the vertical rise, thrust and mass per stage from the game's delta-v
    // data, and each stage dropped the moment it runs dry. It only has to rank the choices
    // correctly - the controller corrects in flight.
    //
    // The work is spread over several frames (Continue); the rocket keeps rising straight up meanwhile.
    public class AscentPredictor
    {
        // No bigger kicks: the prediction treats the rocket as a point, so a big early kick looks
        // cheap, but in test flights picks of 20-25° fell over (one into the space center).
        static readonly float[] Kicks = { 3, 5, 7.5f, 10, 12.5f, 15 };
        // Not below 40 s: the prediction only counts fuel, which always favours flatter climbs, but it
        // can't see heating, and flat, fast climbs through the air are what burned up in test flights.
        static readonly float[] Holds = { 40, 45, 50, 60 };

        // Every candidate is also flown with this much more drag than measured, and dropped if that
        // flight fails; the score is the average of the two. The drag is measured below the speed
        // of sound and misses the extra drag around it, so a choice that only works if the estimate
        // is right would be a crash waiting to happen.
        const double PessimisticDrag = 1.6;

        // A choice whose predicted dynamic pressure goes this far past Max Dynamic Pressure is
        // dropped (the throttle limit can't hold it down while solid boosters burn).
        const float MaxQMargin = 1.15f;

        const double StepS = 0.5;
        const double MaxTimeS = 900;
        // The nose turns about this fast (SAS isn't instant).
        const double TurnRateDegS = 10;
        const double TableStepM = 250;

        struct Stage
        {
            public double end_mass;
            public double next_start_mass;      // NaN for the last stage
            public double liquid_asl, liquid_vac;
            public double locked_asl, locked_vac; // solid boosters: the throttle doesn't touch them
            public double mass_flow;              // at full thrust
        }

        // The rocket at the moment of the decision.
        public struct Start
        {
            public double radius;               // from the body's centre
            public double vertical_speed;       // relative to the surface
            public double horizontal_speed;     // relative to the surface, along the heading
            public double rotation_speed;       // the surface's own speed along the heading
            public double mass;
            public double full_thrust;          // lit engines at full throttle, right now
            public double drag_area;            // drag force / (density * airspeed^2)
        }

        public struct Result
        {
            public bool ok;
            public double delta_v;              // to reach the target apoapsis, plus circularizing there
            public float max_q_kpa;
        }

        readonly CelestialBodyComponent body;
        readonly double mu, body_radius, atmosphere_depth;
        readonly double[] density, pressure_ratio;
        readonly List<Stage> stages = new List<Stage>();
        readonly GravityTurnControl template;
        readonly Start start;
        readonly float target_km, end_error_km;
        double thrust_scale = 1;

        int next_candidate = 0;
        double best_score = double.PositiveInfinity;

        public bool done { get; private set; }
        public bool found { get; private set; }
        public float best_kick_deg { get; private set; }
        public float best_hold_s { get; private set; }
        public double best_delta_v { get; private set; }
        public string failure { get; private set; }

        public AscentPredictor(VesselComponent vessel, Start start, GravityTurnControl template, float target_km, float end_error_km)
        {
            body = vessel.mainBody;
            mu = body.gravParameter;
            body_radius = body.radius;
            atmosphere_depth = body.hasAtmosphere ? body.atmosphereDepth : 0;
            this.start = start;
            this.template = template.Copy();
            this.target_km = target_km;
            this.end_error_km = end_error_km;

            int n = (int)(atmosphere_depth / TableStepM) + 2;
            density = new double[n];
            pressure_ratio = new double[n];
            double sea_pressure = atmosphere_depth > 0 ? body.GetPressure(0) : 0;
            for (int i = 0; i < n; i++)
            {
                double altitude = i * TableStepM;
                if (altitude >= atmosphere_depth || sea_pressure <= 0)
                    continue;
                double pressure = body.GetPressure(altitude);
                density[i] = body.GetDensity(pressure, body.GetTemperature(altitude));
                pressure_ratio[i] = pressure / sea_pressure;
            }

            if (!ReadStages(vessel))
            {
                done = true;
                return;
            }

            // Scale the game's numbers to what the engines actually give right now (thrust limiters,
            // and the readout's own approximations).
            double modelled = TotalThrust(stages[0], start.radius - body_radius);
            if (modelled <= 0 || start.full_thrust <= 0)
            {
                Fail("no thrust in the current stage");
                return;
            }
            // The game's figures for the stage that's burning now should match the rocket as it is:
            // if they don't, the later stages can't be trusted either, and the sliders are safer.
            thrust_scale = start.full_thrust / modelled;
            if (thrust_scale < 0.8 || thrust_scale > 1.25)
            {
                Fail($"stage data doesn't match the engines (thrust {start.full_thrust:n0} kN, game says {modelled:n0} kN)");
                return;
            }
            if (start.mass <= stages[0].end_mass)
            {
                Fail($"stage data doesn't match the rocket (mass {start.mass:n1} t, stage ends at {stages[0].end_mass:n1} t)");
                return;
            }
        }

        static double ActiveThrustVac(DeltaVStageInfo s)
        {
            double thrust = 0;
            if (s.EnginesActiveInStage != null)
                foreach (var e in s.EnginesActiveInStage)
                    if (e?.Engine != null)
                        thrust += e.ThrustVac;
            return thrust;
        }

        // For the log: the game's own stage list, unfiltered, to check what the predictor made of it.
        public static string RawStages(VesselComponent vessel)
        {
            var info = vessel.VesselDeltaV?.StageInfo;
            if (info == null)
                return "none";
            var text = new StringBuilder();
            foreach (var s in info)
                if (s != null)
                    text.Append($" | S{s.Stage}: {s.StartMass:n1}->{s.EndMass:n1}t burn {s.StageBurnTime:n0}s vac {s.ThrustVac:n0}kN active {ActiveThrustVac(s):n0}kN ({s.EnginesActiveInStage?.Count ?? 0}) dropped {s.DecoupledMass:n1}t");
            return text.ToString();
        }

        // For the log: the stages as the predictor sees them.
        public string StageSummary()
        {
            var text = new StringBuilder($"thrust scale {thrust_scale:n2}");
            for (int i = 0; i < stages.Count; i++)
            {
                var s = stages[i];
                text.Append($" | #{i} to {s.end_mass:n2}t, liquid {s.liquid_asl:n0}-{s.liquid_vac:n0}kN, solid {s.locked_asl:n0}-{s.locked_vac:n0}kN, flow {s.mass_flow:n3}t/s, next {s.next_start_mass:n2}t");
            }
            return text.ToString();
        }

        void Fail(string why)
        {
            failure = why;
            done = true;
        }

        // From the game's delta-v readout, current stage first (stages count down as they fire).
        // Stages that don't burn anything (decouplers, parachutes) are skipped; what they drop
        // shows up in the next stage's start mass.
        bool ReadStages(VesselComponent vessel)
        {
            var info = vessel.VesselDeltaV?.StageInfo;
            if (info == null || info.Count == 0)
            {
                Fail("no stage data");
                return false;
            }

            // Judged by the engines burning during the stage, not the stage's own thrust figure: that
            // can be 0 for a stage whose engines were lit earlier (a core that keeps going after its
            // boosters drop), and skipping it made a 1,365 t booster rocket look like 334 t after
            // separation (TWR 1.9 instead of the real 1.0).
            //
            // A stage also has to be able to use fuel: a burn time, or a lit engine with an Isp.
            // While the game is still recalculating its readout it can list a stage with thrust but
            // neither (seen: "42.9->0.0t, burn 0s, 600 kN, no engines"), which used to stop the auto
            // plan with "no fuel use"; a moment later that stage reads 0 kN and is skipped anyway.
            var burning = new List<DeltaVStageInfo>();
            foreach (var s in info)
                if (s != null && s.StartMass > s.EndMass && (ActiveThrustVac(s) > 0 || s.ThrustVac > 0) && CanUseFuel(s))
                    burning.Add(s);
            burning.Sort((a, b) => b.Stage.CompareTo(a.Stage));
            if (burning.Count == 0)
            {
                Fail("no stage with fuel and engines");
                return false;
            }

            for (int i = 0; i < burning.Count; i++)
            {
                var s = burning[i];
                var stage = new Stage
                {
                    end_mass = s.EndMass,
                    next_start_mass = i + 1 < burning.Count ? burning[i + 1].StartMass : double.NaN,
                };

                double engines_asl = 0, engines_vac = 0;
                if (s.EnginesActiveInStage != null)
                    foreach (var e in s.EnginesActiveInStage)
                    {
                        if (e?.Engine == null)
                            continue;
                        bool locked = e.Engine.currentEngineModeData?.throttleLocked ?? false;
                        if (locked)
                        {
                            stage.locked_asl += e.ThrustASL;
                            stage.locked_vac += e.ThrustVac;
                        }
                        else
                        {
                            stage.liquid_asl += e.ThrustASL;
                            stage.liquid_vac += e.ThrustVac;
                        }
                        engines_asl += e.ThrustASL;
                        engines_vac += e.ThrustVac;
                    }
                // No per-engine numbers: treat the stage's total as throttleable.
                if (engines_vac <= 0)
                {
                    stage.liquid_asl = s.ThrustASL;
                    stage.liquid_vac = s.ThrustVac;
                    stage.locked_asl = stage.locked_vac = 0;
                }

                // Fuel used per second at full thrust: from the stage's burn time, or failing that
                // from the engines' own fuel use.
                if (s.StageBurnTime > 0)
                    stage.mass_flow = (s.StartMass - s.EndMass) / s.StageBurnTime;
                else if (s.EnginesActiveInStage != null)
                    foreach (var e in s.EnginesActiveInStage)
                        if (e?.Engine != null && e.IspVac > 0)
                            stage.mass_flow += e.ThrustVac / (e.IspVac * 9.80665);
                if (stage.mass_flow <= 0)
                {
                    Fail($"no fuel use for stage {s.Stage}");
                    return false;
                }
                stages.Add(stage);
            }
            return true;
        }

        static bool CanUseFuel(DeltaVStageInfo s)
        {
            if (s.StageBurnTime > 0)
                return true;
            if (s.EnginesActiveInStage != null)
                foreach (var e in s.EnginesActiveInStage)
                    if (e?.Engine != null && e.IspVac > 0)
                        return true;
            return false;
        }

        // Runs up to `budget` test flights; done (and found, if anything worked) after the last one.
        public void Continue(int budget)
        {
            int total = Kicks.Length * Holds.Length;
            while (!done && budget-- > 0)
            {
                if (next_candidate >= total)
                {
                    done = true;
                    found = !double.IsPositiveInfinity(best_score);
                    if (!found)
                        failure = "every choice failed in the prediction";
                    break;
                }
                float kick = Kicks[next_candidate / Holds.Length];
                float hold = Holds[next_candidate % Holds.Length];
                next_candidate++;

                var nominal = Fly(kick, hold, 1);
                if (!nominal.ok || nominal.max_q_kpa > template.max_q_kpa * MaxQMargin)
                    continue;
                double score = nominal.delta_v;
                if (start.drag_area > 0)
                {
                    var pessimistic = Fly(kick, hold, PessimisticDrag);
                    if (!pessimistic.ok)
                        continue;
                    score = (nominal.delta_v + pessimistic.delta_v) / 2;
                }
                if (score < best_score)
                {
                    best_score = score;
                    best_kick_deg = kick;
                    best_hold_s = hold;
                    best_delta_v = nominal.delta_v;
                }
            }
        }

        // One test flight from the start state. Coordinates: x along "up" at the start, y downrange.
        public Result Fly(float kick_deg, float hold_s, double drag_factor, StringBuilder trace = null)
        {
            double next_trace_s = 0;
            var control = template.Copy();
            control.kick_deg = kick_deg;
            control.hold_time_s = hold_s;
            control.Reset();

            double x = start.radius, y = 0;
            double vx = start.vertical_speed;
            double vy = start.horizontal_speed + start.rotation_speed;
            // The body's spin, as seen in this plane: what the air moves at, at each point.
            double omega = start.rotation_speed / start.radius;
            double mass = start.mass;
            int stage_index = 0;
            double attitude = 90;
            double delta_v = 0;
            double last_ap_km = double.NaN;
            float taper_rate = 0;
            float max_q = 0;
            double drag_area = start.drag_area * drag_factor;

            for (double t = 0; t < MaxTimeS; t += StepS)
            {
                double r = Math.Sqrt(x * x + y * y);
                double ux = x / r, uy = y / r;      // up
                double hx = -uy, hy = ux;           // horizontal, downrange
                double altitude = r - body_radius;
                if (altitude < -10)
                    return default;

                double air_x = vx + omega * y, air_y = vy - omega * x;
                double air_speed = Math.Sqrt(air_x * air_x + air_y * air_y);
                double air_up = air_x * ux + air_y * uy;
                double air_h = air_x * hx + air_y * hy;
                double prograde = Math.Abs(air_h) < 1e-3
                    ? (air_up >= 0 ? 90 : -90)
                    : Math.Atan2(air_up, air_h) * 180 / Math.PI;

                double rho = Table(density, altitude);
                float q = (float)(0.5 * rho * air_speed * air_speed / 1000);
                if (q > max_q)
                    max_q = q;

                var r_vec = new Vector3d(x, y, 0);
                var v_vec = new Vector3d(vx, vy, 0);
                if (!Apsides(r_vec, v_vec, out double ap_radius, out double pe_radius))
                    return default;
                double ap_km = (ap_radius - body_radius) / 1000;
                double remaining_km = target_km - ap_km;
                if (remaining_km <= end_error_km)
                    return new Result { ok = true, delta_v = delta_v + Circularize(ap_radius, pe_radius), max_q_kpa = max_q };

                double time_to_ap = GravityTurnControl.SignedTimeToApoapsis(r_vec, v_vec, mu);

                // Next stage once this one has burnt its fuel.
                while (stage_index < stages.Count && mass <= stages[stage_index].end_mass)
                {
                    double next = stages[stage_index].next_start_mass;
                    stage_index++;
                    if (stage_index < stages.Count && !double.IsNaN(next))
                        mass = Math.Min(mass, next);
                }
                bool has_stage = stage_index < stages.Count;
                double liquid = has_stage ? LiquidThrust(stages[stage_index], altitude) : 0;
                double locked = has_stage ? TotalThrust(stages[stage_index], altitude) - liquid : 0;

                double g = mu / (r * r);
                double v_h = vx * hx + vy * hy;
                double throttle_prev = control.throttle;
                if (!double.IsNaN(last_ap_km) && throttle_prev > 0.1)
                    taper_rate += 0.5f * ((float)((ap_km - last_ap_km) / StepS / throttle_prev) - taper_rate);
                last_ap_km = ap_km;

                var inputs = new GravityTurnControl.Inputs
                {
                    vertical_speed = (float)air_up,
                    prograde_elevation_deg = (float)prograde,
                    orbital_prograde_elevation_deg = (float)(Math.Atan2(vx * ux + vy * uy, v_h) * 180 / Math.PI),
                    atmosphere_fraction = atmosphere_depth > 0 ? (float)(altitude / atmosphere_depth) : 2,
                    time_to_apoapsis_s = (float)time_to_ap,
                    gravity = (float)g,
                    effective_gravity = (float)Math.Max(g - v_h * v_h / r, 0.05 * g),
                    max_accel = (float)((liquid + locked) / mass),
                    dynamic_pressure_kpa = q,
                    remaining_ap_km = (float)remaining_km,
                    end_error_km = end_error_km,
                    taper_rate = taper_rate,
                };
                control.Update(inputs, (float)StepS);

                if (trace != null && t >= next_trace_s)
                {
                    next_trace_s += 10;
                    trace.Append($" | +{t:0}s {altitude / 1000:n1}km {air_speed:n0}m/s pro {prograde:n0}° thr {control.throttle:n2} ap {ap_km:n1}km st{stage_index}");
                }

                double target = control.target_elevation_deg;
                attitude += Math.Max(-TurnRateDegS * StepS, Math.Min(TurnRateDegS * StepS, target - attitude));
                double pitch = attitude * Math.PI / 180;
                double dir_x = Math.Cos(pitch) * hx + Math.Sin(pitch) * ux;
                double dir_y = Math.Cos(pitch) * hy + Math.Sin(pitch) * uy;

                double thrust = liquid * control.throttle + locked;
                double drag = air_speed > 0 ? drag_area * rho * air_speed / mass : 0;
                double ax = thrust / mass * dir_x - g * ux - drag * air_x;
                double ay = thrust / mass * dir_y - g * uy - drag * air_y;
                vx += ax * StepS;
                vy += ay * StepS;
                x += vx * StepS;
                y += vy * StepS;
                delta_v += thrust / mass * StepS;

                if (has_stage && liquid + locked > 0)
                    mass -= stages[stage_index].mass_flow * thrust / (liquid + locked) * StepS;
            }
            return default;
        }

        // Between sea level and vacuum by air pressure.
        double LiquidThrust(in Stage s, double altitude)
        {
            double p = Table(pressure_ratio, altitude);
            return thrust_scale * (s.liquid_vac + (s.liquid_asl - s.liquid_vac) * p);
        }

        double TotalThrust(in Stage s, double altitude)
        {
            double p = Table(pressure_ratio, altitude);
            double locked = s.locked_vac + (s.locked_asl - s.locked_vac) * p;
            return LiquidThrust(s, altitude) + thrust_scale * locked;
        }

        double Table(double[] table, double altitude)
        {
            if (altitude <= 0)
                return table[0];
            double index = altitude / TableStepM;
            int i0 = (int)index;
            if (i0 >= table.Length - 1)
                return 0;
            return table[i0] + (table[i0 + 1] - table[i0]) * (index - i0);
        }

        bool Apsides(Vector3d r, Vector3d v, out double ap, out double pe)
        {
            double r_mag = r.magnitude;
            double v2 = v.sqrMagnitude;
            double inv_a = 2 / r_mag - v2 / mu;
            ap = pe = 0;
            if (inv_a <= 0)
                return false;
            double a = 1 / inv_a;
            double e = (((v2 - mu / r_mag) * r - Vector3d.Dot(r, v) * v) / mu).magnitude;
            ap = a * (1 + e);
            pe = a * (1 - e);
            return true;
        }

        // Speed still to add at apoapsis to make the orbit circular there.
        double Circularize(double ap_radius, double pe_radius)
        {
            double a = (ap_radius + pe_radius) / 2;
            double speed_at_ap = Math.Sqrt(mu * (2 / ap_radius - 1 / a));
            return Math.Max(0, Math.Sqrt(mu / ap_radius) - speed_at_ap);
        }
    }
}
