using UitkForKsp2;
using UitkForKsp2.API;
using UnityEngine;
using UnityEngine.UIElements;
using KTools;

namespace K2D2.UI
{
    /// <summary>
    /// A manipulator that lets a small drag-handle element resize a separate window element's
    /// HEIGHT ONLY, following the same pointer-capture pattern as DragManipulator (which moves a
    /// window via transform.position). Width stays whatever K2D2_Window.uxml sets it to. The UXML
    /// side reuses Redux's own stock ".window-resize-handle"/".window-resize-handle-icon" CSS
    /// classes and resize-handle.png asset (KerbalUI.uss), since Redux ships that styling but no
    /// working C# behavior for it anywhere in the package.
    ///
    /// Two game-embedding quirks this works around:
    ///
    /// 1. Releasing the mouse button over this small handle does not reliably deliver either
    ///    PointerUpEvent or PointerCaptureOutEvent back to it, leaving the manipulator with no
    ///    event to signal that the drag ended - it would otherwise keep treating later mouse moves
    ///    as more resizing. Tick() (called every frame from K2D2Window.Update()) works around this
    ///    by polling the real OS mouse button state directly, ending the gesture within a frame of
    ///    the button coming up regardless of what events do or don't arrive.
    ///
    /// 2. KTools.SettingsFile.Set[T] only special-cases string/bool/int/float/double/Color/Vector3,
    ///    not Vector2, so persisting a Vector2 size threw InvalidCastException on every resize end.
    ///    Since this only tracks height, it persists as a plain Setting&lt;float&gt; instead, which
    ///    SettingsFile already supports natively.
    /// </summary>
    public class ResizeManipulator : IManipulator
    {
        // The handle element itself - this is what receives pointer events.
        private VisualElement _target;

        // The window element whose height actually gets changed. Width is never touched.
        private readonly VisualElement _resizeTarget;

        /// <summary>
        /// Indicates whether the handle is currently being dragged.
        /// </summary>
        public bool IsResizing { get; private set; }

        /// <summary>
        /// Enables or disables the resizing functionality.
        /// </summary>
        public bool IsEnabled { get; set; } = true;

        // Placeholder bounds, tunable to taste. Below MinHeight the window would start clipping
        // its own header/toolbar; above MaxHeight it stops being a compact overlay panel.
        public float MinHeight { get; set; } = 250;
        public float MaxHeight { get; set; } = 900;

        Setting<float> size_setting;
        const float invalid_height = -1f;

        private float _pointerStartY;
        private float _heightStart;

        // Pointer is captured and down, but hasn't necessarily moved past DragThresholdPx yet -
        // distinct from IsResizing, which only turns on once it has. Without this, a plain click
        // (which always has a pixel or two of jitter between down and up) nudged the window size
        // by that same tiny amount every time - "it does stuff even when you just touch it".
        private bool _pointerDown;
        private const float DragThresholdPx = 4f;

        // Which pointer we captured - ignore events from any other pointer id instead of treating
        // them as ours (matters if e.g. a controller/touch pointer and the mouse both generate
        // events; DragManipulator doesn't check this either, but it's cheap insurance here).
        private int _pointerId = -1;

        // Debug instrumentation, cheap to keep in case resizing surfaces a new edge case. Every
        // line is prefixed "ResizeManipulator:" so it's easy to filter for in the log. PointerMove
        // is throttled (every 30th call while dragging) since it fires every frame and would
        // otherwise flood the log.
        private int _moveLogCount;

        /// <summary>
        /// The handle element that will be made draggable to resize <see cref="_resizeTarget"/>.
        /// </summary>
        public VisualElement target
        {
            get => _target;
            set
            {
                _target = value;

                _target.RegisterCallback<PointerDownEvent>(OnPointerDown);
                _target.RegisterCallback<PointerMoveEvent>(OnPointerMove);
                _target.RegisterCallback<PointerUpEvent>(OnPointerUp);

                // Safety net for when PointerCaptureOutEvent does fire (it doesn't always, per the
                // class doc comment, which is why Tick() exists too) - ending the gesture here too
                // costs nothing since EndInteraction() is safe to call redundantly.
                _target.RegisterCallback<PointerCaptureOutEvent>(OnPointerCaptureOut);
            }
        }

