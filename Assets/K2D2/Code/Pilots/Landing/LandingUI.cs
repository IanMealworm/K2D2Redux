using System.Collections.Generic;
using K2D2.UI;
using K2UI;
using K2UI.Tabs;
using KSP.Game;
using UnityEngine.UIElements;
using KTools;



namespace K2D2.Landing
{
    class LandingUI : K2Page
    {
        LandingPilot pilot;
        public LandingUI(LandingPilot pilot)
        {
            this.pilot = pilot;
            code = "landing";
        }

        public VisualElement landing_infos;
        public Label collision_value;
        public FullStatus status_bar;

        public ToggleButton run_button;

        public Button touch_down;

        public DropdownField waypoint_drop;
        // Keyed by the exact string shown in waypoint_drop, since DropdownField only ever gives us
        // the selected label back in its ChangeEvent - not an index or the underlying record.
        Dictionary<string, ReduxWaypoint> waypoint_choices = new();

        public override bool onInit()
        {
            landing_infos = panel.Q<VisualElement>("landing_infos");
            collision_value = panel.Q<Label>("collision_value");

            run_button = panel.Q<ToggleButton>("run");
            touch_down = panel.Q<Button>("touch_down");
            status_bar = new FullStatus(panel);

            // Waypoint picker - lists whatever the player's already placed on the current body via
            // Redux's own Waypoints window (K2D2.Landing.ReduxWaypoints, reflection-based since that
            // system is internal to Assembly-CSharp). Docks' own target/control dropdowns
            // (SelectTargetUI.cs) rebuild their choices via listenClick(), but that turned out not
            // to work here - DropdownField opens its own native list on pointer-down rather than
            // dispatching a bubbling ClickEvent our listenClick() extension can catch, so the click
            // handler just never fired and the list stayed empty. Rebuilding every UI tick instead
            // (see onUpdateUI() below) sidesteps that entirely and also means the list stays current
            // if a waypoint's added while this tab is open.
            waypoint_drop = panel.Q<DropdownField>("waypoint_drop");
            buildWaypointList();
            waypoint_drop.RegisterCallback<ChangeEvent<string>>(evt =>
            {
                if (waypoint_choices.TryGetValue(evt.newValue, out var waypoint))
                {
                    pilot.settings.target_latitude.V = (float)waypoint.latitude;
                    pilot.settings.target_longitude.V = (float)waypoint.longitude;
                }
            });

            pilot.is_running_event += is_running => run_button.Value = is_running;
            // Same "give him a little life" touch as Node/Lift: K2's 3 grille lines cascade
            // on/off with the autopilot, via the same is_running_event run_button already
            // listens to above.
            pilot.is_running_event += is_running => status_bar.avatar?.SetRunning(is_running);
            run_button.listeners += v =>
            {
                pilot.isRunning = v;
                run_button.label = v ? "Stop" : "Brake";
            };

            touch_down.listenClick(() =>
            {
                pilot.isRunning = true;
                pilot.setMode(LandingPilot.Mode.TouchDown);
            });

            pilot.settings.setupUI(pilot, panel);
            addResetButton(panel.Q<Foldout>("advanced_foldout"), "land");

            return true;
        }

        void buildWaypointList()
        {
            waypoint_choices.Clear();

            var body = pilot.current_vessel?.currentBody();
            var choices = new List<string>();

            if (body != null && ReduxWaypoints.available)
            {
                foreach (var waypoint in ReduxWaypoints.GetWaypointsForBody(body.Name))
                {
                    string label = $"{waypoint.name} ({waypoint.latitude:n2}, {waypoint.longitude:n2})";
                    waypoint_choices[label] = waypoint;
                    choices.Add(label);
                }
            }

            if (choices.Count == 0)
                choices.Add(body == null ? "No body" : "No waypoints on " + body.Name);

            // This runs every UI tick (see onUpdateUI()) so the list stays current if a waypoint's
            // added while this tab's open - preserve whatever's currently selected across the
            // rebuild instead of resetting it every frame, via SetValueWithoutNotify so this doesn't
            // re-fire the ChangeEvent handler above.
            string current = waypoint_drop.value;
            waypoint_drop.choices = choices;
            if (choices.Contains(current))
                waypoint_drop.SetValueWithoutNotify(current);
        }

        void AddInfoRow(string label, string value)
        {
            var row = new VisualElement();
            row.AddToClassList("advanced-info-row");

            var label_el = new Label(label);
            label_el.AddToClassList("advanced-info-row-label");
            row.Add(label_el);

            var value_el = new Label(value);
            value_el.AddToClassList("advanced-info-row-value");
            row.Add(value_el);

            landing_infos.Add(row);
        }

