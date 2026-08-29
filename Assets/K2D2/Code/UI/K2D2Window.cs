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
        // The PanelRenderer component of the window game object.
        // CORRECTED during Redux port verification: this used to look for a UIDocument component here
        // (GetComponent<UIDocument>()) - the SpaceWarp1-era way of getting a UI Toolkit window's root
        // element. The current UitkForKsp2.API.Window.Create(...) call in K2D2_Plugin.cs returns a
        // PanelRenderer instead (UIDocument's successor), which doesn't expose .rootVisualElement the same
        // way - so GetComponent<UIDocument>() was silently returning null, and the very next line
        // (_window.rootVisualElement[0]) threw a NullReferenceException inside OnEnable before any control
        // got wired up. That's exactly "the app bar icon shows but nothing pops up when I press it" - the
        // toggle callback then also no-ops on the still-null _rootElement. The real way to get a
        // PanelRenderer's root VisualElement is RegisterUIReloadCallback, which fires once immediately
        // (the UI is already loaded synchronously by the time Create returns) and again on any later live
        // UI reload - the same lesson KerbalAutopilot's MainAppWindow.cs already learned for this exact
        // Redux API (see its Initialize() comment for the fuller explanation).
        private PanelRenderer _panel;

        // The elements of the window that we need to access
        private VisualElement _rootElement;

        // Guards the one-time wiring in OnUiReload against running twice if the callback fires again later
        // (e.g. a live UI reload during development) - re-binding would stack duplicate event handlers.
        private bool _bound;

        // Kept so Update() can call its watchdog Tick() every frame - see ResizeManipulator.cs and
        // the comment where this is created below for why that's necessary.
        private ResizeManipulator _resizeManipulator;

        // The backing field for the IsWindowOpen property
        private bool _isWindowOpen;

        /// <summary>
        /// The state of the window. Setting this value will open or close the window.
        /// </summary>
        public bool IsWindowOpen
        {
            get => _isWindowOpen;
            set
            {
                _isWindowOpen = value;

                // Set the display style of the root element to show or hide the window. Null-conditional
                // because this can in principle be set before OnUiReload has run (it shouldn't happen in
                // practice - see OnEnable - but failing silently beats an NRE if it ever does).
                _rootElement?.Show(value);
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

        /// <summary>
        /// Runs when the window is first created, and every time the window is re-enabled. Gets the
        /// PanelRenderer and registers for its UI-ready callback - see the _panel field's comment for why
        /// this can't just read a rootVisualElement directly here the way the pre-port code did.
        ///
        /// DIAGNOSTIC LOGGING added during Redux port verification: after the UIDocument->PanelRenderer fix
        /// above, the app-bar icon still didn't open the window, with nothing K2D2-related ever appearing in
        /// the Editor console. Root cause candidate: Unity catches and swallows exceptions thrown inside
        /// MonoBehaviour lifecycle methods like OnEnable (it logs them, but doesn't propagate them or stop
        /// the caller) - so if anything in OnUiReload below throws (e.g. a Q<T>() lookup returning null
        /// because a UXML element name doesn't match, which is plausible given the CS0618 UxmlTraits
        /// deprecation warnings on K2UI's custom controls under Unity 6), the window would silently fail to
        /// finish wiring with no obvious error, which matches the reported symptom exactly. Verified via IL
        /// inspection of UitkForKsp2.API.Window.Create that the window's GameObject IS fully activated
        /// (SetActive(true)) before Create returns, and RegisterUIReloadCallback's own IL confirms it invokes
        /// the callback immediately if the root element is already built and attached to a panel at
        /// registration time - so the mechanism itself should fire; these logs exist to catch the case where
        /// it fires but a lookup inside OnUiReload then fails.
        /// </summary>
        private void OnEnable()
        {
            L.Log("K2D2Window.OnEnable running");

            _panel = GetComponent<PanelRenderer>();
            if (_panel == null)
            {
                L.Log("K2D2Window.OnEnable: GetComponent<PanelRenderer>() returned null - the window's " +
                      "GameObject doesn't have a PanelRenderer component. This should not happen given how " +
                      "Window.Create() is used in K2D2_Plugin.cs - if you see this, something upstream " +
                      "changed how the window GameObject is constructed.");
                return;
            }

            _panel.RegisterUIReloadCallback(OnUiReload);

            // If OnUiReload didn't fire synchronously as part of the Register call above (it should, per the
            // IL analysis in the class doc comment, but this confirms it one way or the other rather than
            // silently assuming), say so explicitly instead of leaving it to be inferred from "nothing
            // happened."
            if (_rootElement == null)
            {
                L.Log("K2D2Window.OnEnable: RegisterUIReloadCallback did not invoke OnUiReload immediately " +
                      "(_rootElement is still null right after registering). Waiting for a later UI reload " +
                      "to fire it instead - if the window still never opens, this is the lead to chase.");
            }
        }

        /// <summary>
        /// Fires once immediately when OnEnable registers it (the UI is already loaded synchronously by
        /// then) and again on any later live UI reload. All the wiring that needs the real root
        /// VisualElement lives here instead of OnEnable for that reason.
        /// </summary>
        private void OnUiReload(PanelRenderer panel, VisualElement root)
        {
            L.Log("K2D2Window.OnUiReload running");

            // Since we're cloning the UXML tree from a VisualTreeAsset, the actual root element is a TemplateContainer,
            // so we need to get the first child of the TemplateContainer to get our actual root VisualElement.
            _rootElement = root[0];

            IsWindowOpen = false;

            // Only wire up click handlers/callbacks once - this can fire again on a later live UI reload,
            // and re-running the binding below every time would stack duplicate event subscriptions on top
            // of whatever's still attached from the previous load.
            if (_bound) return;
            _bound = true;

            // From here down: every Q<T>() lookup is explicitly null-checked and logged by name before use,
            // rather than trusting it and letting a bad lookup throw an NRE that Unity would otherwise
            // swallow silently (see the OnEnable doc comment above for why that's exactly the failure mode
            // this is guarding against). If the window still doesn't open after this change, whichever
            // element name gets logged here as missing is the next thing to check against K2D2_Window.uxml.

            // Get the close button from the window
            var closeButton = _rootElement.Q<Button>("close-button");
            if (closeButton == null)
            {
                L.Log("K2D2Window.OnUiReload: Q<Button>(\"close-button\") returned null - stopping here, " +
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
                L.Log("K2D2Window.OnUiReload: Q<TabbedPage>() returned null - the custom <k2-ui-tabs--tabbed-page> " +
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
                L.Log("K2D2Window.OnUiReload: Q(\"toolbar\") returned null - stopping here, the settings/" +
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
                L.Log("K2D2Window.OnUiReload: title_bar.Q<Button>(\"info-toggle\") returned null.");
            }
            if (staging_toggle == null)
            {
                L.Log("K2D2Window.OnUiReload: title_bar.Q<ToggleButton>(\"staging-toggle\") returned null.");
            }

            info_button?.RegisterCallback<ClickEvent>(evt => tab_page.Select("about"));
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

            _rootElement.AddManipulator(new DragManipulator(false, "main_window_pos"));

            // Drag-to-resize via the handle added to K2D2_Window.uxml's AppShell (bottom-right
            // corner, stock resize-handle.png). ResizeManipulator resizes _rootElement itself
            // (the AppShell) by attaching its pointer callbacks to the small handle element
            // instead of the whole window - see ResizeManipulator.cs.
            var resize_handle = _rootElement.Q<VisualElement>("resize-handle");
            if (resize_handle == null)
            {
                L.Log("K2D2Window.OnUiReload: Q<VisualElement>(\"resize-handle\") returned null - " +
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
            // tab_page isn't wired up until OnUiReload has run - guard rather than NRE on an early frame.
            tab_page?.Update();

            // Confirmed via Ksp2-2.log from Reese's stuck-resize repro: PointerUpEvent and
            // PointerCaptureOutEvent both simply never reached the resize handle for that gesture
            // (hasCapture had already gone False, yet neither event's log line ever printed) - so
            // ResizeManipulator was left with no event that could ever tell it the drag had ended,
            // and kept treating every later mouse move as more resizing. Rather than chase why this
            // game's UI Toolkit embedding drops those specific events, Tick() sidesteps the event
            // pipeline entirely: it reads the real OS mouse button state every frame and force-ends
            // the gesture the moment it's no longer actually held, regardless of what UI Toolkit
            // did or didn't deliver. This is the same reason DragManipulator hasn't been reported
            // stuck - dragging the whole window is a much easier target to keep the cursor over, so
            // it's presumably hit this same dropped-event failure far less often, not because it's
            // immune to it.
            _resizeManipulator?.Tick();
        }
    }
}
