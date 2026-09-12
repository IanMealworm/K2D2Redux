using Unity.Properties;
using UnityEngine.UIElements;


namespace K2UI
{
    // UxmlFactory/UxmlTraits -> [UxmlElement]/[UxmlAttribute]: Unity 6.6 removes UxmlFactory
    // entirely, so every K2UI custom control is moving to the newer source-generated attribute
    // system ahead of that (see NOTICE.md's UxmlElement migration entry for the full story). The
    // old UxmlTraits.Init() always applied "text" from the bag - using its defaultValue of
    // "Group Name" whenever a <K2UI.Group> tag didn't specify text="..." - so `text = "Group
    // Name";` below in the constructor reproduces that same guarantee; the new attribute system
    // only calls a property's setter for attributes actually present in the tag, so without this
    // a bare Group would silently show no label at all instead of the old placeholder default.
    // Arbitrary VisualElement children (the old uxmlChildElementsDescription override) need no
    // equivalent here - that was only ever an editor-time authoring hint, not a runtime gate.
    [UxmlElement]
    public partial class Group : VisualElement
    {
        string _text;

        [CreateProperty]
        [UxmlAttribute("text")]
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
            text = "Group Name";
        }
    }
}