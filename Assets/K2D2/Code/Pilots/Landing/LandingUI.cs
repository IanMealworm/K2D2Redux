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

        WaypointPicker waypoints_vac;
        WaypointPicker waypoints_atmo;

        // Shown on the Atmo panel when Redux's prediction isn't set up the way atmospheric
        // precision landing needs (see UpdateAtmoGate).
        Label atmo_gate_label;

        // Precision Landing orbit gate (see UpdateOrbitGate below) - reuses Circularize's own
        // 100km ceiling (Circularize.max_starting_altitude_m) rather than a separate number, so
        // this and the actual in-run refusal in Circularize.cs can never drift apart. Vacuum-only
        // (precision_landing_orbit_gate_vac) - see landing_mode_group_vac/pilot.settings_vac below.
        Label orbit_gate_label;

        // Atmo/Vacuum profile panels (see LandingSettings.cs's class comment) - shown/hidden
        // together every UI tick by UpdateProfilePanels, based on LandingProfile.IsAtmospheric.
        // Each pair is bound once, ever, in onInit() below to its own fixed settings object -
        // never rebound, since K2Page.onInit() only runs once per game session.
        VisualElement landing_mode_group_atmo;
        VisualElement landing_mode_group_vac;
        VisualElement atmo_settings_panel;
        VisualElement vac_settings_panel;

        public override bool onInit()
        {
            landing_infos = panel.Q<VisualElement>("landing_infos");
            collision_value = panel.Q<Label>("collision_value");

            run_button = panel.Q<ToggleButton>("run");
            touch_down = panel.Q<Button>("touch_down");
            status_bar = new FullStatus(panel);

            // Waypoint pickers - one per profile panel, each writing its own profile's target
            // (see WaypointPicker).
            waypoints_vac = new WaypointPicker(panel.Q<DropdownField>("waypoint_drop_vac"), pilot.settings_vac);
            waypoints_atmo = new WaypointPicker(panel.Q<DropdownField>("waypoint_drop_atmo"), pilot.settings_atmo);
            atmo_gate_label = panel.Q<Label>("precision_landing_gate_atmo");

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

            // Atmo/Vacuum profile panels - two full sibling groups/panels in Landing.uxml, each
            // bound exactly once here to its own fixed settings/TouchDown instance (never
            // rebound - see this class's field comments and LandingSettings.cs's class comment).
            landing_mode_group_atmo = panel.Q<VisualElement>("landing_mode_group_atmo");
            landing_mode_group_vac = panel.Q<VisualElement>("landing_mode_group_vac");
            atmo_settings_panel = panel.Q<VisualElement>("atmo_settings_panel");
            vac_settings_panel = panel.Q<VisualElement>("vac_settings_panel");

            // Pass `panel` (the whole tab page) here, not vac_settings_panel/atmo_settings_panel -
            // LandingSettings.setupUI/setupBasicUI and TouchDown's SetupFullUI/SetupBasicUI all
            // reach elements that live OUTSIDE the ADVANCED foldout's own sub-panels
            // (precision_landing_vac, target_settings_vac, and the orbit gate label all live in
            // landing_mode_group_vac, a sibling of advanced_foldout - see Landing.uxml).
            // VisualElement.Q<T>() searches the whole subtree under whatever root it's given, so
            // passing the narrower vac_settings_panel/atmo_settings_panel makes
            // root.Q<K2Toggle>("precision_landing_vac") return null - Bind() on that null throws a
            // NullReferenceException that unwinds all the way up through K2Page.Init ->
            // TabbedPage.Init -> K2D2Window.OnUiReload, aborting every other tab's Init() too.
            // Passing `panel` everywhere is always safe: Q<T>() finds a uniquely-named element
            // anywhere under it regardless of nesting depth.
            pilot.settings_vac.setupUI(panel);
            pilot.brake.SetupFullUI(panel);

            pilot.settings_atmo.setupBasicUI(panel);
            pilot.brake.SetupBasicUI(panel);

            // Reset resets BOTH profiles together, not just whichever panel happens to be showing
            // right now, against two SettingsFile instances instead of the one SettingsFile.
            // Instance the shared addResetButton helper hardcodes (see its own comment in
            // K2Page.cs - it's shared by Node/Lift/Dock too, so it can't just be changed there).
            addLandingResetButton(panel.Q<Foldout>("advanced_foldout"));

            // Precision Landing orbit gate - see UpdateOrbitGate's own comment. Reverts the toggle
            // the instant someone tries to turn it on from too high an orbit, rather than letting
            // it sit on and only get refused once Circularize.cs actually runs. Vacuum-only, same
            // as the toggle itself.
            orbit_gate_label = panel.Q<Label>("precision_landing_orbit_gate_vac");
            pilot.settings_vac.precision_landing.listeners += v =>
            {
                if (!v) return;

                var check = Circularize.CheckOrbit(out double apoapsisAlt_m, out double periapsisAlt_m);
                if (check != Circularize.OrbitCheck.TooHigh)
                    return;

                pilot.logger.LogInfo($"[LandingUI] Precision Landing refused - orbit too high " +
                    $"(Ap {apoapsisAlt_m:n0}m / Pe {periapsisAlt_m:n0}m, max {Circularize.max_starting_altitude_m:n0}m).");
                pilot.settings_vac.precision_landing.V = false;
            };

            return true;
        }

        // Same idea as K2Page.addResetButton (Node/Lift/Dock's shared helper), but resets the
        // land_atmo AND land_vac SettingsFile instances together, since Landing's settings no
        // longer live in the one SettingsFile.Instance that helper hardcodes.
        void addLandingResetButton(VisualElement parent)
        {
            if (parent == null)
                return;

            Button reset_bt = new Button() { text = "Reset" };
            reset_bt.name = "reset_advanced";
            reset_bt.style.height = 30;
            reset_bt.style.marginTop = 8;

            parent.Add(reset_bt);

            reset_bt.RegisterCallback<ClickEvent>(evt =>
            {
                SettingsFile.Get("land_atmo")?.Reset("land");
                SettingsFile.Get("land_vac")?.Reset("land");
            });
        }

        // Shows exactly one of each profile-pair (landing_mode_group_*/*_settings_panel) at a
        // time, based on LandingProfile.IsAtmospheric - same per-tick refresh pattern
        // UpdateOrbitGate below uses, so a body change while this tab's open switches panels
        // immediately instead of needing a tab close/reopen.
        void UpdateProfilePanels()
        {
            bool atmo = LandingProfile.IsAtmospheric;
            landing_mode_group_atmo.Show(atmo);
            landing_mode_group_vac.Show(!atmo);
            atmo_settings_panel.Show(atmo);
            vac_settings_panel.Show(!atmo);
        }

        void buildWaypointList()
        {
            var body = pilot.current_vessel?.currentBody();
            waypoints_vac.Rebuild(body);
            waypoints_atmo.Rebuild(body);
        }

        // Atmospheric precision landing can't work unless Redux's prediction assumes the current
        // attitude and runs in both views (ReduxImpactPrediction.AtmoSettingsProblem). Says what to
        // change, and keeps the toggle off until it's done - checked every tick, so changing the
        // setting in Redux's menu takes effect without reopening anything.
        void UpdateAtmoGate()
        {
            string problem = ReduxImpactPrediction.AtmoSettingsProblem();
            atmo_gate_label.Show(problem != null);
            if (problem == null)
                return;

            atmo_gate_label.text = problem;
            if (!pilot.isRunning && pilot.settings_atmo.precision_landing.V)
                pilot.settings_atmo.precision_landing.V = false;
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

        // Precision Landing orbit gate. The status_bar console/status lines get wiped and
        // rebuilt every single UI tick (see status_bar.Reset() below), so a one-shot message from
        // the precision_landing toggle listener (onInit above) would only ever be visible for a
        // single frame. This runs every tick instead, independent of isRunning/mode, keeping a
        // small persistent label under the toggle in sync with the same Circularize.CheckOrbit()
        // the toggle listener already gates on.
        void UpdateOrbitGate()
        {
            // Circularize.CheckOrbit() isn't itself null-safe against VesselComponent being null -
            // fine for its one existing caller (Circularize.Start(), which only runs mid-landing,
            // i.e. definitely in active flight), but this runs unconditionally every UI tick
            // regardless of game state, so it needs its own check here.
            if (pilot.current_vessel?.VesselComponent == null)
            {
                orbit_gate_label.Show(false);
                return;
            }

            var check = Circularize.CheckOrbit(out double apoapsisAlt_m, out double periapsisAlt_m);
            bool too_high = check == Circularize.OrbitCheck.TooHigh;

            orbit_gate_label.Show(too_high);
            if (!too_high)
                return;

            orbit_gate_label.text = $"Needs a starting orbit within {Circularize.max_starting_altitude_m / 1000:n0}km " +
                $"(currently Ap {apoapsisAlt_m / 1000:n0}km / Pe {periapsisAlt_m / 1000:n0}km) - circularize lower first.";

            // Covers the case where the orbit was too high when the player tries to turn
            // Precision Landing on before a landing sequence has started, re-checked continuously
            // instead of only at the moment of the click, so the toggle doesn't silently stay on
            // if apoapsis creeps up between then and pressing Brake/Touch Down.
            //
            // Gated on !pilot.isRunning: apoapsis routinely sits well above
            // max_starting_altitude_m for a big chunk of a normal descent (Circularize/DeorbitBurn
            // only lower periapsis at first - apoapsis doesn't come down until much later, if at
            // all). Without this guard the check would fire mid-descent too and silently flip
            // precision_landing off, which would mean the guided descent never
            // engages for the rest of that descent. This check is only meaningful as a pre-flight
            // gate; once the sequence has committed (isRunning), the orbit is EXPECTED to move
            // around and must not retroactively cancel precision landing.
            if (!pilot.isRunning && pilot.settings_vac.precision_landing.V)
                pilot.settings_vac.precision_landing.V = false;
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

                // Predicted Landing lets players check the prediction against where the ship
                // actually comes down; Prediction says which predictor produced it.
                AddInfoRow("Predicted Landing", $"{pilot.predicted_landing_lat:n2}, {pilot.predicted_landing_lon:n2}");
                AddInfoRow("Prediction", pilot.using_redux_prediction ? "Redux" : "K2D2");
                // Where the vessel would land with the engine off - meaningless once the guided
                // descent is burning, which shows its own distance to the target instead.
                bool guided_burn = pilot.vacuum_precision && pilot.mode == LandingPilot.Mode.Brake;
                if (pilot.settings.precision_landing.V && !guided_burn)
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
            UpdateOrbitGate();
            UpdateAtmoGate();
            UpdateProfilePanels();

            status_bar.Reset();

            // Collision prediction only matters for the info panel above; it has nothing to do
            // with the mode display below, so this no longer early-returns on
            // !collision_detected (that used to freeze the touch_down button/status text
            // whenever collision detection legitimately flipped false mid-descent).
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
                    case LandingPilot.Mode.AtmoDeorbit:
                        status_bar.Status("Deorbit");
                        break;
                    case LandingPilot.Mode.AtmoCorrection:
                        status_bar.Status("Entry correction");
                        break;
                    case LandingPilot.Mode.AtmoReentry:
                        status_bar.Warning("Entry guidance");
                        break;
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
                        status_bar.Warning(pilot.vacuum_precision ? "Powered Descent" : "Brake !");
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
                // not enabled".
                if (!string.IsNullOrEmpty(pilot.stop_reason))
                    status_bar.Warning(pilot.stop_reason);
                else
                    status_bar.Status("Landing autopilot not enabled");
            }

            //    UI_Tools.Console("SurfaceVelocity" + StrTool.VectorToString(SurfaceVelocity.vector));

            return true;
        }

    }
}
