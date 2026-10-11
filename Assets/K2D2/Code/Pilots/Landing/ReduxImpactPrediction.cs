using System;
using System.Reflection;
using KSP.Sim;
using KSP.Sim.impl;
using KTools;

namespace K2D2.Landing
{
    public enum ReduxImpactState
    {
        Unavailable,  // this Redux build doesn't have the members read below
        Idle,         // nothing to predict: Redux's impact settings are off for the current view, the vessel is
                      // landed, or no part of the trajectory comes low enough to reach terrain
        OtherVessel,  // in map view Redux predicts for the map-focused vessel, which isn't this one
        Calculating,  // the first solve for this trajectory hasn't finished yet
        NoImpact,     // the solve finished without hitting ground: the trajectory escapes, or it ran out of steps or time
        Impact,
    }

    public struct ReduxImpact
    {
        public ReduxImpactState state;
        public double latitude;
        public double longitude;
        public double impact_UT;
        // How old the vessel state this prediction started from is. Redux re-solves continuously, so
        // this normally stays under a second; its own map marker hides results older than 10 s.
        public double age;
        // The moment the vessel state this prediction started from was taken.
        public double snapshot_UT;
        // True while the body's detailed terrain isn't loaded (far from the surface), or while the
        // drag model is only an estimate on an atmospheric body - the point can shift once that resolves.
        public bool approximate;

        // Same freshness limit Redux's own map marker applies before it stops showing a result.
        const double max_age_s = 10;

        public bool usable => state == ReduxImpactState.Impact && age < max_age_s && impact_UT > GeneralTools.Current_UT;
    }

    // Reads the impact predictions Redux draws on the map (Redux.Simulation.ImpactPrediction in
    // Assembly-CSharp): "Flown" for the vessel's current trajectory and "Planned" for the
    // trajectory after its maneuver nodes (the same one the map draws, ManeuverPlanSolver's). Nearly all of it is
    // `internal`, so like ReduxWaypoints it's reached through reflection. Every member is looked up by
    // name - the public ImpactPredictionSystem type included - so a Redux build that renames or removes
    // any of it leaves this reporting Unavailable instead of K2D2 failing to load.
    public static class ReduxImpactPrediction
    {
        const BindingFlags Instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        static FieldInfo running_system;   // ImpactPredictionSystem._running (static)
        static PropertyInfo flown;         // ImpactPredictionSystem.Flown
        static PropertyInfo planned;       // ImpactPredictionSystem.Planned
        static PropertyInfo predictor_vessel, predictor_body, predictor_result;
        static FieldInfo status_field, body_position_field, impact_UT_field, snapshot_UT_field, approximate_field;
        static FieldInfo x, y, z;          // Unity.Mathematics.double3, which K2D2 doesn't reference

        static bool? _available;
        public static bool available => _available ??= Resolve();

        static bool Resolve()
        {
            // Reading a member's type can throw if Redux moved it to an assembly that isn't loaded -
            // that counts as unavailable too, rather than an exception every frame from the caller.
            try
            {
                return ResolveMembers();
            }
            catch (Exception)
            {
                return false;
            }
        }

        static bool ResolveMembers()
        {
            var system_type = Type.GetType("Redux.Simulation.ImpactPrediction.ImpactPredictionSystem, Assembly-CSharp");
            running_system = system_type?.GetField("_running", BindingFlags.NonPublic | BindingFlags.Static);
            flown = system_type?.GetProperty("Flown", Instance);
            planned = system_type?.GetProperty("Planned", Instance);

            var predictor_type = flown?.PropertyType;
            predictor_vessel = predictor_type?.GetProperty("Vessel", Instance);
            predictor_body = predictor_type?.GetProperty("Body", Instance);
            predictor_result = predictor_type?.GetProperty("Result", Instance);

            var result_type = predictor_result?.PropertyType;
            status_field = result_type?.GetField("Status", Instance);
            body_position_field = result_type?.GetField("BodyPosition", Instance);
            impact_UT_field = result_type?.GetField("ImpactUniversalTime", Instance);
            snapshot_UT_field = result_type?.GetField("SnapshotUniversalTime", Instance);
            approximate_field = result_type?.GetField("IsApproximate", Instance);

            var vector_type = body_position_field?.FieldType;
            x = vector_type?.GetField("x");
            y = vector_type?.GetField("y");
            z = vector_type?.GetField("z");

            return running_system != null && flown != null && planned != null && predictor_vessel != null && predictor_body != null
                && predictor_result != null && status_field != null && body_position_field != null && impact_UT_field != null
                && snapshot_UT_field != null && approximate_field != null && x != null && y != null && z != null;
        }

