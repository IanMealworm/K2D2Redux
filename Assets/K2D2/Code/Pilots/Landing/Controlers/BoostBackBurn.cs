using System;
using K2D2.Controller;
using K2D2.KSPService;
using KSP.Sim.impl;
using KTools;
using ILogger = ReduxLib.Logging.ILogger;

namespace K2D2.Landing
{
    // The boost-back burn for landing a recovered stage (or anything else on a suborbital
    // trajectory) on the precision-landing target - flown closed-loop, like the KSP 1
    // BoosterGuidance mod does it, rather than as a maneuver node.
    //
    // Several times a second, from where the vessel is right now: work out the burn that would
    // bring the airless coast down on the target, point the engines along it, and throttle by
    // how much of it is left (tapering off at the end); stop when nothing is left. Re-solving all
    // the way through takes care of what a node can't: the burn being long, the vessel turning
    // into it, thrust and mass changing, and air. (The node version planned 839 m/s for the big
    // booster, let Redux's planned-trajectory prediction trim it to 724 m/s, flew exactly that -
    // and came down 30 km short: Redux's prediction for a long burn starting inside the
    // atmosphere was badly off.)
    //
    // Solving the burn: a booster at separation is still climbing and heading away (the replays:
    // 33-38 km up, 620-660 m/s downrange and 480-590 m/s up). Straight back along the ground
    // track isn't the cheapest - some of the burn can go up or down - so burn directions from a
    // little below the horizon to steeply up are each sized to land on the target (with whatever
    // sideways part that needs), and the smallest is kept. Once little is left, the burn is
    // solved directly instead: the smallest change that cancels the miss, from how the landing
    // point moves with each direction of burn.
    public class BoostBackBurn : ExecuteController
    {
        readonly ILogger logger = ReduxLib.ReduxLib.GetLogger("K2D2.BoostBackBurn");

        // Set when the boost-back can't be done; LandingPilot then stops and shows it.
        public string abort_reason { get; private set; }

        enum Step { WaitForEngines, Turn, Burn }
        Step step;

        // Delta-v that has to be left for the landing burn.
        const double landing_reserve_mps = 600;
        // Right after a replay loads, the game reports no delta-v or thrust for a second or two.
        const double wait_for_engines_s = 10;
        // The burn is re-solved this often.
        const double solve_interval_s = 0.2;
        // Unpowered flip: engines on once pointing within this of the burn, off again beyond the
        // second.
        const double ignite_within_deg = 5, pause_beyond_deg = 15;
        // Powered flip: the throttle while pointing more than 90° off the burn - enough for the
        // engine gimbals to help the turn. (The big booster took 48 s to turn unpowered, and the
        // burn grew from 437 to 547 m/s while it coasted away.)
        const double flip_throttle = 0.25;
        // Throttle tapers off over about the last this-many seconds of full thrust.
        const double taper_s = 2;
        const double min_throttle = 0.02;
        // Done when less than this is left - or, like BoosterGuidance, once the last few m/s stop
        // shrinking: inside the air, drag keeps moving the airless landing point, and the first
        // closed-loop flights spent 50-75 s at minimum throttle chasing the last 10 m/s.
        const double done_mps = 0.5;
        const double stall_below_mps = 10, stall_progress_mps = 0.5, stall_s = 3;

        // Burn directions tried, in degrees above the horizon, back along the starting ground track
        // (negative sizes point forward, for a target ahead). Later solves only look near the last
        // answer.
        const double min_pitch_deg = -20, max_pitch_deg = 80, pitch_step_deg = 5, pitch_window_deg = 10;
        const int solve_iterations = 20;
        const double solved_m = 30;
        const double max_burn_mps = 4000;
        // Below this much left, the burn is solved directly (see the class comment).
        const double direct_solve_below_mps = 50;

        readonly AtmoAttitudeHold hold = new AtmoAttitudeHold();
        readonly KSPVessel current_vessel;

        double wait_start_UT, last_solve_UT, turn_start_UT;
        Vector3d back;          // the starting ground track, backwards - fixed for the whole burn
        bool have_back;
        double best_pitch;
        bool have_pitch;
        Vector3d burn;          // what's left to burn, in ImpactModel's axes
        double first_burn_mps;
        double best_left_mps, best_left_UT;

