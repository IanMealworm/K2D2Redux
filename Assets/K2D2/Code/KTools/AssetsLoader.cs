using System.IO;
using UnityEngine;
using K2D2;
using UnityEngine.UIElements;

namespace KTools
{

    public static class AssetsLoader
    {
        // Backported to Redux 0.2.8.5: the live AssetBundle, assigned in K2D2_Plugin.OnInitialized().
        // v1.2.0 had switched LoadUxml() below to Redux's Addressables system and marked this field
        // unused; the backport uses the proven prebuilt-bundle path again (see
        // Deploy/obj/addressables-verdict.md for the H1/H2/H3 evidence and why the bundle ships first).
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
                // Print an error message to the Console - via L.Error so it stays a visible ERROR
                // (production policy: errors always show) while also being null-safe, which the raw
                // `K2D2_Plugin.logger.LogError` was not.
                L.Error("Failed to load image texture from path: " + path);

                // Print the full path of the resource - diagnostic, routes to LogDebug
                L.Log("Full resource path: " + K2D2_Plugin.Instance.SWMetadata.Folder.FullName + $"/assets/images/{path}");

                // Print the type of resource that was expected - diagnostic
                L.Log("Expected resource type: Texture2D");
            }

            return texture;
        }
        
        // Loads a UXML VisualTreeAsset from the prebuilt k2d2_ui.bundle AssetBundle. This is the v1.1.0
        // 0.2.8.5-proven mechanism. v1.2.0 loaded through Redux's Addressables-backed Assets API instead
        // (Assets.LoadAssetAsync<T>(address).WaitForCompletion(), via K2D2_Plugin's public
        // LoadAddressableAsset<T>() wrapper), but that route's runtime catalog resolution is unproven on
        // this game build - see Deploy/obj/addressables-verdict.md.
        public static VisualTreeAsset LoadUxml(string path)
        {
            var location = $"Assets/UI/K2D2_UI/{path}";
            return Bundle.LoadAsset<VisualTreeAsset>(location);
        }

    }

}
