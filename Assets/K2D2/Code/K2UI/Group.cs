using Unity.Properties;
using UnityEngine.UIElements;


namespace K2UI
{
    // Unity 6.6 removes UxmlFactory/UxmlTraits, so K2UI controls use the source-generated
    // [UxmlElement]/[UxmlAttribute] system (see NOTICE.md). It only calls a property's setter for
    // attributes present in the tag, so defaults that matter are set in the constructor: here
    // `text = "Group Name"`, without which a bare <K2UI.Group> would show no label at all.
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