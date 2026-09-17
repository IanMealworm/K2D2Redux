using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine.UIElements;

namespace K2D2.EditorTools
{
    /// <summary>
    /// Replaces mod-owned generated UXML data with the shipped late-bound runtime proxies while
    /// <see cref="UnityEditor.BuildPipeline.BuildAssetBundles(string, BuildAssetBundleOptions, BuildTarget)"/>
    /// serializes the UI assets, then restores the source assets.
    ///
    /// Why this exists: the K2UI controls are [UxmlElement]-based, so Unity generates a companion
    /// `X/UxmlSerializedData` class per control and bakes an ASSEMBLY-QUALIFIED reference into the
    /// imported UXML asset. Unity resolves such managed-reference types from its own compiled-in
    /// assembly list only, and the mod's `K2D2` assembly is runtime-loaded by SpaceWarp2 - so the
    /// player can never resolve `K2D2 K2UI.*/UxmlSerializedData`, no matter that the types are in
    /// the shipped DLL. Launch 2 measured exactly that: "Unknown managed type referenced:
    /// K2D2 K2UI.Tabs.TabPage/UxmlSerializedData" x7 then "the cloned VisualTreeAsset is empty".
    ///
    /// The Redux SDK builds a proxy, `UitkForKsp2.Controls.LateBoundUxmlSerializedData`, into the
    /// game's shipped `uitkforksp2.controls.Runtime.dll` (a Unity-known assembly, therefore
    /// resolvable). It defers construction to runtime, resolves the real type from the loaded mod
    /// assembly and restores the serialized values. The SDK's own
    /// `Ksp2UnityTools.Editor.Modding.Thunderkit.LateBoundUxmlBuildConversion` applies it around
    /// Addressables builds - but the SDK also ships this script (upstream's `K2D2 > Rebuild UI
    /// Bundle` path), which calls `BuildPipeline.BuildAssetBundles` directly and therefore bypasses
    /// that conversion entirely. This class mirrors the SDK conversion for this direct path.
    ///
    /// The SDK class is `internal`, so this is a replication, not a reference; everything about the
    /// proxy is reached by reflection so the editor assembly needs no asmdef or package reference
    /// for `uitkforksp2.controls.Runtime` (exactly as the SDK's own class does).
    /// </summary>
    internal sealed class LateBoundUxmlBundleConversion : IDisposable
    {
        private const string LateBoundFullTypeName = "UitkForKsp2.Controls.LateBoundUxmlSerializedData";
        private const string LateBoundTypeName =
            "UitkForKsp2.Controls.LateBoundUxmlSerializedData, uitkforksp2.controls.Runtime";

        private static readonly FieldInfo VisualTreeField = typeof(VisualTreeAsset).GetField(
            "m_VisualTree",
            BindingFlags.Instance | BindingFlags.NonPublic);

        private static readonly Type VisualElementAssetType = typeof(VisualTreeAsset).Assembly.GetType(
            "UnityEngine.UIElements.VisualElementAsset",
            true);

        private static readonly FieldInfo ChildrenField = FindFieldInHierarchy(VisualElementAssetType, "m_Children");

