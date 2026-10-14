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

        InfoTable landing_infos;
        public Label collision_value;
        public FullStatus status_bar;

        public ToggleButton run_button;

        public Button touch_down;

        WaypointPicker waypoints_vac;
        WaypointPicker waypoints_atmo;

        // Shown on the Atmo panel when Redux's prediction isn't set up the way atmospheric
        // precision landing needs (see UpdateAtmoGate).
        Label atmo_gate_label;

        // Precision Landing orbit gate (see UpdateOrbitGate below) - uses Circularize's own check,
        // so the warning and the refusal at Start can't disagree. Vacuum-only.
        Label orbit_gate_label;

        // Atmo/Vacuum profile panels, shown/hidden every UI tick by UpdateProfilePanels. Each is
        // bound in onInit() to its own settings object and never rebound, since K2Page.onInit()
        // only runs once per game session.
        VisualElement landing_mode_group_atmo;
        VisualElement landing_mode_group_vac;
        VisualElement atmo_settings_panel;
        VisualElement vac_settings_panel;

        public override bool onInit()
        {
            landing_infos = new InfoTable(panel.Q<VisualElement>("landing_infos"));
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
            // K2's grille lines cascade on/off with the autopilot, as on Node/Lift.
            pilot.is_running_event += is_running => status_bar.avatar?.SetRunning(is_running);
            run_button.listeners += v =>
            {
                pilot.isRunning = v;
                // Follows where the pilot ended up, in case it refused or stopped straight away.
                run_button.Value = pilot.isRunning;
                run_button.label = run_button.Value ? "Stop" : "Brake";
            };

            touch_down.listenClick(() => pilot.StartTouchDown());

            // A run keeps the precision choice it started with (see LandingPilot.run_precision).
            var precision_toggles = new[] { panel.Q<K2Toggle>("precision_landing_vac"), panel.Q<K2Toggle>("precision_landing_atmo") };
            pilot.is_running_event += is_running =>
            {
                foreach (var toggle in precision_toggles)
                    toggle?.SetEnabled(!is_running);
            };

            // Atmo/Vacuum profile panels, both in Landing.uxml (see the field comment above).
            landing_mode_group_atmo = panel.Q<VisualElement>("landing_mode_group_atmo");
            landing_mode_group_vac = panel.Q<VisualElement>("landing_mode_group_vac");
            atmo_settings_panel = panel.Q<VisualElement>("atmo_settings_panel");
            vac_settings_panel = panel.Q<VisualElement>("vac_settings_panel");

            // Pass `panel` (the whole tab page), not the settings sub-panels: these setups also
            // reach elements outside the ADVANCED foldout (precision_landing_vac,
            // target_settings_vac, the orbit gate label - see Landing.uxml). Q<T>() only searches
            // under the root it's given, so a narrower root returns null, and Bind() on it throws
            // up through K2D2Window.OnUiReload, aborting every other tab's Init() too.
            pilot.settings_vac.setupUI(panel);
            pilot.brake.SetupFullUI(panel);

            pilot.settings_atmo.setupBasicUI(panel);
            pilot.brake.SetupBasicUI(panel);

            // Resets both profiles, not just the one showing (see addLandingResetButton).
            addLandingResetButton(panel.Q<Foldout>("advanced_foldout"));

            orbit_gate_label = panel.Q<Label>("precision_landing_orbit_gate_vac");

            return true;
        }

        // Like K2Page.addResetButton, but resets both the land_atmo and land_vac SettingsFile
        // instances. That shared helper only knows SettingsFile.Instance.
        void addLandingResetButton(VisualElement parent)
        {
            if (parent == null)
                return;

            Button reset_bt = new Button() { text = "Reset" };
            reset_bt.name = "reset_advanced";
            reset_bt.AddToClassList("k2-button");
            reset_bt.style.marginTop = 8;

            parent.Add(reset_bt);

            reset_bt.RegisterCallback<ClickEvent>(evt =>
            {
                SettingsFile.Get("land_atmo")?.Reset("land");
                SettingsFile.Get("land_vac")?.Reset("land");
            });
        }

        // Shows one of each profile pair (landing_mode_group_*/*_settings_panel), based on
        // LandingProfile.IsAtmospheric. Refreshed every tick, so a body change while this tab is
        // open switches panels without reopening it.
        void UpdateProfilePanels()
        {
            bool atmo = LandingProfile.IsAtmospheric;
            landing_mode_group_atmo.Show(atmo);
            landing_mode_group_vac.Show(!atmo);
            atmo_settings_panel.Show(atmo);
            vac_settings_panel.Show(!atmo);
        }

        // Reading Redux's waypoints goes through reflection, so a couple of times a second is
        // plenty to notice a waypoint being added or moved.
        const float waypoint_refresh_s = 0.5f;
        float next_waypoint_refresh;

        void buildWaypointList()
        {
            if (UnityEngine.Time.unscaledTime < next_waypoint_refresh)
                return;
            next_waypoint_refresh = UnityEngine.Time.unscaledTime + waypoint_refresh_s;

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
            if (problem == null)
            {
                // Not a settings problem, so the toggle stays on: just says up front why Start will
                // refuse while the vessel is still in orbit.
                bool in_orbit = !LandingPilot.atmo_landing_from_orbit && !pilot.isRunning
                    && pilot.settings_atmo.precision_landing.V && pilot.LandsFromOrbit();
                atmo_gate_label.Show(in_orbit);
                if (in_orbit)
                    atmo_gate_label.text = LandingPilot.from_orbit_unavailable;
                return;
            }
            atmo_gate_label.Show(true);

            atmo_gate_label.text = problem;
            if (!pilot.isRunning && pilot.settings_atmo.precision_landing.V)
                pilot.settings_atmo.precision_landing.V = false;
        }

        // Always-visible "am I about to hit something" readout, next to the Brake/Touch Down
        // buttons regardless of whether LANDING INFO below is expanded. Kept separate from
        // updateContext()/landing_infos so it never depends on that Foldout's collapsed state.
        void updateCollisionStatus()
        {
            collision_value.text = pilot.collision_detected ? "Detected" : "None detected";
        }

        // Precision Landing orbit gate: a label under the vacuum toggle saying the orbit is too high
        // to start from. The toggle itself is left alone - the orbit is often too high only for
        // now (still in the atmosphere of the body being left, or on the transfer), and Start is
        // refused by Circularize if it still is then. Checked every tick, since the status lines
        // are rebuilt every tick.
        void UpdateOrbitGate()
        {
            // Circularize.CheckOrbit() doesn't check for a missing VesselComponent, and this runs
            // every UI tick whatever the game state.
            if (pilot.current_vessel?.VesselComponent == null)
            {
                orbit_gate_label.Show(false);
                return;
            }

            var check = Circularize.CheckOrbit(out double apoapsisAlt_m, out double periapsisAlt_m);
            // Once running, the orbit is expected to move around.
            bool too_high = !pilot.isRunning && pilot.settings_vac.precision_landing.V && check == Circularize.OrbitCheck.TooHigh;

            orbit_gate_label.Show(too_high);
            if (!too_high)
                return;

            orbit_gate_label.text = $"Needs a starting orbit within {Circularize.max_starting_altitude_m / 1000:n0}km " +
                $"(currently Ap {apoapsisAlt_m / 1000:n0}km / Pe {periapsisAlt_m / 1000:n0}km) - circularize lower first.";
        }

        public void updateContext()
        {
            landing_infos.Begin();

            landing_infos.Add("Fall Speed", $"{pilot.current_falling_speed:n2} m/s");
            landing_infos.Add("Altitude", StrTool.DistanceToString(pilot.altitude));

            if (pilot.collision_detected)
            {
                landing_infos.Add("Collision In", StrTool.DurationToString(pilot.adjusted_collision_UT - GeneralTools.Game.UniverseModel.UniverseTime));
                // Airless burn timing: atmospheric precision landing plans its burn with drag.
                if (!pilot.atmo_precision)
                    landing_infos.Add("Start Burn In", StrTool.DurationToString(pilot.startBurn_UT - GeneralTools.Game.UniverseModel.UniverseTime));

                // Where the vessel would land with the engine off - meaningless once the guided
                // descent is burning, which shows its own distance to the target instead.
                bool guided_burn = (pilot.vacuum_precision || pilot.atmo_precision) && pilot.mode == LandingPilot.Mode.Brake;
                if (pilot.settings.precision_landing.V && !guided_burn)
                    landing_infos.Add("Target Error", StrTool.DistanceToString((float)pilot.target_error_m));

                // The rest is for tuning and bug reports. Predicted Landing lets the prediction be
                // checked against where the ship actually comes down; Prediction says which
                // predictor produced it.
                if (K2D2Settings.debug_mode.V)
                {
                    if (!pilot.atmo_precision)
                    {
                        landing_infos.Add("Collision Speed", $"{pilot.speed_collision:n2} m/s");
                        landing_infos.Add("Burn Duration", $"{pilot.burn_duration:n2} s");
                    }
                    landing_infos.Add("Predicted Landing", $"{pilot.predicted_landing_lat:n2}, {pilot.predicted_landing_lon:n2}");
                    landing_infos.Add("Prediction", pilot.using_redux_prediction ? "Redux" : "K2D2");
                }
            }

            // Telemetry from whichever sub-controller is driving the vessel (TouchDown's Max/Delta
            // Speed while braking) goes into this table rather than the console line, as on Lift.
            if (pilot.isRunning)
            {
                pilot.current_executor?.UpdateInfoRows(landing_infos.Add);

                if (isRunning && K2D2Settings.debug_mode.V && pilot.burn_dV.burned_dV > 0)
                    landing_infos.Add("Burned", $"{pilot.burn_dV.burned_dV:n1} m/s");
            }
            landing_infos.End();
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

            // Not gated on collision_detected: it can go false mid-descent, and that would freeze
            // the Touch Down button and the status text.
            var state = GeneralTools.Game.GlobalGameState.GetState();
            if (state != GameState.FlightView)
            {
                status_bar.Console("Landing is only available in flight view");
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
                    case LandingPilot.Mode.BoostBack:
                        status_bar.Status("Boost-back");
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
                        status_bar.Warning(pilot.vacuum_precision ? "Powered Descent" : pilot.atmo_precision ? "Landing Burn" : "Brake !");
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
                if (!string.IsNullOrEmpty(pilot.stop_reason))
                    status_bar.Warning(pilot.stop_reason);
                else
                    status_bar.Status("Landing autopilot not enabled");
            }

            return true;
        }

    }
}
