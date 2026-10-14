using System.Collections.Generic;
using K2UI;
using UnityEngine.UIElements;

namespace K2D2.UI
{
    // A tab's label/value info table (LIFT INFO, NODE INFO...), refilled every UI tick between
    // Begin() and End(). The rows are kept and their text updated in place: clearing and
    // rebuilding them every tick made the whole window lay itself out again every frame.
    public class InfoTable
    {
        readonly VisualElement root;
        readonly List<(VisualElement row, Label label, Label value)> rows = new();
        int used;

        public InfoTable(VisualElement root)
        {
            this.root = root;
        }

        public void Begin()
        {
            used = 0;
        }

        public void Add(string label, string value)
        {
            if (used == rows.Count)
            {
                var row = new VisualElement();
                row.AddToClassList("advanced-info-row");
                var label_el = new Label();
                label_el.AddToClassList("advanced-info-row-label");
                row.Add(label_el);
                var value_el = new Label();
                value_el.AddToClassList("advanced-info-row-value");
                row.Add(value_el);
                root.Add(row);
                rows.Add((row, label_el, value_el));
            }

            var (row_el, l, v) = rows[used++];
            // Label.text only redraws when the text actually changes.
            l.text = label;
            v.text = value;
            row_el.Show(true);
        }

        // Hides the rows this tick didn't use.
        public void End()
        {
            for (int i = used; i < rows.Count; i++)
                rows[i].row.Show(false);
        }
    }
}
