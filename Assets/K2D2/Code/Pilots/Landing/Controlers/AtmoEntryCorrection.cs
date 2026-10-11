using System;
using KSP.Sim;
using KSP.Sim.impl;
using KTools;

namespace K2D2.Landing
{
    // Correction burns between the deorbit burn and the atmosphere, for atmospheric precision
    // landing - the part of the deorbit planning that needs drag.
    //
    // Each round: measure where Redux has the vessel landing, holding attitudes set against the
    // airflow at the entry point (see AtmoAttitudeHold.HoldForEntry and the measuring order below),
    // and how far the middle of the entry guidance's steering range is from the target. If it's
    // too far, a burn is planned from the airless coast
    // (LandingTargeting.BurnSensitivity) and then adjusted against Redux's Planned prediction,
    // which includes drag, before it's flown (TargetedBurnController's refinement). Then measure
    // again; stop once it's close enough for the entry guidance to handle.
    public class AtmoEntryCorrection : TargetedBurnController
    {
        public AtmoEntryCorrection() : base("K2D2.AtmoEntryCorrection") { }

        protected override string label => "Entry correction";

        // The entry guidance moves the landing point tens of km either way, so a miss this small
        // is left to it.
        const double acceptable_miss_m = 2000;
        const int max_burns = 3;
        // A correction has to at least bring the miss down to this fraction of what it was, or the
        // rest is left to the entry guidance rather than burning more fuel on something that
        // isn't working.
        const double required_improvement = 0.7;
        const double lead_time_s = 90;
        // A correction burn has to be over at least this long before the atmosphere. Steep entries
        // come down soon after the deorbit burn, sometimes too soon for any correction.
        const double min_margin_before_atmosphere_s = 30;
        bool too_late;
        // BurnManeuver stops once less than its 0.1 m/s tolerance is left.
        const double smallest_burn_mps = 0.3;

        readonly AtmoAttitudeHold hold = new AtmoAttitudeHold();
        readonly ReduxReading reading = new ReduxReading();
        int burns;
        double previous_miss_m;

        // The landing point is measured three ways each round, in this order:
        //  0: ballistic - retrograde at the entry point, no lift. What Redux's Planned prediction
        //     is read with while a burn is adjusted (TargetedBurnController's refinement).
        //  1, 2: at the entry angle of attack with the lift turned fully to one side, then the
        //     other. Halfway between those two is the middle of where the entry guidance can steer
        //     to - so that's what's aimed at the target, leaving it the same room to correct either
        //     way. (Aiming the ballistic point instead left the third test flight's guidance
        //     starting 43 km short, using most of its range just to get back.)
        int measuring;
        readonly ReduxImpact[] readings = new ReduxImpact[3];
        static readonly double[] measure_bank_deg = { 0, 90, -90 };

        // Ground offsets (m, along the target site's a and b axes) from the target of the middle of
        // the steering range, and of that middle from the ballistic point.
        bool measured_ok;
        double mid_a, mid_b, offset_a, offset_b;

        protected override bool refine_with_redux => true;

        // The burn response is measured where the deorbit burn aimed (see AtmoDeorbitBurn).
        private protected override LandingTargeting.TargetSite MakeSite(ImpactModel model, LandingSettings settings) =>
            AtmoDeorbitBurn.AimSite(model, settings);

        // The refinement reads the ballistic point; the middle of the steering range is that much
        // further on, and that's what goes on the target. (The sixth test flight had this the wrong
        // way round: each correction put the ballistic point where the middle should be, moving
        // the middle away by twice the offset, and the burns grew round after round.)
        private protected override void RefineAimOffset(out double a, out double b)
        {
            a = offset_a;
            b = offset_b;
        }

        public override void Start()
        {
            burns = 0;
            too_late = false;
            previous_miss_m = double.NaN;
            measuring = 0;
            reading.Reset();
            base.Start();
        }

        protected override bool ReadyToPlan()
        {
            var vessel = K2D2_Plugin.Instance.current_vessel?.VesselComponent;
            if (vessel == null)
                return false;

            too_late = SecondsToAtmosphere(vessel) < lead_time_s + min_margin_before_atmosphere_s;
            if (too_late)
            {
                measuring = 0;
                reading.Reset();
                hold.Release();
                return true;
            }

            if (measuring == 0)
                hold.HoldBallistic();
            else
                hold.HoldForEntry(LandingPilot.Instance.settings.entry_angle_of_attack.V, measure_bank_deg[measuring]);
            status_line = $"{label}: measuring the landing point ({measuring + 1}/3, attitude error {hold.attitude_error_deg:n1}°)";
            if (!reading.Update(ReduxImpactPrediction.GetFlown(vessel), hold.attitude_error_deg, vessel.mainBody.radius))
                return false;

            readings[measuring] = reading.result;
            reading.Reset();
            measuring++;
            if (measuring < readings.Length)
                return false;

            measuring = 0;
            hold.Release();
            Summarize(vessel);
            return true;
        }

