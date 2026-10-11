using System.Collections.Generic;
using KSP.Sim.impl;
using UnityEngine.UIElements;

namespace K2D2.Landing
{
    // One waypoint dropdown on the Landing tab, writing the picked waypoint's lat/lon into one
    // profile's target settings. There's one per profile panel (Vacuum and Atmo), each bound to
    // its own LandingSettings, the same way the rest of the two panels are kept apart.
    //
    // Lists whatever the player has placed on the current body in Redux's own Waypoints window
    // (ReduxWaypoints, reflection-based since that system is internal to Assembly-CSharp). Rebuilt
    // every UI tick rather than via a click listener, since DropdownField opens its own native list
    // on pointer-down instead of dispatching a bubbling ClickEvent - the per-tick rebuild also keeps
    // the list current if a waypoint's added while the tab is open.
    public class WaypointPicker
    {
        readonly DropdownField dropdown;
        readonly LandingSettings settings;

        // Keyed by the exact string shown in the dropdown, since DropdownField only ever gives
        // back the selected label in its ChangeEvent - not an index or the underlying record.
        readonly Dictionary<string, ReduxWaypoint> choices = new();

        // The waypoint the target was last taken from, by Redux's waypoint id - labels can't
        // identify it, since they include the coordinates and change if the waypoint is moved -
        // and where it was at that moment, to notice it moving.
        string selected_id;
        double selected_latitude, selected_longitude;

        public WaypointPicker(DropdownField dropdown, LandingSettings settings)
        {
            this.dropdown = dropdown;
            this.settings = settings;
            dropdown.RegisterCallback<ChangeEvent<string>>(evt =>
            {
                if (choices.TryGetValue(evt.newValue, out var waypoint))
                    Select(waypoint);
            });
        }

        void Select(ReduxWaypoint waypoint)
        {
            selected_id = waypoint.id;
            selected_latitude = waypoint.latitude;
            selected_longitude = waypoint.longitude;
            settings.target_latitude.V = (float)waypoint.latitude;
            settings.target_longitude.V = (float)waypoint.longitude;
        }

        static string Label(ReduxWaypoint waypoint) =>
            $"{waypoint.name} ({waypoint.latitude:n2}, {waypoint.longitude:n2})";

        public void Rebuild(CelestialBodyComponent body)
        {
            choices.Clear();
            var labels = new List<string>();
            ReduxWaypoint? selected = null;

            if (body != null && ReduxWaypoints.available)
            {
                foreach (var waypoint in ReduxWaypoints.GetWaypointsForBody(body.Name))
                {
                    string label = Label(waypoint);
                    choices[label] = waypoint;
                    labels.Add(label);
                    if (waypoint.id == selected_id)
                        selected = waypoint;
                }
            }

            if (labels.Count == 0)
                labels.Add(body == null ? "No body" : "No waypoints on " + body.Name);

            dropdown.choices = labels;

            // This also keeps the target in step with the waypoint list: the selected waypoint
            // moved, a first waypoint appeared on this body, or the selection belongs to another
            // body. The target is written here directly rather than through the dropdown's
            // ChangeEvent - that event isn't sent while the tab isn't shown, which used to leave
            // the dropdown showing a new waypoint while the target still held the previous one.
            if (selected is ReduxWaypoint still_there)
            {
                // Only when the waypoint itself moved, so a lat/lon typed in by hand stays.
                if (still_there.latitude != selected_latitude || still_there.longitude != selected_longitude)
                    Select(still_there);
                dropdown.SetValueWithoutNotify(Label(still_there));
            }
            else if (choices.Count > 0)
            {
                // Nothing selected on this body yet - take its first waypoint.
                Select(choices[labels[0]]);
                dropdown.SetValueWithoutNotify(labels[0]);
            }
            else
            {
                // No waypoints on this body. The target keeps whatever was typed in (or picked on
                // another body), but the dropdown stops showing a waypoint that isn't here.
                dropdown.SetValueWithoutNotify(labels[0]);
            }
        }
    }
}
