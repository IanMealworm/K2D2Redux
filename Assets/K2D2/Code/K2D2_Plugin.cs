using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using JetBrains.Annotations;
using SpaceWarp2.UI.API.Appbar;
using K2D2.UI;
using UitkForKsp2.API;
using UnityEngine;
using KTools;
using K2D2.KSPService;
using KSP.Game;
using KSP.Messages;
using K2D2.Controller;

using K2D2.Lift;
using K2D2.Landing;
using K2D2.Node;
using Redux.ExtraModTypes;
using UnityEngine.ResourceManagement.AsyncOperations;
using ILogger = ReduxLib.Logging.ILogger;

namespace K2D2
{
    internal class L
    {
        public static void Log(string txt)
        {
            K2D2_Plugin.logger.LogInfo(txt);
        }

        public static void Vector3(string label, Vector3 value)
        {
            K2D2_Plugin.logger.LogInfo(label + " : " + StrTool.Vector3ToString(value));
        }
    }

    public class K2D2_Plugin : KerbalMod
    {


        private static string _assemblyFolder;
        private static string AssemblyFolder =>
            // Redux loads mods from a byte stream rather than a file on disk, so Assembly.Location
            // is always empty here - GetLoadedAssemblyPath() is Unity's replacement that works
            // under that loading model.
            _assemblyFolder ??= Path.GetDirectoryName(Assembly.GetExecutingAssembly().GetLoadedAssemblyPath());

        private static string _settingsPath;
        private static string SettingsPath =>
            _settingsPath ??= Path.Combine(AssemblyFolder, "k2d2_settings.json");

        // Landing's Atmo/Vacuum profile split (see LandingSettings.cs/KTools.SettingsFile.cs) -
        // two genuinely separate physical files, so tuning one profile can never touch the other's
        // saved values. Same AssemblyFolder-relative pattern as SettingsPath above.
        private static string _atmoLandingSettingsPath;
        private static string AtmoLandingSettingsPath =>
            _atmoLandingSettingsPath ??= Path.Combine(AssemblyFolder, "k2d2_landing_atmo.json");

        private static string _vacLandingSettingsPath;
        private static string VacLandingSettingsPath =>
            _vacLandingSettingsPath ??= Path.Combine(AssemblyFolder, "k2d2_landing_vac.json");


        /// Singleton instance of the plugin class
        [PublicAPI] public static K2D2_Plugin Instance { get; set; }

        // AppBar button IDs
        internal const string ToolbarFlightButtonID = "BTN-K2D2Flight";
        internal const string ToolbarOabButtonID = "BTN-K2D2OAB";
        internal const string ToolbarKscButtonID = "BTN-K2D2KSC";

        public static ILogger logger;

        [System.NonSerialized] public KSPVessel current_vessel = new KSPVessel();

        static bool loaded = false;

        K2D2Window main_window = null;

        public override void OnPreInitialized()
        {
            logger = SWLogger;
        }

