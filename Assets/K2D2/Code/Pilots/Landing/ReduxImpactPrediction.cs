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
        // True while the body's detailed terrain isn't loaded (far from the surface), or while the
        // drag model is only an estimate on an atmospheric body - the point can shift once that resolves.
        public bool approximate;

        // Same freshness limit Redux's own map marker applies before it stops showing a result.
        const double max_age_s = 10;

        public bool usable => state == ReduxImpactState.Impact && age < max_age_s && impact_UT > GeneralTools.Current_UT;
    }

    // Reads the impact prediction Redux draws on the map for the vessel's current trajectory (its
    // "Flown" predictor, Redux.Simulation.ImpactPrediction in Assembly-CSharp). Nearly all of it is
    // `internal`, so like ReduxWaypoints it's reached through reflection. Every member is looked up by
    // name - the public ImpactPredictionSystem type included - so a Redux build that renames or removes
    // any of it leaves this reporting Unavailable instead of K2D2 failing to load.
    public static class ReduxImpactPrediction
    {
        const BindingFlags Instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        static FieldInfo running_system;   // ImpactPredictionSystem._running (static)
        static PropertyInfo flown;         // ImpactPredictionSystem.Flown
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

            return running_system != null && flown != null && predictor_vessel != null && predictor_body != null
                && predictor_result != null && status_field != null && body_position_field != null && impact_UT_field != null
                && snapshot_UT_field != null && approximate_field != null && x != null && y != null && z != null;
        }

        public static ReduxImpact GetFlown(VesselComponent vessel)
        {
            var impact = new ReduxImpact { state = ReduxImpactState.Unavailable };
            if (!available)
                return impact;

            object system = running_system.GetValue(null);
            object predictor = system == null ? null : flown.GetValue(system);
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
            impact.age = GeneralTools.Current_UT - (double)snapshot_UT_field.GetValue(result);
            impact.approximate = (bool)approximate_field.GetValue(result);
            impact.state = ReduxImpactState.Impact;
            return impact;
        }
    }
}