        // Always-visible "am I about to hit something" readout, next to the Brake/Touch Down
        // buttons regardless of whether LANDING INFO below is expanded. Kept separate from
        // updateContext()/landing_infos so it never depends on that Foldout's collapsed state.
        void updateCollisionStatus()
        {
            collision_value.text = pilot.collision_detected ? "Detected" : "None detected";
        }

        public void updateContext()
        {
            landing_infos.Clear();

            AddInfoRow("Fall Speed", $"{pilot.current_falling_speed:n2} m/s");
            AddInfoRow("Altitude", StrTool.DistanceToString(pilot.altitude));

            if (pilot.collision_detected)
            {
                AddInfoRow("Collision In", StrTool.DurationToString(pilot.adjusted_collision_UT - GeneralTools.Game.UniverseModel.UniverseTime));
                AddInfoRow("Collision Speed", $"{pilot.speed_collision:n2} m/s");
                AddInfoRow("Start Burn In", StrTool.DurationToString(pilot.startBurn_UT - GeneralTools.Game.UniverseModel.UniverseTime));
                AddInfoRow("Burn Duration", $"{pilot.burn_duration:n2} s");

                // Precision landing readouts - informational only for now, nothing steers toward
                // this yet. Predicted Landing lets us confirm the lat/lon math is sane in-game
                // (compare it against where the ship actually comes down) before anything gets
                // built on top of it.
                AddInfoRow("Predicted Landing", $"{pilot.predicted_landing_lat:n2}, {pilot.predicted_landing_lon:n2}");
                if (pilot.settings.precision_landing.V)
                    AddInfoRow("Target Error", StrTool.DistanceToString((float)pilot.target_error_m));
            }

            // Same idea as Lift's LIFT INFO: numeric telemetry from whichever sub-controller is
            // actually driving the vessel right now (TouchDown's Max/Delta Speed while braking;
            // WarpTo has nothing to report during the warp phases) folds into this table via
            // ExecuteController.UpdateInfoRows, instead of scrolling through the console text
            // alongside the narrative status line below.
            if (pilot.isRunning)
            {
                pilot.current_executor?.UpdateInfoRows(AddInfoRow);

                if (isRunning && pilot.burn_dV.burned_dV > 0)
                    AddInfoRow("Burned", $"{pilot.burn_dV.burned_dV:n1} m/s");
            }
        }

        public override bool onUpdateUI()
        {
            if (!base.onUpdateUI())
                return false;

            updateCollisionStatus();
            updateContext();
            buildWaypointList();

            status_bar.Reset();

            // This used to return early whenever collision_detected was false, which also skipped the
            // touch_down button's Show()/Hide() and the mode status text below - so once collision
            // detection legitimately flips false mid-descent (e.g. after braking changes the
            // trajectory), the button/status display would freeze instead of updating. Collision
            // prediction only matters for the info panel above; it has nothing to do with the mode
            // display, so the early return was removed.
            var state = GeneralTools.Game.GlobalGameState.GetState();
            if (state != GameState.FlightView)
            {
                status_bar.Console("Landing is only available in Fligh View");
                return true;
            }

            touch_down.Show(pilot.mode != LandingPilot.Mode.TouchDown);
            if (pilot.isRunning)
            {
                switch (pilot.mode)
                {
                    default:
                    case LandingPilot.Mode.Off: break;
                    case LandingPilot.Mode.Pause:
                        status_bar.Status("Pause");
                        break;
                    case LandingPilot.Mode.QuickWarp:
                        status_bar.Status("Quick Warp");
                        break;
                    case LandingPilot.Mode.RotationWarp:
                        status_bar.Warning("Rotating Warp");
                        break;
                    case LandingPilot.Mode.Waiting:
                        status_bar.Status($"Waiting : {StrTool.DurationToString(pilot.startBurn_UT - GeneralTools.Game.UniverseModel.UniverseTime)}");
                        break;
                    case LandingPilot.Mode.Brake:
                        status_bar.Warning($"Brake !");
                        break;
                    case LandingPilot.Mode.TouchDown:
                        status_bar.Warning($"Touch Down...");
                        break;
                }

                if (pilot.current_executor != null && !string.IsNullOrEmpty(pilot.current_executor.status_line))
                    status_bar.Console(pilot.current_executor.status_line);
            }
            else
            {
                // Idle placeholder, same idea as Node's "No Node Created"/Lift's "Lift autopilot
                // not enabled" - K2 has something to say here instead of the status readout just
                // sitting empty before the pilot's ever been run.
                status_bar.Status("Landing autopilot not enabled");
            }

            //    UI_Tools.Console("SurfaceVelocity" + StrTool.VectorToString(SurfaceVelocity.vector));

            return true;
        }

    }
}