        public BoostBackBurn()
        {
            current_vessel = K2D2_Plugin.Instance.current_vessel;
        }

        public override void Start()
        {
            finished = false;
            abort_reason = null;
            step = Step.WaitForEngines;
            wait_start_UT = GeneralTools.Current_UT;
            last_solve_UT = double.NaN;
            have_back = have_pitch = false;
            burn = Vector3d.zero;
            first_burn_mps = double.NaN;
            best_left_mps = double.PositiveInfinity;
            best_left_UT = GeneralTools.Current_UT;
        }

        public void Release()
        {
            hold.Release();
            current_vessel?.SetThrottle(0);
        }

        public override void Update()
        {
            if (finished)
                return;
            var vessel = current_vessel?.VesselComponent;
            if (vessel == null)
                return;
            double now = GeneralTools.Current_UT;

            if (step == Step.WaitForEngines)
            {
                bool engines_known = (vessel.VesselDeltaV?.TotalDeltaVVac ?? 0) > 0 && LandingPilot.Instance.burn_dV.full_dv > 0;
                status_line = "Boost-back: waiting for the engines' figures";
                if (!engines_known && now - wait_start_UT < wait_for_engines_s)
                    return;
                step = Step.Turn;
                turn_start_UT = now;
            }

            if (double.IsNaN(last_solve_UT) || now - last_solve_UT >= solve_interval_s)
            {
                last_solve_UT = now;
                if (!SolveFromHere(vessel, now))
                    return;
            }

            double left = burn.magnitude;
            if (left < best_left_mps - stall_progress_mps)
            {
                best_left_mps = left;
                best_left_UT = now;
            }
            bool stalled = step == Step.Burn && left < stall_below_mps && now - best_left_UT > stall_s;
            if (left < done_mps || stalled)
            {
                Finish(vessel, now);
                return;
            }

            hold.HoldDirection(burn);
            double max_accel = LandingPilot.Instance.burn_dV.full_dv;
            double error = hold.attitude_error_deg;
            bool powered = LandingPilot.Instance.settings.boost_back_flip.V == FlipStyle.Powered;
            if (step == Step.Turn)
            {
                if (error < ignite_within_deg)
                {
                    step = Step.Burn;
                    logger.LogInfo($"[Boost-back] lined up after {now - turn_start_UT:n0}s ({(powered ? "powered" : "unpowered")} flip), " +
                        $"{left:n0}m/s to burn at {vessel.AltitudeFromSeaLevel:n0}m");
                }
                else if (!powered)
                {
                    current_vessel.SetThrottle(0);
                    status_line = $"Boost-back: turning for a {left:n0} m/s burn ({error:n0}° to go)";
                    return;
                }
            }

            // Powered flip: the engines run from the start, so their gimbals help turn the vessel;
            // thrust while still pointing the wrong way is kept low, and grows as the nose comes
            // round. The re-solving takes care of whatever it pushes the wrong way.
            double pointing = powered
                ? flip_throttle + (1 - flip_throttle) * Math.Max(0, Math.Cos(error * Math.PI / 180))
                : error > pause_beyond_deg ? 0 : 1;
            double taper = max_accel > 0 ? Math.Min(1, left / (max_accel * taper_s)) : 0;
            current_vessel.SetThrottle((float)(pointing > 0 ? Math.Max(min_throttle, pointing * taper) : 0));
            status_line = step == Step.Turn
                ? $"Boost-back: powered flip, {error:n0}° to go, {left:n0} m/s to burn"
                : $"Boost-back: {left:n0} m/s to go";
        }

