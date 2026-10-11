using System;
using KSP.Sim;
using KSP.Sim.impl;
using KTools;

namespace K2D2.Landing
{
    // Atmospheric precision landing's deorbit burn. Planned like the vacuum one
    // (LandingTargeting.PlanDeorbit, on the airless coast), with two differences:
    //  - The entry is steeper: periapsis goes deep enough that the vessel comes into the
    //    atmosphere at the Entry Steepness angle (or down to the target's height with that at 0).
    //    A steeper entry costs delta-v - from a low orbit a lot of it, since the vessel only falls
    //    a little way before reaching the air - but spends less time in it: the test flights lost
    //    their steering to heat soaking into side-mounted parts over minutes of shallow entry. If
    //    the vessel can't afford the angle and keep enough for landing, it's flattened to what it
    //    can.
    //  - The airless coast is aimed to pass over the target at a height where an entry like this
    //    has mostly slowed down and is dropping steeply. That's only a first guess: the node is
    //    then moved earlier or later (and its normal part adjusted) until Redux's Planned
    //    prediction, which includes drag, lands on the target. Moving the burn in time keeps the
    //    entry exactly the same, just further along the ground track, so that converges quickly.
    //  - It's flown closed-loop (AimedBurn) rather than by following the node, aiming where the
    //    refined plan's airless coast goes plus whatever Redux still had it missing by.
    //    AtmoEntryCorrection then checks the real trajectory once the burn is done.
    public class AtmoDeorbitBurn : TargetedBurnController
    {
        public AtmoDeorbitBurn() : base("K2D2.AtmoDeorbitBurn") { }

        protected override string label => "Deorbit burn";
        protected override bool refine_with_redux => true;
        protected override RefineAlong refine_along => RefineAlong.Timing;
        // A steep entry's burn is long; see AimedBurn.
        protected override bool fly_closed_loop => true;

        const double earliest_burn_s = 60;
        const int revolutions = 4;

        // Delta-v kept back for the landing burn when sizing a steep entry.
        const double landing_reserve_mps = 600;

        // Fraction of the atmosphere's depth the coast is aimed to pass over the target at.
        const double aim_fraction_of_atmosphere = 0.3;

        // Same limit as the vacuum deorbit: further than this from the target and the target is out
        // of reach of this orbit's ground track.
        const double max_reachable_fraction_of_radius = 0.1;

        internal static LandingTargeting.TargetSite AimSite(ImpactModel model, LandingSettings settings) =>
            new LandingTargeting.TargetSite(model, settings.target_latitude.V, settings.target_longitude.V,
                aim_fraction_of_atmosphere * model.body.atmosphereDepth);

        private protected override LandingTargeting.TargetSite MakeSite(ImpactModel model, LandingSettings settings) =>
            AimSite(model, settings);

        private protected override bool Plan(ImpactModel model, IKeplerPatch orbit, LandingTargeting.TargetSite site,
            double max_normal, out LandingTargeting.TargetedBurn planned)
        {
            double start = GeneralTools.Current_UT + earliest_burn_s;
            double depth = EntryPeriapsisDepth(model, orbit, site, start);
            planned = LandingTargeting.PlanDeorbit(model, orbit, site, start, revolutions, depth, max_normal);
            if (!planned.found)
            {
                status_line = "Couldn't find a deorbit burn that reaches the target.";
                logger.LogInfo($"[{label}] no deorbit burn found");
                return false;
            }

            model.StateAt(orbit, planned.UT, out Vector3d r, out Vector3d v);
            double entry_angle = LandingTargeting.EntryAngle(model, r, LandingTargeting.AfterBurn(r, v, planned.prograde, planned.normal),
                planned.UT, out double entry_speed);
            logger.LogInfo($"[{label}] enters the atmosphere {entry_angle:n1}° below the horizon at {entry_speed:n0}m/s through the air " +
                $"(periapsis {depth / 1000:n0}km below the target's ground)");

            double max_reachable = max_reachable_fraction_of_radius * model.body.radius;
            if (planned.miss_m > max_reachable)
            {
                abort_reason = $"Target is {StrTool.DistanceToString(planned.miss_m)} from anywhere this orbit can come down " +
                    $"(with up to {max_normal:n0} m/s plane trim). Change the orbit's inclination so it passes over the target, " +
                    "or raise Max Plane Trim.";
                status_line = abort_reason;
                logger.LogInfo($"[{label}] best deorbit burn still passes {planned.miss_m:n0}m from the target (limit {max_reachable:n0}m) - stopping");
                return false;
            }
            return true;
        }

        // Periapsis depth below the target's ground for the Entry Steepness setting, judged from
        // the orbit at `UT` (near enough circular that where the burn ends up doesn't change it
        // much), flattened if the vessel can't afford it.
        double EntryPeriapsisDepth(ImpactModel model, IKeplerPatch orbit, LandingTargeting.TargetSite site, double UT)
        {
            double wanted = LandingPilot.Instance.settings.entry_flight_path_angle.V;
            if (wanted <= 0)
                return 0;

            model.StateAt(orbit, UT, out Vector3d r, out Vector3d v);
            double depth = LandingTargeting.PeriapsisDepthForEntryAngle(model, site, r, v, UT, wanted, out double prograde);

            var vessel = K2D2_Plugin.Instance.current_vessel?.VesselComponent;
            double available = vessel?.VesselDeltaV?.TotalDeltaVVac ?? double.NaN;
            double affordable = available - landing_reserve_mps;
            if (!double.IsNaN(available) && available > 0 && -prograde > affordable)
            {
                // The steepest entry that leaves the landing reserve: the angle grows with the burn,
                // so search the angle.
                double lo = 0, hi = wanted;
                for (int i = 0; i < 20; i++)
                {
                    double mid = 0.5 * (lo + hi);
                    LandingTargeting.PeriapsisDepthForEntryAngle(model, site, r, v, UT, mid, out double p);
                    if (-p <= affordable) lo = mid; else hi = mid;
                }
                depth = lo > 0 ? LandingTargeting.PeriapsisDepthForEntryAngle(model, site, r, v, UT, lo, out prograde) : 0;
                logger.LogInfo($"[{label}] a {wanted:n0}° entry needs about {-LandingTargeting.ProgradeForEntryAngle(model, r, v, UT, wanted):n0}m/s " +
                    $"but only {available:n0}m/s is left ({landing_reserve_mps:n0}m/s kept for landing) - flattened to {lo:n1}°");
            }
            return Math.Max(0, depth);
        }
    }
}
