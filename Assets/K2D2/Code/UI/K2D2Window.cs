using System.Collections.Generic;
using K2D2.Controller;
using K2UI;
using K2UI.Tabs;
using KSP.UI.Binding;
using UitkForKsp2.API;
using UnityEngine;
using UnityEngine.UIElements;

namespace K2D2.UI
{
    /// <summary>
    /// Controller for the K2D2Window UI.
    /// </summary>
    public class K2D2Window : MonoBehaviour
    {
        // The PanelRenderer component of the window game object. UitkForKsp2.API.Window.Create(...)
        // in K2D2_Plugin.cs returns a PanelRenderer (UIDocument's successor under Redux), which
        // doesn't expose .rootVisualElement directly - the root VisualElement is obtained via
        // RegisterUIReloadCallback instead, which fires once immediately (the UI is already loaded
        // synchronously by the time Create returns) and again on any later live UI reload. Same
        // pattern KerbalAutopilot's MainAppWindow.cs uses for this Redux API.
        //
        // F5(b)/hand-off: the plugin does not leave the controller to search for this component on
        // its own - `Initialize(PanelRenderer)` below is handed the renderer Window.Create already
        // returned, and this field is only ever resolved by GetComponent in OnEnable's fallback path
        // (the case where the component is added/re-enabled by some other route).
        private PanelRenderer _panel;

        // The elements of the window that we need to access
        private VisualElement _rootElement;

        // Guards the one-time wiring in OnUiReload against running twice if the callback fires again later
        // (e.g. a live UI reload, or Initialize and the OnEnable fallback both landing) - re-binding
        // would stack duplicate event handlers on top of the ones still attached from the previous pass.
        private bool _bound;

        // Kept so Update() can call its watchdog Tick() every frame - see ResizeManipulator.cs and
        // the comment where this is created below for why that's necessary.
        private ResizeManipulator _resizeManipulator;

        // Kept so the drag bounds can be introspected/diagnosed in-game (K2D2Diag reads this field
        // reflectively to print the computed clamp window next to the measured panel rect) - see
        // DragManipulator.cs for the position model and the bounds fix.
        private DragManipulator _dragManipulator;

        // The backing field for the IsWindowOpen property
        private bool _isWindowOpen;

        // F7: the ESC-menu z-order suppression latch. True while the game's escape menu is up and
        // this window has lowered itself *because of it*. Pure presentation: `IsWindowOpen` and the
        // AppBar toggle keep whatever the player set, and the restore re-asserts that state.
        private bool _escapeSuppressed;

        // F7: the last value the per-frame IsEscapeVisible() poll saw. Starts false because a
        // session begins with the menu closed, so the first polled frame is not a transition. A poll
        // that cannot resolve the UI manager does NOT write this field, so a moment with no UI
        // manager can never fake a menu transition.
        private bool _lastEscapeVisible;

        /// <summary>
        /// The state of the window. Setting this value will open or close the window.
        /// </summary>
        public bool IsWindowOpen
        {
            get => _isWindowOpen;
            set
            {
                bool was_open = _isWindowOpen;
                _isWindowOpen = value;

                // F2(a): the hidden -> visible transition. Every cached write in K2UI is keyed by
                // the element and still answers "already written" from before the window was
                // hidden, so without this the reopened window can keep stale styling and need two
                // AppBar clicks to come back - FlightPlan's launch-8 regression, reproduced exactly.
                // It also re-arms the F8 counters, so the first write summary after a reopen covers
                // a full interval including the repaint burst.
                if (value && !was_open)
                    VisualElementExtension.InvalidateUiCaches();

                // Set the display style of the root element to show or hide the window. Null-conditional
                // because this can in principle be set before OnUiReload has run (it shouldn't happen in
                // practice - see OnEnable - but failing silently beats an NRE if it ever does).
                // Show(bool) here is K2UI's own VisualElement extension (K2UI/Tools/Extensions.cs), which
                // flips style.display between Flex and None.
                //
                // F7: gated by the ESC latch. Bringing the window up while the game's escape menu is
                // up would put it straight back above the menu's overlay - the window is logically
                // open (the AppBar toggle the plugin registered reflects that) but stays lowered
                // until the menu closes.
                _rootElement?.Show(value && !_escapeSuppressed);
                // Alternatively, you can deactivate the window game object to close the window and stop it from updating,
                // which is useful if you perform expensive operations in the window update loop. However, this will also
                // mean you will have to re-register any event handlers on the window elements when re-enabled in OnEnable.
                // gameObject.SetActive(value);

                // Update the Flight AppBar button state
                GameObject.Find(K2D2_Plugin.ToolbarFlightButtonID)
                    ?.GetComponent<UIValue_WriteBool_Toggle>()
                    ?.SetValue(value);

                // Update the OAB AppBar button state
                // GameObject.Find(K2D2Plugin.ToolbarOabButtonID)
                //     ?.GetComponent<UIValue_WriteBool_Toggle>()
                //     ?.SetValue(value);
            }
        }