        /// <summary>
        /// Creates a new instance of the <see cref="ResizeManipulator"/> class.
        /// </summary>
        /// <param name="resizeTarget">The window element to resize (its style.height is set directly; width is never touched).</param>
        /// <param name="save_setting">Optional Setting key to persist/restore the chosen height under.</param>
        public ResizeManipulator(VisualElement resizeTarget, string save_setting = null)
        {
            _resizeTarget = resizeTarget;

            if (save_setting != null)
                size_setting = new Setting<float>(save_setting, invalid_height);

            if (size_setting != null && size_setting.V != invalid_height)
                ApplyHeight(size_setting.V);
        }

        /// <summary>
        /// Call once per frame (from K2D2Window.Update()). Watchdog for the case where UI Toolkit
        /// never delivers PointerUp/PointerCaptureOut back to this handle: if we still think a
        /// gesture is active but the real mouse button is no longer down, end it here instead of
        /// waiting for an event that may never come.
        /// </summary>
        public void Tick()
        {
            if (!_pointerDown) return;

            // Left mouse button - matches what actually starts a resize (PointerDownEvent from a
            // standard left-click/drag). If this ever needs to support other buttons, this check
            // would need to track which button OnPointerDown saw (evt.button) instead of assuming 0.
            if (Input.GetMouseButton(0)) return;

            L.Log("ResizeManipulator: Tick() watchdog caught a gesture the event pipeline never " +
                  "ended - mouse button is up but pointerDown/IsResizing was still true.");

            if (_target != null && _pointerId >= 0 && _target.HasPointerCapture(_pointerId))
                _target.ReleasePointer(_pointerId);

            EndInteraction("Tick-watchdog");
        }

        void ApplyHeight(float height)
        {
            height = Mathf.Clamp(height, MinHeight, MaxHeight);

            // Keep the window from being resized past the bottom of the screen, the same idea
            // DragManipulator's clampWindow uses for position.
            height = Mathf.Min(height, Configuration.CurrentScreenHeight - _resizeTarget.resolvedStyle.top);

            _resizeTarget.style.height = height;

            // resolvedStyle/the actual on-screen render lags behind style changes in this game's
            // embedding, so force a repaint immediately after every height change instead of
            // waiting for this panel's own update cadence (same issue as K2Slider's dashed track).
            _resizeTarget.MarkDirtyRepaint();
        }

        private void OnPointerDown(PointerDownEvent evt)
        {
            if (!IsEnabled) return;

            // If we're already mid-gesture when a new PointerDown arrives, we never saw that
            // previous gesture end - that's the stuck-state signature itself, so log it loudly
            // before forcing a clean restart, rather than silently papering over it.
            if (_pointerDown)
                L.Log($"ResizeManipulator: PointerDown id={evt.pointerId} arrived while ALREADY " +
                      $"mid-gesture (prev id={_pointerId}, wasResizing={IsResizing}) - a previous " +
                      $"PointerUp/PointerCaptureOut was missed. Forcing a clean restart.");

            _pointerDown = true;
            IsResizing = false;
            _pointerId = evt.pointerId;
            _moveLogCount = 0;
            // Panel-space position, not localPosition - the handle itself moves (it's pinned to
            // the window's bottom-right corner) as a side effect of resizing, so its own local
            // coordinate space isn't a stable frame to measure drag distance against the way
            // DragManipulator's localPosition-based math can for a window that only translates.
            _pointerStartY = evt.position.y;
            _heightStart = _resizeTarget.resolvedStyle.height;

            _target.CapturePointer(evt.pointerId);
            L.Log($"ResizeManipulator: PointerDown id={evt.pointerId} y={_pointerStartY} " +
                  $"startHeight={_heightStart} hasCapture={_target.HasPointerCapture(evt.pointerId)}");

            // The handle is a child of _rootElement, which DragManipulator is also attached to for
            // whole-window dragging - without this, the same PointerDownEvent bubbles up from the
            // handle into DragManipulator's own handler on every resize gesture, setting its
            // IsDragging=true too, so a drag on the handle would simultaneously resize and move
            // the window. Stopping propagation here (and in OnPointerMove/OnPointerUp below) keeps
            // this gesture exclusive to the handle.
            evt.StopPropagation();
        }