        private static readonly PropertyInfo SerializedDataProperty = VisualElementAssetType.GetProperty(
            "serializedData",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        private static readonly Type TemplateAssetType = typeof(VisualTreeAsset).Assembly.GetType(
            "UnityEngine.UIElements.TemplateAsset",
            true);

        private static readonly PropertyInfo SerializedDataOverridesProperty = TemplateAssetType.GetProperty(
            "serializedDataOverrides",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        private static readonly FieldInfo OverrideSerializedDataField = TemplateAssetType
            .GetNestedType("UxmlSerializedDataOverride", BindingFlags.Public | BindingFlags.NonPublic)
            .GetField("m_SerializedData", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        private sealed class AssetChanges
        {
            public VisualTreeAsset Asset;
            public bool WasDirty;
            public List<Action> RestoreActions = new List<Action>();
        }

        private readonly List<AssetChanges> _changedAssets = new List<AssetChanges>();

        private LateBoundUxmlBundleConversion()
        {
        }

        /// <summary>
        /// Gets the number of generated UXML data references replaced by this conversion.
        /// </summary>
        public int ConvertedReferenceCount
        {
            get
            {
                int count = 0;
                foreach (AssetChanges asset in _changedAssets)
                {
                    count += asset.RestoreActions.Count;
                }
                return count;
            }
        }

        /// <summary>
        /// Converts generated UXML data owned by the specified mod assemblies, for every
        /// <see cref="VisualTreeAsset"/> found under the given folders.
        /// </summary>
        /// <param name="folderPaths">Project folders to scan for VisualTreeAssets.</param>
        /// <param name="modAssemblyNames">The mod assembly names whose generated UXML data must be converted.</param>
        /// <returns>The conversion scope that restores the source assets when disposed.</returns>
        public static LateBoundUxmlBundleConversion Apply(
            IEnumerable<string> folderPaths,
            IReadOnlyCollection<string> modAssemblyNames)
        {
            var conversion = new LateBoundUxmlBundleConversion();
            var assemblyNames = new HashSet<string>(modAssemblyNames, StringComparer.OrdinalIgnoreCase);
            foreach (string assetPath in FindVisualTreeAssetPaths(folderPaths))
            {
                var asset = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(assetPath);
                if (asset != null)
                {
                    conversion.ConvertAsset(asset, assemblyNames);
                }
            }

            return conversion;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            for (int assetIndex = _changedAssets.Count - 1; assetIndex >= 0; assetIndex--)
            {
                AssetChanges assetChanges = _changedAssets[assetIndex];
                if (assetChanges.Asset == null)
                {
                    continue;
                }

                for (int changeIndex = assetChanges.RestoreActions.Count - 1; changeIndex >= 0; changeIndex--)
                {
                    assetChanges.RestoreActions[changeIndex]();
                }

                if (!assetChanges.WasDirty)
                {
                    EditorUtility.ClearDirty(assetChanges.Asset);
                }
            }

            _changedAssets.Clear();
        }

        private void ConvertAsset(VisualTreeAsset asset, HashSet<string> modAssemblyNames)
        {
            var assetChanges = new AssetChanges
            {
                Asset = asset,
                WasDirty = EditorUtility.IsDirty(asset)
            };

            foreach (object element in TraverseElements(asset))
            {
                ConvertReference(
                    SerializedDataProperty.GetValue(element) as UxmlSerializedData,
                    value => SerializedDataProperty.SetValue(element, value),
                    modAssemblyNames,
                    assetChanges);

                if (!TemplateAssetType.IsInstanceOfType(element) ||
                    !(SerializedDataOverridesProperty.GetValue(element) is IList overrides))
                {
                    continue;
                }

                for (int index = 0; index < overrides.Count; index++)
                {
                    int capturedIndex = index;
                    ConvertReference(
                        OverrideSerializedDataField.GetValue(overrides[index]) as UxmlSerializedData,
                        value =>
                        {
                            object item = overrides[capturedIndex];
                            OverrideSerializedDataField.SetValue(item, value);
                            overrides[capturedIndex] = item;
                        },
                        modAssemblyNames,
                        assetChanges);
                }
            }

            if (assetChanges.RestoreActions.Count == 0)
            {
                return;
            }

            _changedAssets.Add(assetChanges);
            EditorUtility.SetDirty(asset);
        }

        private static void ConvertReference(
            UxmlSerializedData source,
            Action<UxmlSerializedData> setValue,
            HashSet<string> modAssemblyNames,
            AssetChanges assetChanges)
        {
            if (source == null ||
                string.Equals(source.GetType().FullName, LateBoundFullTypeName, StringComparison.Ordinal) ||
                !modAssemblyNames.Contains(source.GetType().Assembly.GetName().Name))
            {
                return;
            }

            UxmlSerializedData replacement = CreateLateBoundData(source);
            setValue(replacement);
            assetChanges.RestoreActions.Add(() => setValue(source));
        }

        private static IEnumerable<object> TraverseElements(VisualTreeAsset asset)
        {
            object root = VisualTreeField?.GetValue(asset);
            if (root == null || ChildrenField == null)
            {
                yield break;
            }

            var pending = new Stack<object>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                object current = pending.Pop();
                if (VisualElementAssetType.IsInstanceOfType(current))
                {
                    yield return current;
                }

                if (!(ChildrenField.GetValue(current) is IList children))
                {
                    continue;
                }

                for (int index = children.Count - 1; index >= 0; index--)
                {
                    if (children[index] != null)
                    {
                        pending.Push(children[index]);
                    }
                }
            }
        }

        private static UxmlSerializedData CreateLateBoundData(UxmlSerializedData source)
        {
            var lateBoundType = FindLateBoundType();
            MethodInfo createMethod = lateBoundType?.GetMethod(
                "Create",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(UxmlSerializedData) },
                null);
            if (createMethod == null)
            {
                throw new InvalidOperationException(
                    "The installed UITK for KSP 2 package does not provide late-bound UXML support. " +
                    "Update uitkforksp2.controls before building this mod.");
            }

            return (UxmlSerializedData)createMethod.Invoke(null, new object[] { source });
        }

        /// <summary>
        /// Resolves the shipped proxy type. The assembly-qualified lookup is tried first (the SDK's
        /// own route); the AppDomain scan is the same fallback the proxy's own ResolveType uses, for
        /// the case where the editor has not yet loaded the package assembly by name.
        /// </summary>
        private static Type FindLateBoundType()
        {
            Type lateBoundType = Type.GetType(LateBoundTypeName, false);
            if (lateBoundType != null)
            {
                return lateBoundType;
            }

            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (string.Equals(
                    assembly.GetName().Name,
                    "uitkforksp2.controls.Runtime",
                    StringComparison.OrdinalIgnoreCase))
                {
                    return assembly.GetType(LateBoundFullTypeName, false);
                }
            }

            return null;
        }

        private static FieldInfo FindFieldInHierarchy(Type type, string fieldName)
        {
            for (Type current = type; current != null; current = current.BaseType)
            {
                FieldInfo field = current.GetField(
                    fieldName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (field != null)
                {
                    return field;
                }
            }

            return null;
        }

        private static IEnumerable<string> FindVisualTreeAssetPaths(IEnumerable<string> folderPaths)
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var folders = new List<string>();
            foreach (string folderPath in folderPaths)
            {
                if (!string.IsNullOrEmpty(folderPath) && AssetDatabase.IsValidFolder(folderPath))
                {
                    folders.Add(folderPath);
                }
            }

            if (folders.Count == 0)
            {
                return paths;
            }

            foreach (string guid in AssetDatabase.FindAssets("t:VisualTreeAsset", folders.ToArray()))
            {
                paths.Add(AssetDatabase.GUIDToAssetPath(guid));
            }

            return paths;
        }
    }
}
