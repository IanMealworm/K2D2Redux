using System;
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
    /// <summary>
    /// The mod's null-safe logging shim, and the single place the PRODUCTION LOG LEVELS are defined.
    ///
    /// <para>
    /// <b>Policy (production, v1.2.1):</b> <see cref="Log"/> is diagnostic chatter and routes to
    /// <c>LogDebug</c>, which is invisible in a shipped game - <c>ReduxLib</c>'s filter defaults to
    /// <c>LogLevel.Info</c> and drops anything numerically above it, measured file-and-mirror in
    /// AGENTS.md §8. So the diagnostics stay in the source and in the binary for a future support
    /// pass, at no cost to a user's log. This is FlightPlan's convention - it carries 235
    /// <c>LogDebug</c> call sites against 286 <c>LogInfo</c>.
    /// </para>
    ///
    /// <para>
    /// <see cref="Info"/> is <b>reserved for deliberate production lines</b> - it is what a
    /// post-launch grep must be able to find. There is exactly ONE call site (the boot marker), and
    /// that restraint is the point: AGENTS.md §8 requires a positive marker so "loaded fine" stays
    /// distinguishable from "silently failed to load", and one line is enough to keep that property.
    /// Do not add a second without a reason that survives the question "would a user want this in
    /// their log?".
    /// </para>
    ///
    /// <para>
    /// <see cref="Warn"/> and <see cref="Error"/> are always visible. They are for problems only.
    /// </para>
    ///
    /// <para>
    /// <b>Why a shim at all:</b> `K2D2_Plugin.logger` is assigned by the loader in
    /// <c>OnPreInitialized()</c>, and several Unity callbacks (OnEnable, Update) can run before that -
    /// the window's whole boot path logs through this helper. A log call that dereferences a null
    /// logger THROWS, and a throw from inside a catch block escapes its own try/catch entirely
    /// (FlightPlan measured exactly that shape in its launch 15, where it silently killed two of
    /// three subscription families). So every method here is null-safe: it CANNOT throw, and a null
    /// logger makes it a silent no-op rather than an exception.
    /// </para>
    /// </summary>
    internal class L
    {
        public static void Log(string txt)
        {
            ILogger logger = K2D2_Plugin.logger;
            if (logger != null)
                logger.LogDebug(txt);
        }

        public static void Info(string txt)
        {
            ILogger logger = K2D2_Plugin.logger;
            if (logger != null)
                logger.LogInfo(txt);
        }

        public static void Warn(string txt)
        {
            ILogger logger = K2D2_Plugin.logger;
            if (logger != null)
                logger.LogWarning(txt);
        }

        public static void Error(string txt)
        {
            ILogger logger = K2D2_Plugin.logger;
            if (logger != null)
                logger.LogError(txt);
        }

        public static void Vector3(string label, Vector3 value)
        {
            ILogger logger = K2D2_Plugin.logger;
            if (logger != null)
                logger.LogDebug(label + " : " + StrTool.Vector3ToString(value));
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

        public KSPVessel current_vessel = new KSPVessel();

        static bool loaded = false;

        // F5(b): "OnInitialized() has run far enough that the per-frame paths below can be trusted".
        // Unity ticks Update() from the frame the plugin's GameObject exists, which can be many
        // frames / seconds before the loader calls OnInitialized - FlightPlan measured 241 frames
        // (~11 s) of ticking first, every one of them throwing from its own Update(). Before this
        // flag, K2-D2's Update() had no such guard at all.
        //
        // Set at the TOP of OnInitialized() and never cleared - deliberately, following FlightPlan's
        // shape (FlightPlanPlugin.cs:186-190): a throw later in that body must not be able to leave
        // the mod with a permanently muted Update(). The cost of that choice is that Update() may
        // run against a half-built mod if OnInitialized throws, so every path in Update() that needs
        // something OnInitialized creates is null-guarded instead.
        static bool _initialized;

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
            // F5(b): from this point the per-frame paths are allowed to run - Update() returns while
            // this is false. Set FIRST, on purpose (see the field's comment): a throw later in this
            // body must not leave the mod permanently muted, so the per-frame paths are null-guarded
            // rather than gated on the end of this method.
            _initialized = true;

            Instance = this;
            // SWMetadata.Folder is a System.IO.DirectoryInfo, not a string (confirmed via the current
            // SpaceWarpPluginDescriptor's real field type) - string-concatenating it works today via
            // DirectoryInfo's implicit ToString(), but .FullName is the correct, intended accessor.
            //
            // Backported to Redux 0.2.8.5: load the UI from the proven prebuilt AssetBundle. The
            // Addressables-based LoadAddressableAsset<T>() wrapper below is kept in the file (it compiles
            // against 0.2.8.5 and keeps a later optional experiment cheap) but is deliberately not used
            // for this build's UI loading - see AssetsLoader.LoadUxml() and
            // Deploy/obj/addressables-verdict.md for why the bundle path is the one that ships.
            AssetsLoader.Bundle = AssetBundle.LoadFromFile(SWMetadata.Folder.FullName + "/assets/bundles/k2d2_ui.bundle");

            // K2UIFactoryRegistration.RegisterAll() used to go here - a reflection-based workaround
            // that manually registered every K2UI custom control's legacy UxmlFactory with Unity's
            // internal VisualElementFactoryRegistry, needed because Unity's automatic factory scan
            // never recognized a BepInEx-loaded mod DLL as a "user assembly". That whole class is
            // gone now: every K2UI control moved to [UxmlElement]/[UxmlAttribute] (Unity 6.6 removes
            // UxmlFactory entirely, so this was happening either way), and the newer attribute-based
            // registration is handled by Redux itself for mod assemblies - no manual step needed.

            var k2D2PilotsMgr = new K2D2PilotsMgr();
            SettingsFile.Init(this, SettingsPath);

            // Landing's Atmo/Vacuum profile split - two independent files, loaded up front here
            // (same as the main settings file above) so both are ready before LandingPilot's
            // constructor builds its two LandingSettings instances.
            SettingsFile.GetOrCreate("land_atmo", this, AtmoLandingSettingsPath);
            var landVacFile = SettingsFile.GetOrCreate("land_vac", this, VacLandingSettingsPath);

            // One-time migration: before this split, Landing's settings lived in the main file
            // (SettingsFile.Instance/k2d2_settings.json) under "land." keys - copy whatever's
            // already tuned there into the new VACUUM file specifically (not land_atmo), since
            // that's the profile that's actually been flown/tuned so far. CopyMissingKeys only
            // ever fills in keys land_vac doesn't already have, so this is a no-op on every launch
            // after the first (see its own comment in SettingsFile.cs) - never overwrites anything
            // tuned in land_vac since the split happened, and never touches land_atmo at all.
            int migrated = landVacFile.CopyMissingKeys(SettingsFile.Instance, "land.");
            if (migrated > 0)
                logger.LogInfo($"[K2D2_Plugin] Migrated {migrated} existing Landing setting(s) from the main settings file into k2d2_landing_vac.json.");

            gameObject.hideFlags = HideFlags.HideAndDontSave;
            DontDestroyOnLoad(gameObject);

            // F6: every game-message subscription now lives in SubscribeGameMessages(), which is
            // idempotent per MessageCenter *instance* AND per family, and re-runs from Update()
            // whenever the game replaces the centre (a save reload does exactly that). The old
            // RegisterMessages() bound inline lambdas with no stored handles, so they could not be
            // re-added at all.
            SubscribeGameMessages("OnInitialized");

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

            // F4: rebased on WindowOptions.Default - the shape MicroEngineer uses
            // (mods/MicroEngineer/Assets/MicroEngineer/Code/UI/Uxmls.cs:82-94, InstantiateWindowOptions)
            // and the one FlightPlan was fixed to (FlightPlanPlugin.cs:259-281). The old code built a
            // ZERO-INITIALISED struct, which silently left UseStockScale, BringToFrontOnPointerDown,
            // BlockGameInput and the whole ResizeOptions struct at `false`/unset. Verified from the
            // shipped UitkForKsp2.dll: WindowOptions::get_Default sets IsHidingEnabled / UseStockScale /
            // DisableGameInputForTextFields / BringToFrontOnPointerDown / BlockGameInput true and copies
            // MoveOptions.Default + ResizeOptions.Default (monodis IL, P1 recon section 7.1 items 2-4).
            //
            // This is a RECONCILIATION, not a swap: every field is accounted for below, and the two
            // that must not inherit `Default` are re-asserted with the reason they exist.
            WindowOptions windowOptions = WindowOptions.Default;

            // Default => null. Without an id the window has none, and the AppBar/dialog plumbing has
            // nothing to key on. MANDATORY re-assertion.
            windowOptions.WindowId = "K2D2";
            // Default => null. Agrees with the value the old struct set; stated so the reconciliation
            // is complete (null = the window is created under the game's main canvas).
            windowOptions.Parent = null;
            // Default => true. This is the game's own F2 hide path: EnableHiding() adds a
            // HideManipulator that writes `style.visibility` on the window root (IL of the shipped
            // UitkForKsp2.dll). It is unchanged by this rebase and stays.
            windowOptions.IsHidingEnabled = true;
            // Default => true. NEW behaviour vs the old zero-init struct - launch-observable, see the
            // P3 evidence file section 1.
            windowOptions.UseStockScale = true;
            // Default => true. Agrees with the old struct (the numeric fields rely on it).
            windowOptions.DisableGameInputForTextFields = true;
            // Default => true. NEW behaviour, and the one that matters most: on every pointer-down
            // this raises the window's UI Toolkit panel above anything whose sorting order does not
            // also climb. Measured in the shipped UitkForKsp2.dll:
            // OrderManipulator.OnPointerDown -> OrderManager.BringToFront(PanelSettings) ->
            // `panelSettings.sortingOrder = OrderManager.Next()`, where Next() is a static
            // post-incrementing counter (_top++, with a RenumberAll() wrap guard at 1000000).
            // That behaviour is wanted - it is what stops the window sitting behind other UI - and it
            // is precisely why F7's ESC suppression exists in the same commit:
            // FlightPlanPlugin.cs:613-618 states the coupling and names K2-D2.
            windowOptions.BringToFrontOnPointerDown = true;
            // Default => true. NEW behaviour: the GameInputBlockManipulator holds a reference-counted
            // game-input lock while the pointer interacts with the window and releases it on leave.
            // Measured: AcquireLock -> UitkForKsp2.API.Extensions.SetGameInputDisabled(owner, true)
            // (an owner HashSet, so other mods' UIs share one lock) -> ReduxLib's
            // IInputManager.SetUitkInputLocks() -> Redux.ApiImpls.ReduxInputManager.SetUitkInputLocks()
            // (Assembly-CSharp method 20006), which sets the game's current input lock to
            // InputLocks.GlobalInputDisabled and snapshots the previous enabled-states to restore.
            // That is a KSP.Input.InputDefinition / ToggleableInputAction lock, NOT UnityEngine.Input:
            // K2-D2's own hotkeys are polled directly with UnityEngine.Input in two places -
            // LeftAlt+O (K2D2_Plugin.Update) and LeftControl+O (NodeExPilot.UpdateUI) - so they are
            // not routed through the game's input definitions. Launch-observable, see the P3 evidence.
            windowOptions.BlockGameInput = true;

            // MANDATORY re-assert. `MoveOptions.Default` sets IsMovingEnabled = TRUE, and K2-D2
            // installs its OWN DragManipulator (K2D2Window.BindUi, kept for the horizontal drag-bounds
            // fix in Deploy/obj/launch-6-drag-bounds.md). Inheriting `true` would put the library's
            // built-in mover AND the custom manipulator on the same element, fighting over the
            // position on every pointer move - a new stutter that looks exactly like the freeze this
            // pipeline removes. FlightPlan can leave it true precisely because it has no custom
            // manipulator (FlightPlanPlugin.cs:279). Never set this true while DragManipulator is
            // installed.
            windowOptions.MoveOptions = new MoveOptions
            {
                IsMovingEnabled = false,
                CheckScreenBounds = true
            };

            // Default => ResizeOptions.Default: IsResizingEnabled = false (correct - the custom
            // ResizeManipulator owns resizing), CheckScreenBounds = true, MinWidth/MinHeight = 0.0f.
            // Stated explicitly for the reconciliation. The zero minimums mean there is nothing to
            // fight the custom manipulator's own clamps with (P1 recon section 7.1 item 4), so this
            // keeps the "unset" semantics the old struct had.
            windowOptions.ResizeOptions = ResizeOptions.Default;

            // Create the window. In this Redux runtime Window.Create returns the window's PanelRenderer
            // (see K2D2Window._panel's comment) - hand that straight to the controller so it never has to
            // search for the component itself. Initialize(PanelRenderer) registers the UI-ready callback
            // on it, which is how the controller gets the root VisualElement (UIDocument is the superseded
            // shape and is not what this overload returns at this pin).
            var k2d2_window = Window.Create(windowOptions, myFirstWindowUxml);
            // Add a controller for the UI to the window's game object
            main_window = k2d2_window.gameObject.AddComponent<K2D2Window>();
            main_window.Initialize(k2d2_window);

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

            // ============================ the ONE production log line ============================
            // Deliberate, and deliberately alone. Everything else this mod logs is either a warning/
            // error or routes to LogDebug via L.Log (see the L class for the policy). This exists
            // because AGENTS.md §8 requires a POSITIVE marker: a grep for failure markers cannot tell
            // "loaded fine" from "silently never loaded", and both look identical in a log with no
            // errors in it.
            //
            // It is at the END of OnInitialized, after every pilot, the bundle, the window and the
            // subscriptions are wired, so its ABSENCE is itself the diagnostic: a partial load logs
            // the throw that stopped it and no marker.
            //
            // The version comes from SWMetadata.SWInfo.Version - the same accessor AboutUI.cs:31 uses,
            // so it is compile-proven against the installed runtime - which makes swinfo.json the single
            // source of truth and stops the line drifting from the shipped version. It is read inside a
            // guard because this is a production boot path and a log line must never be the thing that
            // throws; the catch names the failure rather than swallowing it silently.
            string version;
            try
            {
                version = SWMetadata.SWInfo.Version;
            }
            catch (Exception e)
            {
                version = "unknown (" + e.GetType().Name + ")";
            }
            L.Info("K2-D2 " + version + " loaded - pilots, UI bundle, window and game-message bindings OK");
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

        // F2(b): the game-state transition, edge-gated. ValidScene() is called from Update,
        // FixedUpdate and LateUpdate, and it reports "invalid" on every frame spent outside a valid
        // scene - so the invalidation has to fire once per transition, not once per call.
        static bool _scene_was_valid;

        static bool SceneResult(bool is_valid)
        {
            if (is_valid != _scene_was_valid)
            {
                _scene_was_valid = is_valid;
                K2UI.VisualElementExtension.InvalidateUiCaches();
            }
            return is_valid;
        }

        // F2(b)/F6: the game state ITSELF, edge-gated - the last observed GameState, or null before
        // the first sample. See ObserveGameState() below for why this exists next to SceneResult().
        static GameState? _observed_game_state;

        /// <summary>
        /// F2(b)/F6: observe the live game state and act once per <em>transition</em>.
        ///
        /// <para>
        /// This exists because the two obvious routes both fall short on 0.2.8.5.
        /// </para>
        ///
        /// <para>
        /// <b>Route 1 - the <c>GameStateChangedMessage</c> subscription - does not deliver.</b>
        /// Launch 3 measured it: five state transitions in the log
        /// (<c>[State] Swapping game state! prev: [...] --> new: [...]</c>, five times) and zero
        /// invocations of the handler. That is not "the message is never published" - it plainly is,
        /// because the game's own <c>GameStateMachine.OnStateChange</c> is a <em>subscriber</em> to
        /// it and logged all five. The publisher is <c>SimpleStateMachine.SetState</c>, and it
        /// publishes to a <b>cached field</b> (<c>SimpleStateMachine._messageCenter</c>) rather than
        /// to a freshly-read <c>Game.Messages</c> - proved by dumping the IL of
        /// <c>KSP.Game.SimpleStateMachine`1::SetState</c> and reading the <c>ldfld</c> that feeds
        /// <c>PublishStateChangedMessage</c>. Whatever instance that field holds, it is not the one
        /// this mod is subscribed to, and no amount of re-subscribing to <c>Game.Messages</c> can
        /// change that. So the message family is kept below for the day a build does deliver it, but
        /// nothing correctness-critical depends on it any more.
        /// </para>
        ///
        /// <para>
        /// <b>Route 2 - <see cref="SceneResult"/> - is real but partial.</b> It is the
        /// valid&lt;-&gt;invalid edge, and it has worked across three launches. It cannot fire for the
        /// transitions that do not change validity: FlightView&lt;-&gt;Map3DView are both valid, and
        /// MainMenu&lt;-&gt;WarmUpLoading are both invalid. Those are exactly the view swaps where a stale
        /// K2UI write cache is most visible, so the edge they sit on is the wrong edge.
        /// </para>
        ///
        /// <para>
        /// <b>This method is the state edge itself</b>, so every transition is a repaint boundary
        /// and a re-arm point. It is called from all three Unity ticks, which the edge gate makes
        /// harmless: the cost is one comparison per call and no work at all until the state moves.
        /// <c>GetState()</c> is a bare field read on the base class (<c>_currentState</c>), so even
        /// the read is free next to the <c>GetGameState()</c> call <see cref="ValidScene"/> already
        /// makes every frame.
        /// </para>
        ///
        /// <para>
        /// The accessor is <c>GetState()</c>, and it is <b>inherited</b> — which is the whole trap
        /// here. <c>KSP.Game.GameStateMachine</c>'s own method list (mlist 28492..28504) has
        /// <b>no <c>GetState</c></b>, and enumerating only that list is how three files in the rule
        /// layer conclude "absent in 0.2.8.5". It is inherited from the base
        /// <c>KSP.Game.SimpleStateMachine&lt;TEnum&gt;</c>, whose mlist 30237 is
        /// <c>instance default !TEnum GetState ()</c> — and with <c>TEnum = KSP.Game.GameState</c>
        /// that is exactly the enum this wants. It is doc-silent in the pinned set, so there is
        /// nothing to grep for: the only honest proof is the base class plus a compile.
        /// </para>
        /// </summary>
        private static void ObserveGameState(GameState state)
        {
            if (_observed_game_state.HasValue && _observed_game_state.Value == state)
                return;

            bool first_sample = !_observed_game_state.HasValue;
            _observed_game_state = state;

            // The first sample is not a transition - the mod is starting up inside whatever state
            // the game is already in. Acting here would be a spurious repaint at boot.
            if (first_sample)
                return;

            K2UI.VisualElementExtension.InvalidateUiCaches();

            // F6: a state transition is also the moment to re-check the message centre. The
            // per-frame poll in Update() already does this, so this is the same idempotent call on a
            // stronger trigger - it costs one ReferenceEquals unless the centre really did change.
            SubscribeGameMessages("game-state transition");

            L.Log("K2D2: game state -> " + state + "; UI write caches dropped, message centre re-checked");
        }

        /// <summary>
        /// F3: whether the window is currently open - for the per-frame UI paths that live outside
        /// K2D2Window. NodeExPilot.UpdateUI() is reached from pilots_manager.UpdateControllers(),
        /// not from the window's own Update(), so it needs its own gate. False until OnInitialized
        /// has created the window, and false again once the window's GameObject is destroyed.
        /// </summary>
        public static bool IsWindowOpen => Instance?.main_window != null && Instance.main_window.IsWindowOpen;

        //private static GameState last_game_state ;

        private static bool ValidScene()
        {
            // Both GeneralTools.Game and GameInstance.GlobalGameState are null during the early startup
            // scene (GameManager.Instance.Game only exists once CreateGameInstance has run, and
            // GlobalGameState is only assigned later still), and Update/FixedUpdate/LateUpdate all call
            // this - so without these guards the log fills with a NullReferenceException per frame until
            // the game instance is up. During that window the answer is simply "not a valid scene".
            //
            // HISTORICAL NOTE, and the reason the comment below is now worded carefully: an earlier
            // pass concluded from "full member enumeration of GameStateMachine" that no GetState()
            // existed, and wrote the port fix around GetGameState() alone. That conclusion was
            // WRONG, and the enumeration is why: GetState() is inherited from the base
            // KSP.Game.SimpleStateMachine<TEnum> (mlist 30237, `instance default !TEnum GetState ()`),
            // so it is absent from GameStateMachine's own mlist (28492..28504) while being perfectly
            // callable on an instance - and it returns KSP.Game.GameState directly, with no null path.
            // GetState() is used below and is proved by this build's loader pre-flight. The rule layer
            // carried the same false claim in three files until 2026-09-15. Lesson: enumerate the BASE
            // type before calling a member absent.
            var game = GeneralTools.Game;
            if (game == null || game.GlobalGameState == null)
            {
                ResetControllers();
                return SceneResult(false);
            }

            // GetGameState() is itself nullable, which the first version of this guard missed. Its IL
            // returns ldnull when GameStateMachine._possibleStates hasn't been populated yet, and again
            // when the current state isn't found in that list (after logging "Unable to find {0} for
            // current state"). GameStateConfiguration is a class, not a struct, so dereferencing the
            // result blindly threw the NullReferenceException-per-frame that these guards exist to
            // prevent - visible in Ksp2.log immediately after "Creating Game Instance completed". v1.2
            // had dropped this guard; it is restored here for 0.2.8.5.
            // F2(b)/F6: the state edge, read direct off the inherited GetState(). Deliberately placed
            // BEFORE the GetGameState() null-check below, so a transition is still observed in the
            // window where GetGameState() returns null ("Unable to find {0} for current state") - that
            // window is a state change too, and it is precisely the one the old message route missed.
            // This is the invalidation that actually fires; see ObserveGameState() for why the
            // GameStateChangedMessage subscription cannot be relied on for it.
            ObserveGameState(game.GlobalGameState.GetState());

            var state_config = game.GlobalGameState.GetGameState();
            if (state_config == null)
            {
                ResetControllers();
                return SceneResult(false);
            }

            bool is_valid = validScenes.Contains(state_config.GameState);
            if (!is_valid)
            {
                ResetControllers();
            }
            return SceneResult(is_valid);
        }

        void Update()
        {
            // F5(b): no frame may run half-initialised work. Unity ticks Update() from the frame the
            // plugin's GameObject exists, which is before the loader calls OnInitialized() and can be
            // many frames/seconds earlier - and every path below needs Instance, the pilots or the
            // window. _initialized is set at the top of OnInitialized() and never cleared, so once
            // the mod is up this return cannot fire again (the F6 re-arm poll below depends on that).
            if (!_initialized)
                return;

            // main_ui?.Update();

            Debug.developerConsoleVisible = false;
            // Update Models (even on non valid scenes)
            current_vessel.Update();

            // F6, the cheap half: a save reload replaces the game session's MessageCenter, orphaning
            // every handler bound to the old instance. One ReferenceEquals per frame is all it takes
            // to notice; the actual re-subscription inside SubscribeGameMessages is transition-gated
            // and logs only when it does work.
            SubscribeGameMessages("poll detected change");

            /* TODO: Other mods interfacing
            if (K2D2OtherModsInterface.instance == null)
            {
                var other_mods = new K2D2OtherModsInterface();
                other_mods.CheckModsVersions();
            }
            */

            if (ValidScene())
            {
                // F5(b): main_window is only non-null once OnInitialized has created it, and
                // StagingPilot.Instance only once its constructor has run. Both are guarded because
                // _initialized is set before either exists (see the field's comment) - a half-built
                // mod must be inert, not half-ticking.
                if (main_window != null &&
                    Input.GetKey(KeyCode.LeftAlt) && Input.GetKeyDown(KeyCode.O))
                    main_window.IsWindowOpen = !main_window.IsWindowOpen;

                if (StagingPilot.Instance != null)
                {
                    StagingPilot.Instance.Update();

                    if (!StagingPilot.Instance.is_staging)
                    {
                        // Update Controllers only if staging is not in progress
                        pilots_manager.UpdateControllers();
                    }
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

        // ==================== F6: MessageCenter re-arm on instance replacement ====================
        // A save reload (ESC -> Load Game while in flight) tears down and replaces the game
        // session's MessageCenter. Every subscription made on the OLD instance is silently orphaned
        // - the handlers are still registered, on a centre the game no longer publishes to - so the
        // mod goes quiet for the rest of the session with no error anywhere. FlightPlan measured
        // exactly that (its launch 13: not one message delivered across two reloads and ~11
        // minutes) and this is its fix, ported.
        //
        // LAUNCH-3 REALITY CHECK on that premise - read this before trusting the re-arm. It has
        // never fired for K2-D2: not one "changed" line in three launches, including one with a full
        // ESC -> Load Game -> save -> ~30 s flight. What launch 3 proved instead is different and
        // worse: the game's state machine publishes GameStateChangedMessage to a CACHED field
        // (SimpleStateMachine._messageCenter, fed by SetState), and that is NOT the instance
        // Game.Messages hands back. A mod can therefore be subscribed to the "right" centre and
        // still receive nothing, with no instance change for an equality poll to notice. Keep the
        // re-arm - it is correct, cheap, and FlightPlan-measured - but it is NOT the load-safety net
        // it was believed to be. ObserveGameState()'s state poll is what actually carries state
        // changes now.
        //
        // The two subscriptions this replaces were INLINE LAMBDAS with no stored handles, so they
        // could not be unsubscribed or re-added even in principle. They are named methods now.
        //
        // NEVER unsubscribe from the OLD centre: after a reload it is already dead, and touching a
        // stale instance is how this fix would introduce its own crash. Re-subscription targets only
        // the new instance; the old one is abandoned whole (its handlers can never fire again).
        //
        // NOT a justification for this fix: "a throwing subscriber aborts the Publish dispatch". The
        // object-typed Publish(Type, MessageCenterMessage) in the installed 0.2.8.5 assembly has
        // 1 .try + 1 catch(Exception) that logs via Debug.LogException and CONTINUES the loop (P1
        // recon section 7.2). The instance replacement above is the whole reason this exists.

        // The centre the families below are bound to. Compared with ReferenceEquals, never with ==.
        private static MessageCenter _subscribedCenter;

        // F5(c): "bound" has to mean THIS centre AND every family. A partial bind - one family
        // subscribed, a later one throwing - must stay retryable, because marking the centre bound
        // is exactly what would stop the retry forever. FlightPlan's launch-15 defect: its logger
        // was still null, the first frame's `_subscribedCenter = messages` ran, family 1's
        // subscribes succeeded, then the log call inside the try threw; the catch's own log threw
        // AGAIN from inside the catch (nothing catches that), the second throw escaped the method,
        // and families 2 and 3 were never subscribed while the centre already looked bound.
        private const int FamilyGameState = 1 << 0;
        private const int FamilyVesselChanged = 1 << 1;
        private const int FamilyEscapeMenu = 1 << 2;
        private const int AllFamiliesBound = FamilyGameState | FamilyVesselChanged | FamilyEscapeMenu;
        private static int _familiesBound;

        /// <summary>
        /// F6: (re)subscribe every game message on the live MessageCenter. Idempotent per instance
        /// AND per family: a call whose instance equals <see cref="_subscribedCenter"/> with all
        /// three families in returns immediately and logs nothing, and a partially bound centre
        /// retries only what is still missing. Called once from OnInitialized() and once per frame
        /// from Update() - the per-frame call costs one ReferenceEquals unless something changed.
        /// </summary>
        private static void SubscribeGameMessages(string reason)
        {
            // GeneralTools.Game is the same GameInstance the instance `Game` property returns, with
            // the null guard the base property does not have. Null before a game session exists -
            // that is not an error, it just means there is nothing to subscribe to yet, and the
            // per-frame call picks it up as soon as there is.
            MessageCenter messages = GeneralTools.Game?.Messages;
            if (messages == null)
                return;

            bool sameCentre = ReferenceEquals(messages, _subscribedCenter);

            // "Already done" = this exact centre AND every family present.
            if (sameCentre && _familiesBound == AllFamiliesBound)
                return;

            bool firstTime = _subscribedCenter == null;
            if (!sameCentre)
            {
                // A different instance means the old session's centre (a save reload replaces it).
                // Start the binding over for the new one; the old centre is abandoned whole.
                _subscribedCenter = messages;
                _familiesBound = 0;
            }

            int families = 0;
            int failed = 0;
            int missing = AllFamiliesBound & ~_familiesBound;

            // Family 1 - game state. F2(b): a game-state change is a repaint boundary (flight <->
            // map <-> everything else) and this hook fires on the transition itself rather than on
            // the next frame's poll of ValidScene(), so the K2UI write caches are dropped here.
            if ((missing & FamilyGameState) != 0)
            {
                try
                {
                    messages.Subscribe<GameStateChangedMessage>(OnGameStateChanged);

                    // The bit is set BEFORE the log line, and that order is the point: the
                    // subscribes ARE the binding, so nothing a log call can do may leave a completed
                    // family looking unbound (a retry would double-subscribe it).
                    _familiesBound |= FamilyGameState;
                    families++;
                    L.Log("K2D2: GameStateChangedMessage subscribed");
                }
                catch (Exception e)
                {
                    // Never let a catch throw: L.Log is the null-safe shim (F5(a)), and
                    // e.GetType()/e.Message cannot throw on a non-null exception. The per-frame
                    // ValidScene() poll keeps the mod working even if this family never binds.
                    failed++;
                    // PRODUCTION (v1.2.1): Warn, not debug. A family that fails to bind means that
                    // message type never reaches the mod - degraded, though the per-frame ValidScene()
                    // poll keeps the mod working. L.Warn is every bit as null-safe as L.Log was.
                    L.Warn($"K2D2: could not subscribe GameStateChangedMessage ({e.GetType().Name}: {e.Message}) - the per-frame ValidScene() poll covers it");
                }
            }

            // Family 2 - vessel changed (pilot/controller reset).
            if ((missing & FamilyVesselChanged) != 0)
            {
                try
                {
                    messages.Subscribe<VesselChangedMessage>(OnVesselChanged);
                    _familiesBound |= FamilyVesselChanged;
                    families++;
                    L.Log("K2D2: VesselChangedMessage subscribed");
                }
                catch (Exception e)
                {
                    failed++;
                    // PRODUCTION (v1.2.1): Warn - the vessel-reset family is degraded if this fails.
                    L.Warn($"K2D2: could not subscribe VesselChangedMessage ({e.GetType().Name}: {e.Message})");
                }
            }

            // Family 3 - the ESC-menu pair (F7's fast path). Folded into this method so a save
            // reload re-arms it too; the poll in K2D2Window.Update() is the correctness path and
            // does not depend on either of these being delivered.
            if ((missing & FamilyEscapeMenu) != 0)
            {
                try
                {
                    messages.Subscribe<EscapeMenuOpenedMessage>(OnEscapeMenuOpened);
                    messages.Subscribe<EscapeMenuClosedMessage>(OnEscapeMenuClosed);
                    _familiesBound |= FamilyEscapeMenu;
                    families++;
                    L.Log("K2D2: EscapeMenuOpenedMessage + EscapeMenuClosedMessage subscribed");
                }
                catch (Exception e)
                {
                    failed++;
                    // PRODUCTION (v1.2.1): Warn - F7's message fast path is degraded (the poll covers it).
                    L.Warn($"K2D2: could not subscribe the ESC-menu pair ({e.GetType().Name}: {e.Message}) - the per-frame IsEscapeVisible() poll covers it");
                }
            }

            // Logged ONCE per actual binding change, never per frame - this method is called from
            // Update(), and the early return above is what keeps it quiet.
            string instanceState = sameCentre
                ? "retried a partial binding"
                : (firstTime ? "bound" : "changed");
            L.Log($"K2D2 MessageCenter instance {instanceState} - (re)subscribed game messages " +
                  $"({reason}: {families} families ok, {failed} failed, bound={_familiesBound}/{AllFamiliesBound})");
        }

        /// <summary>
        /// F6/F2(b): a game-state change is a repaint boundary. The K2UI write caches are dropped so
        /// the first frame in the new state repaints unconditionally - one Clear() per state change.
        /// </summary>
        private static void OnGameStateChanged(MessageCenterMessage message)
        {
            // LAUNCH-3 VERDICT: this handler NO LONGER FIRES ON THIS BUILD, and the subscription is
            // kept only as a cheap insurance policy for a future one. Five state transitions were
            // logged in launch 3, this ran zero times, and the reason is now known: the game's own
            // publisher (SimpleStateMachine.SetState) publishes to a CACHED MessageCenter field, not
            // to the Game.Messages instance this mod can reach. Full evidence and the corrected
            // invalidation route are on ObserveGameState() above - that method is what actually drops
            // the K2UI write caches per state change now.
            //
            // The probe that proved this lived here in launch 3. It is deliberately NOT restored: it
            // answered its question, and its per-transition hash line would be noise in a log that no
            // longer needs it. `RuntimeHelpers.GetHashCode` is the IDENTITY hash (it does not call an
            // override), which is what made `same=` a real verdict rather than an `==` overload's.
            //
            // The case this must never break: if this ever DOES fire, the invalidation below is
            // idempotent with ObserveGameState()'s, so the two cannot double-count into a visible
            // artefact - the worst case is one redundant cache clear on a state change.
            K2UI.VisualElementExtension.InvalidateUiCaches();

            // The body this handler used to carry is still retired, and deliberately so: it drove
            // ShapeDrawer.Instance.can_draw, and this tree has no ShapeDrawer at all - the only
            // occurrences of the name anywhere under Assets/K2D2/Code are the commented-out lines
            // themselves, so reviving it would not compile. There is nothing else the original body
            // intended: the invalidation above, plus ObserveGameState()'s route, is the whole live job.
        }

        /// <summary>F6: the vessel changed - reset every pilot/controller, as the old lambda did.</summary>
        private static void OnVesselChanged(MessageCenterMessage message)
        {
            ResetControllers();
        }

        /// <summary>
        /// F7 FAST PATH ONLY. WindowOptions.Default sets BringToFrontOnPointerDown = true (F4), which
        /// raises the window above the game's own pause canvases on every click - so while the escape
        /// menu is up the window must lower itself. This message pair is the fast path; the
        /// per-frame poll in K2D2Window.Update() is the correctness path, because message DELIVERY is
        /// not guaranteed (F6's instance replacement is one measured way it stops).
        /// </summary>
        private static void OnEscapeMenuOpened(MessageCenterMessage message)
        {
            var window = Instance?.main_window;
            if (window != null)     // Unity's overloaded comparison also covers a destroyed window
                window.ApplyEscapeSuppression(true);
        }

        /// <summary>F7 fast path: the menu closed - restore whatever this window lowered.</summary>
        private static void OnEscapeMenuClosed(MessageCenterMessage message)
        {
            var window = Instance?.main_window;
            if (window != null)
                window.ApplyEscapeSuppression(false);
        }
        // =========================================================================================

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
