using Unity.Properties;
using UnityEngine.UIElements;
using System;


namespace K2UI
{
    [UxmlElement]
    public partial class Console : Label
    {
        public static new readonly string ussClassName = "console";

        public Console() : base()
        {
            AddToClassList(ussClassName);
        }

        public void Set(string txt)
        {
            this.text = txt;
            this.Show(true);
        }

        public void Add(string line)
        {
            if (string.IsNullOrEmpty(text))
                text = line;
            else      
                text += "\n"+line;
            this.Show(true);
        }
    }

    // No <K2UI.StatusLine> tag sets level="..." (it's set later from C#), and UXML only applies
    // attributes present in the tag (see Group.cs), so the constructor sets Level.Normal to give a
    // fresh StatusLine its "k2-status-line--normal" USS class.
    [UxmlElement]
    public partial class StatusLine : Label
    {
        public enum Level
        {
            Normal,
            Warning,
            Error
        }

        const string uss_name = "k2-status-line";

        string getUss(Level level)
        {
            return uss_name+ "--" + Enum.GetName( typeof(Level), level).ToLower();
        }

        Level _level = Level.Normal;

        [CreateProperty]
        [UxmlAttribute("level")]
        public Level level
        {
            get { return _level; }
            set
            {
                var current_uss = getUss(_level);
                RemoveFromClassList(current_uss);

                _level = value;
                current_uss = getUss(_level);
                AddToClassList(current_uss);
            }
        }

        public void Set(string text, Level level)
        {
            this.text = text;
            this.level = level;
            this.Show(true);
        }

        public StatusLine() : base()
        {
            level = Level.Normal;
            AddToClassList(uss_name);
        }
    }


}