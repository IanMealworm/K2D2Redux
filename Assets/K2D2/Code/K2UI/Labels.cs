using Unity.Properties;
using UnityEngine.UIElements;
using System;


namespace K2UI
{
    // UxmlFactory/UxmlTraits -> [UxmlElement] (see Group.cs's class comment for why). Console
    // exposes no attributes beyond the standard "name" (which is what the old Init() override
    // existed only to re-apply), and node_infos_el = panel.Q<Console>("node_infos") keeps working
    // exactly the same way since UI Toolkit itself handles "name" for every element type now.
    [UxmlElement]
    public partial class Console : Label
    {
        public static new readonly string ussClassName = "console";

        // F1: the frame's console block is assembled here in memory instead of in the element.
        // It used to be built by clearing `text` in FullStatus.Reset() and appending raw onto the
        // element, so a frame cost one clear plus one write per line - and every intermediate value
        // went through the DOM. Now only the assembled block is written, through the text cache, so
        // a frame whose block is unchanged costs nothing at all.
        string _pending;

        // F1: same frame-boundary memory as StatusLine - see the note there. Reset() must not
        // pre-hide the console or every frame flips None -> Flex on it.
        bool _frame_open;
        bool _written_this_frame;

        public Console() : base()
        {
            AddToClassList(ussClassName);
        }

        /// <summary>
        /// Called by FullStatus.Reset() at the top of every frame. Starts a fresh block and hides
        /// the console only if the previous frame produced no output.
        /// </summary>
        public void ResetFrame()
        {
            _pending = null;

            if (_frame_open && !_written_this_frame)
                this.Show(false);

            _frame_open = true;
            _written_this_frame = false;
        }

        public void Set(string txt)
        {
            _written_this_frame = true;
            _pending = txt;
            this.SetText(txt);
            this.Show(true);
        }

        public void Add(string line)
        {
            _written_this_frame = true;

            // Same join rule as before: the first line of the frame replaces, the rest append.
            _pending = string.IsNullOrEmpty(_pending) ? line : _pending + "\n" + line;

            this.SetText(_pending);
            this.Show(true);
        }
    }

    // UxmlFactory/UxmlTraits -> [UxmlElement]/[UxmlAttribute] (see Group.cs's class comment for
    // why). The old UxmlTraits.Init() always applied "level" from the bag, defaulting to
    // Level.Normal - which every single <K2UI.StatusLine> tag in the project relies on, since none
    // of them specify level="..." at all (they only ever set it later from C#, e.g. Set(text,
    // level)). Without setting it explicitly here, a fresh StatusLine would be missing its
    // "k2-status-line--normal" USS class entirely rather than having it applied - `level =
    // Level.Normal;` in the constructor reproduces the old guarantee.
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

        // F1: computed once, at type-init, instead of twice per call (getUss() did an Enum.GetName
        // plus a string concatenation on every read and every write - see P1 recon section 4).
        static readonly string[] _uss_by_level =
        {
            uss_name + "--" + "normal",
            uss_name + "--" + "warning",
            uss_name + "--" + "error"
        };

        static string getUss(Level level)
        {
            return _uss_by_level[(int)level];
        }

        Level _level = Level.Normal;

        [CreateProperty]
        [UxmlAttribute("level")]
        public Level level
        {
            get { return _level; }
            set
            {
                // F1: was RemoveFromClassList(current) then AddToClassList(new), which removed and
                // re-added the SAME class when the level had not changed - two real class-list
                // mutations per call, every frame. SetClass owns the class and skips when equal.
                _level = value;
                this.SetClass(getUss(_level));
            }
        }

        // F1: the frame-boundary memory that removes the None -> Flex flip. FullStatus.Reset() used
        // to Show(false) and Status() then Show(true) - three display writes on the same element in
        // one frame. ResetFrame() records "no producer wrote this frame" instead and resolves it at
        // the NEXT frame boundary, so a frame that produces a status writes the display once (or
        // not at all once it is already Flex), and a frame that produces nothing still ends hidden.
        bool _frame_open;
        bool _written_this_frame;

        /// <summary>
        /// Called by FullStatus.Reset() at the top of every frame, in place of Show(false).
        /// </summary>
        public void ResetFrame()
        {
            if (_frame_open && !_written_this_frame)
                this.Show(false);   // the previous frame produced no status - hide it here

            _frame_open = true;
            _written_this_frame = false;
        }

        public void Set(string text, Level level)
        {
            _written_this_frame = true;

            this.SetText(text);
            this.level = level;
            this.Show(true);        // the frame's single display decision
        }

        public StatusLine() : base()
        {
            level = Level.Normal;
            AddToClassList(uss_name);
        }
    }


}
