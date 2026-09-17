// Temporary diagnostic mod, Phase 3 of the K2-D2 v1.2 / Redux 0.2.8.5 backport.
// Not part of the shipped K2-D2 backport, and deliberately NOT compiled into K2D2.dll.
//
// WHY THIS EXISTS (launch #3 evidence)
// ------------------------------------
// Launch #3 (v1.2 DLL + v1.2 bundle, md5 1db387a84256557c1d1de03a75387c09) showed:
//   * K2D2Window.BindUi finished wiring successfully (so binding/manipulators are fine), and
//   * from the moment the window was opened, an endless storm of
//       NullReferenceException ... TextUtilities.GetTextCoreSettingsForElement(VisualElement, bool)
//       ... RenderTree.ProcessChanges() ... UIRepaintUpdater.Update() ... Panel.UpdateForRepaint()
//     i.e. the panel's render tree never completes a visual update, so nothing paints.
//
// IL of the installed 0.2.8.5 UnityEngine.UIElementsModule.dll, TextUtilities::
// GetTextCoreSettingsForElement, proves the only nullable dereference on that path is:
//   IL_0136: ldloc.0                                   // V_0 = TextUtilities.GetFontAsset(ve)
//   IL_0137: callvirt TextAsset::get_material()
//   IL_013c: callvirt Material::get_mainTexture()
//   IL_0141: castclass Texture2D
//   IL_0146: callvirt Texture2D::get_format()
// and that a null FontAsset returns early (empty settings, no throw). So the throw means the
// element resolved a *non-null* FontAsset whose `material` (or its `mainTexture`) is null.
//
// This mod answers, in ONE game launch, and for several candidate bundles at once:
//   A) which FontAssets/Materials each candidate bundle actually ships, loaded from the BUILT
//      BUNDLE (not from the throwaway project, which is the gap in the Phase-2 verification),
//      with the material/shader/mainTexture/atlas state of each;
//   B) what the LIVE panel resolved for every TextElement in the tree (computed font
//      definition -> fontAsset -> material -> mainTexture/atlas), including the live K2-D2
//      window itself, and whether the NRE storm runs during that candidate's phase.
//
// Everything is wrapped in try/catch per candidate and per element: one bad candidate can
// never abort the rest.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using UnityEngine.TextCore.Text;
using UnityEngine.UIElements;
using Redux.ExtraModTypes;
using UitkForKsp2.API;
// UnityEngine.TextCore.Text (for FontAsset) and UnityEngine.UIElements both define TextElement;
// the mod walks UI Toolkit's one.
using TextElement = UnityEngine.UIElements.TextElement;

namespace K2D2Diag
{
    public class DiagPlugin : KerbalMod
    {
        private const string Tag = "[K2D2DIAG]";

        // Characters the glyph probe asks each bundled theme font to rasterise in the player.
        private const string GlyphProbeChars = "K2-D2 flight 0123456789%";

        // Frames each phase's window is left up so the panel runs real style/layout/repaint
        // passes, with the tree walked at three checkpoints. Variant phases are deliberately
        // short: the broken control reproduces the NRE storm at ~60 exceptions/second, and the
        // point of that phase is to count the storm, not to fill the log with it.
        private const int VariantPhaseFrames = 180;
        private const int LivePhaseFrames = 420;
        private static readonly int[] VariantWalkFrames = { 45, 120, 175 };
        private static readonly int[] LiveWalkFrames = { 90, 240, 390 };

        private int PhaseFrames { get { Cand c = CurrentCand(); return (c != null && c.LiveWindow) ? LivePhaseFrames : VariantPhaseFrames; } }
        private int[] WalkFrames { get { Cand c = CurrentCand(); return (c != null && c.LiveWindow) ? LiveWalkFrames : VariantWalkFrames; } }

        private class Cand
        {
            public string Name;
            public string Path;
            public long Size = -1;
            public string Md5;
            public AssetBundle Bundle;
            public VisualTreeAsset Vta;
            public int ChildCount = -1;
            public int ContainerCount = -1;
            public bool LoadOk;
            public bool LoadDupe;
            public bool LoadFailed;
            public string FailNote = "";
            public UIDocument Doc;
            public VisualElement Root;
            public object WindowComponent;
            public bool Created;
            public bool OwnedWindow;
            public bool LiveWindow;
            public bool LiveWasOpen;
            public int NreInPhase;
            public int BrokenSeen;
            public int TextSeen;
            public int ReferenceFontsSeen;
            public readonly HashSet<string> ProbedFonts = new HashSet<string>();
        }

        private readonly List<Cand> _cands = new List<Cand>();
        private readonly Dictionary<string, string> _md5Seen = new Dictionary<string, string>();
        private Cand _live;

        private bool _hooked;
        private bool _ready;
        private bool _finished;
        private bool _postLogging;
        private float _lastPostLog;
        private float _t0;
        private int _phase = -1;            // -1 = waiting; 0..n-1 = _cands[i]; n = live window; n+1 = done
        private int _frameInPhase;
        private int _walkIdx;
        private int _nrePhase;
        private int _nreTotal;
        private readonly List<string> _phaseExc = new List<string>();
        private readonly List<string> _allExc = new List<string>();

        private void Say(string s)
        {
            // KerbalMod exposes its logger as SWLogger (ReduxLib.Logging.ILogger).
            SWLogger.LogInfo(Tag + " " + s);
        }

        private static string OneLine(string s)
        {
            if (s == null) return "";
            return s.Replace("\r", " ").Replace("\n", " \\n ");
        }

        // ------------------------------------------------------------------ init
        public override void OnInitialized()
        {
            _t0 = Time.realtimeSinceStartup;
            Say("=========== START (phase 3 diag: built-bundle fonts + live-panel walk + drag bounds) ===========");
            Say("Application.unityVersion = " + Application.unityVersion);
            Say("Application.platform    = " + Application.platform);
            Say("Time.realtimeSinceStartup = " + _t0.ToString("F2"));

            HookLog();

            string ownFolder;
            try
            {
                ownFolder = SWMetadata.Folder.FullName;
            }
            catch (Exception e)
            {
                Say("SWMetadata.Folder THREW " + e.GetType().Name + ": " + e.Message);
                ownFolder = null;
            }
            Say("own mod folder = " + ownFolder);

            // Candidate corpus: Mods/K2D2Diag/variants/<name>/*.bundle, sorted by name so the
            // variant order in the log matches the build order.
            if (!string.IsNullOrEmpty(ownFolder))
            {
                string variantsRoot = System.IO.Path.Combine(ownFolder, "variants");
                Say("variants root  = " + variantsRoot);
                string[] files = new string[0];
                try
                {
                    if (Directory.Exists(variantsRoot))
                        files = Directory.GetFiles(variantsRoot, "*.bundle", SearchOption.AllDirectories);
                    else
                        Say("variants root does not exist");
                }
                catch (Exception e)
                {
                    Say("variants enumeration THREW " + e.GetType().Name + ": " + e.Message);
                }
                Array.Sort(files, StringComparer.Ordinal);
                Say("corpus bundles found: " + files.Length);
                foreach (string f in files)
                {
                    string name = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(f));
                    if (string.IsNullOrEmpty(name))
                        name = System.IO.Path.GetFileName(f);
                    _cands.Add(new Cand { Name = name, Path = f });
                }
            }

            // Phase A: load every candidate and inventory it (font/material level, no panel).
            foreach (Cand c in _cands)
                LoadCandidate(c);

            // The live K2-D2 window is the highest-value candidate: it is the exact shipped
            // bundle + DLL combination the user saw fail. It is discovered by reflection later,
            // once the game UI is up - never by LoadFromFile, because the file shares its
            // internal CAB name (CAB-f99fd9624387dc8e95345c9ba08ea1f91) with the bundle the
            // live mod already loaded, so a second LoadFromFile would collide with it.
            _live = new Cand { Name = "LIVE_K2D2_window", Path = "(live window, no bundle load)", LiveWindow = true };
            ShellInventory(_live);

            Say("phase plan: " + _cands.Count + " variant candidate(s), then the live K2-D2 window");
            Say("trigger: phases start after the game's own UI is up (see the readiness line below)");
            Say("=========== PHASE A DONE ===========");
        }

        private void HookLog()
        {
            if (_hooked) return;
            _hooked = true;
            try
            {
                Application.logMessageReceived += OnLogMessage;
                Say("log hook installed (watching for the appbar line + counting the NRE storm)");
            }
            catch (Exception e)
            {
                Say("log hook failed: " + e.GetType().Name + ": " + e.Message);
            }
        }

        private void OnLogMessage(string condition, string stackTrace, LogType type)
        {
            try
            {
                bool isNre = (condition != null && condition.IndexOf("GetTextCoreSettingsForElement", StringComparison.Ordinal) >= 0)
                          || (stackTrace != null && stackTrace.IndexOf("GetTextCoreSettingsForElement", StringComparison.Ordinal) >= 0);
                if (isNre)
                {
                    _nrePhase++;
                    _nreTotal++;
                }
                else if (type == LogType.Exception || type == LogType.Error)
                {
                    string msg = OneLine(condition);
                    if (msg.Length > 300) msg = msg.Substring(0, 300);
                    string key = msg;
                    if (!_phaseExc.Contains(key))
                    {
                        _phaseExc.Add(key);
                        string full = OneLine(condition);
                        if (full.Length > 700) full = full.Substring(0, 700);
                        if (!_allExc.Contains(full)) _allExc.Add(full);
                    }
                }

                if (!_ready && condition != null &&
                    (condition.IndexOf("Added appbar button", StringComparison.Ordinal) >= 0 ||
                     condition.IndexOf("RegisterAppButton", StringComparison.Ordinal) >= 0))
                {
                    _ready = true;
                    Say("readiness: saw '" + OneLine(condition) + "' at t=" +
                        Time.realtimeSinceStartup.ToString("F2") + " - game UI is up");
                }
            }
            catch
            {
                // never let the log callback throw
            }
        }

