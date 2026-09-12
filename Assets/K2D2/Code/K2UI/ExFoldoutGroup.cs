using Unity.Properties;
using UnityEngine.UIElements;
using System.Collections.Generic;

namespace K2UI
{
    // UxmlFactory/UxmlTraits -> [UxmlElement]/[UxmlAttribute] (see Group.cs's class comment for
    // why). openedIndex's own default (-1) already matched the old bag default, and its
    // AttachToPanelEvent-driven updateList()/UpdateState() setup here was already self-contained
    // regardless of Init() - no behavior-preserving fixes needed beyond the mechanical rename and
    // making openedIndex public (required for the source generator to attach an attribute to it).
    // Arbitrary VisualElement children (the old uxmlChildElementsDescription override) need no
    // equivalent - that was only ever an editor-time authoring hint, not a runtime gate.
    [UxmlElement]
    public partial class ExFoldoutGroup : VisualElement
    {
        public ExFoldoutGroup()
        {

            RegisterCallback<AttachToPanelEvent>(onAttached);
        }

        int _openedIndex = -1;

        [CreateProperty]
        [UxmlAttribute("opened-index")]
        public int openedIndex
        {
            get { return _openedIndex;}
            set { _openedIndex = value; UpdateState();}
        }

        List<Foldout> list_foldout;

        private void onAttached(AttachToPanelEvent evt)
        {
            updateList();
        }

        private void onChanged(ChangeEvent<bool> evt)
        {
            VisualElement target = evt.target as VisualElement;
            
            if (target.GetType() != typeof(Foldout))
                return;

            if (evt.newValue)
            {
                openedIndex = list_foldout.IndexOf(evt.target as Foldout);
                // Debug.Log($"index {openedIndex}");
                UpdateState();
            }
            // Debug.Log($"evt {evt.target}");
        }

        public void updateList()
        {
            if (list_foldout != null)
            {
                foreach(var foldout in list_foldout)
                {
                    foldout.UnregisterCallback<ChangeEvent<bool>>(onChanged);     
                }
            }

            list_foldout = this.Query<Foldout>().ToList();
            // openedIndex = -1;
            UpdateState();

            foreach(var foldout in list_foldout)
            {
                // foldout.value = false;
                foldout.RegisterCallback<ChangeEvent<bool>>(onChanged);     
            }
        }


        void UpdateState()
        {
            if (list_foldout == null) return;
            var index = 0;
            foreach(var foldout in list_foldout)
            {
                if (index != openedIndex)
                    foldout.value = false;

                index++;        
            }

        }
    }
}