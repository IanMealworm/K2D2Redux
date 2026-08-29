using UnityEngine;
using UnityEngine.UIElements;
using System.Collections.Generic;
using KTools;

namespace K2UI
{
    /// <summary>
    /// complete copy of the K2Slider, I've not figured out how to make it more generic
    /// </summary>
    public class K2SliderInt : VisualElement
    {
        public new class UxmlFactory : UxmlFactory<K2SliderInt, UxmlTraits> { }

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

            private UxmlIntAttributeDescription m_Value = new()
            { name = "value", defaultValue = 0 };

            private UxmlIntAttributeDescription m_Min = new()
            { name = "min", defaultValue = 0 };

            private UxmlIntAttributeDescription m_Max = new()
            { name = "max", defaultValue = 100 };

            // Base Init() no longer applies "name" in this Unity version, so it's re-applied here.
            private UxmlStringAttributeDescription m_Name = new()
            { name = "name", defaultValue = "" };

            public override void Init(VisualElement ve, IUxmlAttributes bag, CreationContext cc)
            {
                base.Init(ve, bag, cc);
                ve.name = m_Name.GetValueFromBag(bag, cc);
                K2SliderInt k2_slider = (K2SliderInt)ve;
                SliderInt main_slider = k2_slider.main_slider;

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

        public int value
        {
            get { return main_slider.value; }
            set { 
                if (value == main_slider.value) return;
                main_slider.value = value; 
                listeners?.Invoke(value);
            }
        }

        public delegate void OnChanged(int value);

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

        public int Min
        {
            get { return main_slider.lowValue; }
            set { main_slider.lowValue = value; }
        }
        public int Max
        {
            get { return main_slider.highValue; }
            set { main_slider.highValue = value; }
        }

        public void InitValues(int value, int min, int max)
        {
            Min = min;
            Max = max;
            this.value = value;
        }

        protected SliderInt main_slider;

        Label label_element;

        // See K2Slider.cs for the full explanation - title pinned left, value pinned right in the
        // gauge amber, which needs two separate Labels in a row rather than one "Label : value"
        // string.
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

        public K2SliderInt()
        {
            AddToClassList(k2slider_uss);
            main_slider = new SliderInt() { name = "main_slider" };
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
            main_slider.RegisterCallback<ChangeEvent<int>>((evt) => { SliderValueChanged(); });
            main_slider.RegisterCallback<GeometryChangedEvent>((evt) => SliderValueChanged());

            // Mirrors K2Slider.cs (this class is "a complete copy" of it, per the class doc comment
            // above) - see that file for the full story of why the dashed track is drawn directly
            // with generateVisualContent instead of a USS background-image: the background-image
            // approach reported a fully correct resolved style (right sprite, right size, visible)
            // while still not actually painting anything in this game's embedding, so this avoids
            // that whole mechanism rather than continuing to chase it.
            tracker.generateVisualContent += DrawDashedTrack;
            tracker.RegisterCallback<GeometryChangedEvent>((evt) => tracker.MarkDirtyRepaint());
        }

        // See K2Slider.cs's DrawDashedTrack for the full story - the Butt line cap (not the dash
        // size) was the real fix for the rounded-blob look, so this keeps Reese's original wider
        // proportions.
        static readonly Color dash_tint = new Color(110f / 255f, 120f / 255f, 140f / 255f, 1f);
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
            if (label_element.parent == null) return;

            // Pull label_element (and add value_label_element alongside it) into label_row exactly
            // once - see K2Slider.cs's setLabelPos for the full explanation.
            if (label_element.parent != label_row)
            {
                label_element.parent.Remove(label_element);
                label_row.Add(label_element);
                label_row.Add(value_label_element);
            }

            if (_labelOnTop)
            {
                if (label_row.parent != this)
                    Insert(0, label_row);
            }
            else
            {
                if (label_row.parent != main_slider)
                    main_slider.Insert(0, label_row);
            }
        }

        void setLabels()
        {
            // Still goes through main_slider.label (not label_element.text directly) so Unity's
            // own BaseField show/hide-on-empty-label handling for label_element keeps working -
            // this only ever carries the title text now, never the value.
            main_slider.label = Label;

            if (printValue)
            {
                value_label_element.text = $"{value}";
                value_label_element.style.display = DisplayStyle.Flex;
            }
            else
            {
                value_label_element.style.display = DisplayStyle.None;
            }

            setLabelPos();

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
        }

        public void Bind(Setting<int> setting)
        {
            this.value = setting.V;
            setting.listeners += v => this.value = v;
            RegisterCallback<ChangeEvent<int>>(evt => setting.V = evt.newValue);
        }

        public K2SliderInt Bind(ClampSetting<int> setting)
        {
            this.Min = setting.min;
            this.Max = setting.max;
            
            this.value = setting.V;
            setting.listeners += v => this.value = v;
            RegisterCallback<ChangeEvent<int>>(evt => setting.V = evt.newValue);
            return this;
        }
    }

}