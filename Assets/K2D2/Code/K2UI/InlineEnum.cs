using Unity.Properties;
using UnityEngine.UIElements;
using System.Collections.Generic;
// using KTools;
using System;

namespace K2UI
{
    // UxmlFactory/UxmlTraits -> [UxmlElement]/[UxmlAttribute] (see Group.cs's class comment for
    // why). The old UxmlTraits.Init() always applied both "labels" and "value" from the bag,
    // defaulting to "A;B;C"/0 when a tag omitted them (Dock.uxml's "final_mode" only specifies
    // labels="Manual;Auto", relying on the value default) - the new attribute system only calls a
    // setter for attributes actually present, so `labels = "A;B;C";` in the constructor
    // reproduces that default explicitly (value's own field already defaults to 0, matching the
    // old default, so no equivalent assignment is needed there).
    [UxmlElement]
    public partial class InlineEnum : VisualElement
    {
        int _value;

        [CreateProperty]
        [UxmlAttribute("value")]
        public int value
        {
            get { return _value; }
            set { 
                if (value < 0) value = 0;
                if (labels_list != null)
                {
                    if (value >= labels_list.Length)
                        value = labels_list.Length -1;
                }

                if (_value == value) return;
                int old_value = _value;
                _value = value;
                var my_event = ChangeEvent<int>.GetPooled(old_value, value);
                my_event.target = this;
                SendEvent(my_event);
                UpdateValue();
            }
        }

        string _labels;

        [CreateProperty]
        [UxmlAttribute("labels")]
        public string labels
        {
            get { return _labels; }
            set
            {
                if (_labels == value) return;
                _labels = value;
                UpdateContent();
            }
        }

        public InlineEnum()
        {
            AddToClassList("inline_enum");
            labels = "A;B;C";
        }

        string[] labels_list = null;
        public List<Button> buttons = new();

        void UpdateContent()
        {
            Clear();
            buttons.Clear();

            labels_list = labels.Split(';');
            
            for (int i = 0 ; i < labels_list.Length; i++)
            {
                var bt = new Button(); 
                buttons.Add(bt);
                bt.AddToClassList("toggle-button");
                bt.text = labels_list[i];
                bt.name = labels_list[i];
                Add(bt);

                int index =i;
                bt.RegisterCallback<ClickEvent>(evt => value=index);
            }
            UpdateValue();
        }

        void UpdateValue()
        {
            for (int i = 0 ; i < buttons.Count; i++)
            {
                var bt = buttons[i];
                bt.EnableInClassList("checked", i == value); 
            }
        }

        public void Bind<TEnum>(KTools.EnumSetting<TEnum> setting, string labels = null) where TEnum : struct
        {
            this.value = setting.int_value;
            if (labels == null)
                this.labels = String.Join(";", Enum.GetNames( typeof(TEnum) ));
            else
                this.labels = labels;

            setting.listeners += v => this.value = (int)(object) v;
            RegisterCallback<ChangeEvent<int>>(evt => setting.V = (TEnum)Enum.ToObject(typeof(TEnum), evt.newValue));
        }
    }
}