using System.IO;
using UnityEditor;
using UnityEngine;

namespace K2D2.EditorTools
{
    /// <summary>
    /// Rebuilds Copied/assets/bundles/k2d2_ui.bundle from the real UXML/USS/image/font source now
    /// living under Assets/UI/K2D2_UI, Assets/Runtime/K2UI, Assets/Images and Assets/Fonts, instead
    /// of shipping the old prebuilt bundle carried over unchanged from the original SpaceWarp1-era
    /// project (built under Unity 2022.3.5f1). That old bundle's serialized VisualTreeAsset clones
    /// with zero children under this project's Unity 6000.5.8f1 UI Toolkit runtime - it's what made
    /// K2D2's window silently fail to open (see NOTICE.md's "Sixth follow-up" for the full writeup:
    /// the crash is `_rootElement = root[0]` throwing in K2D2Window.OnUiReload because `root` has no
    /// children). Rebuilding here, with the currently-installed Unity version and the
    /// currently-compiled K2UI custom control classes (now [UxmlElement]-based rather than the
    /// old UxmlFactory, which Unity 6.6 removes - see NOTICE.md's UxmlElement migration entry),
    /// removes that version mismatch.
    ///
    /// Run via K2D2 > Rebuild UI Bundle any time something under those source folders changes -
    /// it always rebuilds from whatever is currently in the project, so it's safe to re-run
    /// repeatedly (e.g. after every UI edit) rather than being a one-time fix.
    /// </summary>
    public static class RebuildK2D2UIBundle
    {
        private const string BundleName = "k2d2_ui.bundle";
        private const string SourceDir = "Assets/UI/K2D2_UI";
        private const string RootUxml = SourceDir + "/K2D2_Window.uxml";
        private const string OutputDir = "Assets/K2D2/Copied/assets/bundles";
        private const string ModAssemblyName = "K2D2";

        [MenuItem("K2D2/Rebuild UI Bundle")]
        public static void Rebuild()
        {
            if (!File.Exists(RootUxml))
            {
                Debug.LogError($"[K2D2] RebuildK2D2UIBundle: {RootUxml} not found - the UI source " +
                    "doesn't seem to be in this project yet under Assets/UI/K2D2_UI.");
                return;
            }

            // Mark every UXML page/template and stylesheet under Assets/UI/K2D2_UI with the
            // AssetBundle name explicitly, rather than relying only on K2D2_Window.uxml pulling the
            // rest in as implicit dependencies - this way every page ends up in the bundle even if
            // one isn't currently reachable from the window (e.g. mid-edit), and re-running this
            // after adding a new page picks it up automatically without any manual Inspector step.
            var guids = AssetDatabase.FindAssets("t:VisualTreeAsset t:StyleSheet", new[] { SourceDir });
            int marked = 0;
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var importer = AssetImporter.GetAtPath(path);
                if (importer == null) continue;
                importer.SetAssetBundleNameAndVariant(BundleName, "");
                marked++;
            }
            Debug.Log($"[K2D2] RebuildK2D2UIBundle: marked {marked} UXML/USS asset(s) under {SourceDir} " +
                $"for bundle '{BundleName}'. Their own dependencies (Runtime/K2UI stylesheets, " +
                "Images, Fonts/Caravan) are pulled in automatically by BuildAssetBundles.");

            if (!Directory.Exists(OutputDir))
                Directory.CreateDirectory(OutputDir);

            // [UxmlElement] controls get Unity-generated `X/UxmlSerializedData` companions that the
            // imported UXML references by assembly-qualified name. The player can only resolve
            // managed-reference types from assemblies it was compiled against, and this mod's
            // `K2D2` assembly is runtime-loaded by SpaceWarp2 - so a bundle that names
            // `K2D2 K2UI.*/UxmlSerializedData` deserializes with zero children (launch 2 measured
            // it). The conversion below swaps every mod-owned reference for the shipped
            // `UitkForKsp2.Controls.LateBoundUxmlSerializedData` proxy (in the Unity-known
            // uitkforksp2.controls.Runtime assembly), which defers construction to runtime, and
            // restores the source assets when the scope disposes after the build.
            AssetBundleManifest manifest;
            using (var conversion = LateBoundUxmlBundleConversion.Apply(new[] { SourceDir }, new[] { ModAssemblyName }))
            {
                if (conversion.ConvertedReferenceCount > 0)
                {
                    Debug.Log($"[K2D2] RebuildK2D2UIBundle: converted " +
                        $"{conversion.ConvertedReferenceCount} mod-owned UxmlSerializedData reference(s) " +
                        "to UitkForKsp2.Controls.LateBoundUxmlSerializedData.");
                }
                else
                {
                    Debug.LogWarning("[K2D2] RebuildK2D2UIBundle: no mod-owned UxmlSerializedData " +
                        "references found - the built bundle would not be player-loadable. Check that " +
                        "the [UxmlElement] K2UI controls still compile into the K2D2 assembly.");
                }

                // ForceRebuildAssetBundle is required, and is the second half of the fix. Unity's
                // incremental check compares the bundle's inputs as they are SAVED on disk, and the
                // conversion above deliberately only touches the in-memory objects (the sources are
                // restored on dispose). Without the flag Unity decides every bundle is up to date and
                // skips the write entirely - measured: the build reports only the `bundles` index
                // (1.6 KB) and k2d2_ui.bundle keeps its pre-conversion mtime and contents. With it,
                // the converted tree is what gets serialized; measured on StatusGroup.uxml, the built
                // file's type tree goes from 3 `K2D2 K2UI.*/UxmlSerializedData` records to 0, with the
                // proxy records in their place.
                manifest = BuildPipeline.BuildAssetBundles(
                    OutputDir, BuildAssetBundleOptions.ForceRebuildAssetBundle,
                    EditorUserBuildSettings.activeBuildTarget);
            }

            if (manifest == null)
            {
                Debug.LogError("[K2D2] RebuildK2D2UIBundle: BuildAssetBundles returned null - the " +
                    "build failed, check the console above this message for the actual error.");
                return;
            }

            // BuildAssetBundles also writes a same-named manifest bundle for OutputDir itself plus
            // per-bundle ".manifest" text files alongside k2d2_ui.bundle - neither is read by
            // AssetsLoader/K2D2_Plugin.cs at runtime, so they're harmless to leave in place.
            var builtPath = Path.Combine(OutputDir, BundleName);
            if (File.Exists(builtPath))
            {
                Debug.Log($"[K2D2] RebuildK2D2UIBundle: done - wrote {builtPath} " +
                    $"({new FileInfo(builtPath).Length:N0} bytes).");
            }
            else
            {
                Debug.LogWarning($"[K2D2] RebuildK2D2UIBundle: build finished but {builtPath} " +
                    $"wasn't found - check {OutputDir} for the actual output filename.");
            }

            AssetDatabase.Refresh();
        }
    }
}
