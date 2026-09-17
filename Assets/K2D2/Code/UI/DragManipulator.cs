using UitkForKsp2;
using UitkForKsp2.API;
using UnityEngine;
using UnityEngine.UIElements;


using K2UI;
using KTools;
namespace K2D2.UI
{
    /// <summary>
    /// A manipulator to make UI Toolkit elements draggable within the screen bounds.
    ///
    /// POSITIONING MODEL (rewritten for Redux 0.2.8.5 / Unity 6000.4.1f1)
    /// -----------------------------------------------------------------
    /// The window position is the element's LAYOUT position: `style.position = absolute` plus
    /// `style.left` / `style.top`, relative to the parent (its containing block). That is what the
    /// saved setting stores, what the restore path re-applies, and what the drag bounds are
    /// expressed against. UitkForKsp2's own DragManipulator does the same - its IL (installed
    /// UitkForKsp2.dll, UitkForKsp2.API.Manipulator.DragManipulator.OnPointerMove) computes a
    /// panel-space target position from the pointer, clamps it against
    /// `panel.visualTree.contentRect`, converts it to the parent's local space with WorldToLocal
    /// and assigns style.left/top, zeroing transform.position.
    ///
    /// WHY THE PREVIOUS CODE WAS WRONG - `transform.position` is not style.left/top here
    /// --------------------------------------------------------------------------------
    /// The old implementation moved the window with `_target.transform.position`. In this Unity
    /// that is NOT the layout position: ITransform.get_position returns `resolvedStyle.translate`
    /// and set_position writes `style.translate` (IL of the installed UnityEngine.UIElementsModule
    /// .dll, VisualElement::UnityEngine.UIElements.ITransform.get_position/set_position). So the
    /// drag wrote a CSS **translate** offset on top of a layout position that itself came from the
    /// saved setting, and the clamp
    ///     Mathf.Clamp(position.x, 0, Configuration.CurrentScreenWidth - width)
    /// clamped that translate to [0, 1920-ish]. Two user-visible defects follow:
    ///   * translate can never go below 0, so the window's left edge is pinned at its layout x -
    ///     it reads as a wall that is not the screen edge whenever layout x is not 0, and the
    ///     window can never be dragged back to the left side;
    ///   * the right bound is 1920 reference units, not the real panel width, so the window's
    ///     right edge can reach layout.x + 1920 - well past the real right wall.
    /// `Configuration.CurrentScreenWidth/Height` were never a screen size: they forward to
    /// UitkForKsp2.API.ReferenceResolution.Width/Height, which are compile-time 1920x1080
    /// (ReferenceResolution::.cctor) and are never updated at runtime.
    ///
    /// The bounds below are derived from the live containing rect instead, so they are correct at
    /// any resolution and any UI scale, and they are re-read on every event (a couple of struct
    /// reads), so a panel resize cannot leave a stale wall behind.
    ///
    /// The saved position is applied exactly once, on the first real layout pass, and never on top
    /// of a live drag - see OnGeometryChangedForDefaultPosition. (UitkForKsp2's
    /// Extensions.SetDefaultPosition is also one-shot - its GeometryChangedHandler unregisters
    /// itself via UnregisterCallback immediately after applying - but this class owns the handler so
    /// it can also yield to a drag in progress, and so the saved value round-trips in the same
    /// parent-local left/top units this manipulator writes.)
    /// </summary>
    public class DragManipulator : IManipulator
    {
        private VisualElement _target;

        // Pointer offset inside the target at PointerDown, in panel space. OnPointerMove subtracts
        // it from the pointer's panel position to get the desired panel-space top-left - the same
        // model UitkForKsp2's own DragManipulator uses. Unlike the old localPosition-based delta
        // math this does not feed the window's own motion back into the calculation.
        private Vector2 _mouseOffsetInTarget;

        // The last parent-local left/top this manipulator wrote, so PointerUp can persist a real
        // position instead of a default(0,0,0) for a click that never moved.
        private Vector2 _lastLocalPosition;
        private bool _hasLocalPosition;

        /// <summary>
        /// Indicates whether the element is currently being dragged.
        /// </summary>
        public bool IsDragging { get; private set; }