        // Atmospheric precision landing steers by holding an attitude relative to the airflow, which
        // is what Redux's prediction assumes with Assumed attitude = Current attitude - and also
        // with SAS, since that follows the SAS mode and K2D2 holds its attitude in Stability Assist,
        // which Redux treats as holding the current attitude (PredictedAttitude.ResolveMode).
        // Prograde and Retrograde assume a fixed direction instead, so steering wouldn't show up in
        // the prediction. The prediction also has to be running in both views. These settings are
        // public, unlike the prediction itself. Returns a message for the player saying what to
        // change, or null when everything is set.
        public static string AtmoSettingsProblem()
        {
            var settings = KSP.Game.PersistentProfileManager.Settings;
            if (settings == null)
                return null;

            var attitude = settings.ImpactPredictionAttitude;
            if (attitude != Redux.Simulation.ImpactPrediction.ImpactPredictionAttitude.Current
                && attitude != Redux.Simulation.ImpactPrediction.ImpactPredictionAttitude.SAS)
                return "Needs Redux's impact prediction to assume the current attitude or follow SAS: " +
                    "Settings > Precise Reentry > Assumed attitude > Current attitude (or SAS).";
            if (!settings.ShowImpactPredictionInMap || !settings.ShowImpactPredictionInFlight)
                return "Needs Redux's impact prediction turned on in both map and flight view (Settings > Precise Reentry).";
            return null;
        }

        // True when Redux's Assumed attitude follows SAS: then SAS's Retrograde mode makes it
        // predict exactly retrograde at entry, whatever the vessel is actually pointing at.
        public static bool FollowsSas()
        {
            var settings = KSP.Game.PersistentProfileManager.Settings;
            return settings != null
                && settings.ImpactPredictionAttitude == Redux.Simulation.ImpactPrediction.ImpactPredictionAttitude.SAS;
        }

        public static ReduxImpact GetFlown(VesselComponent vessel) => Get(vessel, flown);

        // Where the trajectory after the vessel's maneuver nodes comes down.
        public static ReduxImpact GetPlanned(VesselComponent vessel) => Get(vessel, planned);

        static ReduxImpact Get(VesselComponent vessel, PropertyInfo kind)
        {
            var impact = new ReduxImpact { state = ReduxImpactState.Unavailable };
            if (!available)
                return impact;

            object system = running_system.GetValue(null);
            object predictor = system == null ? null : kind.GetValue(system);
            var predicted_vessel = predictor == null ? null : predictor_vessel.GetValue(predictor) as VesselComponent;
            var body = predictor == null ? null : predictor_body.GetValue(predictor) as CelestialBodyComponent;

            if (predicted_vessel == null || body == null)
            {
                impact.state = ReduxImpactState.Idle;
                return impact;
            }
            if (predicted_vessel != vessel)
            {
                impact.state = ReduxImpactState.OtherVessel;
                return impact;
            }

            // Redux's ImpactStatus: 0 Calculating, 1 TerrainQuery, 2 Impact, 3 NoImpact, 4 LimitReached, 5 Invalid.
            object result = predictor_result.GetValue(predictor);
            int redux_status = Convert.ToInt32(status_field.GetValue(result));
            if (redux_status <= 1)
            {
                impact.state = ReduxImpactState.Calculating;
                return impact;
            }
            if (redux_status != 2)
            {
                impact.state = ReduxImpactState.NoImpact;
                return impact;
            }

            // BodyPosition is in the body's rotating frame (bodyFrame), with the body's rotation during
            // the fall already applied - the same Position Redux's map marker builds from it
            // (Redux.UI.MapView.SurfaceProjection), so no separate rotation correction is needed here.
            object p = body_position_field.GetValue(result);
            var local = new Vector3d((double)x.GetValue(p), (double)y.GetValue(p), (double)z.GetValue(p));
            body.GetLatLonAltFromRadius(new Position(body.transform.bodyFrame, local),
                out impact.latitude, out impact.longitude, out _);

            impact.impact_UT = (double)impact_UT_field.GetValue(result);
            impact.snapshot_UT = (double)snapshot_UT_field.GetValue(result);
            impact.age = GeneralTools.Current_UT - impact.snapshot_UT;
            impact.approximate = (bool)approximate_field.GetValue(result);
            impact.state = ReduxImpactState.Impact;
            return impact;
        }
    }
}

