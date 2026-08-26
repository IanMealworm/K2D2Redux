using UnityEngine;
using UnityEngine.UIElements;
using System.Collections.Generic;
using KTools;
using K2D2;

namespace K2UI
{
    public class K2Slider : VisualElement
    {
        public new class UxmlFactory : UxmlFactory<K2Slider, UxmlTraits> { }

        public new class UxmlTraits : VisualElement.UxmlTraits
        {
            public override IEnumerable<UxmlChildElementDescription> uxmlChildElementsDescription
            {
                get { yield break; }
            }

            private UxmlStringAttributeDescription m_Label = new()
            { name = "label", defaultValue = "" };

            private UxmlBoolAttributeDescription m_labelOnTop = new()
            { name = "label-on-top", defaultValue = false };

            private UxmlBoolAttributeDescription m_printValue = new()
            { name = "print-value", defaultValue = false };

            private UxmlStringAttributeDescription m_MinMaxLabel = new()
            { name = "min-max-label", defaultValue = "" };

            private UxmlFloatAttributeDescription m_Value = new()
            { name = "value", defaultValue = 0f };

            private UxmlFloatAttributeDescription m_Min = new()
            {
                name = "min",
                defaultValue = 0f
            };

            private UxmlFloatAttributeDescription m_Max = new()
            {
                name = "max",
                defaultValue = 1f
            };

            // Base Init() no longer applies "name" in this Unity version, so it's re-applied here.
            private UxmlStringAttributeDescription m_Name = new()
            { name = "name", defaultValue = "" };

            public override void Init(VisualElement ve, IUxmlAttributes bag, CreationContext cc)
            {
                base.Init(ve, bag, cc);
                ve.name = m_Name.GetValueFromBag(bag, cc);
                K2Slider k2_slider = (K2Slider)ve;
                Slider main_slider = k2_slider.main_slider;

                k2_slider.value = m_Value.GetValueFromBag(bag, cc);
                k2_slider.printValue = m_printValue.GetValueFromBag(bag, cc);
                k2_slider.labelOnTop = m_labelOnTop.GetValueFromBag(bag, cc);
                k2_slider.minMaxLabel = m_MinMaxLabel.GetValueFromBag(bag, cc);

                k2_slider.Label = m_Label.GetValueFromBag(bag, cc);

                k2_slider.Min = m_Min.GetValueFromBag(bag, cc);
                k2_slider.Max = m_Max.GetValueFromBag(bag, cc);

                main_slider.direction = SliderDirection.Horizontal;//m_Direction.GetValueFromBag(bag, cc);
                main_slider.pageSize = 0;//m_PageSize.GetValueFromBag(bag, cc);
                main_slider.showInputField = false;//m_ShowInputField.GetValueFromBag(bag, cc);
                main_slider.inverted = false;//m_Inverted.GetValueFromBag(bag, cc);

                k2_slider.SliderValueChanged();
                k2_slider.setLabels();
            }
        }

        public float value
        {
            get { return main_slider.value; }
            set { 
                if (value == main_slider.value) return;
                main_slider.value = value; 
                listeners?.Invoke(value);
            }
        }

        public delegate void OnChanged(float value);

        public event OnChanged listeners;

        string _label;
        public string Label
        {
            get { return _label; }
            set
            {
                _label = value;
            }
        }

        bool _printValue = false;
        bool printValue
        {
            get { return _printValue; }
            set
            {
                _printValue = value;
                setLabels();
            }
        }

        // Optional override for the amber value text, for callers that need to show something
        // other than the slider's own raw float - e.g. Lift's 5°/45° Alt sliders, whose amber
        // reading is a computed, unit-suffixed altitude ("51 km") derived from this slider's 0-1
        // ratio, not the ratio itself. Sits alongside printValue rather than replacing it -
        // whichever is set makes the value label visible.
        string _value_text_override;
        public string ValueText
        {
            get { return _value_text_override; }
            set
            {
                _value_text_override = value;
                setLabels();
            }
        }

        bool _labelOnTop = true;
        bool labelOnTop
        {
            get { return _labelOnTop; }
            set
            {
                _labelOnTop = value;
                setLabels();
            }
        }