        static double SecondsToAtmosphere(VesselComponent vessel)
        {
            double now = GeneralTools.Current_UT;
            var model = new ImpactModel(vessel.mainBody, now);
            model.StateAt(vessel.Orbit, now, out Vector3d r, out Vector3d v);
            double entry_radius = vessel.mainBody.radius + vessel.mainBody.atmosphereDepth;
            if (r.magnitude <= entry_radius)
                return 0;
            return model.Impact(r, v, now, entry_radius, out double entry_UT, out _) ? entry_UT - now : double.PositiveInfinity;
        }

        void Summarize(VesselComponent vessel)
        {
            var settings = LandingPilot.Instance.settings;
            var model = new ImpactModel(vessel.mainBody, GeneralTools.Current_UT);
            var ground = new LandingTargeting.TargetSite(model, settings.target_latitude.V, settings.target_longitude.V, 0);
            var a = new double[3];
            var b = new double[3];
            for (int i = 0; i < readings.Length; i++)
            {
                if (!readings[i].usable)
                    continue;
                ground.Offset(model.SurfaceUp(readings[i].latitude, readings[i].longitude) * ground.radius, out a[i], out b[i]);
                logger.LogInfo($"[{label}] {(i == 0 ? "ballistic" : $"angle of attack {settings.entry_angle_of_attack.V:n0}°, tilted {measure_bank_deg[i]:n0}°")}: " +
                    $"Redux lands ({a[i]:n0}, {b[i]:n0})m from the target{(readings[i].approximate ? " (approximate)" : "")}");
            }

            measured_ok = readings[0].usable;
            offset_a = offset_b = 0;
            mid_a = a[0];
            mid_b = b[0];
            if (measured_ok && readings[1].usable && readings[2].usable)
            {
                mid_a = 0.5 * (a[1] + a[2]);
                mid_b = 0.5 * (b[1] + b[2]);
                offset_a = mid_a - a[0];
                offset_b = mid_b - b[0];
            }
            logger.LogInfo($"[{label}] middle of the steering range: ({mid_a:n0}, {mid_b:n0})m from the target, " +
                $"({offset_a:n0}, {offset_b:n0})m from the ballistic point");
        }

        private protected override bool Plan(ImpactModel model, IKeplerPatch orbit, LandingTargeting.TargetSite site,
            double max_normal, out LandingTargeting.TargetedBurn planned)
        {
            planned = default;
            if (too_late)
            {
                status_line = $"{label}: too close to the atmosphere for a correction - leaving it to the entry guidance.";
                logger.LogInfo($"[{label}] less than {lead_time_s + min_margin_before_atmosphere_s:n0}s to the atmosphere - no correction");
                return false;
            }
            if (!measured_ok)
            {
                status_line = $"{label}: no prediction from Redux, leaving the landing point to the entry guidance.";
                logger.LogInfo($"[{label}] Redux had no usable prediction - skipping");
                return false;
            }

            double a = mid_a, b = mid_b;
            double miss = Math.Sqrt(a * a + b * b);

            if (miss <= acceptable_miss_m)
            {
                status_line = $"{label}: on target, no burn needed.";
                return false;
            }
            if (!double.IsNaN(previous_miss_m) && miss > required_improvement * previous_miss_m)
            {
                status_line = $"{label}: the last correction didn't help - leaving the rest to the entry guidance.";
                logger.LogInfo($"[{label}] the miss went from {previous_miss_m:n0}m to {miss:n0}m after the last correction - stopping corrections");
                return false;
            }
            previous_miss_m = miss;

            double UT = GeneralTools.Current_UT + lead_time_s;
            if (!LandingTargeting.BurnSensitivity(model, orbit, site, UT,
                    out double da_p, out double db_p, out double da_n, out double db_n))
            {
                logger.LogInfo($"[{label}] couldn't work out how a burn moves the landing point - skipping");
                return false;
            }

            LandingTargeting.SolveCorrection(a, b, da_p, db_p, da_n, db_n, max_normal, out double prograde, out double normal);
            if (Math.Sqrt(prograde * prograde + normal * normal) < smallest_burn_mps)
            {
                logger.LogInfo($"[{label}] correction of {Math.Sqrt(prograde * prograde + normal * normal):n2}m/s is too small to fly - skipping");
                return false;
            }

            planned = LandingTargeting.Burn(model, orbit, site, UT, prograde, normal);
            return planned.found;
        }

        protected override bool BurnComplete() => ++burns >= max_burns;

        // Hands the attitude back to normal SAS - for when the landing is stopped mid-wait.
        public override void Release()
        {
            hold.Release();
            base.Release();
        }
    }
}
