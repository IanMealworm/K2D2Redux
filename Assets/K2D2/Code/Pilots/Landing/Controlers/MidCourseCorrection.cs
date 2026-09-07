using KSP.Sim;
using KSP.Sim.Maneuver;
using KSP.Sim.impl;
using KTools;
using UnityEngine.UIElements;
using K2D2.UI;
using K2D2.Controller;
using K2D2.KSPService;
using K2D2.Node;
using ILogger = ReduxLib.Logging.ILogger;

namespace K2D2.Landing
{
    // Precision landing's second phase, run right after DeorbitBurn finishes and before the
    // normal Pause -> QuickWarp -> RotationWarp -> Waiting -> Brake -> TouchDown sequence starts.
    //
    // Per Reese: the deorbit burn's own targeting has to commit to a burn based on a PREDICTION
    // (where FindBestDeorbitBurn's search thinks the resulting trajectory will land), and in
    // testing that prediction was still landing 27-28km off target even with plane trim maxed
    // out - which meant the descent-phase steering (TouchDown.ComputeSteeredDirection) was doing
    // all the real work, and doing it late, close to the ground, where there's the least room
    // (time, altitude, fuel budget) left to fix things. Reese's ask: do the heavy correcting soon
    // after the deorbit burn instead, while there's still plenty of altitude and time left, so
    // the descent only has to mop up whatever small residual is left.
    //
    // This phase re-runs the EXACT SAME search DeorbitBurn already uses (LandingTargeting.
    // FindBestDeorbitBurn) - not new math - but against the REAL, already-flown post-deorbit-burn
    // orbit instead of a pre-burn prediction, and over a short, near-term window instead of a
    // full orbit. Since periapsis is already set close to the target depth by the deorbit burn,
    // ComputeDeorbitDeltaV's prograde/retrograde component for these candidates should come out
    // small - this is mostly the plane-trim component doing real work a second time, now against
    // whatever the deorbit burn actually achieved (including its own execution error) rather than
    // a prediction of it. If the deorbit burn already nailed it, this phase finds nothing useful
    // to do and just falls straight through to Pause (see the "nothing meaningful to correct"
    // branch in Start() below) - it should never make things worse, only sometimes do nothing.
    //
    // NOT a substitute for starting Brake earlier - checkDirection() in TouchDown.cs deliberately
    // holds the engine off (SetThrottle(0), no steering at all) until the vessel's actual velocity
    // is already predominantly downward (see its "Waiting for speed Down" branch), specifically to
    // avoid thrusting into a climb on a low-gravity body (the Minmus overshoot bug this same
    // session already fixed once). Right after a deorbit burn the vessel is still mostly
    // horizontal/outbound for a good stretch, so entering Brake early would just idle doing
    // nothing until the same natural falling point Brake already starts near today - it wouldn't
    // actually buy any extra correction time. A real, separate, deliberately-sized burn (this
    // phase) is what actually spends dV early instead of late.
    //
    // Mirrors DeorbitBurn.cs's own structure and its reasons almost exactly (own Turn/Warp/Burn
    // instances rather than NodeExPilot, for the same K2D2_Plugin.ResetControllers() reason - see
    // DeorbitBurn's comment) - new and untested in-game, same caveat.
    public class MidCourseCorrection : ExecuteController
    {
        public ILogger logger = ReduxLib.ReduxLib.GetLogger("K2D2.MidCourseCorrection");

        public enum Mode
        {
            Turn,
            Warp,
            Burn
        }

        public Mode mode = Mode.Turn;

        TurnTo turn = new TurnTo();
        WarpTo warp = new WarpTo();
        BurnManeuver burn = new BurnManeuver();
        ManeuverCreator maneuver_creator = new ManeuverCreator();

        public SingleExecuteController current_executor = new SingleExecuteController();

        ManeuverNodeData node;

        public MidCourseCorrection()
        {
            sub_contollers.Add(current_executor);
        }

        // Same depth-below-terrain target as DeorbitBurn - see its own comment. Kept identical so
        // this correction is "true up the SAME target periapsis, now against real post-burn
        // state" rather than something that could disagree with what the deorbit burn was aiming
        // for in the first place.
        const double periapsis_safety_margin = 2000;

        // How much of the remaining coast (from right now to wherever the CURRENT, uncorrected
        // trajectory would land) this phase's own search is allowed to consider for its burn
        // time. Deliberately small - this only exists to fire the correction SOON after the
        // deorbit burn, not to re-run a full deorbit-style search across the whole remaining
        // flight (TouchDown's own steering already owns the close-in part of the descent).
        const double searchWindowFraction = 0.25;
        // Always leave at least this much real time in the window, even on a short hop, so a
        // very-soon impact prediction can't collapse the search down to a handful of seconds.
        const double minSearchWindowSeconds = 60;

