using Unity.Properties;
using UnityEngine.UIElements;
using System.Collections.Generic;
using KTools;


namespace K2UI.Tabs
{
    /// <summary>
    /// TabbedPage is the main class to use tabs feature.
    ///
    /// It create a tabsBar that will accept tabbed buttons
    ///
    /// It change the content in #Content element depending on the current Tab
    ///
    /// Adds TabButton to the Element they will be finally added in the #TabBar
    /// </summary>
    // UxmlFactory/UxmlTraits -> [UxmlElement]/[UxmlAttribute] (see Group.cs's class comment for
    // why). SelectedTabName's own field default ("") already matches the old bag default, and
    // K2D2_Window.uxml's only <K2UI.Tabs.TabbedPage> tag sets selected-tab-Name="controls"
    // explicitly anyway, so no extra behavior-preserving fix is needed here. Arbitrary
    // VisualElement children (the old uxmlChildElementsDescription override) need no equivalent -
    // that was only ever an editor-time authoring hint, not a runtime gate.
    [UxmlElement]
    public partial class TabbedPage : VisualElement
    {
        string _selected_tab_name = "";

        [CreateProperty]
        [UxmlAttribute("selected-tab-Name")]
        public string SelectedTabName
        {
            get {return _selected_tab_name;}
            set { _selected_tab_name = value; }
        }

        TabsBar tabsbar_el;
        VisualElement content_el;

        public override VisualElement contentContainer 
        {
            get{
                 if (content_el != null) 
                    return content_el;
                 else 
                    return this;
            }      
        }

        public TabbedPage()
        {       
            Setup();
            InitializeUI();
        }

        void Setup()
        {
            tabsbar_el = new TabsBar() {name = "TabsBar"};
            var content = new VisualElement() {name = "Content"};

            Add(tabsbar_el);
            Add(content);
            // setup main content after adding to parent
            content_el = content;
            tabsbar_el.RegisterCallback<ChangeEvent<string>>(onTabChanged);
        }

        private void InitializeUI()
        {
            content_el.RegisterCallback<GeometryChangedEvent>(HandleContentChanged);
            
        }

        private void HandleContentChanged(GeometryChangedEvent evt)
        {
            BuildButtons();

        }


        bool hasChanged()
        {
            var buttons = tabsbar_el.Query<TabButton>().ToList();
            var pages = content_el.Query<TabPage>().ToList();

            if (buttons.Count != pages.Count)
                return true;

            for (int i = 0; i < buttons.Count ; i++)
            {
                if (buttons[i].name != pages[i].name)
                    return true;
            }

            return false;
        }

        void BuildButtons()
        {
            if (!hasChanged())
                return;

            tabsbar_el.Clear();
            var pages = content_el.Query<TabPage>().ToList();
            foreach (var page in pages)
            {
                var bt = new TabButton();
                page.setButton(bt);
                tabsbar_el.Add(bt);
            }
            tabsbar_el.updateList();
        }

        private void onTabChanged(ChangeEvent<string> evt)
        {
            if (evt.target != tabsbar_el)
                return;

            // Debug.Log("changed "+evt.newValue);
            ShowContent(evt.newValue);  
            if (!string.IsNullOrEmpty(this.setting_path))
                SettingsFile.Instance.SetString(setting_path, evt.newValue);
        }

        string setting_path = "";

        public void Bind(string setting_path, string default_tab)
        {
            this.setting_path = setting_path;
            if (!string.IsNullOrEmpty(this.setting_path))
            {
                ShowContent(SettingsFile.Instance.GetString(setting_path, default_tab));
            }
        }


        string current_tab = "";
        public string CurrentTabCode
        {
            get => current_tab;
        }

        public void SelectFirst()
        {
            foreach (var panel in panels)
            {
                if (panel.enabled)
                {
                    ShowContent(panel.code);
                    return;
                }
            }
        }

        void ShowContent(string code)
        {
            current_tab = code;
            foreach (var page in content_el.Children())
            {
                page.Show(page.name == code);
            }

            if (panels == null)
                return;

            foreach (var panel in panels)
            {
                panel.isVisible = panel.code == code;    
            }

            // F2(c): a tab switch is a repaint boundary. The tab that is about to be shown may have
            // been written last as hidden, and every cached value still says "already written" for
            // elements whose styling this switch just touched - drop the caches so the newly shown
            // tab repaints unconditionally on its first frame.
            VisualElementExtension.InvalidateUiCaches();

            tabsbar_el.setOpenedPage(code);
        }

        public void Enable(string code, bool enable)
        {
            foreach (var panel in panels)
            {
                if (panel.code == code)
                    panel.enabled = enable;
            }
        }

        List<K2Page> panels;

        public void Init(List<K2Page> panels)
        {
            BuildButtons();
            this.panels = panels;
            foreach(K2Page panel in this.panels)
                panel.Init(tabsbar_el, content_el);
        }

        public void Select(string code)
        {
            tabsbar_el.setOpenedPage(code);
            ShowContent(code);         
        }



        public void Update()
        {
            foreach(K2Page panel in this.panels)
                panel.onUpdateUI();
        }


    }
}