        TabbedPage tab_page;

        List<K2Page> all_panels = new();

        // Which tab the info button should return to when clicked again from About - see the
        // info_button click handler in OnUiReload below.
        string _lastTabBeforeAbout = "node";

        /// <summary>
        /// F5(b)/hand-off: called by K2D2_Plugin.cs immediately after Window.Create(...) returns, with
        /// the PanelRenderer that call already produced. Registers for its UI-ready callback - see the
        /// _panel field's comment - so the controller never has to search for the component itself.
        ///
        /// Unity swallows exceptions thrown inside MonoBehaviour lifecycle methods like OnEnable (it
        /// logs them but doesn't propagate them or stop the caller), so a failed Q&lt;T&gt;() lookup
        /// inside OnUiReload below (e.g. a UXML element name mismatch) would otherwise fail silently
        /// with no obvious error. The logging below exists to surface exactly which lookup failed.
        /// </summary>
        public void Initialize(PanelRenderer panel)
        {
            if (panel == null)
            {
                L.Error("K2D2Window.Initialize: the PanelRenderer passed in by K2D2_Plugin.cs is null - " +
                      "the window will not be wired up.");
                return;
            }

            _panel = panel;
            _panel.RegisterUIReloadCallback(OnUiReload);

            // RegisterUIReloadCallback should invoke OnUiReload synchronously here; log explicitly
            // if it didn't, rather than leaving it to be inferred from nothing happening.
            if (_rootElement == null)
            {
                L.Warn("K2D2Window.Initialize: RegisterUIReloadCallback did not invoke OnUiReload immediately " +
                      "(_rootElement is still null right after registering). Waiting for a later UI reload " +
                      "to fire it instead.");
            }
        }

        /// <summary>
        /// Runs when the window is first created, and every time the window is re-enabled. Initialize()
        /// normally gets here first (K2D2_Plugin.cs calls it immediately after Window.Create), so this is
        /// a fallback for the case where the component is added/re-enabled by some other path.
        ///
        /// DIAGNOSTIC LOGGING carried over from the Redux port: Unity catches and swallows exceptions
        /// thrown inside MonoBehaviour lifecycle methods like OnEnable (it logs them, but doesn't
        /// propagate them or stop the caller), so if anything in the wiring below throws, the window would
        /// silently fail to finish wiring with no obvious error. These logs exist to catch that.
        /// </summary>
        private void OnEnable()
        {
            if (_panel == null)
            {
                L.Warn("K2D2Window.OnEnable running without Initialize - falling back to " +
                      "GetComponent<PanelRenderer>().");
                _panel = GetComponent<PanelRenderer>();
            }

            if (_panel == null)
            {
                L.Error("K2D2Window.OnEnable: GetComponent<PanelRenderer>() returned null - the window's " +
                      "GameObject doesn't have a PanelRenderer component. This should not happen given how " +
                      "Window.Create() is used in K2D2_Plugin.cs - if you see this, something upstream " +
                      "changed how the window GameObject is constructed.");
                return;
            }

            _panel.RegisterUIReloadCallback(OnUiReload);
        }

