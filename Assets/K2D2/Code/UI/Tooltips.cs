using UnityEngine;
using UnityEngine.UIElements;

namespace K2D2.UI
{
    // Shows the "tooltip" text of whatever control the pointer rests on. UI Toolkit only draws
    // tooltips in the Unity editor, so in game they never appeared; this draws them itself, as a
    // small box just under the control (or above it near the bottom of the window).
    public class Tooltips
    {
        const long show_after_ms = 500;
        const float gap = 4, edge = 6;

        readonly VisualElement host;
        readonly Label box;
        VisualElement owner;
        IVisualElementScheduledItem pending;

        // host: the element tooltips are drawn inside (the window frame); every control under it
        // with a tooltip gets one.
        public Tooltips(VisualElement host)
        {
            this.host = host;

            box = new Label();
            box.AddToClassList("k2-tooltip");
            box.pickingMode = PickingMode.Ignore;
            box.style.position = Position.Absolute;
            box.style.display = DisplayStyle.None;
            host.hierarchy.Add(box);

            host.RegisterCallback<PointerOverEvent>(OnPointerOver, TrickleDown.TrickleDown);
            host.RegisterCallback<PointerLeaveEvent>(evt => Hide());
            host.RegisterCallback<PointerDownEvent>(evt => Hide(), TrickleDown.TrickleDown);
            host.RegisterCallback<WheelEvent>(evt => Hide(), TrickleDown.TrickleDown);
        }

        void OnPointerOver(PointerOverEvent evt)
        {
            var found = FindOwner(evt.target as VisualElement);
            if (found == owner)
                return;
            Hide();
            owner = found;
            if (owner != null)
                pending = host.schedule.Execute(Show).StartingIn(show_after_ms);
        }

        // The control under the pointer, or the nearest of its parents, that has a tooltip.
        VisualElement FindOwner(VisualElement element)
        {
            for (var e = element; e != null && e != host; e = e.parent)
                if (!string.IsNullOrEmpty(e.tooltip) && e.resolvedStyle.display != DisplayStyle.None)
                    return e;
            return null;
        }

        void Show()
        {
            if (owner == null || owner.panel == null)
                return;

            var parent = box.parent;
            float width = parent.layout.width;
            box.text = owner.tooltip;
            box.style.maxWidth = Mathf.Max(80, width - 2 * edge);
            box.style.display = DisplayStyle.Flex;
            box.style.visibility = Visibility.Hidden;
            box.BringToFront();

            // Placed once its size is known: under the control, or above it if it would run past
            // the bottom of the window.
            box.schedule.Execute(() =>
            {
                if (owner == null)
                    return;
                Rect bounds = owner.worldBound;
                Vector2 below = parent.WorldToLocal(new Vector2(bounds.xMin, bounds.yMax));
                Vector2 above = parent.WorldToLocal(new Vector2(bounds.xMin, bounds.yMin));
                float w = box.layout.width, h = box.layout.height;

                float left = Mathf.Clamp(below.x, edge, Mathf.Max(edge, width - w - edge));
                float top = below.y + gap;
                if (top + h > parent.layout.height - edge)
                    top = Mathf.Max(edge, above.y - gap - h);

                box.style.left = left;
                box.style.top = top;
                box.style.visibility = Visibility.Visible;
            });
        }

        void Hide()
        {
            pending?.Pause();
            pending = null;
            owner = null;
            box.style.display = DisplayStyle.None;
        }
    }
}