namespace K2D2.Landing
{
    // Waits for a Redux prediction that can be trusted as a measurement: made after the vessel has
    // settled into the attitude being measured, and holding still. The test flights showed why both
    // matter: an attitude still 1-2° from where it's heading changes the predicted landing point by
    // several km, and the first result after a big turn can be from before the turn settled.
    //
    // Call Update every frame while holding the attitude; it returns true once `result` is ready
    // (or once it has waited long enough - check result.usable).
    public class ReduxReading
    {
        // Settled: attitude error below this, or below Redux's own 2° restart tolerance after
        // waiting settle_timeout_s (a vessel that can't hold it tighter).
        const double settled_deg = 0.5;
        const double loose_settled_deg = 2;
        const double settle_timeout_s = 15;
        // Holding still: two results at least this far apart in time that agree to within this.
        // (Only for predictions that keep re-solving as the vessel moves, like Flown. Planned
        // solves once per change of node or attitude, so there's no second result to compare.)
        const double compare_interval_s = 1;
        const double agree_m = 500;
        const double timeout_s = 40;

        public ReduxImpact result { get; private set; }

        double start_UT = double.NaN, settled_UT;
        ReduxImpact previous;
        bool have_previous;

        public void Reset()
        {
            start_UT = double.NaN;
            have_previous = false;
            result = default;
        }

        // not_before_UT: results from before this moment don't count (e.g. before a maneuver node
        // changed).
        public bool Update(ReduxImpact impact, double attitude_error_deg, double body_radius,
            double not_before_UT = double.NegativeInfinity, bool require_steady = true)
        {
            double now = GeneralTools.Current_UT;
            if (double.IsNaN(start_UT))
            {
                start_UT = now;
                settled_UT = double.NaN;
            }
            if (double.IsNaN(settled_UT) && (attitude_error_deg < settled_deg
                    || attitude_error_deg < loose_settled_deg && now - start_UT > settle_timeout_s))
                settled_UT = now;

            bool fresh = impact.usable && !double.IsNaN(settled_UT) && impact.snapshot_UT >= Math.Max(settled_UT, not_before_UT);
            if (fresh)
            {
                result = impact;
                if (!require_steady)
                    return true;
                if (!have_previous)
                {
                    previous = impact;
                    have_previous = true;
                }
                else if (impact.snapshot_UT - previous.snapshot_UT >= compare_interval_s)
                {
                    if (Distance(previous, impact, body_radius) <= agree_m)
                        return true;
                    previous = impact;
                }
            }
            return now - start_UT > timeout_s;
        }

        static double Distance(ReduxImpact a, ReduxImpact b, double radius)
        {
            double to_rad = Math.PI / 180;
            double dlat = (b.latitude - a.latitude) * to_rad, dlon = (b.longitude - a.longitude) * to_rad;
            double h = Math.Sin(dlat / 2) * Math.Sin(dlat / 2)
                + Math.Cos(a.latitude * to_rad) * Math.Cos(b.latitude * to_rad) * Math.Sin(dlon / 2) * Math.Sin(dlon / 2);
            return 2 * radius * Math.Asin(Math.Min(1, Math.Sqrt(h)));
        }
    }
}
