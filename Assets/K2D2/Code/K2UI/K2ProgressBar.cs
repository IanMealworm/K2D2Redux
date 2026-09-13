using Unity.Properties;
using UnityEngine;
using UnityEngine.UIElements;
using UITKRotate = UnityEngine.UIElements.Rotate;

namespace K2UI
{
    // UxmlFactory/UxmlTraits -> [UxmlElement]/[UxmlAttribute] (see Group.cs's class comment for
    // why). The old UxmlTraits.Init() always applied every one of these seven attributes from the
    // bag, falling back to its own defaultValue for any one a tag omitted - the new attribute
    // system only calls a setter for attributes actually present, so the constructor below sets
    // all seven explicitly (same values, same order) to reproduce that guarantee for any current
    // or future <K2UI.K2ProgressBar> tag that doesn't specify all of them.
    [UxmlElement]
    public partial class K2ProgressBar : VisualElement
    {
        // Must expose your element class to a { get; set; } property that has the same name
        // as the name you set in your UXML attribute description with the camel case format
        public string _label;

        [CreateProperty]
        [UxmlAttribute("label")]
        public string Label
        {
            get { return _label; }
            set
            { _label = value; updateRender(); }

        }

        float _value;

        [CreateProperty]
        [UxmlAttribute("value")]
        public float value
        {
            get { return _value; }
            set { _value = value; updateRender(); }
        }

        bool _centered;

        [CreateProperty]
        [UxmlAttribute("centered")]
        public bool Centered
        {
            get { return _centered; }
            set { _centered = value; updateRender(); }
        }

        float _min;

        [CreateProperty]
        [UxmlAttribute("min")]
        public float Min
        {
            get { return _min; }
            set { _min = value; updateRender(); }
        }

        float _max;

        [CreateProperty]
        [UxmlAttribute("max")]
        public float Max
        {
            get { return _max; }
            set { _max = value; updateRender(); }
        }


        bool _label_value;

        [CreateProperty]
        [UxmlAttribute("set-label-to-value")]
        public bool LabelValue
        {
            get { return _label_value; }
            set { _label_value = value; updateRender(); }
        }

        string _postfix;

        [CreateProperty]
        [UxmlAttribute("postfix")]
        public string Postfix
        {
            get { return _postfix; }
            set { _postfix = value; updateRender(); }
        }

        void updateRender()
        {


            if (el_progress != null)
            {
                // el_progress.style.transformOrigin = new StyleTransformOrigin(new TransformOrigin(Length.Percent(50f), Length.Percent(50f)));


                if (Centered)
                {
                    float range = Max-Min;
                    float center_value = (Min + Max)/2;
                    // float width = 0;


                    el_progress.style.left = new StyleLength(Length.Percent(50));
                    float width = Mathf.Abs((center_value - value)/range);        
                    
                    if (value < center_value)
                        el_progress.style.rotate = new StyleRotate(new UITKRotate(180));
                    else
                        el_progress.style.rotate = new StyleRotate(new UITKRotate(0));


                    // width = (value - center_value)/half_range;
                        

                    var len = new StyleLength(Length.Percent(width * 100));
                    el_progress.style.width = len;
                    //.x
                }
                else
                {
                    var len = new StyleLength(Length.Percent(Mathf.InverseLerp(Min, Max, value) * 100));
                    el_progress.style.width = len;
                    el_progress.style.rotate = new StyleRotate(new UITKRotate(0));
                    el_progress.style.left = new StyleLength(Length.Percent(0));
                }
            }
            if (el_label != null)
            {
                if (LabelValue)
                    el_label.text = Label + $"{value:n2}{Postfix}";
                else
                   el_label.text = Label;
            }

        }

        // In the spirit of the BEM standard, the BigToggleButton has its own block class and two element classes. It also
        // has a class that represents the enabled state of the toggle.
        public static readonly string ussClassName = "k2-progress-bar";

        Label el_label;
        VisualElement el_frame;
        VisualElement el_progress;

        // This constructor allows users to set the contents of the label.
        public K2ProgressBar()
        {
            el_frame = new VisualElement() { name = "frame" };
            Add(el_frame);


            el_progress = new VisualElement() { name = "progress" };
            el_label = new Label();
            el_label.name = "label";


            el_frame.Add(el_progress);
            el_frame.Add(el_label);

            // Style the control overall.
            AddToClassList(ussClassName);

            // Reproduces the old UxmlTraits.Init()'s unconditional bag-default application - see
            // class comment above.
            Label = "K2-Progress-Bar";
            value = 0f;
            Centered = false;
            Min = 0f;
            Max = 1f;
            LabelValue = false;
            Postfix = "%";
        }
    }

}