        // Re-solves the burn from the current state. False (and finished/abort set) when there's
        // nothing to fly.
        bool SolveFromHere(VesselComponent vessel, double now)
        {
            var settings = LandingPilot.Instance.settings;
            var model = new ImpactModel(vessel.mainBody, now);
            var site = new LandingTargeting.TargetSite(model, settings.target_latitude.V, settings.target_longitude.V, 0);
            model.StateAt(vessel.Orbit, now, out Vector3d r, out Vector3d v);

            if (!have_back)
            {
                Vector3d up0 = r.normalized;
                Vector3d horizontal0 = v - up0 * Vector3d.Dot(v, up0);
                if (horizontal0.magnitude < 1)
                {
                    Abort("Boost-back needs the vessel to be moving over the ground.");
                    return false;
                }
                back = -horizontal0.normalized;
                have_back = true;
            }

            bool solved = burn.magnitude > 0 && burn.magnitude < direct_solve_below_mps
                ? SolveDirect(model, site, r, v, now, out Vector3d dv)
                : SolveByDirection(model, site, r, v, now, out dv);
            if (!solved)
            {
                if (step == Step.Burn)
                {
                    // Lost the solution partway (very close to done, say) - stop rather than guess.
                    Finish(vessel, now);
                    return false;
                }
                Abort("Couldn't find a boost-back burn that reaches the target.");
                return false;
            }
            burn = dv;

            if (double.IsNaN(first_burn_mps))
            {
                first_burn_mps = dv.magnitude;
                double available = vessel.VesselDeltaV?.TotalDeltaVVac ?? 0;
                logger.LogInfo($"[Boost-back] needs {first_burn_mps:n0}m/s pointing {best_pitch:n0}° above the horizon " +
                    $"| {available:n0}m/s available, {landing_reserve_mps:n0}m/s kept for landing");
                if (available > 0 && first_burn_mps + landing_reserve_mps > available)
                {
                    Abort($"The boost-back needs {first_burn_mps:n0} m/s and landing about {landing_reserve_mps:n0} m/s more, " +
                        $"but only {available:n0} m/s is left.");
                    return false;
                }
            }
            return true;
        }

        void Abort(string reason)
        {
            abort_reason = reason;
            status_line = reason;
            logger.LogInfo($"[Boost-back] {reason}");
            Release();
            finished = true;
        }

        void Finish(VesselComponent vessel, double now)
        {
            current_vessel.SetThrottle(0);
            hold.Release();
            var settings = LandingPilot.Instance.settings;
            var model = new ImpactModel(vessel.mainBody, now);
            var site = new LandingTargeting.TargetSite(model, settings.target_latitude.V, settings.target_longitude.V, 0);
            model.StateAt(vessel.Orbit, now, out Vector3d r, out Vector3d v);
            string airless = LandingTargeting.Miss(model, site, r, v, now, 0, 0, out double a, out double b, out _, out _)
                ? $"{Math.Sqrt(a * a + b * b):n0}m" : "-";
            var redux = ReduxImpactPrediction.GetFlown(vessel);
            string redux_miss = redux.usable
                ? $"{LandingPilot.HaversineDistanceMeters(redux.latitude, redux.longitude, settings.target_latitude.V, settings.target_longitude.V, vessel.mainBody.radius):n0}m"
                : redux.state.ToString();
            logger.LogInfo($"[Boost-back] done after {now - turn_start_UT:n0}s ({first_burn_mps:n0}m/s planned at the start) at " +
                $"{vessel.AltitudeFromSeaLevel:n0}m | airless coast lands {airless} from the target, Redux {redux_miss}");
            status_line = "Boost-back done";
            finished = true;
        }

        // Burn directions from back along the ground track (pitched up/down), each sized - with a
        // sideways part - to land on the site; the smallest wins.
        bool SolveByDirection(ImpactModel model, LandingTargeting.TargetSite site, Vector3d r, Vector3d v, double UT,
            out Vector3d best_dv)
        {
            best_dv = Vector3d.zero;
            Vector3d up = r.normalized;
            Vector3d side = Vector3d.Cross(r, v).normalized;
            double from = have_pitch ? best_pitch - pitch_window_deg : min_pitch_deg;
            double to = have_pitch ? best_pitch + pitch_window_deg : max_pitch_deg;
            double start_size = have_pitch ? Vector3d.Dot(burn, back) : (v - up * Vector3d.Dot(v, up)).magnitude;

            double best = double.PositiveInfinity;
            for (double pitch = from; pitch <= to; pitch += pitch_step_deg)
            {
                double angle = pitch * Math.PI / 180;
                Vector3d direction = (back * Math.Cos(angle) + up * Math.Sin(angle)).normalized;
                if (!Size(model, site, r, v, UT, direction, side, start_size, out Vector3d dv))
                    continue;
                if (dv.magnitude < best)
                {
                    best = dv.magnitude;
                    best_dv = dv;
                    best_pitch = pitch;
                }
            }
            have_pitch = !double.IsInfinity(best);
            return have_pitch;
        }