        /// <summary>
        /// Enables or disables the dragging functionality.
        /// </summary>
        public bool IsEnabled { get; set; } = true;

        /// <summary>
        /// Indicates whether the element can be dragged off screen.
        /// </summary>
        public bool AllowDraggingOffScreen { get; set; }

        /// <summary>
        /// The target element that will be made draggable.
        /// </summary>
        public VisualElement target
        {
            get => _target;
            set
            {
                _target = value;

                if (position_setting != null && position_setting.V != invalid_vector)
                {
                    _defaultPositionPending = true;
                    _target.RegisterCallback<GeometryChangedEvent>(OnGeometryChangedForDefaultPosition);

                    // If the element is already laid out (wired while visible), the callback above
                    // would only fire on the *next* geometry change; apply now instead.
                    try
                    {
                        if (_target.layout.width > 0 && _target.layout.height > 0 && _target.panel != null)
                            ApplySavedDefaultPosition();
                    }
                    catch { }
                }

                _target.RegisterCallback<PointerDownEvent>(OnPointerDown);
                _target.RegisterCallback<PointerMoveEvent>(OnPointerMove);
                _target.RegisterCallback<PointerUpEvent>(OnPointerUp);
            }
        }

        Setting<Vector3> position_setting;
        Vector3 invalid_vector = new Vector3(-1000,-1000,-1000);
        bool _defaultPositionPending;

        /// <summary>
        /// Creates a new instance of the <see cref="DragManipulator"/> class.
        /// </summary>
        /// <param name="allowDraggingOffScreen">Allow dragging off screen?</param>
        public DragManipulator(bool allowDraggingOffScreen = false, string save_setting = null)
        {
            AllowDraggingOffScreen = allowDraggingOffScreen;

            if (save_setting != null)
                position_setting = new Setting<Vector3>(save_setting, invalid_vector);
        }

        /// <summary>
        /// recursive search in parent element for a type
        /// </summary>
        /// <typeparam name="TargetType">the type we search</typeparam>
        /// <param name="target">target element</param>
        /// <param name="nb_parents">nb parents that will be checked</param>
        /// <returns>true if the type is valid or if one of the parent type is valid</returns>
        private bool checkTargetType<TargetType>(VisualElement target, int nb_parents = 5)
        {
            // Debug.Log("type is " + target);
            if (target is TargetType)
            {
                // Debug.Log("OK " + target);
                return true;
            }

            if (nb_parents == 0)
                return false;

            if (target.parent == null)
                return false;

            nb_parents--;

            return checkTargetType<TargetType>(target.parent, nb_parents);
        }

        /// <summary>
        /// Same recursive parent walk as <see cref="checkTargetType{TargetType}"/>, but matching a
        /// USS class name instead of a C# type - for plain VisualElements (like the resize handle)
        /// that don't have a dedicated type of their own to check against.
        /// </summary>
        private bool checkTargetClass(VisualElement target, string className, int nb_parents = 5)
        {
            if (target.ClassListContains(className))
                return true;

            if (nb_parents == 0)
                return false;

            if (target.parent == null)
                return false;

            nb_parents--;

            return checkTargetClass(target.parent, className, nb_parents);
        }

        /// <summary>
        /// The rect the window must stay inside, in PANEL coordinates - the space
        /// <see cref="PointerMoveEvent.position"/> and the parent-local style.left/top live in.
        /// The live panel root is the authority: it is laid out in the panel's own units, so it is
        /// already correct for any screen resolution and any PanelSettings scale/scale-mode, and it
        /// is a live read, not a stored constant. The Screen fallback is in real pixels and can
        /// disagree with panel units when a scale is applied; it exists only so an element with no
        /// panel (during teardown) cannot put the window at (0,0) forever.
        /// </summary>
        public Rect GetScreenBounds()
        {
            try
            {
                IPanel panel = _target != null ? _target.panel : null;
                if (panel != null && panel.visualTree != null)
                {
                    Rect rect = panel.visualTree.contentRect;
                    if (rect.width > 0 && rect.height > 0)
                        return rect;
                }
            }
            catch { }

            return new Rect(0, 0, Screen.width, Screen.height);
        }

