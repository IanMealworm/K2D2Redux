using UnityEngine.UIElements;

namespace K2UI.Tabs
{
     
    /// <summary>
    /// a simple visual element just used to contains label and icon
    /// </summary>
    public class TabPage : VisualElement
    {
        public new class UxmlFactory : UxmlFactory<TabPage, UxmlTraits> { }

        // Add the two custom UXML attributes.
        public new class UxmlTraits : VisualElement.UxmlTraits
        {
            // This Unity version's base VisualElement.UxmlTraits.Init() no longer applies the "name"
            // UXML attribute for legacy controls - it's now just a warn-and-return stub. This one
            // matters more than most: TabbedPage.ShowContent()'s page.Show(page.name == code),
            // K2Page.Init()'s panels.Q<TabPage>(code), and setButton() below (which copies this.name
            // onto the TabButton) all depend on a TabPage's "name" actually being set from its UXML
            // tag, so without this fix every tab silently fails to match and switching tabs blanks
            // the whole content area.
            UxmlStringAttributeDescription m_Name =
                new() { name = "name", defaultValue = "" };

            UxmlStringAttributeDescription m_Label =
                new() { name = "label", defaultValue = "My Tab" };

            public override void Init(VisualElement ve, IUxmlAttributes bag, CreationContext cc)
            {
                base.Init(ve, bag, cc);
                ve.name = m_Name.GetValueFromBag(bag, cc);
                var ate = ve as TabPage;

                ate.label = m_Label.GetValueFromBag(bag, cc);

            }
        }

        public string _label;
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
    public class TabButton : VisualElement
    {
        public new class UxmlFactory : UxmlFactory<TabButton, UxmlTraits> { }

        // Add the two custom UXML attributes.
        public new class UxmlTraits : VisualElement.UxmlTraits
        {
            UxmlStringAttributeDescription m_Label =
                new() { name = "label", defaultValue = "Tab Button" };
            UxmlBoolAttributeDescription m_Active =
                new() { name = "active", defaultValue = false };
            UxmlBoolAttributeDescription m_Lighted =
                new() { name = "lighted", defaultValue = false };

            // Base Init() no longer applies "name" in this Unity version, so it's re-applied here.
            UxmlStringAttributeDescription m_Name =
                new() { name = "name", defaultValue = "" };

            public override void Init(VisualElement ve, IUxmlAttributes bag, CreationContext cc)
            {
                base.Init(ve, bag, cc);
                ve.name = m_Name.GetValueFromBag(bag, cc);
                var ate = ve as TabButton;

                ate.label = m_Label.GetValueFromBag(bag, cc);
                ate.Active = m_Active.GetValueFromBag(bag, cc);
                ate.Lighted = m_Lighted.GetValueFromBag(bag, cc);
            }
        }

        // Must expose your element class to a { get; set; } property that has the same name 
        // as the name you set in your UXML attribute description with the camel case format
        public string _label;
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