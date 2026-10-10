using System;
using KSP.Sim;
using KSP.Sim.impl;
using KTools;

namespace K2D2.Landing
{
    // Small burn right after the deorbit burn, fixing how far the burn as flown missed the plan
    // (the engine never delivers exactly the planned delta-v, at exactly the planned moment). A
    // small change this early moves the landing point a long way, so it's much cheaper here than
    // during the powered descent.
    public class MidCourseCorrection : TargetedBurnController
    {
        public MidCourseCorrection() : base("K2D2.MidCourseCorrection") { }

        protected override string label => "Mid-course correction";

        const double lead_time_s = 60;

        // A miss this small is left to the powered descent.
        const double acceptable_miss_m = 250;

        const double smallest_burn_mps = 0.3;

        private protected override bool Plan(ImpactModel model, IKeplerPatch orbit, LandingTargeting.TargetSite site,
            double max_normal, out LandingTargeting.TargetedBurn planned)
        {
            double now = GeneralTools.Current_UT;
            var coast = LandingTargeting.Coast(model, orbit, site, now);
            planned = default;

            if (coast.found)
            {
                // Redux's prediction for where the same coast hits the ground, against ImpactModel's,
                // as a cross-check. Redux uses the real terrain there rather than the target's
                // height, so the two can differ by the slope of the ground.
                var redux = ReduxImpactPrediction.GetFlown(K2D2_Plugin.Instance.current_vessel.VesselComponent);
                model.StateAt(orbit, now, out Vector3d r, out Vector3d v);
                string comparison = $"Redux: {redux.state}";
                if (redux.usable && model.Impact(r, v, now, site.ground_radius, out double ground_UT, out Vector3d ground))
                {
                    model.LatLon(model.BodyFixed(ground, ground_UT), out double lat, out double lon);
                    comparison = $"ground impact ({lat:n4}, {lon:n4}) vs Redux ({redux.latitude:n4}, {redux.longitude:n4}): " +
                        $"{LandingPilot.HaversineDistanceMeters(lat, lon, redux.latitude, redux.longitude, model.body.radius):n0}m apart";
                }
                logger.LogInfo($"[{label}] coast passes {coast.miss_m:n0}m from the aim point | {comparison}");

                if (coast.miss_m <= acceptable_miss_m)
                {
                    status_line = $"{label}: already on target, no burn needed.";
                    return false;
                }
            }

            planned = LandingTargeting.PlanCorrection(model, orbit, site, now + lead_time_s, max_normal);
            if (!planned.found || (coast.found && planned.miss_m >= coast.miss_m))
            {
                status_line = $"{label}: no improvement possible, skipping.";
                logger.LogInfo($"[{label}] correction wouldn't improve the landing point - skipping");
                return false;
            }

            // BurnManeuver stops once less than its tolerance (0.1 m/s by default) is left, so a
            // burn about that size would end before doing anything.
            double delta_v = Math.Sqrt(planned.prograde * planned.prograde + planned.normal * planned.normal);
            if (delta_v < smallest_burn_mps)
            {
                status_line = $"{label}: correction too small to fly, leaving it to the powered descent.";
                logger.LogInfo($"[{label}] correction of {delta_v:n2}m/s is too small to fly accurately - skipping");
                return false;
            }
            return true;
        }
    }
}
