using System.IO;
using UnityEngine;
using K2D2;
using UnityEngine.UIElements;

namespace KTools
{

    public static class AssetsLoader
    {
        // No longer used now that LoadUxml() below loads through Addressables instead - left in place
        // (unused, harmless) rather than deleted so reverting to the old AssetBundle path is a one-line
        // job if the Addressables switch doesn't pan out in testing. See K2D2_Plugin.cs's OnInitialized()
        // for the matching commented-out AssetBundle.LoadFromFile() call.
        internal static AssetBundle Bundle;

        public static Texture2D LoadIcon(string path)
        {
            // var imageTexture = AssetManager.GetAsset<Texture2D>($"{K2D2_Plugin.ModGuid}/images/{path}.png");

            // SWMetadata.Folder is a System.IO.DirectoryInfo, not a string (confirmed via the current
            // SpaceWarpPluginDescriptor's real field type - see K2D2_Plugin.cs's OnInitialized for the same
            // fix) - .FullName is the correct, intended accessor.
            var texture = new Texture2D(1, 1);
            texture.LoadImage(File.ReadAllBytes(K2D2_Plugin.Instance.SWMetadata.Folder.FullName + $"/assets/images/{path}"));
            //   Check if the texture is null
            if (texture == null)
            {
                // Print an error message to the Console
                K2D2_Plugin.logger.LogError("Failed to load image texture from path: " + path);

                // Print the full path of the resource
                K2D2_Plugin.logger.LogInfo("Full resource path: " + K2D2_Plugin.Instance.SWMetadata.Folder.FullName + $"/assets/images/{path}");

                // Print the type of resource that was expected
                K2D2_Plugin.logger.LogInfo("Expected resource type: Texture2D");
            }

            return texture;
        }
        
        // Loads a UXML VisualTreeAsset through Redux's Addressables-backed Assets API instead of the old
        // prebuilt k2d2_ui.bundle AssetBundle (see NOTICE.md's "AssetBundle version mismatch" history for
        // why the old mechanism was fragile - it was tied to the exact Unity/UI-Toolkit version it was
        // built under, and had to be manually rebuilt by hand on every UI source change). Follows the same
        // pattern already proven in the KerbalAutopilot project's KerbalAutopilotMod.OnInitialized():
        // Assets.LoadAssetAsync<T>(address).WaitForCompletion() - .WaitForCompletion() specifically, not
        // .Result, since .Result on a handle that hasn't finished yet can silently return null instead of
        // actually blocking for the asset.
        //
        // For this to resolve, K2D2_Window.uxml needs to be marked Addressable in the Unity Editor
        // (select it, check "Addressable" in the Inspector, leave the auto-filled address as its project
        // path so it matches the address string built below). Its dependency closure - the nested tab
        // templates (Dock.uxml, Landing.uxml, etc via <ui:Template>), stylesheets, and fonts - do NOT need
        // separate Addressable marking; Addressables pulls those in automatically the same way an
        // AssetBundle build already did.
        public static VisualTreeAsset LoadUxml(string path)
        {
            var address = $"Assets/UI/K2D2_UI/{path}";
            // KerbalMonoBehaviour.Assets is protected, so this goes through K2D2_Plugin's public
            // LoadAddressableAsset<T>() wrapper rather than K2D2_Plugin.Instance.Assets directly
            // (confirmed via CS0122 build error - see the wrapper's own comment in K2D2_Plugin.cs).
            var handle = K2D2_Plugin.Instance.LoadAddressableAsset<VisualTreeAsset>(address);
            var asset = handle.WaitForCompletion();

            if (asset == null)
            {
                K2D2_Plugin.logger.LogError($"Failed to load UXML from Addressables at '{address}' - " +
                    "check that the asset is marked Addressable in the Unity Editor and that its address " +
                    "matches this path exactly.");
            }

            return asset;
        }

    }

}
