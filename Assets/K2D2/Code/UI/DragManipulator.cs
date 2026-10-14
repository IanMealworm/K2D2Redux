using UnityEngine;
using UnityEngine.UIElements;

using K2UI;
using KTools;
namespace K2D2.UI
{
    /// <summary>
    /// A manipulator to make UI Toolkit elements draggable within the screen bounds.
    ///
    /// The window is placed by its layout position (absolute style.left/top, relative to its
    /// parent), the same way UitkForKsp2's own mover does it. Bounds come from the live panel rect:
    /// Configuration.CurrentScreenWidth/Height are a fixed 1920x1080 reference size, not the real
    /// screen, so they put the walls in the wrong place at other resolutions and UI scales.
    /// Ported from LeoMarinDev's fix.
    /// </summary>
    public class DragManipulator : IManipulator
    {
        private VisualElement _target;

        // Where inside the window the pointer grabbed it, in panel space.
        private Vector2 _mouseOffsetInTarget;

        // Last position written, parent-local. Only a gesture that actually moved the window saves
        // it, so a plain click can't store (0,0) and send the window to the corner next launch.
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
                    // The saved position can only be clamped once the window has a real size and
                    // panel, so it's applied on the first layout pass, or now if that has happened.
                    _defaultPositionPending = true;
                    _target.RegisterCallback<GeometryChangedEvent>(OnGeometryChangedForDefaultPosition);
                    if (_target.panel != null && _target.layout.width > 0 && _target.layout.height > 0)
                        ApplySavedDefaultPosition();
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
            if (target is TargetType)
                return true;

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

        // The rect the window has to stay inside, in panel space (where pointer positions live).
        // Falls back to the screen size only when the window has no panel, e.g. during teardown.
        Rect GetScreenBounds()
        {
            var panel = _target?.panel;
            if (panel?.visualTree != null)
            {
                Rect rect = panel.visualTree.contentRect;
                if (rect.width > 0 && rect.height > 0)
                    return rect;
            }
            return new Rect(0, 0, Screen.width, Screen.height);
        }

        // resolvedStyle reads 0 before the first layout pass, so fall back to the rendered bounds.
        Vector2 GetTargetSize()
        {
            float w = _target.resolvedStyle.width;
            float h = _target.resolvedStyle.height;
            if (w > 0 && h > 0) return new Vector2(w, h);

            Vector2 size = _target.worldBound.size;
            if (size.x > 0 && size.y > 0) return size;

            return Vector2.zero;
        }

        // Clamps a panel-space top-left so the whole window stays on screen.
        Vector2 clampWindow(Vector2 position)
        {
            Rect bounds = GetScreenBounds();
            Vector2 size = GetTargetSize();

            position.x = Mathf.Clamp(position.x, bounds.xMin, bounds.xMin + Mathf.Max(0, bounds.width - size.x));
            position.y = Mathf.Clamp(position.y, bounds.yMin, bounds.yMin + Mathf.Max(0, bounds.height - size.y));
            return position;
        }

        VisualElement GetPositionParent() => _target.parent ?? _target.panel?.visualTree;

        // Writes a panel-space top-left as style.left/top in the parent's space, and clears any
        // translate left over from older versions that moved the window that way.
        void ApplyPanelPosition(Vector2 panelPosition)
        {
            var parent = GetPositionParent();
            Vector2 local = parent != null ? parent.WorldToLocal(panelPosition) : panelPosition;

            _target.style.position = Position.Absolute;
            _target.style.left = local.x;
            _target.style.top = local.y;
            _target.style.translate = new Translate(0, 0);

            _lastLocalPosition = local;
            _hasLocalPosition = true;
        }

        void OnGeometryChangedForDefaultPosition(GeometryChangedEvent evt)
        {
            // 0x0 while the window is still hidden (display:none).
            if (evt.newRect.width == 0 || evt.newRect.height == 0)
                return;

            if (IsDragging)
                return;

            ApplySavedDefaultPosition();
        }

        void ApplySavedDefaultPosition()
        {
            if (!_defaultPositionPending)
                return;
            _defaultPositionPending = false;
            _target.UnregisterCallback<GeometryChangedEvent>(OnGeometryChangedForDefaultPosition);

            // Saved as parent-local left/top. Clamped like a drag, so a position saved on a bigger
            // screen can't come back off-screen on a smaller one.
            Vector2 saved = new Vector2(position_setting.V.x, position_setting.V.y);
            var parent = GetPositionParent();
            Vector2 position = parent != null ? parent.LocalToWorld(saved) : saved;

            if (!AllowDraggingOffScreen)
                position = clampWindow(position);

            ApplyPanelPosition(position);
        }

        /// <summary>
        /// Handles the initiation of the dragging process.
        /// </summary>
        private void OnPointerDown(PointerDownEvent evt)
        {
            // Left button only: KSP2 rotates the camera with a right-drag, which otherwise moved
            // the window at the same time.
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
            // ResizeManipulator already stops its events from reaching this window-level handler;
            // this is a second check in case the game's event handling lets one through.
            if (checkTargetClass(target, "window-resize-handle")) return;

            // Placing the window by hand replaces any saved position still waiting to be applied.
            if (_defaultPositionPending)
            {
                _defaultPositionPending = false;
                _target.UnregisterCallback<GeometryChangedEvent>(OnGeometryChangedForDefaultPosition);
            }

            IsDragging = true;
            _mouseOffsetInTarget = (Vector2)evt.position - _target.worldBound.position;
            _hasLocalPosition = false;
            _target.CapturePointer(evt.pointerId);
        }

        /// <summary>
        /// Handles the movement of the draggable element.
        /// </summary>
        private void OnPointerMove(PointerMoveEvent evt)
        {
            if (!IsDragging || !IsEnabled)
                return;

            Vector2 desired = (Vector2)evt.position - _mouseOffsetInTarget;
            if (!AllowDraggingOffScreen)
                desired = clampWindow(desired);

            ApplyPanelPosition(desired);
        }

        /// <summary>
        /// Handles the end of the dragging process.
        /// </summary>
        private void OnPointerUp(PointerUpEvent evt)
        {
            IsDragging = false;
            _target.ReleasePointer(evt.pointerId);

            if (position_setting != null && _hasLocalPosition)
                position_setting.V = new Vector3(_lastLocalPosition.x, _lastLocalPosition.y, 0);

            _hasLocalPosition = false;
        }
    }
}
