using System;
using KSP.Sim;
using KSP.Sim.impl;
using KTools;

namespace K2D2.Landing
{
    // Vacuum precision landing's first burn: from a roughly circular orbit (see Circularize), drop
    // periapsis below the surface at the moment that makes the coast come down on the target.
    // See LandingTargeting.PlanDeorbit for how the moment is chosen.
    public class DeorbitBurn : TargetedBurnController
    {
        public DeorbitBurn() : base("K2D2.DeorbitBurn") { }

        protected override string label => "Deorbit burn";

        // Time to turn toward the burn before it starts.
        const double earliest_burn_s = 60;

        // How many orbits ahead to look for a pass over the target.
        const int revolutions = 4;

        // The powered descent can fly out a sideways miss of a few km cheaply, and has managed a
        // few tens of km at a large fuel cost; more than this and the target is out of reach of
        // this orbit's ground track.
        const double max_reachable_fraction_of_radius = 0.1;

        private protected override bool Plan(ImpactModel model, IKeplerPatch orbit, LandingTargeting.TargetSite site,
            double max_normal, out LandingTargeting.TargetedBurn planned)
        {
            // Periapsis this far below the target's terrain gives a coast steep enough to clear
            // typical terrain on the way in. If it doesn't, try steeper.
            double depth = Math.Max(2000, 0.02 * model.body.radius);
            double start = GeneralTools.Current_UT + earliest_burn_s;

            planned = default;
            for (int attempt = 0; attempt < 3; attempt++, depth *= 2)
            {
                planned = LandingTargeting.PlanDeorbit(model, orbit, site, start, revolutions, depth, max_normal);
                if (!planned.found)
                    break;

                double max_reachable = max_reachable_fraction_of_radius * model.body.radius;
                if (planned.miss_m > max_reachable)
                {
                    abort_reason = $"Target is {StrTool.DistanceToString(planned.miss_m)} from anywhere this orbit can come down " +
                        $"(with up to {max_normal:n0} m/s plane trim). Change the orbit's inclination so it passes over the target, " +
                        "or raise Max Plane Trim.";
                    status_line = abort_reason;
                    logger.LogInfo($"[{label}] best deorbit burn still lands {planned.miss_m:n0}m from the target " +
                        $"(limit {max_reachable:n0}m) - stopping");
                    return false;
                }

                if (planned.clearance_m >= LandingTargeting.required_clearance_m)
                    return true;

                logger.LogInfo($"[{label}] coast with periapsis {depth:n0}m below the target passes only {planned.clearance_m:n0}m " +
                    "above terrain on the way in - trying a steeper one");
            }

            if (!planned.found)
            {
                status_line = "Couldn't find a deorbit burn that reaches the target.";
                logger.LogInfo($"[{label}] no deorbit burn found");
                return false;
            }

            logger.LogWarning($"[{label}] no coast clears the terrain by {LandingTargeting.required_clearance_m:n0}m - " +
                "using the steepest one tried");
            return true;
        }
    }
}
