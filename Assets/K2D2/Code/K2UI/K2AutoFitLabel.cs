namespace K2UI
{
    using UnityEngine;
    using UnityEngine.UIElements;

    // UxmlFactory/UxmlTraits -> [UxmlElement] (see Group.cs's class comment for why).
    // K2AutoFitLabel exposes no attributes beyond Label's own standard ones (including "name"),
    // which UI Toolkit itself already handles - nothing else to convert here. Not currently
    // referenced from any UXML tag in the project either way.
    [UxmlElement]
    public partial class K2AutoFitLabel : Label
    {
        public K2AutoFitLabel()
        {
            RegisterCallback<AttachToPanelEvent>(OnAttachToPanel);
        }

        private void OnAttachToPanel(AttachToPanelEvent e)
        {
            UnregisterCallback<AttachToPanelEvent>(OnAttachToPanel);
            RegisterCallback<GeometryChangedEvent>(OnGeometryChanged);
        }

        private void OnGeometryChanged(GeometryChangedEvent e)
        {
            UpdateFontSize();
            e.StopPropagation();
        }

        private void UpdateFontSize()
        {
            UnregisterCallback<GeometryChangedEvent>(OnGeometryChanged);
            var previousWidthStyle = style.width;

            try
            {
                var width = resolvedStyle.width;
                var height = resolvedStyle.height;

                // Set width to auto temporarily to get the actual width of the label
                style.width = StyleKeyword.Auto;
                var currentFontSize = MeasureTextSize(text, 0, MeasureMode.Undefined, 0, MeasureMode.Undefined);

                var multiplier = width / Mathf.Max(currentFontSize.x, 1);
                var newFontSize = Mathf.RoundToInt(Mathf.Clamp(multiplier * currentFontSize.y, 1, height));
                //   Debug.Log("newFontSize"+newFontSize);

                if (Mathf.RoundToInt(currentFontSize.y) != newFontSize)
                    style.fontSize = new StyleLength(new Length(newFontSize));
            }
            finally
            {
                style.width = previousWidthStyle;
                RegisterCallback<GeometryChangedEvent>(OnGeometryChanged);
            }
        }
    }
}