        // Below this, treat the found correction as "nothing meaningful to do" and skip flying a
        // node entirely - a burn this small isn't worth turning the vessel for, and the deorbit
        // burn evidently already got this close on its own.
        const double negligibleDeltaV = 0.05;

        public override void Start()
        {
            finished = false;
            node = null;

            var current_vessel = K2D2_Plugin.Instance.current_vessel;
            if (current_vessel == null)
            {
                finished = true;
                return;
            }

            IKeplerPatch orbit = current_vessel.VesselComponent.Orbit;
            var body = orbit.referenceBody;

            double now = GeneralTools.Current_UT;
            Vector3d r_now = orbit.GetRelativePositionAtUTZup(now);
            Vector3d v_now = orbit.GetOrbitalVelocityAtUTZup(now);

            double target_latitude = LandingPilot.Instance.settings.target_latitude.V;
            double target_longitude = LandingPilot.Instance.settings.target_longitude.V;
            double target_periapsis_radius = body.radius - periapsis_safety_margin;
            double max_plane_trim_dv = LandingPilot.Instance.settings.max_plane_trim_dv.V;

            // Where would we land with NO further correction? Only used to size the search
            // window below (see searchWindowFraction) - if this fails to find a crossing at all
            // (shouldn't normally happen right after a real deorbit burn), fall back to a fixed,
            // conservative window instead of guessing something unbounded.
            bool stillOnCourse = LandingTargeting.PredictImpactLongitude(r_now, v_now, body, now,
                out _, out _, out double uncorrectedImpactUT);
            double timeToImpact = stillOnCourse ? uncorrectedImpactUT - now : 600;

            double search_start = now + 30;
            double search_duration = System.Math.Max(minSearchWindowSeconds, timeToImpact * searchWindowFraction);

            bool found = LandingTargeting.FindBestDeorbitBurn(orbit, body, search_start, search_duration,
                target_periapsis_radius, target_latitude, target_longitude, max_plane_trim_dv,
                out double burn_UT, out double deltaV, out double normalDeltaV,
                out double predicted_lon, out double predicted_lat, out double predicted_error_m,
                out double predicted_impact_UT);

            if (!found)
            {
                status_line = "Mid-course correction: no valid window found - skipping.";
                logger.LogInfo("[MidCourseCorrection] search found no valid candidate - skipping, falling through unchanged.");
                finished = true;
                return;
            }

            logger.LogInfo($"[MidCourseCorrection] now={now:n1} search_start={search_start:n1} " +
                $"search_duration={search_duration:n1}s (uncorrected time-to-impact={timeToImpact:n1}s) | " +
                $"burn_UT={burn_UT:n1} (T+{burn_UT - now:n1}s) deltaV={deltaV:n2}m/s " +
                $"normalDeltaV={normalDeltaV:n2}m/s (max_plane_trim_dv={max_plane_trim_dv:n2}m/s) | " +
                $"target=({target_latitude:n3}, {target_longitude:n3}) predicted=({predicted_lat:n3}, {predicted_lon:n3}) " +
                $"predicted_error={predicted_error_m:n1}m");

            if (System.Math.Abs(deltaV) < negligibleDeltaV && System.Math.Abs(normalDeltaV) < negligibleDeltaV)
            {
                status_line = "Mid-course correction: deorbit burn already on target, nothing to do.";
                logger.LogInfo("[MidCourseCorrection] found correction is negligible - skipping the burn, falling through unchanged.");
                finished = true;
                return;
            }

            maneuver_creator.Update();
            // Same reasoning as DeorbitBurn.Start() - clear any lingering node (there shouldn't be
            // one at this point, since DeorbitBurn already cleans up its own, but this is cheap
            // insurance against a second node ever getting created alongside a stale first one)
            // before adding ours.
            status_line = normalDeltaV != 0
                ? $"Mid-course correction planned: {deltaV:n1} m/s (+ {normalDeltaV:n1} m/s plane trim)"
                : $"Mid-course correction planned: {deltaV:n1} m/s";
            maneuver_creator.RemoveAllNodesThenCreate(burn_UT, deltaV, created_node =>
            {
                node = created_node;
                mode = Mode.Turn;
                current_executor.setController(turn);
                turn.StartManeuver(node);
            }, normalDeltaV);
        }

        public override void Update()
        {
            // Start() already decided there was nothing worth flying (not found, or negligible)
            // and set finished = true itself in that case - nothing else to do here either way.
            if (node == null)
                return;

            base.Update();

            if (!current_executor.finished)
                return;

            switch (mode)
            {
                case Mode.Turn:
                    mode = Mode.Warp;
                    current_executor.setController(warp);
                    warp.StartManeuver(node);
                    break;
                case Mode.Warp:
                    mode = Mode.Burn;
                    current_executor.setController(burn);
                    burn.StartManeuver(node);
                    break;
                case Mode.Burn:
                    maneuver_creator.RemoveAllNodes();
                    status_line = "Mid-course correction complete";
                    finished = true;
                    break;
            }
        }
    }
}