        /// <summary>
        /// Fires once immediately when registration happens (the UI is already loaded synchronously by
        /// then) and again on any later live UI reload. All the wiring that needs the real root
        /// VisualElement lives here instead of OnEnable for that reason.
        /// </summary>
        private void OnUiReload(PanelRenderer panel, VisualElement root)
        {
            L.Log("K2D2Window.OnUiReload running");

            if (panel == null)
            {
                L.Error("K2D2Window.OnUiReload: the PanelRenderer handed to the callback is null - the " +
                      "window will not be wired up.");
                return;
            }

            if (root == null)
            {
                // FAILURE - promoted to Error for production (v1.2.1): the visual tree is not built, so
                // nothing can be wired up. Invisible chatter would hide a completely dead window.
                L.Error("K2D2Window.OnUiReload: the PanelRenderer's root VisualElement is null - the " +
                      "document hasn't built its visual tree yet. The window will not be wired up.");
                return;
            }

            if (root.childCount == 0)
            {
                // FAILURE - promoted to Error for production (v1.2.1). This is the blank-window trap
                // documented in AGENTS.md §9, so a user hitting it needs it in the log by name.
                L.Error("K2D2Window.OnUiReload: the PanelRenderer's root VisualElement has no children - the " +
                      "cloned VisualTreeAsset is empty. This is the version-mismatch symptom (a bundle built " +
                      "by a different Unity than the one loading it can deserialize a VisualTreeAsset with " +
                      "zero children); re-check the k2d2_ui.bundle build.");
                return;
            }

            // Since we're cloning the UXML tree from a VisualTreeAsset, the actual root element is a TemplateContainer,
            // so we need to get the first child of the TemplateContainer to get our actual root VisualElement.
            // (The childCount check above already rejected an empty tree, so this cannot throw.)
            _rootElement = root[0];

            // Keep the window hidden on every pass, even a re-entrant one - it is re-opened explicitly by
            // the app-bar toggle.
            IsWindowOpen = false;

            // Only wire up click handlers/callbacks once - this can fire again on a later live UI reload,
            // and re-running the binding below every time would stack duplicate event subscriptions on top
            // of whatever's still attached from the previous load.
            if (_bound) return;
            _bound = true;

            // From here down: every Q<T>() lookup is explicitly null-checked and logged by name before
            // use, rather than trusting it and letting a bad lookup throw an NRE that Unity would
            // otherwise swallow silently (see the OnEnable doc comment above). If the window fails to
            // wire up, whichever element name gets logged here as missing is the one to check against
            // K2D2_Window.uxml.

            // Get the close button from the window
            var closeButton = _rootElement.Q<Button>("close-button");
            if (closeButton == null)
            {
                // FAILURE - promoted to Error for production (v1.2.1): this one STOPS the binding
                // chain, so everything below is left unwired.
                L.Error("K2D2Window.OnUiReload: Q<Button>(\"close-button\") returned null - stopping here, " +
                      "the rest of the window's controls will not be wired up.");
                return;
            }
            // Add a click event handler to the close button
            closeButton.clicked += () => IsWindowOpen = false;

            // list all pilot panel
            all_panels.Clear();
            foreach(var pilot in K2D2_Plugin.Instance.pilots_manager.pilots)
            {
                var panel_ = pilot.page;
                if (panel_ != null)
                    all_panels.Add(panel_);
            }

            all_panels.Add(new AboutUI());

            tab_page = _rootElement.Q<TabbedPage>();
            if (tab_page == null)
            {
                // FAILURE - promoted to Error for production (v1.2.1): stops the binding chain; the
                // window would come up with no tabs.
                L.Error("K2D2Window.OnUiReload: Q<TabbedPage>() returned null - the custom <k2-ui-tabs--tabbed-page> " +
                      "(or however it's tagged in K2D2_Window.uxml) element wasn't found or didn't instantiate " +
                      "as a TabbedPage. Stopping here, the rest of the window's controls will not be wired up.");
                return;
            }
            tab_page.Init(all_panels);
            // save the current_tab to settings
            tab_page.Bind("main_page", "node");

            // Renamed from "title-bar" when the window chrome switched from a hand-built header to
            // UitkForKsp2.Controls.AppShell (which owns its own header - icon/title/close - and has no
            // slot for extra buttons). The settings and staging toggles moved into this new row instead.
            var title_bar = _rootElement.Q("toolbar");
            if (title_bar == null)
            {
                // FAILURE - promoted to Error for production (v1.2.1): stops the binding chain.
                L.Error("K2D2Window.OnUiReload: Q(\"toolbar\") returned null - stopping here, the settings/" +
                      "staging toggle buttons will not be wired up.");
                return;
            }

            // The settings gear/page is gone - this is now a plain "i" button that jumps straight to
            // the About tab via TabbedPage.Select(), instead of toggling the old shared
            // GlobalSetting.settings_visible flag. That flag itself is left in place (see
            // GlobalSettings.cs/K2Page.cs) since every tab's generic onSettingsChanged handler is
            // harmless as long as nothing ever sets it back to true again - not worth the extra risk
            // of ripping out for something that wasn't actually part of this ask.
            var info_button = title_bar.Q<Button>("info-toggle");
            var staging_toggle = title_bar.Q<ToggleButton>("staging-toggle");
            if (info_button == null)
            {
                // DEGRADATION - Warn for production (v1.2.1): the window still works, one button does not.
                L.Warn("K2D2Window.OnUiReload: title_bar.Q<Button>(\"info-toggle\") returned null.");
            }
            if (staging_toggle == null)
            {
                // DEGRADATION - Warn for production (v1.2.1): the auto-staging toggle stays unwired.
                L.Warn("K2D2Window.OnUiReload: title_bar.Q<ToggleButton>(\"staging-toggle\") returned null.");
            }

            // Toggle, not one-way: clicking it while already on About goes back to whichever tab
            // was open before, instead of leaving the only way out being to click another tab
            // button by hand. "node" as the fallback matches tab_page.Bind's own default below.
            info_button?.RegisterCallback<ClickEvent>(evt =>
            {
                if (tab_page.CurrentTabCode == "about")
                {
                    tab_page.Select(_lastTabBeforeAbout);
                }
                else
                {
                    _lastTabBeforeAbout = tab_page.CurrentTabCode;
                    tab_page.Select("about");
                }
            });
            // This used to write to StagingPilot.Instance.Enabled, which only gates BaseController.isActive
            // (tab visibility/availability) - nothing in StagingPilot.Update()/CheckStaging() ever reads it,
            // so toggling it had no effect on whether auto-staging actually ran. The setting that
            // CheckStaging() actually checks is StagingSettings.auto_staging, which had no UI control at
            // all - binding this already-existing title-bar toggle to it directly is both the fix and the
            // toggle's original apparent intent.
            staging_toggle?.Bind(StagingSettings.auto_staging);

            _rootElement.Query<IntegerField>().ForEach(field => field.DisableGameInputOnFocus());
            _rootElement.Query<FloatField>().ForEach(field => field.DisableGameInputOnFocus());
            _rootElement.Query<RepeatButton>().ForEach(field => field.DisableGameInputOnFocus());

            _dragManipulator = new DragManipulator(false, "main_window_pos");
            _rootElement.AddManipulator(_dragManipulator);

            // Drag-to-resize via the handle added to K2D2_Window.uxml's AppShell (bottom-right
            // corner, stock resize-handle.png). ResizeManipulator resizes _rootElement itself
            // (the AppShell) by attaching its pointer callbacks to the small handle element
            // instead of the whole window - see ResizeManipulator.cs.
            var resize_handle = _rootElement.Q<VisualElement>("resize-handle");
            if (resize_handle == null)
            {
                // DEGRADATION, and the code says so itself ("still open and drag normally, just not
                // resizable") - Warn for production (v1.2.1) rather than Error.
                L.Warn("K2D2Window.OnUiReload: Q<VisualElement>(\"resize-handle\") returned null - " +
                      "the window will still open and drag normally, it just won't be resizable.");
            }
            else
            {
                _resizeManipulator = new ResizeManipulator(_rootElement, "main_window_size");
                resize_handle.AddManipulator(_resizeManipulator);
            }

            L.Log("K2D2Window.OnUiReload finished wiring successfully");
        }