        /// <summary>
        /// Runs when the mod is first initialized.
        /// </summary>
        public override void OnInitialized()
        {

            Instance = this;
            // UI loads through Redux's Addressables (see AssetsLoader.LoadUxml()). The prebuilt
            // AssetBundle line below is a fallback in case that has to be backed out.
            // SWMetadata.Folder is a System.IO.DirectoryInfo, not a string; .FullName gives its path.
            //
            // AssetsLoader.Bundle = AssetBundle.LoadFromFile(SWMetadata.Folder.FullName + "/assets/bundles/k2d2_ui.bundle");

            // No UI factory registration is needed here: K2UI controls use [UxmlElement]/[UxmlAttribute],
            // and Redux registers those for mod assemblies.

            var k2D2PilotsMgr = new K2D2PilotsMgr();
            SettingsFile.Init(this, SettingsPath);

            // Landing's Atmo and Vacuum settings files, loaded before LandingPilot's constructor
            // builds its two LandingSettings instances.
            SettingsFile.GetOrCreate("land_atmo", this, AtmoLandingSettingsPath);
            var landVacFile = SettingsFile.GetOrCreate("land_vac", this, VacLandingSettingsPath);

            // Older installs kept Landing's settings in the main file (k2d2_settings.json) under
            // "land." keys. They're copied into the Vacuum file, the profile they were tuned for.
            // CopyMissingKeys only fills keys land_vac doesn't have yet, so after the first launch
            // this does nothing, and land_atmo is never touched.
            int migrated = landVacFile.CopyMissingKeys(SettingsFile.Instance, "land.");
            if (migrated > 0)
                logger.LogInfo($"[K2D2_Plugin] Migrated {migrated} existing Landing setting(s) from the main settings file into k2d2_landing_vac.json.");

            gameObject.hideFlags = HideFlags.HideAndDontSave;
            DontDestroyOnLoad(gameObject);
            RegisterMessages();

            // create staging 
            new StagingPilot();

            pilots_manager.AddPilot(new NodeExPilot());
            pilots_manager.AddPilot(new LiftPilot());
            pilots_manager.AddPilot(new LandingPilot());
            pilots_manager.AddPilot(new DockingPilot());
            pilots_manager.AddPilot(new AttitudePilot());

            // pilots_manager.AddPilot(new DronePilot());
            // pilots_manager.AddPilot(new AttitudePilot());
            // pilots_manager.AddPilot(new LiftController());
     
            // controllerManager.AddController(new WarpController());
            // pilots_manager.AddPilot(new DockingAssist());

            // Load the UI from the asset bundle
            var myFirstWindowUxml = AssetsLoader.LoadUxml("K2D2_Window.uxml");

            // Create the window options object
            var windowOptions = new WindowOptions
            {
                // The ID of the window. It should be unique to your mod.
                WindowId = "K2D2",
                // The transform of parent game object of the window.
                // If null, it will be created under the main canvas.
                Parent = null,
                // Whether or not the window can be hidden with F2.
                IsHidingEnabled = true,
                // Whether to disable game input when typing into text fields.
                DisableGameInputForTextFields = true,
                MoveOptions = new MoveOptions
                {
                    // Whether or not the window can be moved by dragging.
                    IsMovingEnabled = false,
                    // Whether or not the window can only be moved within the screen bounds.
                    CheckScreenBounds = true
                }
            };

            // Create the window
            var k2d2_window = Window.Create(windowOptions, myFirstWindowUxml);
            // Add a controller for the UI to the window's game object
            main_window = k2d2_window.gameObject.AddComponent<K2D2Window>();

            // Register Flight AppBar button
            Appbar.RegisterAppButton(
                SWMetadata.Name,
                ToolbarFlightButtonID,
                AssetsLoader.LoadIcon("icon.png"),
                isOpen => main_window.IsWindowOpen = isOpen
            );

            // Register OAB AppBar Button
            // Appbar.RegisterOABAppButton(
            //     ModName,
            //     ToolbarOabButtonID,
            //     AssetManager.GetAsset<Texture2D>($"{ModGuid}/images/icon.png"),
            //     isOpen => myFirstWindowController.IsWindowOpen = isOpen
            // );

            // Register KSC AppBar Button
            // Appbar.RegisterKSCAppButton(
            //     ModName,
            //     ToolbarKscButtonID,
            //     AssetManager.GetAsset<Texture2D>($"{ModGuid}/images/icon.png"),
            //     () => myFirstWindowController.IsWindowOpen = !myFirstWindowController.IsWindowOpen
            // );



            loaded = true;
        }

        public override void OnPostInitialized()
        {
        }

        // AssetsLoader.LoadUxml() needs Assets.LoadAssetAsync<T>(), but KerbalMonoBehaviour.Assets
        // is `protected`, so it can't be reached from outside a KerbalMod subclass, even through
        // K2D2_Plugin.Instance.Assets. This wrapper exposes just that one call.
        public AsyncOperationHandle<T> LoadAddressableAsset<T>(string address)
        {
            return Assets.LoadAssetAsync<T>(address);
        }


        // Pilots keep flying in these views (photo mode included, so taking a screenshot doesn't
        // stop a landing); the window only shows in the first two.
        private static readonly GameState[] flight_scenes = { GameState.FlightView, GameState.Map3DView, GameState.PhotoMode };
        private static readonly GameState[] window_scenes = { GameState.FlightView, GameState.Map3DView };

        // Worked out once per frame in Update; FixedUpdate and LateUpdate use the same answer.
        static bool in_flight, window_allowed;

