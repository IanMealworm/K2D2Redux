using UnityEngine.UIElements;
using System.Collections.Generic;

namespace K2UI
{
    /// <summary>
    /// K2's mascot face, used to replace the plain "K2:" text prefix that used to open every
    /// status line - Reese wanted the status readout to visually read as "K2 talking" instead of
    /// just a labelled line of text.
    ///
    /// Also drives the "give him a little life" polish: k2d2_big_icon.png already has 3 small
    /// horizontal grille lines baked into its own artwork, bottom-left of the body (measured
    /// directly from the source PNG - see k2-avatar-light-0/1/2 in K2UI.uss for the exact pixel
    /// positions). SetRunning(true) lights those 3 lines up blue in sequence, one every step_ms;
    /// SetRunning(false) turns them back off in the reverse order. Wire this to a pilot's own
    /// is_running_event (BaseController.cs) so it tracks autopilot on/off automatically.
    /// </summary>
    public class K2Avatar : VisualElement
    {
        public new class UxmlFactory : UxmlFactory<K2Avatar, UxmlTraits> { }

        public new class UxmlTraits : VisualElement.UxmlTraits
        {
            // Base Init() no longer applies "name" in this Unity version, so it's re-applied here
            // (same pattern as every other K2UI custom control in this project).
            private UxmlStringAttributeDescription m_Name = new() { name = "name", defaultValue = "" };

            public override void Init(VisualElement ve, IUxmlAttributes bag, CreationContext cc)
            {
                base.Init(ve, bag, cc);
                ve.name = m_Name.GetValueFromBag(bag, cc);
            }
        }

        const string avatar_uss = "k2-avatar";
        const string icon_uss = "k2-avatar-icon";
        const string light_uss = "k2-avatar-light";
        const string light_on_uss = "k2-avatar-light--on";

        VisualElement icon;
        VisualElement[] lights;

        // Time between each light turning on/off during a cascade.
        const long step_ms = 90;

        public K2Avatar()
        {
            AddToClassList(avatar_uss);

            icon = new VisualElement() { name = "icon" };
            icon.AddToClassList(icon_uss);
            Add(icon);

            lights = new VisualElement[3];
            for (int i = 0; i < lights.Length; i++)
            {
                var light = new VisualElement() { name = "light_" + i };
                light.AddToClassList(light_uss);
                light.AddToClassList(light_uss + "-" + i);
                icon.Add(light);
                lights[i] = light;
            }
        }

        bool _running = false;

        // How many lights (from index 0 up) are currently lit.
        int lit_count = 0;

        IVisualElementScheduledItem scheduled;

        public void SetRunning(bool running)
        {
            if (running == _running) return;
            _running = running;

            scheduled?.Pause();

            if (running)
            {
                scheduled = schedule.Execute(() =>
                {
                    if (lit_count >= lights.Length)
                    {
                        scheduled.Pause();
                        return;
                    }

                    lights[lit_count].AddToClassList(light_on_uss);
                    lit_count++;
                }).Every(step_ms);
            }
            else
            {
                scheduled = schedule.Execute(() =>
                {
                    if (lit_count <= 0)
                    {
                        scheduled.Pause();
                        return;
                    }

                    lit_count--;
                    lights[lit_count].RemoveFromClassList(light_on_uss);
                }).Every(step_ms);
            }
        }
    }
}