        void Update()
        {
            // F7: the ESC poll runs UNCONDITIONALLY - deliberately outside the F3 gate below. Once
            // F4 has landed, "the window is closed" is not the same as "the window is not on top":
            // the window can be logically open while the escape menu is up, and a poll that lived
            // inside the IsWindowOpen gate would never be reached to lower it. It is also the
            // correctness path for the suppression - message delivery is not guaranteed (F6's
            // MessageCenter instance replacement is one measured way it stops), so the state is read
            // from the game's own UI manager rather than waited for.
            PollEscapeMenu();

            // F3(a): the per-frame UI update is gated on the window actually being open. It used to
            // run the whole page refresh - including the status line's Reset()/Status() sequence -
            // every frame even with the window shut, which is the freeze this increment removes.
            // tab_page is null until OnUiReload has run, so that is the same guard.
            //
            // "the game is not in a state where the window can be visible" needs no second test here:
            // K2D2_Plugin.Update() force-closes the window outside a valid scene, so IsWindowOpen is
            // already false there.
            if (IsWindowOpen && tab_page != null)
            {
                tab_page.Update();

                // F8: the probe ticks immediately after the frame's UI writes, inside the same
                // gate - so a closed window produces no summary lines at all, and that absence is
                // itself the evidence that F3 works.
                K2UiWriteStats.Tick();
            }

            // PointerUpEvent/PointerCaptureOutEvent can fail to reach the resize handle in this
            // game's UI Toolkit embedding, which would leave ResizeManipulator with no event to end
            // the drag and treating every later mouse move as more resizing. Tick() sidesteps the
            // event pipeline: it reads the real OS mouse button state every frame and force-ends
            // the gesture the moment it's no longer held, regardless of what UI Toolkit delivers.
            //
            // F3(c): deliberately OUTSIDE the gate above. This is a watchdog, not a UI write, and
            // it has to run even while the window is hidden to force-end a gesture whose PointerUp
            // went missing.
            _resizeManipulator?.Tick();
        }

