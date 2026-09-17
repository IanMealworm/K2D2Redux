using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace K2D2.Landing
{
    // Redux's own player-waypoint system (Redux.UI.Waypoints.PlayerWaypointManager and friends,
    // in Assembly-CSharp) is declared `internal`, so it is invisible to K2D2.dll through a normal
    // reference and is reached here by reflection. The K2UI custom controls once needed the same
    // reflection trick to register; they now use the `[UxmlElement]` attribute route instead (see
    // Assets/K2D2/Code/K2UI/), and NOTICE.md's UI Toolkit section records that history.
    // Confirmed against the real decompiled PlayerWaypointManager.cs/PlayerWaypointRecord.cs
    // rather than guessed at.
    //
    // Only reads the player's saved waypoints (PlayerWaypointManager.Instance.Records) for now.
    // Redux's own click-to-place flow (BeginPlacement()/PlacementCompleted) isn't hooked up here
    // yet - that would let players pick a landing site by clicking the map instead of typing
    // lat/lon or picking a saved waypoint, reusing Redux's own raycasting instead of us
    // reimplementing it, but it needs reflectively subscribing to an internal event
    // (Action<string, double, double>), which deserves its own pass and in-game test rather than
    // going in alongside everything else here.
    public struct ReduxWaypoint
    {
        public string name;
        public string body_name;
        public double latitude;
        public double longitude;
    }

    public static class ReduxWaypoints
    {
        const string manager_type_name = "Redux.UI.Waypoints.PlayerWaypointManager, Assembly-CSharp";

        static Type _manager_type;
        static bool _type_lookup_failed;

        static Type manager_type
        {
            get
            {
                if (_manager_type == null && !_type_lookup_failed)
                {
                    _manager_type = Type.GetType(manager_type_name);
                    if (_manager_type == null)
                        _type_lookup_failed = true;
                }
                return _manager_type;
            }
        }

        // True once we've confirmed the type + Instance + Records are all reachable this run -
        // lets callers show "Waypoints unavailable" instead of just an empty list if Redux ever
        // renames/removes this internally between versions.
        public static bool available => GetManagerInstance() != null;

        static object GetManagerInstance()
        {
            var type = manager_type;
            if (type == null)
                return null;

            var instance_prop = type.GetProperty("Instance", BindingFlags.NonPublic | BindingFlags.Static);
            return instance_prop?.GetValue(null);
        }

        public static List<ReduxWaypoint> GetWaypointsForBody(string body_name)
        {
            var result = new List<ReduxWaypoint>();

            var manager_instance = GetManagerInstance();
            if (manager_instance == null)
                return result;

            var records_prop = manager_type.GetProperty("Records", BindingFlags.NonPublic | BindingFlags.Instance);
            if (records_prop?.GetValue(manager_instance) is not IEnumerable records)
                return result;

            foreach (var record in records)
            {
                var record_type = record.GetType();
                string this_body = record_type.GetField("BodyName")?.GetValue(record) as string;

                if (!string.IsNullOrEmpty(body_name) && this_body != body_name)
                    continue;

                result.Add(new ReduxWaypoint
                {
                    name = record_type.GetField("Name")?.GetValue(record) as string,
                    body_name = this_body,
                    latitude = (double)(record_type.GetField("Latitude")?.GetValue(record) ?? 0.0),
                    longitude = (double)(record_type.GetField("Longitude")?.GetValue(record) ?? 0.0),
                });
            }

            return result;
        }
    }
}
