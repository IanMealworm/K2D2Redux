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

        const string PickPrompt = "Pick a waypoint";

        // The target still holds what was taken from the waypoint, so nobody has typed over it.
        bool TargetFrom(ReduxWaypoint waypoint) =>
            settings.target_latitude.V == (float)waypoint.latitude && settings.target_longitude.V == (float)waypoint.longitude;

        bool MovedFrom(ReduxWaypoint waypoint) =>
            waypoint.latitude != selected_latitude || waypoint.longitude != selected_longitude;

        bool TargetAtSelection() =>
            settings.target_latitude.V == (float)selected_latitude && settings.target_longitude.V == (float)selected_longitude;

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
            else
                labels.Insert(0, PickPrompt);

            dropdown.choices = labels;

            // The target only changes when the player picks a waypoint, or the picked one moves.
            // A target typed in, or saved from an earlier session, stays: the dropdown then shows
            // the prompt rather than a waypoint the target isn't at. The target is written here
            // directly rather than through the dropdown's ChangeEvent, which isn't sent while the
            // tab isn't shown.
            if (selected is ReduxWaypoint still_there
                && (TargetFrom(still_there) || (MovedFrom(still_there) && TargetAtSelection())))
            {
                if (!TargetFrom(still_there))
                    Select(still_there);
                dropdown.SetValueWithoutNotify(Label(still_there));
            }
            else
            {
                selected_id = null;
                dropdown.SetValueWithoutNotify(labels[0]);
            }
        }
    }
}
