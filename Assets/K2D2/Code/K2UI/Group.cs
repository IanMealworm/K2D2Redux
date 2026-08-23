using UnityEngine.UIElements;
using System.Collections.Generic;


namespace K2UI
{
    public class Group : VisualElement
    {
        public new class UxmlFactory : UxmlFactory<Group, UxmlTraits> { }

        public new class UxmlTraits : VisualElement.UxmlTraits
        {
            public override IEnumerable<UxmlChildElementDescription> uxmlChildElementsDescription
            {
                get
                {
                    yield return new UxmlChildElementDescription(typeof(VisualElement));
                }
            }
            private UxmlStringAttributeDescription m_Text = new()
            { name = "text", defaultValue = "Group Name" };

            // Base Init() no longer applies "name" in this Unity version, so it's re-applied here.
            private UxmlStringAttributeDescription m_Name = new()
            { name = "name", defaultValue = "" };

            public override void Init(VisualElement ve, IUxmlAttributes bag, CreationContext cc)
            {
                base.Init(ve, bag, cc);
                ve.name = m_Name.GetValueFromBag(bag, cc);
                Group group = (Group) ve;
                group.text = m_Text.GetValueFromBag(bag, cc);     
            }
        }

        string _text;
        public string text
        {
            get { return _text; }
            set
            {
                if (_text == value) return;
                _text = value;
                label_el.text = value;
            }
        }

        Label label_el;

        public Group() : base()
        {
            AddToClassList("group");
            label_el = new Label();
            label_el.AddToClassList("group_label");
            Add(label_el);
        }
    }
}