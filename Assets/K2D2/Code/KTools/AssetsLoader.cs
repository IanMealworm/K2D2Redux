using System.IO;
using UnityEngine;
using K2D2;
using UnityEngine.UIElements;

namespace KTools
{

    public static class AssetsLoader
    {
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
        
        public static VisualTreeAsset LoadUxml(string path)
        {
            var location = $"Assets/UI/K2D2_UI/{path}";
            return Bundle.LoadAsset<VisualTreeAsset>(location);
        }

    }

}
