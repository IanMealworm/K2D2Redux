using Unity.Properties;
using UnityEngine;
using UnityEngine.UIElements;
using KTools;
using K2D2;

namespace K2UI
{
    // UxmlFactory/UxmlTraits -> [UxmlElement]/[UxmlAttribute] (see Group.cs's class comment for
    // why). The new attribute system only calls a setter for attributes actually present in a tag,
    // unlike the old bag-based Init() which always applied every attribute, falling back to its
    // own defaultValue for anything omitted. attitude.uxml's "elevation_slider" is a bare
    // <K2UI.K2Slider name="elevation_slider" /> with none of these attributes set, so this
    // constructor sets main_slider's lowValue/highValue explicitly (0/1, matching the old bag
    // defaults) instead of trusting Slider's own defaults, and _labelOnTop's field initializer is
    // `false` to match the old bag default. An AttachToPanelEvent hook (same idiom as
    // ExFoldoutGroup.cs) reproduces the old Init()'s unconditional trailing
    // SliderValueChanged()/setLabels() calls, guaranteeing a consistent visual state even for a
    // bare tag.
    [UxmlElement]
    public partial class K2Slider : VisualElement
    {
        [CreateProperty]
        [UxmlAttribute("value")]
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

        string _label = "";

        [CreateProperty]
        [UxmlAttribute("label")]
        public string Label
        {
            get { return _label; }
            set
            {
                _label = value;
            }
        }

        bool _printValue = false;

        [CreateProperty]
        [UxmlAttribute("print-value")]
        public bool printValue
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

        // Default matches the old UxmlTraits "label-on-top" defaultValue - matters for tags that
        // omit this attribute, since the new attribute system only calls the setter when present
        // (see class comment / elevation_slider).
        bool _labelOnTop = false;

        [CreateProperty]
        [UxmlAttribute("label-on-top")]
        public bool labelOnTop
        {
            get { return _labelOnTop; }
            set
            {
                _labelOnTop = value;
                setLabels();
            }
        }

        string _min_max_label = "";

        [CreateProperty]
        [UxmlAttribute("min-max-label")]
        public string minMaxLabel
        {
            get { return _min_max_label; }
            set
            {
                _min_max_label = value;
                setLabels();
            }
        }

        [CreateProperty]
        [UxmlAttribute("min")]
        public float Min
        {
            get { return main_slider.lowValue; }
            set { main_slider.lowValue = value; }
        }

        [CreateProperty]
        [UxmlAttribute("max")]
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

        // Title text pinned left, live value pinned right in the game's cockpit-gauge amber,
        // rather than both baked into one "Label : value" string (which can only be one
        // alignment/color for its whole length). label_row wraps both so a single Insert(0, ...)
        // in setLabelPos still moves them together.
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
            // Explicit, matching the old bag defaults (min=0/max=1) rather than trusting Slider's
            // own built-in lowValue/highValue defaults - see class comment.
            main_slider.lowValue = 0f;
            main_slider.highValue = 1f;
            main_slider.direction = SliderDirection.Horizontal;
            main_slider.pageSize = 0;
            main_slider.showInputField = false;
            main_slider.inverted = false;
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

            // The dashed track was originally a USS background-image (a package-sourced sprite),
            // but that image reliably fails to paint in this game's UI Toolkit embedding even when
            // resolvedStyle reports a correct sprite, size, and visibility - the failure is below
            // what resolvedStyle can see. Drawn directly instead with generateVisualContent: plain
            // rectangles via Painter2D, no texture/sprite/background-image resolution involved.
            tracker.generateVisualContent += DrawDashedTrack;
            tracker.RegisterCallback<GeometryChangedEvent>((evt) => tracker.MarkDirtyRepaint());

            // Reproduces the old UxmlTraits.Init()'s unconditional trailing
            // SliderValueChanged()/setLabels() calls, guaranteeing a consistent visual state even
            // for a bare tag. Runs once attached, by which point any UXML attributes have already
            // been applied - same idiom ExFoldoutGroup.cs uses.
            RegisterCallback<AttachToPanelEvent>(evt => { SliderValueChanged(); setLabels(); });
        }

        // Matches the look the CSS background-image was going for: a thin horizontal dashed line,
        // tinted to the same blue-grey the retro pass uses elsewhere, tiled left-to-right at a
        // fixed dash/gap pitch regardless of this slider's actual width.
        static readonly Color dash_tint = new Color(110f / 255f, 120f / 255f, 140f / 255f, 1f);
        // A stock KerbalUI.uss rule ghosts a second, lighter-blue dashed line behind this one
        // unless #unity-tracker's background-image is set to none (see K2Slider.uss).
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
                value_label_element.SetDisplay(DisplayStyle.Flex);
            }
            else
            {
                value_label_element.SetDisplay(DisplayStyle.None);
            }

            if (string.IsNullOrEmpty(minMaxLabel))
            {
                min_max_bar.SetDisplay(DisplayStyle.None);
            }
            else
            {
                min_max_bar.SetDisplay(DisplayStyle.Flex);

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