        /// <summary>
        /// The element's own size, used for the "don't overlap the far wall" term. resolvedStyle is
        /// the authoritative laid-out style size, but it reads 0 before the first layout pass and
        /// while the element is display:none - blindly trusting it collapses the far wall onto the
        /// panel edge and lets a normal-sized window be pushed out of the right/bottom. So fall
        /// back to worldBound (the rendered bounds, which include style borders but not overflow)
        /// and then layout. If all three read 0, Vector2.zero is returned and the caller treats the
        /// size as unknown (the position is then still kept inside the panel rect).
        /// </summary>
        public Vector2 GetTargetSize()
        {
            if (_target == null) return Vector2.zero;

            try
            {
                float w = _target.resolvedStyle.width;
                float h = _target.resolvedStyle.height;
                if (w > 0 && h > 0) return new Vector2(w, h);
            }
            catch { }

            try
            {
                Vector2 size = _target.worldBound.size;
                if (size.x > 0 && size.y > 0) return size;
            }
            catch { }

            try
            {
                Vector2 size = _target.layout.size;
                if (size.x > 0 && size.y > 0) return size;
            }
            catch { }

            return Vector2.zero;
        }

        /// <summary>
        /// Clamps a desired window top-left, in panel coordinates, to the live screen rect:
        /// x in [panel.xMin, panel.xMin + max(0, panel.width - window.width)] (same for y). When
        /// the window's size reads 0 (see <see cref="GetTargetSize"/>) only the panel edges are
        /// enforced.
        /// </summary>
        public Vector3 clampWindow(Vector3 position)
        {
            Rect bounds = GetScreenBounds();
            Vector2 size = GetTargetSize();

            float maxX = bounds.xMin + Mathf.Max(0, bounds.width - size.x);
            float maxY = bounds.yMin + Mathf.Max(0, bounds.height - size.y);

            position.x = Mathf.Clamp(position.x, bounds.xMin, maxX);
            position.y = Mathf.Clamp(position.y, bounds.yMin, maxY);

            return position;
        }

        /// <summary>
        /// The element the layout position is relative to (style.left/top are parent-local). The
        /// panel root is the fallback for an element that has no parent yet.
        /// </summary>
        private VisualElement GetPositionParent()
        {
            if (_target == null) return null;

            VisualElement parent = _target.parent;
            if (parent != null) return parent;

            IPanel panel = _target.panel;
            return panel != null ? panel.visualTree : null;
        }

        /// <summary>
        /// Writes a panel-space top-left as the element's layout position (style.left/top,
        /// converted to the parent's local space) and clears any leftover CSS translate, so the
        /// layout position is the only thing moving the window.
        /// </summary>
        private void ApplyPanelPosition(Vector2 panelPosition)
        {
            if (_target == null) return;

            VisualElement parent = GetPositionParent();
            Vector2 local = parent != null
                ? VisualElementExtensions.WorldToLocal(parent, panelPosition)
                : panelPosition;

            _target.style.position = Position.Absolute;
            _target.style.left = local.x;
            _target.style.top = local.y;
            _target.transform.position = Vector3.zero;

            _lastLocalPosition = local;
            _hasLocalPosition = true;
        }

        /// <summary>
        /// Applies the saved position once, on the first real layout pass. Skips - without
        /// unregistering - while a drag is live, so a restored default can never override the
        /// user's gesture; the handler is dropped as soon as the user starts positioning the
        /// window by hand (see OnPointerDown).
        /// </summary>
        private void OnGeometryChangedForDefaultPosition(GeometryChangedEvent evt)
        {
            // display:none reports 0x0 - the window has not been opened/laid out yet.
            if (evt.newRect.width == 0 || evt.newRect.height == 0)
                return;

            // A drag in progress always wins over the restored default.
            if (IsDragging)
                return;

            ApplySavedDefaultPosition();
        }

