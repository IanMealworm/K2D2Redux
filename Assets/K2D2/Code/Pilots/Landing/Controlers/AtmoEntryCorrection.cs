using System;
using KSP.Sim;
using KSP.Sim.impl;
using KTools;

namespace K2D2.Landing
{
    // Correction burns between the deorbit burn and the atmosphere, for atmospheric precision
    // landing - the part of the deorbit planning that needs drag.
    //
    // Each round: measure where Redux has the vessel landing straight into the airflow (no lift -
    // see AtmoAttitudeHold.HoldBallistic). That's where the entry guidance starts from, and the
    // middle of where it can steer to: it tilts toward whichever way the landing point has to
    // move, up, down or to either side. If it's too far from the target, a burn is planned from
    // the airless coast (LandingTargeting.BurnSensitivity) and then adjusted against Redux's
    // Planned prediction, which includes drag, before it's flown (TargetedBurnController's
    // refinement). Then measure again; stop once it's close enough for the entry guidance to handle.
    public class AtmoEntryCorrection : TargetedBurnController
    {
        public AtmoEntryCorrection() : base("K2D2.AtmoEntryCorrection") { }

        protected override string label => "Entry correction";

        // A miss this small is left to the entry guidance. A steep entry - in the air briefly, like
        // a boost-back - gives it far less room than a shallow one, and a correction burn up here
        // is cheap.
        const double acceptable_miss_m = 500;
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

        bool measured_ok;
        // The ballistic landing point, as ground offsets (m, along the target site's a and b axes)
        // from the target.
        double miss_a, miss_b;

        protected override bool refine_with_redux => true;

        // The burn response is measured where the deorbit burn aimed (see AtmoDeorbitBurn).
        private protected override LandingTargeting.TargetSite MakeSite(ImpactModel model, LandingSettings settings) =>
            AtmoDeorbitBurn.AimSite(model, settings);

        public override void Start()
        {
            burns = 0;
            too_late = false;
            previous_miss_m = double.NaN;
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
                reading.Reset();
                hold.Release();
                return true;
            }

            hold.HoldBallistic();
            status_line = $"{label}: measuring the landing point (attitude error {hold.attitude_error_deg:n1}°)";
            if (!reading.Update(ReduxImpactPrediction.GetFlown(vessel), hold.attitude_error_deg, vessel.mainBody.radius))
                return false;

            var result = reading.result;
            reading.Reset();
            hold.Release();
            Summarize(vessel, result);
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

        void Summarize(VesselComponent vessel, ReduxImpact impact)
        {
            measured_ok = impact.usable;
            if (!measured_ok)
                return;
            var settings = LandingPilot.Instance.settings;
            var model = new ImpactModel(vessel.mainBody, GeneralTools.Current_UT);
            var ground = new LandingTargeting.TargetSite(model, settings.target_latitude.V, settings.target_longitude.V, 0);
            ground.Offset(model.SurfaceUp(impact.latitude, impact.longitude) * ground.radius, out miss_a, out miss_b);
            logger.LogInfo($"[{label}] ballistic: Redux lands ({miss_a:n0}, {miss_b:n0})m from the target" +
                (impact.approximate ? " (approximate)" : ""));
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

            double a = miss_a, b = miss_b;
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
