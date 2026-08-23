using System;
using System.Reflection;
using UnityEngine.UIElements;
using K2D2;

namespace KTools
{
    /// <summary>
    /// Manually (re-)registers every K2UI custom control's legacy UxmlFactory with Unity's
    /// internal VisualElementFactoryRegistry.
    ///
    /// Why this exists: custom K2UI elements were failing to load from UXML with "Unknown type"
    /// errors. VisualElementFactoryRegistry.RegisterUserFactories() only scans assemblies that
    /// GetAllUserAssemblies() considers "user assemblies" - a BepInEx-injected mod DLL like
    /// K2D2's never qualifies, no matter when the scan runs. The fix is the standard workaround
    /// for this symptom: register the factories yourself, from code that's guaranteed to run.
    ///
    /// VisualElementFactoryRegistry.RegisterFactory(IUxmlFactory) is `protected static`, not
    /// public - there's no supported public API for a mod to call it directly - so this uses
    /// reflection. Call RegisterAll() once, early in plugin startup, before any UXML is loaded.
    /// </summary>
    internal static class K2UIFactoryRegistration
    {
        public static void RegisterAll()
        {
            var registryType = typeof(VisualElement).Assembly
                .GetType("UnityEngine.UIElements.VisualElementFactoryRegistry");
            if (registryType == null)
            {
                L.Log("[K2D2] K2UIFactoryRegistration: could not find VisualElementFactoryRegistry - " +
                    "this Unity version's internal UI Toolkit types may have changed. Custom K2UI " +
                    "controls will likely fail to render.");
                return;
            }

            var registerMethod = registryType.GetMethod("RegisterFactory",
                BindingFlags.NonPublic | BindingFlags.Static);
            if (registerMethod == null)
            {
                L.Log("[K2D2] K2UIFactoryRegistration: could not find " +
                    "VisualElementFactoryRegistry.RegisterFactory - this Unity version's internal " +
                    "UI Toolkit API may have changed. Custom K2UI controls will likely fail to render.");
                return;
            }

            IUxmlFactory[] factories =
            {
                new K2UI.ToggleButton.UxmlFactory(),
                new K2UI.Tabs.TabbedPage.UxmlFactory(),
                new K2UI.K2Compass.UxmlFactory(),
                new K2UI.Group.UxmlFactory(),
                new K2UI.InlineEnum.UxmlFactory(),
                new K2UI.K2Slider.UxmlFactory(),
                new K2UI.K2AutoFitLabel.UxmlFactory(),
                new K2UI.Graph.GraphLine.UxmlFactory(),
                new K2UI.Console.UxmlFactory(),
                new K2UI.StatusLine.UxmlFactory(),
                new K2UI.K2Toggle.UxmlFactory(),
                new K2UI.K2ProgressBar.UxmlFactory(),
                new K2UI.ExFoldoutGroup.UxmlFactory(),
                new K2UI.K2SliderInt.UxmlFactory(),
                new K2UI.Tabs.TabsBar.UxmlFactory(),
                new K2UI.Tabs.TabPage.UxmlFactory(),
                new K2UI.Tabs.TabButton.UxmlFactory(),
            };

            int registered = 0;
            foreach (var factory in factories)
            {
                try
                {
                    registerMethod.Invoke(null, new object[] { factory });
                    registered++;
                }
                catch (Exception e)
                {
                    L.Log($"[K2D2] K2UIFactoryRegistration: failed to register {factory.GetType()}: {e}");
                }
            }

            L.Log($"[K2D2] K2UIFactoryRegistration: manually registered {registered}/{factories.Length} " +
                "K2UI custom control factories with VisualElementFactoryRegistry.");
        }
    }
}