        private void ApplySavedDefaultPosition()
        {
            if (!_defaultPositionPending || position_setting == null)
                return;
            _defaultPositionPending = false;
            _target.UnregisterCallback<GeometryChangedEvent>(OnGeometryChangedForDefaultPosition);

            Vector2 saved = new Vector2(position_setting.V.x, position_setting.V.y);

            // The setting is stored as parent-local left/top (see OnPointerUp). Convert it to panel
            // space so the same live clamp the drag uses applies here too: a position saved on a
            // bigger panel must not be restored off-screen on a smaller one.
            VisualElement parent = GetPositionParent();
            Vector2 panelPosition = parent != null
                ? VisualElementExtensions.LocalToWorld(parent, saved)
                : saved;

            Vector3 position = panelPosition;
            if (!AllowDraggingOffScreen)
                position = clampWindow(position);

            ApplyPanelPosition(position);
        }

        /// <summary>
        /// Handles the initiation of the dragging process.
        /// </summary>
        private void OnPointerDown(PointerDownEvent evt)
        {
            // Left button only. This manipulator registers PointerDownEvent directly, which bypasses
            // MouseManipulator's `activators` list - the mechanism whose default is left-button-only.
            // Without this guard a RIGHT-click starts a window drag, and KSP2 uses right-drag for
            // camera rotation, so right-dragging over the window moved the window and the camera at
            // once (launch-2 user report). FlightPlan has no such bug because it uses the library's
            // own mover, which is a MouseManipulator and therefore filtered by its activators.
            if (evt.button != 0)
                return;

            if (!(evt.target is VisualElement))
                return;
            VisualElement target = evt.target as VisualElement;

            if (!IsEnabled) return;
            if (checkTargetType<IntegerField>(target)) return;
            if (checkTargetType<FloatField>(target)) return;
            if (checkTargetType<TextField>(target)) return;
            if (checkTargetType<K2Toggle>(target)) return;
            if (checkTargetType<K2Compass>(target)) return;
            // The resize handle is a plain VisualElement (no dedicated C# type to check via
            // checkTargetType<T>), and it's a child of whatever this manipulator's target is
            // (the whole window) - so without this exclusion, every resize-handle drag also
            // bubbled into here and moved the entire window at the same time as it was resizing.
            // ResizeManipulator now calls evt.StopPropagation() itself, which should already
            // prevent this - this is a second, class-name-based check kept as cheap insurance,
            // matching this codebase's existing habit of not fully trusting this game's UI
            // Toolkit event pipeline (see ResizeManipulator's Tick() watchdog for the same idea).
            if (checkTargetClass(target, "window-resize-handle")) return;

            // The user is positioning the window by hand now: a pending default position must not
            // land on top of this gesture (or right after it), so drop it.
            if (_defaultPositionPending)
            {
                _defaultPositionPending = false;
                _target.UnregisterCallback<GeometryChangedEvent>(OnGeometryChangedForDefaultPosition);
            }

            IsDragging = true;
            _mouseOffsetInTarget = (Vector2)evt.position - (Vector2)_target.worldBound.position;
            _hasLocalPosition = false; // a gesture that never moves must not save a position
            _target.CapturePointer(evt.pointerId);
        }

        /// <summary>
        /// Handles the movement of the draggable element.
        /// </summary>
        private void OnPointerMove(PointerMoveEvent evt)
        {
            if (!IsDragging || !IsEnabled)
            {
                return;
            }

            // Panel-space desired top-left of the window, from the pointer position minus the grab
            // offset captured at PointerDown.
            Vector2 desired = (Vector2)evt.position - _mouseOffsetInTarget;

            if (!AllowDraggingOffScreen)
                desired = clampWindow(desired);

            // Clamped in panel space (where evt.position lives), then converted parent-local and
            // written as style.left/top - the position model described on the class.
            ApplyPanelPosition(desired);
        }

        /// <summary>
        /// Handles the end of the dragging process.
        /// </summary>
        private void OnPointerUp(PointerUpEvent evt)
        {
            IsDragging = false;
            _target.ReleasePointer(evt.pointerId);

            // Record the window position - parent-local left/top, the same units the restore path
            // reads back. Only a gesture that actually moved saves: the old code stored
            // default(0,0,0) on every PointerUp, so a plain click could teleport the window to the
            // top-left corner on the next launch.
            if (position_setting != null && _hasLocalPosition)
                position_setting.V = new Vector3(_lastLocalPosition.x, _lastLocalPosition.y, 0);

            _hasLocalPosition = false;
        }
    }
}
