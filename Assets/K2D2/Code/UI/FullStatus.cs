using K2UI;
using UnityEngine.UIElements;

namespace K2D2.UI
{
    public class FullStatus
    {
        public FullStatus(VisualElement group)
        {
            status = group.Q<StatusLine>("status_pilot");
            console = group.Q<K2UI.Console>("pilot_console");
            progressBar = group.Q<K2UI.K2ProgressBar>("progress");
            // Not every tab has a K2Avatar yet (only the Node tab, so far) - Q<>() just returns
            // null when it's not there, which is fine since nothing here calls avatar.* itself;
            // callers (e.g. NodeExUI) are the ones that need to null-check before using it.
            avatar = group.Q<K2UI.K2Avatar>("k2_avatar");
            main_group = status.parent;
        }

        VisualElement main_group;
        public K2UI.Console console;
        public K2UI.StatusLine status;
        public K2UI.K2ProgressBar progressBar;
        public K2UI.K2Avatar avatar;

        public void Reset()
        {
            main_group.Show(true);

            // F1: this used to pre-clear and pre-hide both read-outs (console.Show(false) +
            // console.text = "", status.Show(false) + status.text = ""), so the frame that followed
            // wrote None and "" and then immediately wrote Flex and the real value - the flip-flop
            // and the clear-then-set pairs in the P1 recon, section 4. ResetFrame() books "no
            // producer has written this frame" instead; Status()/Console() then decide the display
            // once, and the hide happens at the next frame boundary only if nothing wrote.
            console.ResetFrame();
            status.ResetFrame();

            progressBar.ResetFrame();
        }

        public void Console(string txt)
        {
            console.Add(txt);
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
            // Set() also shows the line - the redundant `status.Show(true)` that used to follow is
            // gone, so the frame's display decision is made in exactly one place.
            status.Set(text, level);
        }

        public void Progress(double ratio, string label = null)
        {
            progressBar.value = (float)(ratio * 100);
            progressBar.ShowFrame();
            if (!string.IsNullOrEmpty(label))
            {
                progressBar.Label = label;
            }
        }
    }
}