        // Pilots are reset once when leaving flight, not every frame (and every physics step) after.
        static void UpdateScene()
        {
            // Game and its state machine aren't set up yet for the first few frames after loading.
            var game_state = GeneralTools.Game?.GlobalGameState?.GetGameState();
            bool was_in_flight = in_flight;
            if (game_state == null)
            {
                in_flight = window_allowed = false;
            }
            else
            {
                // GameStateMachine has no GetState() method on Redux's current assemblies; the
                // current GameState comes from GetGameState().GameState instead.
                var state = game_state.GameState;
                in_flight = flight_scenes.Contains(state);
                window_allowed = window_scenes.Contains(state);
            }
            if (was_in_flight && !in_flight)
                ResetControllers();
        }

        void Update()
        {
            // main_ui?.Update();

            UpdateScene();

            // Update Models (even on non valid scenes)
            current_vessel.Update();

            /* TODO: Other mods interfacing
            if (K2D2OtherModsInterface.instance == null)
            {
                var other_mods = new K2D2OtherModsInterface();
                other_mods.CheckModsVersions();
            }
            */

            if (in_flight)
            {
                if (window_allowed && Input.GetKey(KeyCode.LeftAlt) && Input.GetKeyDown(KeyCode.O))
                    main_window.IsWindowOpen = !main_window.IsWindowOpen;

                StagingPilot.Instance.Update();

                if (!StagingPilot.Instance.is_staging)
                {
                    // Update Controllers only if staging is not in progress
                    pilots_manager.UpdateControllers();
                }   
            }
            // Hidden in photo mode and outside flight, and opened again on the way back if it was
            // open.
            if (main_window != null)
            {
                if (!window_allowed && main_window.IsWindowOpen)
                {
                    main_window.IsWindowOpen = false;
                    window_hidden_by_scene = true;
                }
                else if (window_allowed && window_hidden_by_scene)
                {
                    main_window.IsWindowOpen = true;
                    window_hidden_by_scene = false;
                }
            }
        }

        bool window_hidden_by_scene;

        // call on reset on controller, each on can reset it's status
        public static void ResetControllers()
        {
            if (!loaded) return;
            StagingPilot.Instance.onReset();
            Instance.pilots_manager.onReset(); 
        }

        public bool settings_visible = false;

        [System.NonSerialized] public PilotsManager pilots_manager = new PilotsManager();

        void FixedUpdate()
        {
            if (in_flight)
            {
                pilots_manager.FixedUpdateControllers();
            }
        }

        private void LateUpdate()
        {
            if (in_flight)
            {
                pilots_manager.LateUpdateControllers();
            }
        }

        private void RegisterMessages()
        {
            Game.Messages.Subscribe<GameStateChangedMessage>(msg =>
            {
                var message = (GameStateChangedMessage)msg;

                // if (message.CurrentState == GameState.FlightView)
                // {
                //     ShapeDrawer.Instance.can_draw = true;
                // }
                // else if (message.PreviousState == GameState.FlightView)
                // {
                //     ShapeDrawer.Instance.can_draw = false;
                // }
            });

            Game.Messages.Subscribe<VesselChangedMessage>(msg =>
            {
                var message = (VesselChangedMessage)msg;
                ResetControllers();
            });
        }

        // Public API to enable or disable a Pilot / Page
        [PublicAPI] public bool isPilotEnabled(string pilotName)
        {
            return K2D2PilotsMgr.Instance.isPilotEnabled(pilotName);
        }

        [PublicAPI] public void EnableAllPilots(bool enabled)
        {
            K2D2PilotsMgr.Instance.EnableAllPilots(enabled);
        }

        [PublicAPI] public void EnablePilot(string pilotName, bool enabled)
        {
            K2D2PilotsMgr.Instance.EnablePilot(pilotName, enabled);
        }

        [PublicAPI] public List<string> GetPilotsNames()
        {
            return K2D2PilotsMgr.Instance.GetPilotsNames();
        }

        // Public API to perform a precision node execution using K2-D2
        [PublicAPI] public void FlyNode()
        {
            NodeExPilot.Instance.Start();
        }

        [PublicAPI] public void StopFlyNode()
        {
            NodeExPilot.Instance.Stop();
        }

        [PublicAPI] public bool IsFlyNodeRunning()
        {
            return NodeExPilot.Instance.isRunning;
        }

        // Public API to get the status of K2D2 (used by FlightPlan)
        [PublicAPI] public string GetStatus()
        {
            return NodeExPilot.Instance.ApiStatus();
        }
    }
}
