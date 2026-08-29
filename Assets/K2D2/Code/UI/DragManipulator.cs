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
    /// </summary>
    public class DragManipulator : IManipulator
    {
        private VisualElement _target;
        private Vector3 _offset;
        // private PickingMode _mode;

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

                if (position_setting != null)  
                {
                    if (position_setting.V != invalid_vector)
                    {
                        _target.SetDefaultPosition( windowSize => clampWindow(
                            new Vector2(
                                position_setting.V.x, 
                                position_setting.V.y)));
                    }        
                }         

                _target.RegisterCallback<PointerDownEvent>(OnPointerDown);
                _target.RegisterCallback<PointerMoveEvent>(OnPointerMove);
                _target.RegisterCallback<PointerUpEvent>(OnPointerUp);
            }
        }

        Setting<Vector3> position_setting;
        Vector3 invalid_vector = new Vector3(-1000,-1000,-1000);

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

        public Vector3 clampWindow(Vector3 position)
        {
            position.x = Mathf.Clamp(
                position.x, 0,
                Configuration.CurrentScreenWidth - _target.resolvedStyle.width
            );
            position.y = Mathf.Clamp(
                position.y, 0,
                Configuration.CurrentScreenHeight - _target.resolvedStyle.height
            );

            return position;
        }

        /// <summary>
        /// Handles the initiation of the dragging process.
        /// </summary>
        private void OnPointerDown(PointerDownEvent evt)
        {
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

            // _mode = target.pickingMode;
            // target.pickingMode = PickingMode.Ignore;
            IsDragging = true;
            _offset = evt.localPosition;
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

            var delta = evt.localPosition - _offset;
            var newPosition = target.transform.position + delta;

            if (!AllowDraggingOffScreen)
            {
               newPosition = clampWindow(newPosition);
            }
            positon = newPosition;
            _target.transform.position = newPosition;
        }

        Vector3 positon;

        /// <summary>
        /// Handles the end of the dragging process.
        /// </summary>
        private void OnPointerUp(PointerUpEvent evt)
        {
            IsDragging = false;
            _target.ReleasePointer(evt.pointerId);

            // record the window position
            if (position_setting != null)
                position_setting.V = positon;
        }
    }
}