        string _min_max_label = "";
        public string minMaxLabel
        {
            get { return _min_max_label; }
            set
            {
                _min_max_label = value;
                setLabels();
            }
        }

        public float Min
        {
            get { return main_slider.lowValue; }
            set { main_slider.lowValue = value; }
        }
        public float Max
        {
            get { return main_slider.highValue; }
            set { main_slider.highValue = value; }
        }



        public void InitValues(float value, float min, float max)
        {
            Min = min;
            Max = max;
            this.value = value;
        }

        protected Slider main_slider;

        Label label_element;

        // Reese wants these rows to read like the game's own cockpit gauges: title text pinned
        // left, the live value pinned right in the same amber/yellow as those gauges, instead of
        // both baked into one "Label : value" string (which can only be one alignment/color for
        // its whole length). label_row wraps both so a single Insert(0, ...) in setLabelPos still
        // moves them together as before.
        VisualElement label_row;
        Label value_label_element;

        VisualElement dragger;
        VisualElement tracker;

        VisualElement fill_bar;

        VisualElement min_max_bar;
        Label min_element;
        Label max_element;

        const string slider_uss = "k2-slider";

        const string k2slider_uss = "k2-slider-main";

        public K2Slider()
        {
            AddToClassList(k2slider_uss);
            main_slider = new Slider() { name = "main_slider" };
            main_slider.AddToClassList(slider_uss);
            Add(main_slider);
            dragger = main_slider.Q<VisualElement>("unity-dragger");
            tracker = main_slider.Q<VisualElement>("unity-tracker");
            var container = main_slider.Q<VisualElement>("unity-drag-container");
            fill_bar = new VisualElement() { name = "fill_bar" };
            label_element = main_slider.labelElement;

            value_label_element = new Label() { name = "value_label" };
            value_label_element.AddToClassList("k2-slider-value-label");

            label_row = new VisualElement() { name = "label_row" };
            label_row.AddToClassList("k2-slider-label-row");

            min_max_bar = new VisualElement() { name = "min_max_bar" };
            min_element = new Label() { name = "min_label" };
            max_element = new Label() { name = "max_label" };

            Add(min_max_bar);

            min_max_bar.Add(min_element);
            min_max_bar.Add(max_element);

            tracker.Add(fill_bar);
            main_slider.RegisterCallback<ChangeEvent<float>>((evt) => { SliderValueChanged(); });
            main_slider.RegisterCallback<GeometryChangedEvent>((evt) => SliderValueChanged());

            // Three theories tried and disproven via Reese's log before this one (search for
            // "K2Slider dash diag" for the trail): (1) a display:none->visible transition losing
            // the image - disproven by Max Throttle, which never goes through display:none;
            // (2) the resolved background reference itself getting dropped on relayout - disproven
            // directly, every logged relayout on every slider showed a correctly-resolved
            // bg.sprite="dash"; (3) the tracker's own authored height rounding down to an invisible
            // sub-pixel sliver, or the parent drag-container's overflow clipping it - both ruled
            // out too: height read back as a clean 3, overflow was switched to visible, and every
            // single slider still showed no dashes at all afterward.
            //
            // That last result is the real tell: resolvedStyle reported everything as it should be
            // (correct sprite, correct size, Visible, opacity 1) and Reese still saw nothing. That
            // means the actual GPU-side draw of this background-image is failing somewhere below
            // what resolvedStyle can even see - not a sizing/visibility/clipping problem we can
            // fix by tuning USS numbers further. Rather than keep guessing at properties of a
            // background-image url() reference to a package-sourced sprite that's evidently not
            // reliably paintable in this game's embedding, this drops that approach entirely and
            // draws the dashes ourselves with generateVisualContent - plain rectangles via
            // Painter2D, no texture/sprite/background-image resolution involved at all, so it
            // can't be hit by whatever this was.
            tracker.generateVisualContent += DrawDashedTrack;
            tracker.RegisterCallback<GeometryChangedEvent>((evt) => tracker.MarkDirtyRepaint());
        }

