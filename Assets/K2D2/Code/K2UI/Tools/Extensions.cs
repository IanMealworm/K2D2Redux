using System;
using System.Collections.Generic;
using UnityEngine.UIElements;
using KTools;

namespace K2UI
{
    /// <summary>
    /// Extensions usied for VisualElement
    /// </summary>
    public static class VisualElementExtension
    {
        // F1: one last-written-value cache per (element, property kind). Each cache is keyed by the
        // element, so every writer of that property for that element must go through the helper -
        // a raw write beside a cached one makes the two disagree and the element sticks (see the
        // P1 recon, section 9 item 4).
        static readonly Dictionary<VisualElement, string> _last_text = new Dictionary<VisualElement, string>();
        static readonly Dictionary<VisualElement, DisplayStyle> _last_display = new Dictionary<VisualElement, DisplayStyle>();
        static readonly Dictionary<VisualElement, string> _last_class = new Dictionary<VisualElement, string>();

        public static void Clean(this VisualElement el)
        {
            var count = el.childCount;
            for (int i=0; i< count; i++)
            {
                el.RemoveAt(0);
            }
        }

        /// <summary>
        /// `style.display`, written only when the value differs from the last one written for this
        /// element. The bool overload must not be used beside this one for the same element.
        /// </summary>
        public static void Show(this VisualElement element, bool show)
        {
            element.SetDisplay(show ? DisplayStyle.Flex : DisplayStyle.None);
        }

        public static void SetDisplay(this VisualElement element, DisplayStyle value)
        {
            if (element == null)
                return;

            DisplayStyle last;
            if (_last_display.TryGetValue(element, out last) && last == value)
            {
                K2UiWriteStats.Skipped();
                return;
            }

            _last_display[element] = value;
            element.style.display = value;
            K2UiWriteStats.Performed();
        }

        /// <summary>`Label.text`, written only when the string differs from the last one written.</summary>
        public static void SetText(this Label label, string value)
        {
            if (label == null)
                return;

            string last;
            if (_last_text.TryGetValue(label, out last) && string.Equals(last, value, StringComparison.Ordinal))
            {
                K2UiWriteStats.Skipped();
                return;
            }

            _last_text[label] = value;
            label.text = value;
            K2UiWriteStats.Performed();
        }

        /// <summary>
        /// The status-line level class. This cache owns exactly one class on the element: the level
        /// class it wrote last. The value may be null/empty, which removes the owned class and
        /// claims nothing. One level change is one write unit (add + remove counted together),
        /// consistently with one `Show()` being one unit.
        /// </summary>
        public static void SetClass(this VisualElement element, string value)
        {
            if (element == null)
                return;

            string last;
            if (_last_class.TryGetValue(element, out last) && string.Equals(last, value, StringComparison.Ordinal))
            {
                K2UiWriteStats.Skipped();
                return;
            }

            // One write unit: release the class this cache owns, then claim the new one.
            if (!string.IsNullOrEmpty(last))
                element.RemoveFromClassList(last);
            if (!string.IsNullOrEmpty(value))
                element.AddToClassList(value);

            _last_class[element] = value;
            K2UiWriteStats.Performed();
        }

        /// <summary>
        /// F2: drop every cached value so the next frame's writes all happen again. Non-negotiable
        /// companion to the caches above - after a hide/show cycle, a tab switch, a game-state
        /// transition or a teardown the tree may carry UXML-default styling while the caches still
        /// say "already written", which is exactly FlightPlan's launch-8 regression. Also re-arms
        /// the F8 counters so the write burst that follows is measured from zero.
        /// </summary>
        public static void InvalidateUiCaches()
        {
            _last_text.Clear();
            _last_display.Clear();
            _last_class.Clear();

            K2UiWriteStats.ReArm();
        }

        public static IntegerField Bind(this IntegerField element, Setting<int> setting) 
        {
            element.value = setting.V;
            setting.listeners += v => element.value = v;
            element.RegisterCallback<ChangeEvent<int>>(evt => setting.V = evt.newValue);
            element.isDelayed = true;
            return element;
        }

        public static FloatField Bind(this FloatField element, Setting<float> setting)
        {
            element.value = setting.V;
            setting.listeners += v => element.value = v;
            element.RegisterCallback<ChangeEvent<float>>(evt => setting.V = evt.newValue);
            element.isDelayed = true;
            return element;
        }

        public static Label AddLine(this Label label, string text)
        {
            label.text += "\n" + text;
            return label;
        }

        public delegate void onClicFct();
        public static VisualElement listenClick(this VisualElement button, onClicFct on_clic)
        {
            button.RegisterCallback<ClickEvent>(evt =>
            {
                on_clic();
            });

            return button;
        }
    }
}
