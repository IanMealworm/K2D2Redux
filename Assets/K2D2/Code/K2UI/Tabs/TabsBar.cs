using Unity.Properties;
using UnityEngine.UIElements;
using System.Collections.Generic;

namespace K2UI.Tabs
{
    // UxmlFactory/UxmlTraits -> [UxmlElement]/[UxmlAttribute] (see Group.cs's class comment for
    // why). TabsBar is never actually instantiated from a UXML tag anywhere in the project - only
    // ever via `new TabsBar()` from TabbedPage.Setup() - so its old Init() (including the
    // updateList() call it used to make unconditionally) never actually ran in practice. Converted
    // for consistency/future-proofing anyway; openedIndex needs to be public for the source
    // generator to attach an attribute to it. Restricting children to TabButton (the old
    // uxmlChildElementsDescription override) was only ever an editor-time authoring hint, not a
    // runtime gate, so it has no equivalent here.
    [UxmlElement]
    public partial class TabsBar : VisualElement
    {
        public TabsBar()
        {
            AddToClassList("k2-tabsbar");
        }

        int _openedIndex = -1;

        [CreateProperty]
        [UxmlAttribute("opened-index")]
        public int openedIndex
        {
            get { return _openedIndex; }
            set { _openedIndex = value; UpdateState(); }
        }

        public List<TabButton> list_tabs;

        private void onChanged(ChangeEvent<bool> evt)
        {
            VisualElement target = evt.target as VisualElement;

            if (target.GetType() != typeof(TabButton))
                return;

            evt.StopPropagation();

            if (evt.newValue)
            {
                // click on a tab
                var previousValue = openedIndex;

                openedIndex = list_tabs.IndexOf(evt.target as TabButton);
                // Debug.Log($"index {openedIndex}");
                if (previousValue != openedIndex)
                {
                    UpdateState();
                     string previous_name = "";
                    if (previousValue >=0 && previousValue < list_tabs.Count)
                        previous_name = list_tabs[previousValue].name;
            
                    string new_name = list_tabs[openedIndex].name;
                    var my_event = ChangeEvent<string>.GetPooled(previous_name, new_name);
                    my_event.target = this;
                    SendEvent(my_event);
                }
            }
            // Debug.Log($"evt {evt.target}");
        }

        




        public void updateList()
        {
            // Debug.Log("updateList");
            if (list_tabs != null)
            {
                foreach (var foldout in list_tabs)
                {
                    foldout.UnregisterCallback<ChangeEvent<bool>>(onChanged);
                }
            }

            list_tabs = this.Query<TabButton>().ToList();
            // openedIndex = -1;
            UpdateState();

            foreach (var tab in list_tabs)
            {
                // foldout.value = false;
                tab.RegisterCallback<ChangeEvent<bool>>(onChanged);
            }
        }

        int findIndex(string code)
        {
            if (list_tabs.Count == 0)
                updateList();

            var index = 0;
            foreach (var tab in list_tabs)
            {
                if (tab.name == code)
                    return index;
                index++;
            }
            return -1;
        }

        public void setOpenedPage(string code)
        {
            int index = findIndex(code);
            if (index >=0)
            {
                openedIndex = index;
                list_tabs[openedIndex].Active = true;
            }
            UpdateState();
        }

        void UpdateState()
        {
            if (list_tabs == null) return;
            var index = 0;
            foreach (var tab in list_tabs)
            {
                if (index != openedIndex)
                    tab.Active = false;

                index++;
            }
        }
    }
}