        /// <summary>
        /// F7 correctness path: ask the game's own UI manager whether its escape menu is up, and act
        /// only when the answer CHANGES - never per frame, so no write goes back into the per-frame
        /// path F1/F2/F3 just cleaned up.
        ///
        /// `KSP.Game.UIManager.IsEscapeVisible()` is the accessor. It is doc-SILENT in the pinned
        /// doc set (it has no XML doc comment, so the canonical set's Assembly-CSharp.xml has no
        /// entry for it) - it was resolved from the installed assembly instead:
        ///   monodis --method "$KSP2_ROOT/KSP2_x64_Data/Managed/Assembly-CSharp.dll" \
        ///     | grep -i IsEscapeVisible
        ///   30832: instance default bool IsEscapeVisible ()  (param: 23243 impl_flags: cil managed )
        /// The `Game?.UI` chain is the same one FlightPlan's ESC poll uses on this pin, and the built
        /// assembly resolves both hops against the shipped runtime:
        ///   ['Assembly-CSharp']KSP.Game.GameInstance.get_UI
        ///   ['Assembly-CSharp']KSP.Game.UIManager.IsEscapeVisible
        /// (`monodis --memberref Deploy/K2D2/K2D2.dll` - the loader's own view, not just my reading).
        /// </summary>
        private void PollEscapeMenu()
        {
            // GeneralTools.Game is GameManager.Instance.Game with the null guard - null until the
            // game instance exists, which is not an error and is not a transition.
            var game = KTools.GeneralTools.Game;
            if (game == null)
                return;

            var ui = game.UI;
            if (ui == null)
                return;

            bool escapeVisible = ui.IsEscapeVisible();
            if (escapeVisible == _lastEscapeVisible)
                return;     // the steady state: one bool compare per frame, no log, no write

            _lastEscapeVisible = escapeVisible;
            ApplyEscapeSuppression(escapeVisible);
        }

