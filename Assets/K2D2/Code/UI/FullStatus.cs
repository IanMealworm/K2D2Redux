using System.Text;
using K2UI;
using UnityEngine.UIElements;

namespace K2D2.UI
{
    // The status line, console and progress bar under K2's avatar. Each UI tick starts with
    // Reset() and then says what to show. Written straight to the elements, that hid and showed
    // them again every tick, which made the whole window lay itself out again every frame; so the
    // tick's content is collected here and written once, after the tick, only where it changed.
    public class FullStatus
    {
        public FullStatus(VisualElement group)
        {
            status = group.Q<StatusLine>("status_pilot");
            console = group.Q<K2UI.Console>("pilot_console");
            progressBar = group.Q<K2UI.K2ProgressBar>("progress");
            // Not every tab has a K2Avatar - callers null-check before using it.
            avatar = group.Q<K2UI.K2Avatar>("k2_avatar");
            main_group = status.parent;
        }

        VisualElement main_group;
        public K2UI.Console console;
        public K2UI.StatusLine status;
        public K2UI.K2ProgressBar progressBar;
        public K2UI.K2Avatar avatar;

        string status_text;
        StatusLine.Level status_level;
        readonly StringBuilder console_text = new();
        bool show_progress;
        float progress_value;
        string progress_label;
        bool apply_scheduled;

        public void Reset()
        {
            status_text = null;
            status_level = StatusLine.Level.Normal;
            console_text.Clear();
            show_progress = false;
            ScheduleApply();
        }

        public void Console(string txt)
        {
            if (console_text.Length > 0)
                console_text.Append('\n');
            console_text.Append(txt);
            ScheduleApply();
        }

        public void Warning(string text)
        {
            Status(text, StatusLine.Level.Warning);
        }

        public void Error(string text)
        {
            Status(text, StatusLine.Level.Error);
        }

        public void Status(string text, StatusLine.Level level = StatusLine.Level.Normal)
        {
            status_text = text;
            status_level = level;
            ScheduleApply();
        }

        public void Progress(double ratio, string label = null)
        {
            show_progress = true;
            progress_value = (float)(ratio * 100);
            if (!string.IsNullOrEmpty(label))
                progress_label = label;
            ScheduleApply();
        }

        void ScheduleApply()
        {
            if (apply_scheduled)
                return;
            apply_scheduled = true;
            main_group.schedule.Execute(Apply);
        }

        void Apply()
        {
            apply_scheduled = false;
            main_group.Show(true);

            bool show_status = status_text != null;
            if (show_status)
            {
                status.text = status_text;
                if (status.level != status_level)
                    status.level = status_level;
            }
            status.Show(show_status);

            string console_now = console_text.ToString();
            console.text = console_now;
            console.Show(console_now.Length > 0);

            if (show_progress)
            {
                // Both setters redraw the bar even when nothing changed.
                if (progressBar.value != progress_value)
                    progressBar.value = progress_value;
                if (progress_label != null && progressBar.Label != progress_label)
                    progressBar.Label = progress_label;
            }
            progressBar.Show(show_progress);
        }
    }
}