        // Matches the look the CSS background-image was going for: a thin horizontal dashed line,
        // tinted to the same blue-grey the retro pass uses elsewhere, tiled left-to-right at a
        // fixed dash/gap pitch regardless of this slider's actual width.
        static readonly Color dash_tint = new Color(110f / 255f, 120f / 255f, 140f / 255f, 1f);
        // Round-cap blobs from the first attempt turned out to be a stock KerbalUI.uss rule
        // ghosting a second, lighter-blue dashed line behind ours (see #unity-tracker's
        // background-image: none in K2Slider.uss for the full story) - the Butt line cap below
        // was already the real fix for the rounded-end look, so these go back to Reese's original
        // wider proportions now that the ghosting itself is what's actually being fixed.
        const float dash_length = 8f;
        const float dash_gap = 6f;

        void DrawDashedTrack(MeshGenerationContext mgc)
        {
            float width = tracker.resolvedStyle.width;
            float height = tracker.resolvedStyle.height;
            if (width <= 0 || height <= 0) return;

            var painter = mgc.painter2D;
            painter.strokeColor = dash_tint;
            painter.lineWidth = height;
            painter.lineCap = LineCap.Butt;
            painter.BeginPath();

            float x = 0f;
            float y = height / 2f;
            while (x < width)
            {
                float segment_end = Mathf.Min(x + dash_length, width);
                painter.MoveTo(new Vector2(x, y));
                painter.LineTo(new Vector2(segment_end, y));
                x += dash_length + dash_gap;
            }

            painter.Stroke();
        }

        void SliderValueChanged()
        {
            Vector2 pos = dragger.parent.LocalToWorld(dragger.transform.position);
            fill_bar.transform.position = fill_bar.parent.WorldToLocal(pos);

            setLabels();
        }

        void setLabelPos()
        {
            if (label_element.parent == null)
            {
                // Debug.Log("no parent el");
                return;
            }

            // label_element starts out parented inside main_slider's own internal BaseField
            // structure (Unity's default layout, before this ever runs) - pull it (and add
            // value_label_element alongside it) into label_row exactly once, so label_row can be
            // moved as a single unit below the same way label_element alone used to be.
            if (label_element.parent != label_row)
            {
                label_element.parent.Remove(label_element);
                label_row.Add(label_element);
                label_row.Add(value_label_element);
            }

            if (labelOnTop)
            {
                if (label_row.parent != this)
                {
                    // Debug.Log("moving to top");
                    Insert(0, label_row);
                }
            }
            else
            {
                if (label_row.parent != main_slider)
                {
                    // Debug.Log("moving to line");
                    main_slider.Insert(0, label_row);
                }
            }
        }

        void setLabels()
        {
            // Still goes through main_slider.label (not label_element.text directly) so Unity's
            // own BaseField show/hide-on-empty-label handling for label_element keeps working -
            // this only ever carries the title text now, never the value.
            main_slider.label = Label;

            if (printValue || _value_text_override != null)
            {
                value_label_element.text = _value_text_override ?? value.ToStringInvariant("N2");
                value_label_element.style.display = DisplayStyle.Flex;
            }
            else
            {
                value_label_element.style.display = DisplayStyle.None;
            }

            if (string.IsNullOrEmpty(minMaxLabel))
            {
                min_max_bar.style.display = DisplayStyle.None;
            }
            else
            {
                min_max_bar.style.display = DisplayStyle.Flex;

                if (minMaxLabel == "x")
                {
                    // magic code to take from min max values
                    min_element.text = Min.ToStringInvariant();
                    max_element.text = Max.ToStringInvariant();
                }
                else
                {
                    var labels = minMaxLabel.Split("-");
                    if (labels.Length >= 1)
                        min_element.text = labels[0];

                    if (labels.Length >= 2)
                        max_element.text = labels[1];
                    else
                        max_element.text = "";
                }
            }

            setLabelPos();
        }

        // 2 ways binding
        public K2Slider Bind(Setting<float> setting)
        {
            this.value = setting.V;
            setting.listeners += v => this.value = v;
            RegisterCallback<ChangeEvent<float>>(evt => setting.V = evt.newValue);
            return this;
        }

        public K2Slider Bind(ClampSetting<float> setting)
        {
            this.Min = setting.min;
            this.Max = setting.max;
            this.value = setting.V;
            setting.listeners += v => this.value = v;
            RegisterCallback<ChangeEvent<float>>(evt => setting.V = evt.newValue);
            return this;
        }
    }

}