        /// <summary>
        /// F7: the ONE body both ESC triggers drive - the EscapeMenuOpened/Closed message pair
        /// (fast path; when delivery reaches us) and the per-frame IsEscapeVisible() poll in
        /// Update() (the correctness path). Idempotent by design: the second trigger for the same
        /// transition is a no-op, so the Info lines below fire once per menu transition, not once
        /// per trigger.
        ///
        /// WHY this must land with F4: `WindowOptions.Default` sets BringToFrontOnPointerDown = true,
        /// which raises this window's UI Toolkit panel above anything whose sorting order does not
        /// also climb - measured in the shipped UitkForKsp2.dll as
        /// `OrderManipulator.OnPointerDown -> OrderManager.BringToFront(PanelSettings) ->
        /// panelSettings.sortingOrder = OrderManager.Next()` (a static post-incrementing counter).
        /// That includes the escape menu's full-screen overlay and the save-before-exit dialog. The
        /// bring-to-front behaviour is wanted (it is what stops the window sitting behind other UI);
        /// hiding the window while the menu is up is the other half of the same decision.
        /// FlightPlanPlugin.cs:613-618 states the coupling explicitly and names K2-D2 as the mod that
        /// only escaped the overlay because its WindowOptions was a zero-initialised struct
        /// (BringToFrontOnPointerDown stayed false).
        ///
        /// WHAT IT DOES NOT DO: it never touches `IsWindowOpen`, the AppBar toggle, or the game's own
        /// F2 hide path. Those are the logical state; this is presentation only. The F2 key writes
        /// `style.visibility` on the window root (UitkForKsp2's HideManipulator, IL-verified) - a
        /// different style property from the `display` this and IsWindowOpen share - so the two
        /// compose instead of fighting, and neither the F2 hide nor the AppBar re-show (which routes
        /// through the IsWindowOpen setter's F2(a) invalidation) is disturbed.
        /// </summary>
        public void ApplyEscapeSuppression(bool menuVisible)
        {
            if (menuVisible)
            {
                if (_escapeSuppressed)
                    return;     // already suppressed - the other trigger for this transition

                _escapeSuppressed = true;

                // `display: none` through the F1 helper, so this write and the IsWindowOpen write
                // share ONE cache per element. A hidden window cannot be clicked, so no repaint
                // burst is owed on the way down.
                _rootElement?.SetDisplay(DisplayStyle.None);
                L.Log("K2D2 ESC suppression: escape menu is visible - window lowered " +
                      $"(BringToFrontOnPointerDown is on, IsWindowOpen={IsWindowOpen})");
                return;
            }

            if (!_escapeSuppressed)
                return;     // this window never lowered itself - nothing to restore

            _escapeSuppressed = false;

            // F2 contract: never show a tree the caches still call "already written". The window has
            // been hidden since the menu opened, and the game's own F2 path can have written
            // `visibility` on the window root in the meantime - so the restore drops the K2UI caches
            // and the next frame restyles from a blank slate, exactly as the AppBar reopen does.
            VisualElementExtension.InvalidateUiCaches();

            // Re-assert the LOGICAL state rather than blindly showing: the player can toggle the
            // window from the AppBar while the menu is up (that write is gated by this same latch),
            // and a window that was closed when the menu opened must not be opened by the menu
            // closing. Show() is the F1 helper, so a no-change restore writes nothing.
            _rootElement?.Show(IsWindowOpen);
            L.Log($"K2D2 ESC suppression: escape menu closed - window restored (IsWindowOpen={IsWindowOpen})");
        }

        /// <summary>
        /// F2(d): the mod had no teardown hook anywhere before this one. Two things need it:
        /// the write caches (a destroyed tree must not stay reachable as their dictionary keys),
        /// and the pages' static settings listeners - K2Page.Init subscribes on the static
        /// GlobalSetting.settings_visible, which nothing else ever clears, so without the unlink
        /// the whole window's visual tree outlives the window.
        /// </summary>
        private void OnDestroy()
        {
            L.Log("K2D2Window.OnDestroy - invalidating UI caches and unlinking page listeners");

            // Stop the per-frame paths first, without going through the property: the setter would
            // write to the tree and poke the AppBar toggle while everything is being torn down.
            _isWindowOpen = false;

            // F7: drop the ESC latch and the poll's memory with the tree they described. Both the
            // poll and the message pair read these fields, and a torn-down window must not answer
            // "suppressed" to a poll whose element no longer exists.
            _escapeSuppressed = false;
            _lastEscapeVisible = false;

            if (all_panels != null)
            {
                foreach (var panel in all_panels)
                    panel?.onDestroy();
                all_panels.Clear();
            }

            tab_page = null;
            _rootElement = null;
            _bound = false;

            VisualElementExtension.InvalidateUiCaches();
        }

        /// <summary>
        /// PROBE (launch-3): OnDestroy has never produced a log line across two launches, so we cannot
        /// tell "Unity skipped it" from "this object is never destroyed". These two callbacks bracket
        /// that without changing behaviour. Read all three together: OnDisable proves the component was
        /// deactivated, OnApplicationQuit proves the engine reached its normal quit path, and OnDestroy
        /// proves it was actually destroyed. Quit or disable without destroy means the engine did not
        /// destroy this object; none of the three means it outlives the session entirely.
        /// </summary>
        private void OnDisable()
        {
            L.Log("K2D2Window.OnDisable - component deactivated (destroy NOT yet observed)");
        }

        private void OnApplicationQuit()
        {
            L.Log("K2D2Window.OnApplicationQuit - engine reached the quit path (destroy NOT yet observed)");
        }
    }
}