        // The size along `direction`, plus a sideways part, that lands on the site (Newton's
        // method on the two ground offsets).
        static bool Size(ImpactModel model, LandingTargeting.TargetSite site, Vector3d r, Vector3d v, double UT,
            Vector3d direction, Vector3d side, double start_size, out Vector3d dv)
        {
            double m = start_size, c = 0;
            dv = Vector3d.zero;
            for (int i = 0; i < solve_iterations; i++)
            {
                if (!Miss(model, site, r, v, UT, direction * m + side * c, out double a, out double b))
                    return false;
                if (a * a + b * b < solved_m * solved_m)
                {
                    dv = direction * m + side * c;
                    return true;
                }
                const double dm = 5, dc = 2;
                if (!Miss(model, site, r, v, UT, direction * (m + dm) + side * c, out double am, out double bm)
                    || !Miss(model, site, r, v, UT, direction * m + side * (c + dc), out double ac, out double bc))
                    return false;
                double j11 = (am - a) / dm, j21 = (bm - b) / dm, j12 = (ac - a) / dc, j22 = (bc - b) / dc;
                double det = j11 * j22 - j12 * j21;
                if (Math.Abs(det) < 1e-9)
                    return false;
                // Newton steps can overshoot wildly far from the answer; keep them moderate.
                m += Math.Max(-500, Math.Min(500, (-a * j22 + b * j12) / det));
                c += Math.Max(-500, Math.Min(500, (-b * j11 + a * j21) / det));
                m = Math.Max(-max_burn_mps, Math.Min(max_burn_mps, m));
            }
            return false;
        }

        // Near the end: the smallest burn that cancels the miss, from how each direction of burn
        // moves the landing point (a 2x3 linear solve, least-norm).
        static bool SolveDirect(ImpactModel model, LandingTargeting.TargetSite site, Vector3d r, Vector3d v, double UT,
            out Vector3d dv)
        {
            dv = Vector3d.zero;
            if (!Miss(model, site, r, v, UT, Vector3d.zero, out double a, out double b))
                return false;
            const double d = 1;
            var axes = new[] { new Vector3d(1, 0, 0), new Vector3d(0, 1, 0), new Vector3d(0, 0, 1) };
            var ja = new double[3];
            var jb = new double[3];
            for (int i = 0; i < 3; i++)
            {
                if (!Miss(model, site, r, v, UT, axes[i] * d, out double ai, out double bi))
                    return false;
                ja[i] = (ai - a) / d;
                jb[i] = (bi - b) / d;
            }
            // dv = J^T (J J^T)^-1 (-miss)
            double s11 = ja[0] * ja[0] + ja[1] * ja[1] + ja[2] * ja[2];
            double s12 = ja[0] * jb[0] + ja[1] * jb[1] + ja[2] * jb[2];
            double s22 = jb[0] * jb[0] + jb[1] * jb[1] + jb[2] * jb[2];
            double det = s11 * s22 - s12 * s12;
            if (Math.Abs(det) < 1e-12)
                return false;
            double ya = (-a * s22 + b * s12) / det;
            double yb = (-b * s11 + a * s12) / det;
            dv = new Vector3d(ja[0] * ya + jb[0] * yb, ja[1] * ya + jb[1] * yb, ja[2] * ya + jb[2] * yb);
            return true;
        }

        // Where the airless coast after an instant burn `dv` (ImpactModel's axes) comes down,
        // relative to the site.
        static bool Miss(ImpactModel model, LandingTargeting.TargetSite site, Vector3d r, Vector3d v, double UT, Vector3d dv,
            out double a, out double b)
        {
            a = b = 0;
            if (!model.Impact(r, v + dv, UT, site.radius, out double impact_UT, out Vector3d p))
                return false;
            site.Offset(model.BodyFixed(p, impact_UT), out a, out b);
            return true;
        }

        public override void UpdateInfoRows(Action<string, string> addRow)
        {
            addRow("Burn Left", $"{burn.magnitude:n1} m/s");
            addRow("Attitude Error", $"{hold.attitude_error_deg:n1}°");
        }
    }
}
