using Unity.Properties;
using UnityEngine.UIElements;

namespace K2UI.Tabs
{

    /// <summary>
    /// a simple visual element just used to contains label and icon
    /// </summary>
    // UxmlFactory/UxmlTraits -> [UxmlElement]/[UxmlAttribute] (see Group.cs's class comment for
    // why). "name" itself needs no special handling any more - UI Toolkit's own attribute
    // application always sets it now, regardless of custom control type - so
    // TabbedPage.ShowContent()'s page.Show(page.name == code), K2Page.Init()'s
    // panels.Q<TabPage>(code), and setButton() below all keep working unchanged. TabPage is never
    // actually instantiated from a UXML tag in this project though (K2D2_Window.uxml's tab content
    // is <ui:Instance> template references, not literal <K2UI.Tabs.TabPage> tags) - only ever via
    // `new TabPage()` - so none of this was actually exercised via Init() before either way.
    [UxmlElement]
    public partial class TabPage : VisualElement
    {
        public string _label;

        [CreateProperty]
        [UxmlAttribute("label")]
        public string label
        {
            get { return _label; }
            set { 
                    if (value == _label) return;
                    _label = value; 
                    
                    if (tab_button != null)
                    {
                        tab_button.label = label;
                    }
                }
        }

        public TabButton tab_button;
        public void setButton(TabButton bt)
        {
            bt.label = label;
            bt.name = name;
        }
    }


    /// <summary>
    /// TabButton have two states : active (showing current content) and lighted (pilot is on)
    /// </summary>
    // UxmlFactory/UxmlTraits -> [UxmlElement]/[UxmlAttribute] (see Group.cs's class comment for
    // why). TabButton, like TabsBar and TabPage, is never actually instantiated from a UXML tag in
    // this project - only ever via `new TabButton()` from TabbedPage/TabPage's own code - so its
    // old Init() (and the "Tab Button"/false/false defaults it unconditionally applied) never
    // actually ran in practice. Converted for consistency/future-proofing anyway.
    [UxmlElement]
    public partial class TabButton : VisualElement
    {
        // Must expose your element class to a { get; set; } property that has the same name
        // as the name you set in your UXML attribute description with the camel case format
        public string _label;

        [CreateProperty]
        [UxmlAttribute("label")]
        public string label
        {
            get { return _label; }
            set
            {
                _label = value;
                el_label.text = value;
            }
        }
        bool _active;

        [CreateProperty]
        [UxmlAttribute("active")]
        public bool Active
        {
            get { return _active; }
            set
            {
                if (_active == value)
                    return;

                var evt = ChangeEvent<bool>.GetPooled(_active, value);
                evt.target = this;
                _active = value;

                EnableInClassList(activeUss, _active);
                SendEvent(evt);
            }
        }

        bool _lighted;

        [CreateProperty]
        [UxmlAttribute("lighted")]
        public bool Lighted
        {
            get { return _lighted; }
            set
            {
                _lighted = value;
                el_light.EnableInClassList(lightedUss, _lighted);
            }
        }

        // In the spirit of the BEM standard, the TabButton has its own block class and two element classes. It also
        // has a class that represents the enabled state of the toggle.
        public static readonly string ussClassName = "k2-tab-button";
        public static readonly string activeUss = ussClassName+"--active";

        public static readonly string usslightName = "tab_light";

        public static readonly string lightedUss = usslightName+"--lighted";

        Label el_label;
        VisualElement el_light;
        
        // This constructor allows users to set the contents of the label.
        public TabButton()
        {
            el_light = new VisualElement();
            el_light.name = "tab_light";
            el_light.AddToClassList(usslightName);
            Add(el_light);

            el_label = new Label();
            Add(el_label);

            // Style the control overall.
            AddToClassList(ussClassName);
            this.AddManipulator(new Clickable(evt => {
                Active = true;
                }));
        }

    }
}