using System.IO;
using UnityEngine;
using K2D2;
using UnityEngine.UIElements;

namespace KTools
{

    public static class AssetsLoader
    {
        // Unused while UXML loads through Addressables (LoadUxml). Kept for the commented-out
        // AssetBundle fallback in K2D2_Plugin.OnInitialized().
        internal static AssetBundle Bundle;

        public static Texture2D LoadIcon(string path)
        {
            // var imageTexture = AssetManager.GetAsset<Texture2D>($"{K2D2_Plugin.ModGuid}/images/{path}.png");

            // SWMetadata.Folder is a System.IO.DirectoryInfo, not a string; .FullName gives its path.
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
        
        // Loads a UXML VisualTreeAsset through Redux's Addressables-backed Assets API. A prebuilt
        // AssetBundle is tied to the exact Unity/UI Toolkit version it was built with and has to be
        // rebuilt by hand after every UI change (see NOTICE.md). Uses .WaitForCompletion(), not
        // .Result: .Result on an unfinished handle can return null instead of blocking.
        //
        // For this to resolve, K2D2_Window.uxml must be marked Addressable in the Unity Editor, with
        // the auto-filled address (its project path) left as is to match the address built below.
        // Its nested tab templates, stylesheets and fonts are pulled in automatically and need no
        // marking of their own.
        public static VisualTreeAsset LoadUxml(string path)
        {
            var address = $"Assets/UI/K2D2_UI/{path}";
            // KerbalMonoBehaviour.Assets is protected, so this goes through K2D2_Plugin's public
            // LoadAddressableAsset<T>() wrapper (see its comment in K2D2_Plugin.cs).
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