        // ------------------------------------------------------- phase A: bundle inventory
        private void LoadCandidate(Cand c)
        {
            Say("--- A[" + c.Name + "] " + c.Path);
            try
            {
                if (!File.Exists(c.Path))
                {
                    c.LoadFailed = true;
                    c.FailNote = "MISSING FILE";
                    Say("A[" + c.Name + "] MISSING FILE");
                    return;
                }

                c.Size = new FileInfo(c.Path).Length;
                c.Md5 = FileMd5(c.Path);
                Say("A[" + c.Name + "] size=" + c.Size + " md5=" + c.Md5);

                string prevName;
                if (c.Md5 != null && _md5Seen.TryGetValue(c.Md5, out prevName))
                {
                    c.LoadDupe = true;
                    c.FailNote = "identical file to '" + prevName + "'";
                    Say("A[" + c.Name + "] DUPLICATE of " + prevName + " (same md5) - not loaded again");
                    return;
                }

                int logLinesBefore = CountLogLines();
                AssetBundle bundle = AssetBundle.LoadFromFile(c.Path);
                if (bundle == null)
                {
                    c.LoadFailed = true;
                    c.FailNote = "LoadFromFile -> null (" + (FindErrorSince(logLinesBefore, System.IO.Path.GetFileName(c.Path)) ?? "no loader reason found") + ")";
                    Say("A[" + c.Name + "] LoadFromFile -> NULL: " + c.FailNote);
                    return;
                }

                c.LoadOk = true;
                c.Bundle = bundle;
                if (c.Md5 != null) _md5Seen[c.Md5] = c.Name;

                string[] names = bundle.GetAllAssetNames();
                Array.Sort(names, StringComparer.Ordinal);
                c.ContainerCount = names.Length;
                Say("A[" + c.Name + "] LoadFromFile OK name='" + bundle.name + "' sceneBundle=" +
                    bundle.isStreamedSceneAssetBundle + " containerAssets=" + names.Length);
                foreach (string n in names)
                    Say("A[" + c.Name + "]   container: " + n);

                FontAndMaterialInventory(bundle, "A[" + c.Name + "]");

                foreach (string n in names)
                {
                    if (!n.EndsWith(".uxml", StringComparison.OrdinalIgnoreCase)) continue;
                    VisualTreeAsset vta = null;
                    try { vta = bundle.LoadAsset<VisualTreeAsset>(n); } catch { }
                    if (vta == null)
                    {
                        Say("A[" + c.Name + "]   uxml " + n + " -> NULL");
                        continue;
                    }
                    int cc = -1;
                    string note = "";
                    try
                    {
                        TemplateContainer inst = vta.Instantiate();
                        cc = inst == null ? -1 : inst.childCount;
                    }
                    catch (Exception e)
                    {
                        note = " Instantiate THREW " + e.GetType().Name + ": " + OneLine(e.Message);
                    }
                    Say("A[" + c.Name + "]   uxml " + n + " childCount=" + cc + note);
                    if (c.Vta == null || n.IndexOf("k2d2_window", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        c.Vta = vta;
                        c.ChildCount = cc;
                    }
                }
                if (c.Vta == null)
                    Say("A[" + c.Name + "] no VisualTreeAsset found in container - live phase will be skipped");

                // The stylesheets' own asset arrays are exactly the objects the runtime will
                // hand to the text code, so their state is the decisive Phase-A evidence.
                foreach (StyleSheet sheet in SafeLoadAll<StyleSheet>(bundle))
                    SheetInventory(sheet, "A[" + c.Name + "]");
            }
            catch (Exception e)
            {
                c.LoadFailed = true;
                c.FailNote = e.GetType().Name + ": " + OneLine(e.Message);
                Say("A[" + c.Name + "] THREW " + e.GetType().FullName + ": " + OneLine(e.Message));
            }
        }

        private void FontAndMaterialInventory(AssetBundle bundle, string tag)
        {
            try
            {
                // LoadAllAssets returns main assets only; fonts that are pulled in as dependencies
                // of a stylesheet (every theme font in this bundle) show up through SheetInventory
                // below instead, which reads the stylesheet's own asset table.
                List<FontAsset> fonts = SafeLoadAll<FontAsset>(bundle);
                Say(tag + " bundle font assets (LoadAllAssets<FontAsset>): " + fonts.Count);
                foreach (FontAsset fa in fonts)
                    Say(tag + "   FONT '" + fa.name + "' " + DescribeFontAsset(fa));
            }
            catch (Exception e)
            {
                Say(tag + " font inventory THREW " + e.GetType().Name + ": " + OneLine(e.Message));
            }

            try
            {
                List<Material> mats = SafeLoadAll<Material>(bundle);
                Say(tag + " bundle materials (LoadAllAssets<Material>): " + mats.Count);
                foreach (Material m in mats)
                    Say(tag + "   MAT '" + m.name + "' " + DescribeMaterial(m));
            }
            catch (Exception e)
            {
                Say(tag + " material inventory THREW " + e.GetType().Name + ": " + OneLine(e.Message));
            }
        }

        private void SheetInventory(StyleSheet sheet, string tag)
        {
            try
            {
                Say(tag + "   SHEET '" + sheet.name + "' " + DescribeSheetImportState(sheet));
                IList assets = SheetAssets(sheet);
                if (assets == null)
                {
                    Say(tag + "     m_Assets: <unreadable>");
                    return;
                }
                Say(tag + "     m_Assets.Count=" + assets.Count);
                for (int i = 0; i < assets.Count; i++)
                {
                    UnityEngine.Object o = assets[i] as UnityEngine.Object;
                    if (o == null)
                    {
                        Say(tag + "     m_Assets[" + i + "] = NULL (unresolved reference)");
                        continue;
                    }
                    FontAsset fa = o as FontAsset;
                    if (fa != null)
                        Say(tag + "     m_Assets[" + i + "] FontAsset '" + fa.name + "' " + DescribeFontAsset(fa));
                    else
                        Say(tag + "     m_Assets[" + i + "] " + o.GetType().Name + " '" + o.name + "'");
                }
            }
            catch (Exception e)
            {
                Say(tag + "   sheet inventory THREW " + e.GetType().Name + ": " + OneLine(e.Message));
            }
        }

        /// <summary>
        /// Phase C: inventory the asset bundle the shipped K2-D2 mod itself loaded, plus every
        /// already-loaded bundle that carries one of the theme fonts as a reference copy. This is
        /// the in-player measurement of the BUILT bundle - exactly the gap the Phase-2 project-side
        /// instantiation left open. Never calls LoadFromFile on the live bundle: it shares its CAB
        /// name with the already-loaded instance and Unity refuses a second load.
        /// </summary>
        private void ShellInventory(Cand c)
        {
            AssetBundle[] loaded;
            try
            {
                // 6000.4 returns IEnumerable<AssetBundle>, not an array.
                loaded = AssetBundle.GetAllLoadedAssetBundles().ToArray();
                if (loaded == null) loaded = new AssetBundle[0];
            }
            catch (Exception e)
            {
                Say("C loaded-bundle enumeration THREW " + e.GetType().Name + ": " + OneLine(e.Message));
                loaded = new AssetBundle[0];
            }
            Say("C loaded asset bundles in the player: " + loaded.Length);

            try
            {
                string ownFolder = SWMetadata.Folder.FullName;
                string candidate = System.IO.Path.GetFullPath(System.IO.Path.Combine(
                    ownFolder, "..", "K2D2", "assets", "bundles", "k2d2_ui.bundle"));
                if (File.Exists(candidate))
                    Say("C live k2d2_ui.bundle on disk: " + candidate + " size=" + new FileInfo(candidate).Length +
                        " md5=" + FileMd5(candidate));
                else
                    Say("C live k2d2_ui.bundle NOT FOUND at " + candidate);
            }
            catch (Exception e)
            {
                Say("C live bundle path probe THREW " + e.GetType().Name + ": " + OneLine(e.Message));
            }

            foreach (AssetBundle b in loaded)
            {
                try
                {
                    if (b == null || b.name == null) continue;
                    if (b.name.IndexOf("k2d2_ui", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    string[] names = b.GetAllAssetNames();
                    Say("C live bundle name='" + b.name + "' containerAssets=" + names.Length);
                    FontAndMaterialInventory(b, "C");
                    DynamicGlyphProbe(b, c);
                }
                catch (Exception e)
                {
                    Say("C live bundle inventory THREW " + e.GetType().Name + ": " + OneLine(e.Message));
                }
            }

            // Reference copies of the same theme fonts, loaded by the game/K2-D2 itself. If those
            // have working materials in the player, the defect is in the mod bundle's recipe (a
            // reference that did not survive packing), not in the player or the font files.
            string[] watch = { "SFPixelate SDF", "RosesareFF0000 SDF", "JetBrainsMono-Regular SDF", "pixelate SDF", "led_counter-7 SDF" };
            int inspected = 0, matches = 0;
            foreach (AssetBundle b in loaded)
            {
                if (inspected >= 200)
                {
                    Say("C reference scan stopped after 200 loaded bundles");
                    break;
                }
                if (b == null) continue;
                inspected++;
                try
                {
                    if (b.name != null && b.name.IndexOf("k2d2_ui", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    foreach (FontAsset fa in SafeLoadAll<FontAsset>(b))
                    {
                        if (fa == null) continue;
                        bool hit = false;
                        foreach (string w in watch) if (fa.name == w) hit = true;
                        if (!hit) continue;
                        c.ReferenceFontsSeen++;
                        if (matches < 40) Say("C REF bundle='" + b.name + "' FONT '" + fa.name + "' " + DescribeFontAsset(fa));
                        matches++;
                    }
                }
                catch (Exception e)
                {
                    Say("C REF bundle '" + (b.name ?? "?") + "' THREW " + e.GetType().Name + ": " + OneLine(e.Message));
                }
            }
            Say("C reference scan: inspected=" + inspected + " loaded bundles, reference theme fonts found=" + matches);

            if (c.ReferenceFontsSeen == 0)
                Say("C no reference copies of the theme fonts were found among the loaded bundles");
        }

        /// <summary>
        /// Proves, in the player, that a dynamic theme font can still rasterise glyphs - i.e. that
        /// the text will be drawn (not merely that the font's material is non-null).
        /// </summary>
        private void DynamicGlyphProbe(AssetBundle b, Cand c)
        {
            try
            {
                foreach (StyleSheet sheet in SafeLoadAll<StyleSheet>(b))
                {
                    IList assets = SheetAssets(sheet);
                    if (assets == null) continue;
                    for (int i = 0; i < assets.Count; i++)
                    {
                        FontAsset fa = assets[i] as FontAsset;
                        if (fa == null) continue;
                        if (c.ProbedFonts.Contains(fa.name)) continue;
                        c.ProbedFonts.Add(fa.name);
                        ProbeOneFont(fa);
                    }
                }
            }
            catch (Exception e)
            {
                Say("C glyph probe THREW " + e.GetType().Name + ": " + OneLine(e.Message));
            }
        }

        private void ProbeOneFont(FontAsset fa)
        {
            try
            {
                Texture2D before = fa.atlasTextures != null && fa.atlasTextures.Length > 0 ? fa.atlasTextures[0] : null;
                string beforeSize = before == null ? "null" : before.width + "x" + before.height;
                // TextCore's FontAsset only exposes the uint[] overloads (checked in the installed
                // UnityEngine.TextCoreTextEngineModule.dll IL), so convert the probe string here.
                uint[] codes = new uint[GlyphProbeChars.Length];
                for (int i = 0; i < GlyphProbeChars.Length; i++) codes[i] = GlyphProbeChars[i];
                uint[] missing;
                bool ok = fa.TryAddCharacters(codes, out missing, false);
                Texture2D after = fa.atlasTextures != null && fa.atlasTextures.Length > 0 ? fa.atlasTextures[0] : null;
                string afterSize = after == null ? "null" : after.width + "x" + after.height;
                Say("C   GLYPH-PROBE '" + fa.name + "' addCharacters()=" + ok + " missing=" +
                    (missing == null ? "?" : missing.Length.ToString()) + " atlasBefore=" + beforeSize +
                    " atlasAfter=" + afterSize + " atlasCount=" + (fa.atlasTextures == null ? -1 : fa.atlasTextures.Length) +
                    " material=" + DescribeMaterial(fa.material));
            }
            catch (Exception e)
            {
                Say("C   GLYPH-PROBE '" + (fa == null ? "?" : fa.name) + "' THREW " + e.GetType().Name + ": " + OneLine(e.Message));
            }
        }

        private static string DescribeSheetImportState(StyleSheet sheet)
        {
            try
            {
                Type t = typeof(StyleSheet);
                object errors = t.GetField("m_ImportedWithErrors", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(sheet);
                object warnings = t.GetField("m_ImportedWithWarnings", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(sheet);
                return "importedWithErrors=" + (errors ?? "?") + " importedWithWarnings=" + (warnings ?? "?");
            }
            catch
            {
                return "importState=<unreadable>";
            }
        }

        private static FieldInfo _sheetAssetsField;

        private static IList SheetAssets(StyleSheet sheet)
        {
            try
            {
                if (_sheetAssetsField == null)
                    _sheetAssetsField = typeof(StyleSheet).GetField("assets", BindingFlags.Instance | BindingFlags.NonPublic);
                object v = _sheetAssetsField?.GetValue(sheet);
                return v as IList;
            }
            catch
            {
                return null;
            }
        }

        private static string DescribeMaterial(Material m)
        {
            if (m == null) return "material=NULL";
            try
            {
                Shader sh = m.shader;
                Texture tex = m.mainTexture;
                return "mat='" + m.name + "' shader=" + (sh == null ? "NULL" : "'" + sh.name + "'") +
                       " mainTex=" + (tex == null ? "NULL" : "'" + tex.name + "' (" + tex.width + "x" + tex.height + ")");
            }
            catch (Exception e)
            {
                return "mat='" + m.name + "' describe THREW " + e.GetType().Name;
            }
        }

        private static string DescribeFontAsset(FontAsset fa)
        {
            if (fa == null) return "<null>";
            StringBuilder sb = new StringBuilder();
            try { sb.Append("family='" + (fa.faceInfo.familyName ?? "?") + "' "); } catch { }
            try { sb.Append("popMode=" + fa.atlasPopulationMode + " "); } catch { }
            try { sb.Append("sourceFontFile=" + (fa.sourceFontFile == null ? "null" : "'" + fa.sourceFontFile.name + "'") + " "); } catch { }
            try
            {
                Texture2D at = fa.atlasTexture;
                sb.Append("atlasTexture=" + (at == null ? "null" : "'" + at.name + "' (" + at.width + "x" + at.height + ")") + " ");
            }
            catch (Exception e) { sb.Append("atlasTexture THREW " + e.GetType().Name + " "); }
            try
            {
                Texture2D[] ats = fa.atlasTextures;
                sb.Append("atlasTextures=" + (ats == null ? "null" : ats.Length.ToString()) + " ");
            }
            catch { }
            try { sb.Append("material=" + DescribeMaterial(fa.material)); }
            catch (Exception e) { sb.Append("material THREW " + e.GetType().Name); }
            return sb.ToString();
        }

        private static string DescribeFontDefinition(FontDefinition fd)
        {
            StringBuilder sb = new StringBuilder();
            FontAsset fa = null;
            Font f = null;
            try { fa = fd.fontAsset; } catch (Exception e) { sb.Append("fontAsset THREW " + e.GetType().Name + " "); }
            try { f = fd.font; } catch (Exception e) { sb.Append("font THREW " + e.GetType().Name + " "); }
            sb.Append("fontAsset=" + (fa == null ? "null" : "'" + fa.name + "'"));
            sb.Append(" legacyFont=" + (f == null ? "null" : "'" + f.name + "'"));
            if (fa != null) sb.Append(" {" + DescribeFontAsset(fa) + "}");
            return sb.ToString();
        }

        /// <summary>
        /// Reproduces the exact null-dereference the IL of GetTextCoreSettingsForElement allows:
        /// a non-null FontAsset whose material (or material.mainTexture) is null.
        /// </summary>
        private static bool FontIsBroken(FontDefinition fd, out string detail)
        {
            detail = "";
            try
            {
                FontAsset fa = fd.fontAsset;
                if (fa != null)
                {
                    Material m = fa.material;
                    if (m == null)
                    {
                        detail = "fontAsset '" + fa.name + "' material=NULL";
                        return true;
                    }
                    if (m.mainTexture == null)
                    {
                        detail = "fontAsset '" + fa.name + "' material '" + m.name + "' mainTexture=NULL shader=" +
                                 (m.shader == null ? "NULL" : "'" + m.shader.name + "'");
                        return true;
                    }
                    Texture2D at = fa.atlasTexture;
                    if (at == null)
                    {
                        // The IL-provable NRE is material/mainTexture only; a null atlas is a
                        // separate (non-throwing) defect, reported but not counted as broken.
                        detail = "ok (material+mainTexture fine) but atlasTexture=NULL";
                        return false;
                    }
                    detail = "ok (material+mainTexture+atlas fine)";
                    return false;
                }
                if (fd.font != null)
                {
                    detail = "legacy Font '" + fd.font.name + "' -> TextSettings.GetCachedFontAsset()";
                    return false;
                }
                detail = "no fontAsset and no legacy font -> panel default font asset";
                return false;
            }
            catch (Exception e)
            {
                detail = "FontIsBroken THREW " + e.GetType().Name + ": " + OneLine(e.Message);
                return false;
            }
        }

        private static List<T> SafeLoadAll<T>(AssetBundle b) where T : UnityEngine.Object
        {
            List<T> list = new List<T>();
            if (b == null) return list;
            try
            {
                T[] arr = b.LoadAllAssets<T>();
                if (arr != null) list.AddRange(arr);
            }
            catch (Exception e)
            {
                // Reported by the caller through the (empty) list plus this log line on stdout.
                Debug.LogWarning(Tag + " LoadAllAssets<" + typeof(T).Name + "> THREW " + e.GetType().Name + ": " + e.Message);
            }
            return list;
        }

        // ------------------------------------------------------------ phase driver
        private void Update()
        {
            if (_finished)
            {
                PostRunWatchdog();
                PostRunBounds();
                return;
            }
            try
            {
                if (!_ready && Time.realtimeSinceStartup - _t0 > 75f)
                {
                    _ready = true;
                    Say("readiness: TIMEOUT fallback after 75 s (no appbar line seen) - starting phases anyway");
                }
                if (!_ready) return;

                if (_phase < 0)
                {
                    _phase = 0;
                    BeginPhase();
                    return;
                }

                Cand c = CurrentCand();
                if (c == null)
                {
                    Finish();
                    return;
                }

                _frameInPhase++;

                if (_walkIdx < WalkFrames.Length && _frameInPhase >= WalkFrames[_walkIdx])
                {
                    _walkIdx++;
                    Walk(c, _walkIdx.ToString());
                }

                if (_frameInPhase >= PhaseFrames)
                {
                    EndPhase(c);
                    _phase++;
                    if (_phase > _cands.Count)
                    {
                        Finish();
                    }
                    else
                    {
                        BeginPhase();
                    }
                }
            }
            catch (Exception e)
            {
                Say("Update THREW " + e.GetType().FullName + ": " + OneLine(e.Message) + " | " + OneLine(e.StackTrace));
                // Never stop the run on one bad phase: advance.
                try
                {
                    Cand c = CurrentCand();
                    if (c != null) EndPhase(c);
                    _phase++;
                    if (_phase > _cands.Count) Finish(); else BeginPhase();
                }
                catch { Finish(); }
            }
        }

        private Cand CurrentCand()
        {
            if (_phase < 0) return null;
            if (_phase < _cands.Count) return _cands[_phase];
            if (_phase == _cands.Count) return _live;
            return null;
        }

        private void BeginPhase()
        {
            Cand c = CurrentCand();
            if (c == null) return;
            _frameInPhase = 0;
            _walkIdx = 0;
            _nrePhase = 0;
            _phaseExc.Clear();
            c.NreInPhase = -1;
            Say("===== B[" + c.Name + "] live phase start (t=" + Time.realtimeSinceStartup.ToString("F2") +
                ", nreTotal=" + _nreTotal + ") =====");

            if (c.LiveWindow) BeginLiveWindow(c);
            else BeginVariantWindow(c);
        }

        private void BeginVariantWindow(Cand c)
        {
            if (c.Vta == null)
            {
                Say("B[" + c.Name + "] no VisualTreeAsset from the built bundle - live phase skipped");
                return;
            }
            try
            {
                WindowOptions opts = new WindowOptions
                {
                    WindowId = "K2D2Diag_" + c.Name,
                    Parent = null,
                    IsHidingEnabled = false,
                    DisableGameInputForTextFields = false,
                    BringToFrontOnPointerDown = false,
                    BlockGameInput = false,
                    MoveOptions = new MoveOptions
                    {
                        IsMovingEnabled = false,
                        CheckScreenBounds = true
                    }
                };
                UIDocument doc = Window.Create(opts, c.Vta);
                if (doc == null)
                {
                    Say("B[" + c.Name + "] Window.Create -> null");
                    return;
                }
                c.Doc = doc;
                c.OwnedWindow = true;
                c.Created = true;
                VisualElement root = doc.rootVisualElement;
                c.Root = root;
                Say("B[" + c.Name + "] window created go='" + doc.gameObject.name + "' rootChildren=" +
                    (root == null ? -1 : root.childCount) + " panel=" +
                    (root == null || root.panel == null ? "NULL" : "ok"));
                DumpPanelSettings(c, doc);
            }
            catch (Exception e)
            {
                Say("B[" + c.Name + "] creating the window THREW " + e.GetType().FullName + ": " +
                    OneLine(e.Message) + " | " + OneLine(e.StackTrace));
            }
        }

        private void BeginLiveWindow(Cand c)
        {
            try
            {
                UIDocument doc = null;
                object windowComponent = null;
                string how = "";

                Type pluginType = FindLoadedType("K2D2.K2D2_Plugin");
                if (pluginType != null)
                {
                    object inst = pluginType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                    FieldInfo f = pluginType.GetField("main_window", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
                    if (inst != null && f != null)
                    {
                        windowComponent = f.GetValue(inst);
                        if (windowComponent != null)
                        {
                            FieldInfo df = windowComponent.GetType().GetField("_document", BindingFlags.NonPublic | BindingFlags.Instance);
                            doc = df?.GetValue(windowComponent) as UIDocument;
                            if (doc != null) how = "K2D2_Plugin.Instance.main_window._document";
                        }
                    }
                }

                if (doc == null)
                {
                    foreach (UIDocument d in Resources.FindObjectsOfTypeAll<UIDocument>())
                    {
                        try
                        {
                            if (d == null) continue;
                            VisualElement r = d.rootVisualElement;
                            if (r == null) continue;
                            if (r.Q("tab-scroll-view") != null || r.Q("MainFrame") != null)
                            {
                                doc = d;
                                how = "Resources.FindObjectsOfTypeAll<UIDocument> (Q(tab-scroll-view))";
                                break;
                            }
                        }
                        catch { }
                    }
                }

                if (doc == null)
                {
                    Say("B[" + c.Name + "] live K2-D2 window NOT FOUND (no main_window._document, no UIDocument with k2d2_window content)");
                    return;
                }

                c.Doc = doc;
                c.Root = doc.rootVisualElement;
                c.Created = true;
                Say("B[" + c.Name + "] live window found via " + how + " go='" + doc.gameObject.name +
                    "' rootChildren=" + (c.Root == null ? -1 : c.Root.childCount) +
                    " panel=" + (c.Root == null || c.Root.panel == null ? "NULL" : "ok"));

                // Second snapshot of the loaded-bundle world: by the time the game UI is up the
                // shipped K2-D2 bundle is definitely loaded (it may not have been at OnInitialized).
                ShellInventory(c);

                if (windowComponent != null)
                {
                    c.WindowComponent = windowComponent;
                    LogManipulators(c, windowComponent);
                    PropertyInfo open = windowComponent.GetType().GetProperty("IsWindowOpen");
                    if (open != null && open.CanRead && open.CanWrite)
                    {
                        object v = open.GetValue(windowComponent);
                        c.LiveWasOpen = v is bool && (bool)v;
                        Say("B[" + c.Name + "] live window IsWindowOpen before = " + c.LiveWasOpen);
                        if (!c.LiveWasOpen)
                        {
                            open.SetValue(windowComponent, true);
                            Say("B[" + c.Name + "] forced IsWindowOpen=true (reproducing the appbar click that showed the empty window)");
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Say("B[" + c.Name + "] opening the live window THREW " + e.GetType().FullName + ": " +
                    OneLine(e.Message) + " | " + OneLine(e.StackTrace));
            }
        }

        private void LogManipulators(Cand c, object windowComponent)
        {
            try
            {
                FieldInfo rf = windowComponent.GetType().GetField("_rootElement", BindingFlags.NonPublic | BindingFlags.Instance);
                VisualElement root = rf?.GetValue(windowComponent) as VisualElement;
                if (root == null)
                {
                    Say("B[" + c.Name + "] K2D2Window._rootElement is null (BindUi has not run)");
                    return;
                }
                Say("B[" + c.Name + "] K2D2Window._rootElement = '" + DescribeElement(root) + "'");
                FieldInfo mf = typeof(VisualElement).GetField("m_Manipulators", BindingFlags.NonPublic | BindingFlags.Instance);
                IList list = mf?.GetValue(root) as IList;
                if (list == null)
                {
                    Say("B[" + c.Name + "] manipulator list unreadable on that element");
                    return;
                }
                List<string> names = new List<string>();
                foreach (object o in list) if (o != null) names.Add(o.GetType().FullName);
                Say("B[" + c.Name + "] _rootElement manipulators (" + names.Count + "): " + string.Join(", ", names.ToArray()));
                if (names.Count == 0)
                    Say("B[" + c.Name + "] WARNING: no manipulator on _rootElement - dragging cannot work");
            }
            catch (Exception e)
            {
                Say("B[" + c.Name + "] manipulator inspection THREW " + e.GetType().Name + ": " + OneLine(e.Message));
            }
        }

        /// <summary>
        /// The window's own geometry and the chrome the user has to be able to grab. A panel whose
        /// render tree never completes a visual update still reports layout, so this is supporting
        /// evidence only - the paint proof is the absence of the NRE storm (and the user's eyes).
        /// </summary>
        private void LogKeyElements(Cand c, string tag)
        {
            string[] names = { "MainFrame", "tab-scroll-view", "toolbar", "resize-handle", "info-toggle", "k2_avatar" };
            string[] classes = { "oab-window-header", "oab-window-header-title", "oab-window-header-dashes", "oab-close-button" };
            try
            {
                foreach (string id in names)
                {
                    VisualElement ve = c.Root.Q(id);
                    if (ve == null)
                    {
                        Say("B[" + c.Name + "] KEY#" + tag + " name '" + id + "' -> NOT FOUND");
                        continue;
                    }
                    Say("B[" + c.Name + "] KEY#" + tag + " name '" + id + "' -> " + DescribeElement(ve) + " " + DescribeGeometry(ve));
                }
                foreach (string cls in classes)
                {
                    VisualElement ve = c.Root.Q(null, cls);
                    if (ve == null)
                    {
                        Say("B[" + c.Name + "] KEY#" + tag + " class '" + cls + "' -> NOT FOUND");
                        continue;
                    }
                    Say("B[" + c.Name + "] KEY#" + tag + " class '" + cls + "' -> " + DescribeElement(ve) + " " + DescribeGeometry(ve));
                    LogManipulatorList("B[" + c.Name + "] KEY#" + tag + " class '" + cls + "'", ve);
                }
                if (c.WindowComponent != null)
                {
                    FieldInfo rf = c.WindowComponent.GetType().GetField("_rootElement", BindingFlags.NonPublic | BindingFlags.Instance);
                    VisualElement re = rf?.GetValue(c.WindowComponent) as VisualElement;
                    if (re != null)
                    {
                        Say("B[" + c.Name + "] KEY#" + tag + " manipulator host (" + DescribeElement(re) + ") " + DescribeGeometry(re));
                        LogManipulatorList("B[" + c.Name + "] KEY#" + tag + " manipulator host", re);
                    }
                }
            }
            catch (Exception e)
            {
                Say("B[" + c.Name + "] KEY#" + tag + " THREW " + e.GetType().Name + ": " + OneLine(e.Message));
            }
        }

        private static string DescribeGeometry(VisualElement ve)
        {
            try
            {
                return "layout=(" + ve.layout.x.ToString("F0") + "," + ve.layout.y.ToString("F0") + " " +
                       ve.layout.width.ToString("F0") + "x" + ve.layout.height.ToString("F0") + ") world=(" +
                       ve.worldBound.x.ToString("F0") + "," + ve.worldBound.y.ToString("F0") + " " +
                       ve.worldBound.width.ToString("F0") + "x" + ve.worldBound.height.ToString("F0") + ")" +
                       " pickingMode=" + ve.pickingMode + " display=" + SafeDisplay(ve) +
                       " visible=" + ve.visible + " enabledSelf=" + ve.enabledSelf;
            }
            catch (Exception e)
            {
                return "geometry THREW " + e.GetType().Name;
            }
        }

        private void LogManipulatorList(string tag, VisualElement ve)
        {
            try
            {
                FieldInfo mf = typeof(VisualElement).GetField("m_Manipulators", BindingFlags.NonPublic | BindingFlags.Instance);
                IList list = mf?.GetValue(ve) as IList;
                if (list == null)
                {
                    Say(tag + " manipulators=<unreadable>");
                    return;
                }
                List<string> names = new List<string>();
                foreach (object o in list) if (o != null) names.Add(o.GetType().Name);
                Say(tag + " manipulators(" + names.Count + "): " + (names.Count == 0 ? "<none>" : string.Join(", ", names.ToArray())));
            }
            catch (Exception e)
            {
                Say(tag + " manipulator list THREW " + e.GetType().Name);
            }
        }

        private static string DescribeElement(VisualElement ve)        {
            if (ve == null) return "<null>";
            string cls = "";
            try
            {
                List<string> c = new List<string>();
                foreach (string s in ve.GetClasses()) c.Add(s);
                if (c.Count > 0) cls = " class=[" + string.Join(" ", c.ToArray()) + "]";
            }
            catch { }
            return ve.GetType().Name + " name='" + (string.IsNullOrEmpty(ve.name) ? "<unnamed>" : ve.name) + "'" + cls;
        }

        private void DumpPanelSettings(Cand c, UIDocument doc)
        {
            try
            {
                PanelSettings ps = doc.panelSettings;
                if (ps == null)
                {
                    Say("B[" + c.Name + "] panelSettings=NULL");
                    return;
                }
                PanelTextSettings ts = ps.textSettings;
                if (ts == null)
                {
                    Say("B[" + c.Name + "] panelSettings='" + ps.name + "' textSettings=NULL (no panel default font)");
                    return;
                }
                FontAsset fa = ts.defaultFontAsset;
                Say("B[" + c.Name + "] panelSettings='" + ps.name + "' textSettings='" +
                    (ts.name ?? "<unnamed>") + "' defaultFontAsset=" +
                    (fa == null ? "null" : "'" + fa.name + "' " + DescribeFontAsset(fa)));
            }
            catch (Exception e)
            {
                Say("B[" + c.Name + "] panelSettings dump THREW " + e.GetType().Name + ": " + OneLine(e.Message));
            }
        }

        private void EndPhase(Cand c)
        {
            c.NreInPhase = _nrePhase;
            Say("B[" + c.Name + "] PHASE SUMMARY created=" + c.Created + " childCount=" + c.ChildCount +
                " textElementsSeen=" + c.TextSeen + " fontBrokenSeen=" + c.BrokenSeen +
                " nreDuringPhase=" + _nrePhase + " otherExceptionsDuringPhase=" + _phaseExc.Count);

            try
            {
                if (c.OwnedWindow && c.Doc != null)
                {
                    if (c.Root != null)
                    {
                        try { c.Root.style.display = DisplayStyle.None; } catch { }
                    }
                    UnityEngine.Object.Destroy(c.Doc.gameObject);
                    Say("B[" + c.Name + "] diag window hidden + destroyed (NRE storm for this candidate stops here)");
                }
                else if (c.LiveWindow && c.Doc != null && !c.LiveWasOpen && c.WindowComponent != null)
                {
                    PropertyInfo open = c.WindowComponent.GetType().GetProperty("IsWindowOpen");
                    if (open != null && open.CanWrite)
                    {
                        open.SetValue(c.WindowComponent, false);
                        Say("B[" + c.Name + "] live window IsWindowOpen restored to false");
                    }
                }
            }
            catch (Exception e)
            {
                Say("B[" + c.Name + "] cleanup THREW " + e.GetType().Name + ": " + OneLine(e.Message));
            }
        }

        // =====================================================================
        // SECTION: drag-bounds instrumentation (launch #6)
        //
        // Measures, on the LIVE K2-D2 window while it is open:
        //   * the static ReferenceResolution constants vs Screen.width/height vs the real panel rect;
        //   * the AppShell's own rect and its three size sources (resolvedStyle / layout /
        //     worldBound) plus its parent chain up to the panel root - the numbers a correct drag
        //     clamp has to be derived from;
        //   * the OLD hard-coded walls (0 .. 1920-w in translate space) vs the REAL walls
        //     (panel.xMin .. panel.xMax-w in panel space), and the baseline the old translate-only
        //     drag could never go left of - i.e. the numeric version of the user's two screenshots;
        //   * the manipulator's own clampWindow() at both extremes, asserted against the measured
        //     panel rect: this is the numeric proof that the rebuilt DLL clamps to the real walls;
        //   * a per-frame trace of style.left/top + translate + worldBound while the user drags
        //     (grep TRACE / bounds-sample);
        //   * a controlled stability probe: move the window 60px with the drag idle, watch 30 frames
        //     for any re-assert of a saved position (grep STAB). This is the "tug of war" hypothesis
        //     (Cause B) tested directly, independent of any reading of the IL.
        // =====================================================================
        private bool _boundsDumped;
        private int _openFrames;
        private float _openForcedAt = -1f;
        private bool _noWindowLogged;
        private bool _baselineLogged;
        private VisualElement _boundsRoot;
        private bool _geomTraceHooked;
        private int _geomTraceLines;
        private int _traceLines;
        private long _boundsSamples;
        private float _lastSample;
        private float _baseX = float.NaN, _baseY = float.NaN;
        private float _minWorldX = float.NaN, _maxWorldX = float.NaN, _minWorldY = float.NaN, _maxWorldY = float.NaN;
        private float _lastLeft = float.NaN, _lastTop = float.NaN, _lastTx = float.NaN, _lastTy = float.NaN;
        private float _lastWorldX = float.NaN, _lastWorldY = float.NaN;
        private int _dragsSeen;
        private int _stabState;
        private int _stabFrames;
        private int _stabChanges;
        private float _stabSetLeft, _stabSetTop, _stabOrigLeft, _stabOrigTop, _stabStartAt;

        private void PostRunBounds()
        {
            try
            {
                if (_live == null || _live.WindowComponent == null)
                {
                    if (!_noWindowLogged && Time.realtimeSinceStartup - _t0 > 30f)
                    {
                        _noWindowLogged = true;
                        Say("BOUNDS: live K2D2Window component was never found - nothing to measure (no window was ever bound)");
                    }
                    return;
                }

                VisualElement root = WindowRootElement();
                if (root == null) return;
                _boundsRoot = root;

                bool open = LiveWindowOpenState();
                if (!open)
                {
                    // If the user has not opened it after 60 s, open it ourselves once so the
                    // measurement is not hostage to a launch step. Opening the live window this
                    // way was already proven harmless in launch #5 (the live phase did the same
                    // and measured 0 NREs), and it is left open for the user's own drag test.
                    if (_openForcedAt < 0f && Time.realtimeSinceStartup - _t0 > 60f)
                    {
                        _openForcedAt = Time.realtimeSinceStartup;
                        SetWindowOpen(true);
                        Say("BOUNDS: window still closed 60 s after mod init - forcing IsWindowOpen=true so the automated measurement runs; it is left open for your drag test");
                        return;
                    }
                    return;
                }
                _openFrames++;

                if (!_baselineLogged && root.worldBound.width > 0)
                {
                    _baselineLogged = true;
                    DumpBoundsBaseline(root);
                }

                PerFrameBoundsTrace(root);

                if (!_boundsDumped && _openFrames >= 30)
                {
                    _boundsDumped = true;
                    DumpBounds("AUTO");
                    HookGeometryTrace(root);
                }

                RunStabilityProbe(root);
            }
            catch (Exception e)
            {
                if (_boundsErrors++ < 5)
                    Say("BOUNDS probe THREW " + e.GetType().FullName + ": " + OneLine(e.Message) + " | " + OneLine(e.StackTrace));
            }
        }

        private int _boundsErrors;

        private VisualElement WindowRootElement()
        {
            try
            {
                FieldInfo rf = _live.WindowComponent.GetType().GetField("_rootElement", BindingFlags.NonPublic | BindingFlags.Instance);
                return rf?.GetValue(_live.WindowComponent) as VisualElement;
            }
            catch { return null; }
        }

        private object GetDragManipulator()
        {
            try
            {
                FieldInfo mf = _live.WindowComponent.GetType().GetField("_dragManipulator", BindingFlags.NonPublic | BindingFlags.Instance);
                return mf?.GetValue(_live.WindowComponent);
            }
            catch { return null; }
        }

        private bool IsManipulatorDragging()
        {
            try
            {
                object m = GetDragManipulator();
                if (m == null) return false;
                object v = Member(m, "IsDragging");
                return v is bool && (bool)v;
            }
            catch { return false; }
        }

        private void SetWindowOpen(bool open)
        {
            try
            {
                PropertyInfo p = _live.WindowComponent.GetType().GetProperty("IsWindowOpen");
                if (p != null) p.SetValue(_live.WindowComponent, open, null);
            }
            catch (Exception e)
            {
                Say("BOUNDS: SetWindowOpen THREW " + e.GetType().Name + ": " + OneLine(e.Message));
            }
        }

        private static Rect PanelRect(VisualElement root)
        {
            try
            {
                IPanel panel = root.panel;
                if (panel != null && panel.visualTree != null)
                {
                    Rect r = panel.visualTree.contentRect;
                    if (r.width > 0 && r.height > 0) return r;
                }
            }
            catch { }
            return new Rect(0, 0, Screen.width, Screen.height);
        }

        private static float SizeDim(VisualElement ve, bool width)
        {
            try { float v = width ? ve.resolvedStyle.width : ve.resolvedStyle.height; if (v > 0) return v; } catch { }
            try { float v = width ? ve.worldBound.width : ve.worldBound.height; if (v > 0) return v; } catch { }
            try { float v = width ? ve.layout.width : ve.layout.height; if (v > 0) return v; } catch { }
            return 0f;
        }

        private static void ReadPositions(VisualElement root, out float l, out float t, out float tx, out float ty, out float wx, out float wy)
        {
            l = float.NaN; t = float.NaN; tx = float.NaN; ty = float.NaN; wx = float.NaN; wy = float.NaN;
            try { l = root.resolvedStyle.left; t = root.resolvedStyle.top; } catch { }
            try
            {
                object tr = root.resolvedStyle.translate;
                object xv = Member(Member(tr, "x"), "value");
                object yv = Member(Member(tr, "y"), "value");
                if (xv is float) tx = (float)xv;
                if (yv is float) ty = (float)yv;
            }
            catch { }
            try { wx = root.worldBound.x; wy = root.worldBound.y; } catch { }
        }

        private void DumpBoundsBaseline(VisualElement root)
        {
            _baseX = root.worldBound.x;
            _baseY = root.worldBound.y;
            Rect panel = PanelRect(root);
            float w = SizeDim(root, true);
            float h = SizeDim(root, false);
            Say("BOUNDS-BASELINE | first open frame: AppShell world=(" + F(_baseX) + "," + F(_baseY) + ") size=" + F(w) + "x" + F(h) + " panel=" + R(panel));
            Say("BOUNDS-BASELINE | the OLD translate-only drag could never move LEFT of this baseline (translate >= 0): x=" + F(_baseX) +
                " = " + F(panel.width > 0 ? _baseX / panel.width * 100f : float.NaN) + "% of the panel width. The real left wall is x=" + F(panel.xMin));
            Say("BOUNDS-BASELINE | the OLD right wall sat at baseline.x + (1920 - w) = " + F(_baseX + 1920f - w) +
                " in panel space; the real right wall is panel.xMax - w = " + F(panel.xMax - w));
        }

        private void PerFrameBoundsTrace(VisualElement root)
        {
            float l, t, tx, ty, wx, wy;
            ReadPositions(root, out l, out t, out tx, out ty, out wx, out wy);

            if (!float.IsNaN(wx))
            {
                if (float.IsNaN(_minWorldX) || wx < _minWorldX) _minWorldX = wx;
                if (float.IsNaN(_maxWorldX) || wx > _maxWorldX) _maxWorldX = wx;
                if (float.IsNaN(_minWorldY) || wy < _minWorldY) _minWorldY = wy;
                if (float.IsNaN(_maxWorldY) || wy > _maxWorldY) _maxWorldY = wy;
            }

            bool changed = Changed(l, _lastLeft) || Changed(t, _lastTop) || Changed(tx, _lastTx) ||
                           Changed(ty, _lastTy) || Changed(wx, _lastWorldX) || Changed(wy, _lastWorldY);
            if (changed)
            {
                _lastLeft = l; _lastTop = t; _lastTx = tx; _lastTy = ty; _lastWorldX = wx; _lastWorldY = wy;
                bool dragging = IsManipulatorDragging();
                if (dragging) _dragsSeen++;
                if (_traceLines < 400 && (dragging || _traceLines < 60))
                {
                    _traceLines++;
                    Say("TRACE #" + _traceLines + (dragging ? " DRAG" : " move") + " left=" + F(l) + " top=" + F(t) +
                        " translate=(" + F(tx) + "," + F(ty) + ") world=(" + F(wx) + "," + F(wy) + ") " +
                        WallNote(root, wx, wy));
                }
            }

            if (Time.realtimeSinceStartup - _lastSample >= 15f)
            {
                _lastSample = Time.realtimeSinceStartup;
                _boundsSamples++;
                Say("bounds-sample t=+" + (Time.realtimeSinceStartup - _t0).ToString("F0") + "s open=" + (LiveWindowOpenState() ? "True" : "False") +
                    " world=(" + F(wx) + "," + F(wy) + ") worldRangeX=[" + F(_minWorldX) + ".." + F(_maxWorldX) +
                    "] worldRangeY=[" + F(_minWorldY) + ".." + F(_maxWorldY) + "] " + WallNote(root, wx, wy) +
                    " drags=" + _dragsSeen);
            }
        }

        /// <summary>
        /// Live verdict on where the window is relative to the real panel walls, so the log itself
        /// says whether the window is currently inside the left/right/top/bottom limits.
        /// </summary>
        private static string WallNote(VisualElement root, float wx, float wy)
        {
            try
            {
                Rect panel = PanelRect(root);
                float w = SizeDim(root, true);
                float h = SizeDim(root, false);
                float right = panel.xMin + Mathf.Max(0, panel.width - w);
                float bottom = panel.yMin + Mathf.Max(0, panel.height - h);
                float leftGap = wx - panel.xMin;                 // >= 0 means inside the left wall
                float rightOver = wx - right;                    // > 0 means past the right wall
                float bottomOver = wy - bottom;
                return "panel=" + R(panel) + " realWallX=[" + F(panel.xMin) + ".." + F(right) + "] leftGap=" +
                       F(leftGap) + " rightOver=" + F(rightOver) + " bottomOver=" + F(bottomOver) +
                       " (leftGap<0 = outside left, rightOver>0 = outside right)";
            }
            catch { return "wall note THREW"; }
        }

        private void DumpBounds(string tag)
        {
            VisualElement root = _boundsRoot;
            if (root == null) return;

            Say("=================== BOUNDS DUMP (" + tag + ") ===================");

            // 1. static reference space vs the real screen
            Type rt = FindLoadedType("UitkForKsp2.API.ReferenceResolution");
            object rw = MemberStatic(rt, "Width");
            object rh = MemberStatic(rt, "Height");
            Say("BOUNDS | ReferenceResolution(static, compile-time)=" + FV(rw) + "x" + FV(rh) +
                "  Screen.width/height=" + Screen.width + "x" + Screen.height +
                "  Screen.currentResolution=" + Screen.currentResolution.width + "x" + Screen.currentResolution.height);
            Say("BOUNDS | Configuration.CurrentScreenWidth/Height forward to those static fields (never updated at runtime); the pre-fix clamp used them as if they were the live panel rect.");

            // 2. the containing rect the drag must clamp against
            Rect panel = PanelRect(root);
            Say("BOUNDS | panel.visualTree.contentRect=" + R(panel) + "   <-- the real wall");
            Say("BOUNDS | panel resolvedStyle " + F(root.panel == null ? float.NaN : root.panel.visualTree.resolvedStyle.width) + "x" +
                F(root.panel == null ? float.NaN : root.panel.visualTree.resolvedStyle.height) +
                " layout=" + (root.panel == null ? "<null>" : DescribeGeometry(root.panel.visualTree)));

            // 3. the AppShell itself, all three size sources
            Say("BOUNDS | AppShell(_rootElement) " + DescribeElement(root) + " " + DescribeGeometry(root));
            Say("BOUNDS | AppShell size sources: resolvedStyle=" + F(root.resolvedStyle.width) + "x" + F(root.resolvedStyle.height) +
                " layout=" + F(root.layout.width) + "x" + F(root.layout.height) +
                " worldBound=" + F(root.worldBound.width) + "x" + F(root.worldBound.height));
            Say("BOUNDS | AppShell resolvedStyle.left=" + F(root.resolvedStyle.left) + " top=" + F(root.resolvedStyle.top) +
                " style.left='" + SafeStyle(() => root.style.left.ToString()) + "' style.top='" + SafeStyle(() => root.style.top.ToString()) + "'" +
                " style.position='" + SafeStyle(() => root.style.position.ToString()) + "' style.translate='" + SafeStyle(() => root.style.translate.ToString()) + "'" +
                " resolvedStyle.translate=" + TranslateStr(root));

            // 4. the parent chain - the containing block style.left/top are relative to
            VisualElement p = root.parent;
            for (int i = 0; i < 10 && p != null; i++)
            {
                Say("BOUNDS | parent[" + i + "] " + DescribeElement(p) + " " + DescribeGeometry(p) +
                    " style.left='" + SafeStyle(() => p.style.left.ToString()) + "' style.top='" + SafeStyle(() => p.style.top.ToString()) + "'" +
                    " style.position='" + SafeStyle(() => p.style.position.ToString()) + "'" +
                    " resolvedStyle.left=" + F(p.resolvedStyle.left) + " top=" + F(p.resolvedStyle.top) + " translate=" + TranslateStr(p));
                p = p.parent;
            }

            // 5. old walls vs real walls + the baseline the old code could not cross
            float w = SizeDim(root, true);
            float h = SizeDim(root, false);
            float refW = ToF(rw, 1920f);
            float refH = ToF(rh, 1080f);
            Say("BOUNDS | OLD clamp domain (translate space, pre-fix): x=[0.." + F(refW - w) + "] y=[0.." + F(refH - h) + "]");
            Say("BOUNDS | OLD observable left wall = baseline.x (" + F(_baseX) + ") because translate cannot go negative; OLD observable right wall = baseline.x + " +
                F(refW - w) + " = " + F(_baseX + refW - w));
            Say("BOUNDS | REAL walls (panel space): x=[" + F(panel.xMin) + ".." + F(panel.xMax - w) + "] y=[" + F(panel.yMin) + ".." + F(panel.yMax - h) + "]");
            Say("BOUNDS | DELTA old-vs-real: unreachable-left=" + F(_baseX - panel.xMin) +
                "px  overshoot-right=" + F((_baseX + refW - w) - (panel.xMax - w)) + "px");

            // 6. what the BUILT manipulator actually clamps to now
            object manip = GetDragManipulator();
            if (manip == null)
            {
                Say("BOUNDS | K2D2Window._dragManipulator field NOT FOUND (pre-fix DLL?) - numeric clampWindow assertion skipped");
            }
            else
            {
                Say("BOUNDS | manipulator=" + manip.GetType().FullName);
                MethodInfo gsb = manip.GetType().GetMethod("GetScreenBounds");
                MethodInfo gts = manip.GetType().GetMethod("GetTargetSize");
                MethodInfo clamp = manip.GetType().GetMethod("clampWindow");
                if (gsb != null) Say("BOUNDS | manipulator.GetScreenBounds()=" + R((Rect)gsb.Invoke(manip, null)));
                if (gts != null) Say("BOUNDS | manipulator.GetTargetSize()=" + V((Vector2)gts.Invoke(manip, null)));
                if (clamp != null)
                {
                    Vector3 lo = (Vector3)clamp.Invoke(manip, new object[] { new Vector3(-100000f, -100000f, 0f) });
                    Vector3 hi = (Vector3)clamp.Invoke(manip, new object[] { new Vector3(100000f, 100000f, 0f) });
                    Say("BOUNDS | manipulator.clampWindow(-inf,-inf)=" + V(lo) + "  clampWindow(+inf,+inf)=" + V(hi));
                    float expLoX = panel.xMin, expLoY = panel.yMin;
                    float expHiX = panel.xMin + Mathf.Max(0, panel.width - w);
                    float expHiY = panel.yMin + Mathf.Max(0, panel.height - h);
                    bool ok = Mathf.Abs(lo.x - expLoX) < 1f && Mathf.Abs(lo.y - expLoY) < 1f &&
                              Mathf.Abs(hi.x - expHiX) < 1f && Mathf.Abs(hi.y - expHiY) < 1f;
                    Say("BOUNDS-ASSERT | " + (ok ? "PASS" : "FAIL") + " clampWindow extremes vs the measured panel rect: expected lo=(" +
                        F(expLoX) + "," + F(expLoY) + ") hi=(" + F(expHiX) + "," + F(expHiY) + ")");
                }
                else
                {
                    Say("BOUNDS | manipulator has no clampWindow method - assertion skipped");
                }

                // 7. in-memory saved setting + the on-disk file
                try
                {
                    FieldInfo psf = manip.GetType().GetField("position_setting", BindingFlags.NonPublic | BindingFlags.Instance);
                    object ps = psf == null ? null : psf.GetValue(manip);
                    object v = Member(ps, "V");
                    Say("BOUNDS | manipulator.position_setting.V=" + (v is Vector3 ? V((Vector3)v) : "<unreadable>") +
                        "  (sentinel invalid_vector=(-1000,-1000,-1000) means 'never dragged')");
                    FieldInfo dpf = manip.GetType().GetField("_defaultPositionPending", BindingFlags.NonPublic | BindingFlags.Instance);
                    if (dpf != null)
                    {
                        object pending = dpf.GetValue(manip);
                        Say("BOUNDS | manipulator._defaultPositionPending=" + (pending is bool ? pending.ToString() : "<unreadable>") +
                            "  (True = the one-shot saved-position restore has not been applied/dropped yet; False = applied once or superseded by a drag)");
                    }
                    else
                    {
                        Say("BOUNDS | manipulator has no _defaultPositionPending field (pre-fix DLL?) - restore-registration state not readable");
                    }
                }
                catch (Exception e) { Say("BOUNDS | position_setting read THREW " + e.GetType().Name); }
            }
            Say("BOUNDS | settings file: " + SavedPositionSettingLine());
            Say("BOUNDS | SetDefaultPosition in the installed UitkForKsp2 is ONE-SHOT: Extensions.GeometryChangedHandler ends with UnregisterCallback(geometryChanged) (IL), so it applies the saved position once, on the first non-zero GeometryChangedEvent. The STAB probe below tests this live rather than trusting the IL.");
            Say("=================== END BOUNDS DUMP ===================");
        }

        private void HookGeometryTrace(VisualElement root)
        {
            if (_geomTraceHooked) return;
            _geomTraceHooked = true;
            try
            {
                root.RegisterCallback<GeometryChangedEvent>(OnBoundsGeometryChanged);
                Say("BOUNDS | geometry trace hooked on the AppShell - every layout change logs 'GEOMCHG' with newRect + style.left/top + translate (capped at 120)");
            }
            catch (Exception e)
            {
                Say("BOUNDS | geometry trace hook THREW " + e.GetType().Name);
            }
        }

        private void OnBoundsGeometryChanged(GeometryChangedEvent evt)
        {
            try
            {
                VisualElement ve = evt.target as VisualElement;
                if (ve == null || _geomTraceLines >= 120) return;
                _geomTraceLines++;
                Say("GEOMCHG #" + _geomTraceLines + " newRect=(" + F(evt.newRect.x) + "," + F(evt.newRect.y) + " " +
                    F(evt.newRect.width) + "x" + F(evt.newRect.height) + ")" +
                    " style.left='" + SafeStyle(() => ve.style.left.ToString()) + "' style.top='" + SafeStyle(() => ve.style.top.ToString()) + "'" +
                    " translate=" + TranslateStr(ve) + " world=(" + F(ve.worldBound.x) + "," + F(ve.worldBound.y) + ")" +
                    " dragging=" + (IsManipulatorDragging() ? "True" : "False"));
            }
            catch { }
        }

        /// <summary>
        /// Cause-B test, live and controlled: nudge the window with the drag idle, then watch 30
        /// frames for anything rewriting style.left/top back to a saved/default value. Zero changes
        /// refutes a live re-assert; any change confirms a second writer existed at that moment.
        /// </summary>
        private void RunStabilityProbe(VisualElement root)
        {
            if (_stabState == 0)
            {
                if (!_boundsDumped) return;
                if (_stabStartAt <= 0f) { _stabStartAt = Time.realtimeSinceStartup + 3f; return; }
                if (Time.realtimeSinceStartup < _stabStartAt) return;
                if (IsManipulatorDragging()) return;   // never fight a live gesture

                _stabOrigLeft = root.resolvedStyle.left;
                _stabOrigTop = root.resolvedStyle.top;
                _stabSetLeft = _stabOrigLeft + 60f;
                _stabSetTop = _stabOrigTop + 20f;
                root.style.left = _stabSetLeft;
                root.style.top = _stabSetTop;
                Say("STAB | probe start: set style.left/top (" + F(_stabOrigLeft) + "," + F(_stabOrigTop) + ") -> (" +
                    F(_stabSetLeft) + "," + F(_stabSetTop) + "), watching 30 frames for a re-assert of a saved/default position");
                _stabState = 1;
                _stabFrames = 0;
                _stabChanges = 0;
                return;
            }

            if (_stabState == 1)
            {
                // If the user grabs the window mid-probe, abandon it: the drag owns the position
                // now, and restoring our probe values afterwards would undo the user's gesture.
                if (IsManipulatorDragging())
                {
                    Say("STAB | probe aborted after " + _stabFrames + " frames: a user drag started - leaving the position to the drag");
                    _stabState = 3;
                    return;
                }

                _stabFrames++;
                float l = root.resolvedStyle.left;
                float t = root.resolvedStyle.top;
                if (Mathf.Abs(l - _stabSetLeft) > 0.5f || Mathf.Abs(t - _stabSetTop) > 0.5f)
                {
                    _stabChanges++;
                    if (_stabChanges <= 6)
                        Say("STAB | frame " + _stabFrames + " CHANGED to left=" + F(l) + " top=" + F(t) +
                            " (we set " + F(_stabSetLeft) + "," + F(_stabSetTop) + ") style.left='" +
                            SafeStyle(() => root.style.left.ToString()) + "' translate=" + TranslateStr(root));
                }
                if (_stabFrames >= 30)
                {
                    Say("STAB | RESULT frames=30 changes=" + _stabChanges + " -> " + (_stabChanges == 0
                        ? "NO live re-assert: nothing rewrote the position with the drag idle (tug-of-war refuted on this build)"
                        : "RE-ASSERT SEEN: a second writer rewrote the position " + _stabChanges + " time(s) with the drag idle"));
                    _stabState = 2;
                }
                return;
            }

            if (_stabState == 2)
            {
                if (IsManipulatorDragging())
                {
                    Say("STAB | restore skipped: a user drag started");
                }
                else
                {
                    try
                    {
                        root.style.left = _stabOrigLeft;
                        root.style.top = _stabOrigTop;
                        Say("STAB | restored original left/top (" + F(_stabOrigLeft) + "," + F(_stabOrigTop) + ")");
                    }
                    catch { }
                }
                _stabState = 3;
            }
        }

        private string SavedPositionSettingLine()
        {
            try
            {
                string own = SWMetadata.Folder.FullName;
                string path = Path.GetFullPath(Path.Combine(own, "..", "K2D2", "k2d2_settings.json"));
                if (!File.Exists(path)) return "<no file at " + path + ">";
                string[] lines = File.ReadAllLines(path);
                for (int i = 0; i < lines.Length; i++)
                    if (lines[i].IndexOf("main_window_pos", StringComparison.OrdinalIgnoreCase) >= 0)
                        return lines[i].Trim() + "   (" + path + ")";
                return "<file present, no main_window_pos entry> (" + path + ")";
            }
            catch (Exception e) { return "<read THREW " + e.GetType().Name + ">"; }
        }

        private static string TranslateStr(VisualElement ve)
        {
            try
            {
                object tr = ve.resolvedStyle.translate;
                object xv = Member(Member(tr, "x"), "value");
                object yv = Member(Member(tr, "y"), "value");
                return "(" + FV(xv) + "," + FV(yv) + ") raw='" + (tr == null ? "?" : tr.ToString()) + "'";
            }
            catch (Exception e) { return "<" + e.GetType().Name + ">"; }
        }

        private static object Member(object o, string name)
        {
            if (o == null) return null;
            try
            {
                Type t = o.GetType();
                PropertyInfo p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (p != null && p.CanRead) return p.GetValue(o, null);
                FieldInfo f = t.GetField(name, BindingFlags.Public | BindingFlags.Instance);
                if (f != null) return f.GetValue(o);
            }
            catch { }
            return null;
        }

        private static object MemberStatic(Type t, string name)
        {
            if (t == null) return null;
            try
            {
                FieldInfo f = t.GetField(name, BindingFlags.Public | BindingFlags.Static);
                if (f != null) return f.GetValue(null);
                PropertyInfo p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Static);
                if (p != null && p.CanRead) return p.GetValue(null, null);
            }
            catch { }
            return null;
        }

        private static bool Changed(float a, float b)
        {
            if (float.IsNaN(a)) return false;
            if (float.IsNaN(b)) return true;
            return Mathf.Abs(a - b) > 0.05f;
        }

        private static float ToF(object v, float dflt)
        {
            if (v is float) return (float)v;
            if (v is int) return (float)(int)v;
            if (v is double) return (float)(double)v;
            return dflt;
        }

        private static string F(float v)
        {
            return (float.IsNaN(v) || float.IsInfinity(v)) ? "<n/a>" : v.ToString("F1");
        }

        private static string FV(object v)
        {
            return v is float ? F((float)v) : (v is int ? ((int)v).ToString() : "<n/a>");
        }

        private static string R(Rect r)
        {
            return "(" + F(r.x) + "," + F(r.y) + " " + F(r.width) + "x" + F(r.height) + ")";
        }

        private static string V(Vector2 v) { return "(" + F(v.x) + "," + F(v.y) + ")"; }

        private static string V(Vector3 v) { return "(" + F(v.x) + "," + F(v.y) + "," + F(v.z) + ")"; }

        private static string SafeStyle(Func<string> f)
        {
            try { return f(); } catch { return "<err>"; }
        }

        private void Finish()
        {
            if (_finished) return;
            _finished = true;
            Say("=========== MATRIX ===========");
            foreach (Cand c in _cands)
            {
                Say("MATRIX | " + c.Name + " | " + (c.LoadOk ? "LOADED" : (c.LoadDupe ? "DUPLICATE" : "FAILED")) +
                    " | size=" + c.Size + " | md5=" + c.Md5 + " | container=" + c.ContainerCount +
                    " | childCount=" + c.ChildCount + " | fontBrokenSeen=" + c.BrokenSeen +
                    " | nreDuringPhase=" + c.NreInPhase);
            }
            Say("MATRIX | " + _live.Name + " | live window | childCount=n/a | fontBrokenSeen=" + _live.BrokenSeen +
                " | nreDuringPhase=" + _live.NreInPhase);
            Say("distinct exceptions seen during phases (" + _allExc.Count + "):");
            foreach (string s in _allExc) Say("  EXC: " + s);
            Say("total GetTextCoreSettingsForElement NREs counted by the log hook: " + _nreTotal);
            Say("=========== END ===========");
            Say("post-run watchdog: the log hook stays installed so the user's own open/drag test is " +
                "measured too; a line is emitted every 15 s (grep 'post-run watchdog')");
            _postLogging = true;
            _lastPostLog = Time.realtimeSinceStartup;
        }

        /// <summary>
        /// Keeps measuring after the phases are done, so the user's manual open/drag test of the
        /// window is covered by the same NRE counter as the automated phases.
        /// </summary>
        private void PostRunWatchdog()
        {
            try
            {
                if (Time.realtimeSinceStartup - _lastPostLog < 15f) return;
                _lastPostLog = Time.realtimeSinceStartup;
                Say("post-run watchdog t=+" + (Time.realtimeSinceStartup - _t0).ToString("F0") + "s totalNRE=" +
                    _nreTotal + " distinctOtherExceptions=" + _allExc.Count + " liveWindowOpen=" +
                    (LiveWindowOpenState() ? "True" : "False"));
            }
            catch
            {
                // never let the watchdog throw
            }
        }

        private bool LiveWindowOpenState()
        {
            try
            {
                if (_live == null || _live.WindowComponent == null) return false;
                PropertyInfo open = _live.WindowComponent.GetType().GetProperty("IsWindowOpen");
                object v = open?.GetValue(_live.WindowComponent);
                return v is bool && (bool)v;
            }
            catch
            {
                return false;
            }
        }

        private static Type FindLoadedType(string fullName)
        {
            try
            {
                foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        Type t = a.GetType(fullName, false);
                        if (t != null) return t;
                    }
                    catch { }
                }
            }
            catch { }
            return null;
        }

        // ------------------------------------------------------------------ walk
        private void Walk(Cand c, string tag)
        {
            VisualElement root = c.Root;
            if (root == null)
            {
                Say("B[" + c.Name + "] WALK#" + tag + " root=NULL (nothing to walk)");
                return;
            }

            Say("B[" + c.Name + "] WALK#" + tag + " root=" + DescribeElement(root) + " panel=" +
                (root.panel == null ? "NULL" : "ok") + " layout=(" + root.layout.width.ToString("F0") + "x" +
                root.layout.height.ToString("F0") + ") children=" + root.childCount +
                " resolvedDisplay=" + SafeDisplay(root) + " nreSoFar=" + _nrePhase);

            if (c.LiveWindow) LogKeyElements(c, tag);

            int total = 0, text = 0, broken = 0, nullFont = 0, unresolved = 0;
            int brokenLogged = 0;
            Stack<VisualElement> stack = new Stack<VisualElement>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                VisualElement ve = stack.Pop();
                total++;
                try
                {
                    TextElement te = ve as TextElement;
                    if (te != null)
                    {
                        text++;
                        c.TextSeen++;
                        string detail;
                        bool bad = false;
                        try { bad = FontIsBroken(SafeFontDefinition(te), out detail); }
                        catch (Exception e) { detail = "resolvedStyle THREW " + e.GetType().Name; }
                        if (detail.StartsWith("no fontAsset and no legacy font", StringComparison.Ordinal)) { nullFont++; unresolved++; }
                        else if (detail.StartsWith("no fontAsset", StringComparison.Ordinal)) nullFont++;

                        string preview = te.text ?? "";
                        if (preview.Length > 32) preview = preview.Substring(0, 32) + "..";

                        if (bad)
                        {
                            broken++;
                            c.BrokenSeen++;
                            brokenLogged++;
                            if (brokenLogged <= 12)
                            {
                                Say("B[" + c.Name + "] WALK#" + tag + " *** FONT-BROKEN " + DescribeElement(te) +
                                    " text='" + OneLine(preview) + "' resolved: " + detail);
                                Say("B[" + c.Name + "] WALK#" + tag + "     resolved: " + DescribeFontDefinition(SafeFontDefinition(te)) +
                                    " | computed: " + DescribeFontDefinition(SafeComputedFontDefinition(te)));
                            }
                        }
                        else
                        {
                            Say("B[" + c.Name + "] WALK#" + tag + " text " + DescribeElement(te) +
                                " text='" + OneLine(preview) + "' -> " + detail);
                        }
                    }
                }
                catch (Exception e)
                {
                    Say("B[" + c.Name + "] WALK#" + tag + " element THREW " + e.GetType().Name + ": " + OneLine(e.Message));
                }

                try
                {
                    VisualElement.Hierarchy h = ve.hierarchy;
                    for (int i = h.childCount - 1; i >= 0; i--)
                        stack.Push(h[i]);
                }
                catch { }
            }

            Say("B[" + c.Name + "] WALK#" + tag + " SUMMARY elements=" + total + " textElements=" + text +
                " fontBroken=" + broken + " fontUnresolved(fallbackToPanelDefault)=" + unresolved +
                " (nullFontAsset=" + nullFont + ") nreDuringPhase=" + _nrePhase);
        }

        private static FontDefinition SafeFontDefinition(TextElement te)
        {
            // VisualElement.computedStyle is `assembly` (internal to the UI Toolkit module and
            // marked VisibleToOtherModules), so a mod cannot call it. resolvedStyle is the public
            // equivalent and already includes the inherited value of -unity-font-definition,
            // which is the value the text code acts on.
            try { return te.resolvedStyle.unityFontDefinition; }
            catch { return default(FontDefinition); }
        }

        private static MethodInfo _getComputedStyle;

        private static FontDefinition SafeComputedFontDefinition(VisualElement ve)
        {
            // Second opinion, via reflection, so the log can show both the computed and the
            // resolved font definition. Never throws.
            try
            {
                if (_getComputedStyle == null)
                    _getComputedStyle = typeof(VisualElement).GetMethod("get_computedStyle", BindingFlags.Instance | BindingFlags.NonPublic);
                object boxed = _getComputedStyle?.Invoke(ve, null);
                if (boxed == null) return default(FontDefinition);
                PropertyInfo p = boxed.GetType().GetProperty("unityFontDefinition", BindingFlags.Instance | BindingFlags.Public);
                object fd = p?.GetValue(boxed);
                if (fd is FontDefinition) return (FontDefinition)fd;
                return default(FontDefinition);
            }
            catch
            {
                return default(FontDefinition);
            }
        }

        private static string SafeDisplay(VisualElement ve)
        {
            try { return ve.resolvedStyle.display.ToString(); }
            catch { return "?"; }
        }

        // ------------------------------------------------------------------ misc
        private static string FileMd5(string path)
        {
            try
            {
                using (MD5 md5 = MD5.Create())
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    byte[] hash = md5.ComputeHash(stream);
                    StringBuilder sb = new StringBuilder(hash.Length * 2);
                    foreach (byte b in hash) sb.Append(b.ToString("x2"));
                    return sb.ToString();
                }
            }
            catch
            {
                return null;
            }
        }

        private static string LogPath()
        {
            try
            {
                return System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Application.dataPath), "Ksp2.log");
            }
            catch
            {
                return null;
            }
        }

        private static int CountLogLines()
        {
            string logPath = LogPath();
            if (logPath == null || !File.Exists(logPath)) return -1;
            try
            {
                int count = 0;
                using (FileStream stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (StreamReader reader = new StreamReader(stream))
                {
                    while (reader.ReadLine() != null) count++;
                }
                return count;
            }
            catch
            {
                return -1;
            }
        }

        private static string FindErrorSince(int lineCount, string fileName)
        {
            string logPath = LogPath();
            if (logPath == null || !File.Exists(logPath)) return null;
            try
            {
                List<string> all = new List<string>();
                using (FileStream stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (StreamReader reader = new StreamReader(stream))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null) all.Add(line);
                }
                int start = lineCount >= 0 && lineCount <= all.Count ? lineCount : Math.Max(0, all.Count - 4000);
                string loadFailure = null;
                for (int i = all.Count - 1; i >= start; i--)
                {
                    if (all[i].IndexOf("[ERR]", StringComparison.Ordinal) < 0) continue;
                    if (all[i].IndexOf(fileName, StringComparison.OrdinalIgnoreCase) >= 0)
                        return all[i].Trim();
                    if (loadFailure == null &&
                        (all[i].IndexOf("could not be loaded", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         all[i].IndexOf("can't be loaded", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         all[i].IndexOf("Failed to load", StringComparison.OrdinalIgnoreCase) >= 0))
                        loadFailure = all[i].Trim();
                }
                return loadFailure;
            }
            catch
            {
                return null;
            }
        }
    }
}
