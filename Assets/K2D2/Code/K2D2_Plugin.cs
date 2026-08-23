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
            _assemblyFolder ??= Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

        private static string _settingsPath;
        private static string SettingsPath =>
            _settingsPath ??= Path.Combine(AssemblyFolder, "k2d2_settings.json");


        /// Singleton instance of the plugin class
        [PublicAPI] public static K2D2_Plugin Instance { get; set; }

        // AppBar button IDs
        internal const string ToolbarFlightButtonID = "BTN-K2D2Flight";
        internal const string ToolbarOabButtonID = "BTN-K2D2OAB";
        internal const string ToolbarKscButtonID = "BTN-K2D2KSC";

        public static ILogger logger;

        public KSPVessel current_vessel = new KSPVessel();

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
            // SWMetadata.Folder is a System.IO.DirectoryInfo, not a string (confirmed via the current
            // SpaceWarpPluginDescriptor's real field type) - string-concatenating it works today via
            // DirectoryInfo's implicit ToString(), but .FullName is the correct, intended accessor.
            //
            // AssetsLoader.Bundle = AssetBundle.LoadFromFile(SWMetadata.Folder.FullName + "/assets/bundles/k2d2_ui.bundle");
            //
            // Switched UI loading from the old prebuilt AssetBundle above to Redux's Addressables system -
            // see AssetsLoader.LoadUxml() for the new loading call and the reasoning. The line above is
            // commented out (not deleted) so reverting is a one-line job if Addressables doesn't pan out
            // in testing; k2d2_ui.bundle itself is left untouched on disk either way.

            // Manually register K2UI's custom controls' UxmlFactory instances with Unity's internal
            // VisualElementFactoryRegistry - see KTools/K2UIFactoryRegistration.cs for the full story
            // on why this is needed (Unity's automatic factory discovery doesn't pick up custom
            // controls shipped in a BepInEx-loaded mod DLL). Must run before any UXML referencing
            // K2UI's custom elements is loaded/cloned, so it happens first thing here.
            KTools.K2UIFactoryRegistration.RegisterAll();

            var k2D2PilotsMgr = new K2D2PilotsMgr();
            SettingsFile.Init(this, SettingsPath);
        
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

        // AssetsLoader.LoadUxml() (a separate static class, not a KerbalMod subclass) needs to call
        // Assets.LoadAssetAsync<T>() to load UI Toolkit assets via Addressables, but the base
        // KerbalMonoBehaviour.Assets property is `protected` - confirmed via CS0122 build error - so it's
        // not reachable from outside a KerbalMod subclass's own code, even through an instance reference
        // like K2D2_Plugin.Instance.Assets. This thin public wrapper exposes just the one call
        // AssetsLoader needs without loosening protection on anything else.
        public AsyncOperationHandle<T> LoadAddressableAsset<T>(string address)
        {
            return Assets.LoadAssetAsync<T>(address);
        }


        private static GameState[] validScenes = { GameState.FlightView, GameState.Map3DView };

        //private static GameState last_game_state ;

        private static bool ValidScene()
        {
            if (GeneralTools.Game == null) return false;

            // FIXED during the Redux port verification pass: KSP.Game.GameStateMachine has no GetState()
            // method in the current Redux assemblies (confirmed via full member enumeration of
            // GameStateMachine in Assembly-CSharp.dll - no such member exists, and no unrelated overload
            // matches either). The real accessor is GetGameState(), which returns a GameStateConfiguration
            // struct whose public GameState field is what this code actually wants. Without this fix,
            // ValidScene() - which gates every per-frame pilot update - would throw a MissingMethodException
            // the instant it ran.
            var state = GeneralTools.Game.GlobalGameState.GetGameState().GameState;
            bool is_valid = validScenes.Contains(state);
            if (!is_valid)
            {
                ResetControllers();
            }
            return is_valid;
        }

        void Update()
        {
            // main_ui?.Update();

            Debug.developerConsoleVisible = false;
            // Update Models (even on non valid scenes)
            current_vessel.Update();

            /* TODO: Other mods interfacing
            if (K2D2OtherModsInterface.instance == null)
            {
                var other_mods = new K2D2OtherModsInterface();
                other_mods.CheckModsVersions();
            }
            */

            if (ValidScene())
            {
                // Debug.developerConsoleVisible = false;
                if (Input.GetKey(KeyCode.LeftAlt) && Input.GetKeyDown(KeyCode.O))
                    main_window.IsWindowOpen = !main_window.IsWindowOpen;

                StagingPilot.Instance.Update();

                if (!StagingPilot.Instance.is_staging)
                {
                    // Update Controllers only if staging is not in progress
                    pilots_manager.UpdateControllers();
                }   
            }
            else
            {
                if (main_window != null && main_window.IsWindowOpen)
                    main_window.IsWindowOpen = false;
            }
        }

        // call on reset on controller, each on can reset it's status
        public static void ResetControllers()
        {
            if (!loaded) return;
            StagingPilot.Instance.onReset();
            Instance.pilots_manager.onReset(); 
        }

        public bool settings_visible = false;

        public PilotsManager pilots_manager = new PilotsManager();

        void FixedUpdate()
        {
            if (ValidScene())
            {
                pilots_manager.FixedUpdateControllers();
            }
        }

        private void LateUpdate()
        {
            if (ValidScene())
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