        private void OnPointerMove(PointerMoveEvent evt)
        {
            if (!_pointerDown || !IsEnabled || evt.pointerId != _pointerId) return;

            // See the matching comment in OnPointerDown - keep every move of this gesture from
            // also reaching DragManipulator on _rootElement.
            evt.StopPropagation();

            float deltaY = evt.position.y - _pointerStartY;

            // Don't commit to resizing until the drag clears a small threshold - see _pointerDown's
            // comment. Once it does, treat it like the drag started right here so there's no jump.
            if (!IsResizing)
            {
                if (Mathf.Abs(deltaY) < DragThresholdPx)
                    return;

                IsResizing = true;
                L.Log($"ResizeManipulator: threshold crossed, resizing started, deltaY={deltaY}");
            }

            _moveLogCount++;
            if (_moveLogCount % 30 == 0)
                L.Log($"ResizeManipulator: PointerMove #{_moveLogCount} id={evt.pointerId} " +
                      $"deltaY={deltaY} hasCapture={_target.HasPointerCapture(_pointerId)} " +
                      $"height={_resizeTarget.resolvedStyle.height}");

            ApplyHeight(_heightStart + deltaY);
        }

        private void OnPointerUp(PointerUpEvent evt)
        {
            // Logged unconditionally, even the id-mismatch/not-mid-gesture case below, so a stuck
            // episode shows whether PointerUp is arriving at all (and just being filtered out) or
            // never arriving in the first place - those point to very different bugs.
            L.Log($"ResizeManipulator: PointerUp received id={evt.pointerId}, expected={_pointerId}, " +
                  $"pointerDown={_pointerDown}, isResizing={IsResizing}");

            if (!_pointerDown || evt.pointerId != _pointerId) return;

            // See the matching comment in OnPointerDown.
            evt.StopPropagation();

            _target.ReleasePointer(evt.pointerId);
            EndInteraction("PointerUp");
        }

        // Fires whenever pointer capture on the handle is released, for ANY reason - our own
        // ReleasePointer() in OnPointerUp above, but also anything external stealing/clearing
        // capture out from under us. Per the class doc comment, this doesn't always fire in this
        // game - Tick() is the reliable fallback - but ending the gesture here too when it does is
        // free (EndInteraction is safe to call redundantly).
        private void OnPointerCaptureOut(PointerCaptureOutEvent evt)
        {
            L.Log($"ResizeManipulator: PointerCaptureOut received id={evt.pointerId}, " +
                  $"pointerDown={_pointerDown}, isResizing={IsResizing}");

            EndInteraction("PointerCaptureOut");
        }

        private void EndInteraction(string reason)
        {
            if (!_pointerDown && !IsResizing) return;

            bool wasResizing = IsResizing;

            _pointerDown = false;
            IsResizing = false;
            _pointerId = -1;

            L.Log($"ResizeManipulator: EndInteraction({reason}) - wasResizing={wasResizing}, " +
                  $"finalHeight={_resizeTarget.resolvedStyle.height}");

            if (wasResizing && size_setting != null)
                size_setting.V = _resizeTarget.resolvedStyle.height;
        }
    }
}
