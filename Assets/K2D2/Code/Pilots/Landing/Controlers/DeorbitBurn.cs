using System.Reflection;
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
    // Precision landing's first phase, run before the normal Pause -> QuickWarp -> RotationWarp
    // -> Waiting -> Brake -> TouchDown sequence even starts: create and fly a real, visible
    // deorbit maneuver node timed to bring the vessel down near the player's chosen target (see
    // LandingTargeting.cs, specifically FindBestDeorbitBurn, for the math and why it works this
    // way rather than the simpler "shift node longitude" approach it replaced - the first
    // in-game test of that approach sent the ship to a 424,000km apoapsis instead of a small
    // correction). Reese specifically wants this node visible rather than a silent automatic
    // burn, both so the player can see what's about to happen and as a learning tool for people
    // who want to try it by hand later.
    //
    // Deliberately NOT reusing NodeExPilot.Instance to fly this node: NodeExPilot.isRunning's
    // start branch calls K2D2_Plugin.ResetControllers(), which resets every registered pilot
    // including this one (LandingPilot.onReset() sets isRunning = false) - driving NodeExPilot
    // from inside a Landing phase would immediately stop Landing itself. So this controller
    // carries its own Turn/Warp/Burn instances instead, mirroring NodeExPilot's internal
    // pattern without touching NodeExPilot at all.
    //
    // New and untested in-game, same as LandingTargeting.cs/KeplerPropagator.cs and the
    // ManeuverCreator method it calls - needs a real flight before being trusted. Burn timing
    // precision (how well the Turn/Warp handoff lands on the exact UT the search picked) may
    // need tuning once we see it fly.
    public class DeorbitBurn : ExecuteController
    {
        public ILogger logger = ReduxLib.ReduxLib.GetLogger("K2D2.DeorbitBurn");

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

        public DeorbitBurn()
        {
            sub_contollers.Add(current_executor);
        }

        // How far below the body's mean radius to target the new periapsis - needs to be safely
        // below terrain almost everywhere for the search below to reliably find an impact point
        // to evaluate, without being so steep the final descent becomes unreasonably fast.
        // Tunable; may need adjusting for very mountainous targets.
        const double periapsis_safety_margin = 2000;

        // Reflection-based dump of the fields KSP.Sim.impl.UniverseModel.ZupAtUT actually uses
        // internally to compute a body's rotation frame at a given UT (found via ILSpy - see
        // LandingTargeting.cs's PredictImpactLongitude comment for the full story). Reading these
        // by name via reflection instead of "body.hasInverseRotation" etc. directly, because we
        // only know these field names from a decompiler view - we don't actually know their real
        // access modifiers, and a wrong guess there is a compile error, not a runtime one. This
        // is purely diagnostic and never used for any actual math.
        static string DumpRotationFields(CelestialBodyComponent body)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var t = body.GetType();

            string Get(string name)
            {
                var f = t.GetField(name, flags);
                if (f != null) return f.GetValue(body)?.ToString() ?? "null";

                var p = t.GetProperty(name, flags);
                if (p != null) return p.GetValue(body)?.ToString() ?? "null";

                return "<not found>";
            }

            return $"hasInverseRotation={Get("hasInverseRotation")} rotPeriodRecip={Get("rotPeriodRecip")} " +
                $"initialRotation={Get("initialRotation")} RotationOffset={Get("RotationOffset")} " +
                $"directRotAngle={Get("directRotAngle")}";
        }

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
            double period = LandingTargeting.OrbitalPeriodFromStateVectors(r_now, v_now, body.gravParameter);

            double target_latitude = LandingPilot.Instance.settings.target_latitude.V;
            double target_longitude = LandingPilot.Instance.settings.target_longitude.V;
            double target_periapsis_radius = body.radius - periapsis_safety_margin;
            double max_plane_trim_dv = LandingPilot.Instance.settings.max_plane_trim_dv.V;

            double search_start = now + 30;

            // Now scores candidates against target latitude AND longitude together (real ground
            // distance), not longitude alone, and can add a small normal/antinormal trim on top
            // of the usual prograde/retrograde burn (capped by max_plane_trim_dv, the Max Plane
            // Trim slider - 0 disables it) - see FindBestDeorbitBurn's own comment for both.
            bool found = LandingTargeting.FindBestDeorbitBurn(orbit, body, search_start, period,
                target_periapsis_radius, target_latitude, target_longitude, max_plane_trim_dv,
                out double burn_UT, out double deltaV, out double normalDeltaV,
                out double predicted_lon, out double predicted_lat, out double predicted_error_m,
                out double predicted_impact_UT);

            if (!found)
            {
                status_line = "Couldn't find a safe deorbit window - try again in a moment.";
                finished = true;
                return;
            }

            // Diagnostic - kept from the debugging round that found and fixed the ~4deg/13-16km
            // landing error (see the big comment on PredictImpactLongitude in LandingTargeting.cs
            // for the root cause and fix: using the engine's own UniverseModel.ZupAtUT instead of
            // guessing the body's future orientation ourselves). Worth watching on the next few
            // flights: predicted_error_m here should now track the REAL in-game Target Error
            // readout much more closely than before - if it doesn't, that's a sign something else
            // is still off downstream (turn/warp/burn timing, the braking phase). normalDeltaV is
            // new (the plane trim) - worth specifically checking that the real in-game error gets
            // BETTER, not worse, whenever this comes out nonzero; see the sign-convention note on
            // FindBestDeorbitBurn if it doesn't.
            logger.LogInfo($"[DeorbitBurn] now={now:n1} search_start={search_start:n1} period={period:n1}s " +
                $"body={body.Name} rotationPeriod={body.rotationPeriod:n1}s | " +
                $"burn_UT={burn_UT:n1} (T+{burn_UT - now:n1}s) deltaV={deltaV:n2}m/s normalDeltaV={normalDeltaV:n2}m/s | " +
                $"target=({target_latitude:n3}, {target_longitude:n3}) predicted=({predicted_lat:n3}, {predicted_lon:n3}) " +
                $"predicted_error={predicted_error_m:n1}m | " +
                $"predicted_impact_UT={predicted_impact_UT:n1} " +
                $"(coast={predicted_impact_UT - burn_UT:n1}s, total_elapsed={predicted_impact_UT - now:n1}s)");

            // Diagnostic for the open "is ZupAtUT a no-op for this body" question - see the big
            // comment above PredictImpactLongitude in LandingTargeting.cs. Pulled via reflection
            // (not a direct body.hasInverseRotation-style read) specifically so a wrong guess
            // about these fields' actual access modifiers can't cost us another failed Unity
            // compile - we don't have ground truth on that from ILSpy's decompile view alone.
            logger.LogInfo($"[DeorbitBurn] rotation fields: {DumpRotationFields(body)}");

            maneuver_creator.Update();
            // Circularize's own node (see Circularize.cs) is still sitting on the plan at this
            // point - its burn finishing doesn't remove it. Clear it (and anything else lingering)
            // before adding ours, or CreateManeuverNodeAtUT just appends a second node and the
            // vessel can end up turning to align with the wrong one. Confirmed in-game.
            //
            // Removing and creating in the very next line (same frame) was the first thing tried -
            // the old node visibly disappeared but the new deorbit node never actually came to
            // life. RemoveAllNodesThenCreate defers the create by a fixed update instead (see its
            // own comment in ManeuverCreator.cs) - Turn/Warp/Burn setup below now happens in the
            // completion callback since node isn't available synchronously anymore; Update() below
            // already no-ops while node is still null, so there's nothing else to guard.
            status_line = normalDeltaV != 0
                ? $"Deorbit burn planned: {deltaV:n1} m/s (+ {normalDeltaV:n1} m/s plane trim)"
                : $"Deorbit burn planned: {deltaV:n1} m/s";
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
                    // Was StartManeuver(node, true) - that enables WarpTo's in-flight direction
                    // check, which is gated on a "max_angle" field that StartManeuver itself never
                    // sets (only Start_Retrograde sets it, elsewhere). Left at C#'s default 0, ANY
                    // nonzero pointing error immediately canceled the warp, which then re-engaged
                    // next frame - rapid engage/cancel spam. NodeExPilot's own proven pattern calls
                    // StartManeuver with no check_direction flag at all; matching that here.
                    warp.StartManeuver(node);
                    break;
                case Mode.Warp:
                    mode = Mode.Burn;
                    current_executor.setController(burn);
                    burn.StartManeuver(node);
                    break;
                case Mode.Burn:
                    // Clean up the node once it's actually been flown - otherwise it just sits on
                    // the plan for the rest of the descent (Circularize's own node had this same
                    // problem, which is why RemoveAllNodes exists and is already proven safe - see
                    // its own comment in ManeuverCreator.cs about GetNodes() handing back a live
                    // list, not a copy).
                    maneuver_creator.RemoveAllNodes();
                    status_line = "Deorbit burn complete";
                    finished = true;
                    break;
            }
        }
    }
}
