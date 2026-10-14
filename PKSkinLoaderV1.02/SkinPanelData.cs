using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PrehistoricKingdom;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace PKSkinLoader
{
    /// <summary>One custom skin as the skin panel shows it.</summary>
    internal class PanelEntry
    {
        public BaseAnimalData Species;
        public SkinDef Def;          // from the scan (Source is shared with the injected skin)
        public SkinDef Live;         // the injected one (has BaseSkin), null when switched off
        public int Slot = -1;
        public int Wearing;

        public SkinSource Src { get { return Def.Source; } }
        public string Id { get { return Species.name + "|" + Src.Location; } }

        public string Name
        {
            get
            {
                string n = Def.DisplayName ?? "";
                int cut = n.IndexOf("  <color=#9a9a9a>by ", StringComparison.Ordinal);   // credit added by WithAuthor
                if (cut >= 0) n = n.Substring(0, cut);
                return System.Text.RegularExpressions.Regex.Replace(n, "<[^>]*>", "").Trim();
            }
        }

        public string Author { get { return !string.IsNullOrEmpty(Src.Author) ? Src.Author : Src.PackAuthor; } }
    }

    /// <summary>Everything the skin panel reads from and writes to disk.</summary>
    internal static class SkinFiles
    {
        // ------------------------------------------------------------------ model

        public static List<PanelEntry> BuildEntries(List<BaseAnimalData> species)
        {
            var result = new List<PanelEntry>();
            var wearing = SkinApplier.WearingCounts();
            var live = new Dictionary<SkinSource, SkinDef>();
            foreach (var d in SkinInjector.AllDefs) if (d != null && d.Source != null && d.Species != null) live[d.Source] = d;

            foreach (var sp in species)
            {
                List<SkinDef> defs;
                try { defs = SkinScanner.DefsFor(sp, species, true); } catch { continue; }
                foreach (var d in defs)
                {
                    var e = new PanelEntry { Species = sp, Def = d };
                    SkinDef l;
                    if (live.TryGetValue(d.Source, out l) && l.Species == sp) e.Live = l;
                    int n; if (wearing.TryGetValue(d.Source, out n)) e.Wearing = n;
                    try
                    {
                        int slot;
                        if (SkinRegistry.For(sp).Skins.TryGetValue(d.Key, out slot)) e.Slot = slot;
                    }
                    catch { }
                    result.Add(e);
                }
            }
            return result;
        }

        public static string SpeciesLabel(BaseAnimalData sp)
        {
            try { string n = sp.DisplayName; if (!string.IsNullOrEmpty(n)) return n; } catch { }
            return sp.name;
        }

        /// <summary>The vanilla skins of a species (index, skin).</summary>
        public static List<KeyValuePair<int, AnimalSkinData>> VanillaSkins(BaseAnimalData sp)
        {
            var r = new List<KeyValuePair<int, AnimalSkinData>>();
            for (int i = 0; i < sp.skinsData.Count; i++)
            {
                var s = sp.skinsData[i];
                if (s == null || SkinInjector.IsOurs(s) || SkinDumper.IsCustomSlot(sp, i)) continue;
                if (TyrantCompat.IsStandIn(s) || TyrantCompat.IsAdded(s)) continue;
                r.Add(new KeyValuePair<int, AnimalSkinData>(i, s));
            }
            return r;
        }

        // ------------------------------------------------------------------ paths

        public static string FolderOf(SkinSource s)
        {
            if (s.Legacy) return SkinScanner.SkinsDir;
            return Path.Combine(SkinScanner.SkinsDir, s.Location.Replace('/', Path.DirectorySeparatorChar));
        }

        public static string JsonPathOf(SkinSource s)
        {
            return s.Legacy ? Path.Combine(SkinScanner.SkinsDir, s.LegacyName + ".json") : Path.Combine(FolderOf(s), "skin.json");
        }

        // ------------------------------------------------------------------ skin.json editing

        /// <summary>Sets (or removes, when value is null) one setting. Returns an error message, or null.</summary>
        public static string SetSetting(SkinSource s, string key, JToken value)
        {
            string path = JsonPathOf(s);
            try
            {
                JObject j;
                if (File.Exists(path))
                {
                    string text = File.ReadAllText(path);
                    try { j = text.Trim().Length == 0 ? new JObject() : JObject.Parse(text); }
                    catch (Exception ex) { return Path.GetFileName(path) + " has a typo, so the panel won't overwrite it (" + ex.Message.Split('\n')[0] + ")."; }
                }
                else j = new JObject();

                if (value == null) j.Remove(key); else j[key] = value;

                if (j.Count == 0 && s.Legacy) { if (File.Exists(path)) File.Delete(path); }
                else File.WriteAllText(path, j.ToString(Formatting.Indented));
                return null;
            }
            catch (Exception ex) { return "Could not save " + Path.GetFileName(path) + ": " + ex.Message; }
        }

        // ------------------------------------------------------------------ switching on/off

        /// <summary>The folder name starting with "_" that switches this skin off, or null.</summary>
        public static string UnderscoreFolder(SkinSource s)
        {
            if (s.Legacy) return null;
            var parts = s.Location.Split('/');
            for (int i = parts.Length - 1; i >= 0; i--)
                if (parts[i].StartsWith("_")) return string.Join("/", parts.Take(i + 1).ToArray());
            return null;
        }

        public static string SetEnabled(SkinSource s, bool on)
        {
            if (on)
            {
                string under = UnderscoreFolder(s);
                if (under != null)
                {
                    try
                    {
                        string from = Path.Combine(SkinScanner.SkinsDir, under.Replace('/', Path.DirectorySeparatorChar));
                        string name = Path.GetFileName(from);
                        string to = Path.Combine(Path.GetDirectoryName(from), name.TrimStart('_'));
                        if (Directory.Exists(to)) return "Can't switch on: a folder called '" + name.TrimStart('_') + "' already exists next to it.";
                        Directory.Move(from, to);
                    }
                    catch (Exception ex) { return "Could not rename the folder: " + ex.Message; }
                }
                return SetSetting(s, "enabled", null);
            }
            return SetSetting(s, "enabled", false);
        }

        // ------------------------------------------------------------------ generated pattern

        public static string GeneratedPatternFor(PanelEntry e)
        {
            string diff = e.Def.FileFor(TexSlots.Diffuse, true);
            if (diff == null) return null;
            string p = TextureStore.GeneratedPatternPath(diff);
            return File.Exists(p) ? p : null;
        }

        public static string UseGeneratedPattern(PanelEntry e)
        {
            string gen = GeneratedPatternFor(e);
            if (gen == null) return "No generated pattern yet - place an animal with this skin first.";
            var s = e.Src;
            string target = s.Legacy ? Path.Combine(SkinScanner.SkinsDir, s.LegacyName + "_pattern.png") : Path.Combine(FolderOf(s), "pattern.png");
            if (File.Exists(target)) return Path.GetFileName(target) + " already exists - delete or rename it first.";
            try { File.Copy(gen, target); }
            catch (Exception ex) { return "Could not copy: " + ex.Message; }
            return null;
        }

        // ------------------------------------------------------------------ explorer

        public static void OpenFolder(string path, string selectFile)
        {
            try
            {
                if (selectFile != null && File.Exists(selectFile))
                    System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + selectFile + "\"");
                else
                    System.Diagnostics.Process.Start("explorer.exe", "\"" + path + "\"");
            }
            catch
            {
                try { Application.OpenURL("file:///" + path.Replace('\\', '/')); } catch { }
            }
        }

        // ------------------------------------------------------------------ new skin

        public static string CleanFolderName(string s)
        {
            if (s == null) return "";
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c.ToString(), "");
            return s.Trim().TrimStart('_', '.').Trim();
        }

        public static string NewSkinFolder(BaseAnimalData sp, string pack, string name)
        {
            string dir = SkinScanner.SkinsDir;
            if (!string.IsNullOrEmpty(pack)) dir = Path.Combine(dir, pack);
            return Path.Combine(Path.Combine(dir, sp.name), name);
        }

        /// <summary>Copies the base skin's diffuse files from its exported templates and writes skin.json.</summary>
        public static string CreateSkin(BaseAnimalData sp, int baseIdx, string pack, string name, string author, string ownDiffuse)
        {
            string folder = NewSkinFolder(sp, pack, name);
            if (Directory.Exists(folder)) return "A skin folder with that name already exists.";
            string tpl = SkinDumper.TemplateDir(sp, baseIdx);
            if (ownDiffuse == null && !Directory.Exists(tpl)) return "The templates for this species could not be exported (see the console).";
            if (ownDiffuse != null && !File.Exists(ownDiffuse)) return "Your diffuse file can't be found any more.";
            try
            {
                Directory.CreateDirectory(folder);
                int copied = 0;
                if (ownDiffuse != null)
                {
                    // babies use it too unless an infant_diffuse.png is added later
                    File.Copy(ownDiffuse, Path.Combine(folder, "diffuse.png"));
                    copied++;
                }
                else foreach (var f in Directory.GetFiles(tpl, "*.png"))
                {
                    string n = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
                    // Just the colour maps: everything else comes from the base skin until you add it yourself.
                    if (n == "diffuse" || n == "diffuse_male" || n == "diffuse_female"
                        || n == "infant_diffuse" || n == "infant_diffuse_male" || n == "infant_diffuse_female")
                    {
                        File.Copy(f, Path.Combine(folder, Path.GetFileName(f)));
                        copied++;
                    }
                }
                if (copied == 0) return "The base skin has no diffuse texture to start from.";

                var j = new JObject();
                j["base"] = sp.skinsData[baseIdx].skinName;
                if (!string.IsNullOrEmpty(author)) j["author"] = author;
                File.WriteAllText(Path.Combine(folder, "skin.json"), j.ToString(Formatting.Indented));
                return null;
            }
            catch (Exception ex) { return "Could not create the skin: " + ex.Message; }
        }

        public static List<string> ExistingPacks()
        {
            var r = new List<string>();
            foreach (var s in SkinScanner.Sources)
                if (!string.IsNullOrEmpty(s.Pack) && !r.Contains(s.Pack)) r.Add(s.Pack);
            r.Sort(StringComparer.OrdinalIgnoreCase);
            return r;
        }

        // ------------------------------------------------------------------ base values for sliders

        public static AnimalSkinVariationAsset BaseVariation(PanelEntry e)
        {
            AnimalSkinData b = e.Live != null ? e.Live.BaseSkin : null;
            if (b == null)
            {
                var v = VanillaSkins(e.Species);
                if (v.Count > 0) b = v[0].Value;
            }
            if (b == null) return null;
            return b.maleVariationData ?? b.femaleVariationData;
        }
    }
}

namespace PKSkinLoader
{
    /// <summary>The Windows "Open file" dialog (the game pauses while it's open).</summary>
    internal static class FileDialog
    {
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private class OpenFileName
        {
            public int structSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(OpenFileName));
            public IntPtr dlgOwner = IntPtr.Zero;
            public IntPtr instance = IntPtr.Zero;
            public string filter;
            public string customFilter;
            public int maxCustFilter;
            public int filterIndex;
            public string file;
            public int maxFile;
            public string fileTitle;
            public int maxFileTitle;
            public string initialDir;
            public string title;
            public int flags;
            public short fileOffset;
            public short fileExtension;
            public string defExt;
            public IntPtr custData = IntPtr.Zero;
            public IntPtr hook = IntPtr.Zero;
            public string templateName;
            public IntPtr reservedPtr = IntPtr.Zero;
            public int reservedInt;
            public int flagsEx;
        }

        [System.Runtime.InteropServices.DllImport("comdlg32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern bool GetOpenFileName([System.Runtime.InteropServices.In, System.Runtime.InteropServices.Out] OpenFileName ofn);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetActiveWindow();

        /// <summary>Returns the chosen PNG's full path, or null if cancelled / not available.</summary>
        public static string OpenPng(string title, string initialDir)
        {
            try
            {
                var ofn = new OpenFileName();
                try { ofn.dlgOwner = GetActiveWindow(); } catch { }
                ofn.filter = "PNG images (*.png)\0*.png\0\0";
                ofn.file = new string('\0', 1024);
                ofn.maxFile = ofn.file.Length;
                ofn.fileTitle = new string('\0', 256);
                ofn.maxFileTitle = ofn.fileTitle.Length;
                ofn.initialDir = initialDir;
                ofn.title = title;
                ofn.defExt = "png";
                ofn.flags = 0x00080000 | 0x00001000 | 0x00000800 | 0x00000008;   // explorer, file + path must exist, keep game's folder
                if (!GetOpenFileName(ofn)) return null;
                string f = ofn.file;
                int z = f.IndexOf('\0');
                if (z >= 0) f = f.Substring(0, z);
                return string.IsNullOrEmpty(f) ? null : f;
            }
            catch (Exception ex)
            {
                SkinModBase.Log.Warning("[Panel] The file picker isn't available (" + ex.Message + "). Put the PNG in the skin folder yourself as diffuse.png.");
                return null;
            }
        }
    }
}

namespace PKSkinLoader
{
    /// <summary>Fur droop / iridescence changes for a vanilla skin (applied live; game files and saves untouched).</summary>
    internal class VanillaTweak
    {
        public Vector3? Droop;
        public bool? Iridescence;
        public float? IridescenceStrength;
        public float? FurLength;
        public string FurCoverage;
        public bool Empty { get { return Droop == null && Iridescence == null && IridescenceStrength == null && FurLength == null && FurCoverage == null; } }
    }

    /// <summary>UserData/PKSkinLoader/vanilla-tweaks.json: { "Gallimimus": { "Allium": { "fuzzDroopiness": [0,-10,0] } } }</summary>
    internal static class VanillaTweaks
    {
        private static readonly Dictionary<string, VanillaTweak> Map = new Dictionary<string, VanillaTweak>(StringComparer.OrdinalIgnoreCase);
        private static bool _loaded;

        public static string FilePath
        {
            get { return Path.Combine(Path.Combine(Path.Combine(Directory.GetCurrentDirectory(), "UserData"), "PKSkinLoader"), "vanilla-tweaks.json"); }
        }

        public static bool Any { get { if (!_loaded) Load(); return Map.Count > 0; } }

        private static string Key(BaseAnimalData sp, AnimalSkinData skin) { return sp.name + "|" + skin.skinName; }

        public static void Load()
        {
            _loaded = true;
            Map.Clear();
            try
            {
                if (!File.Exists(FilePath)) return;
                var root = JObject.Parse(File.ReadAllText(FilePath));
                foreach (var sp in root.Properties())
                {
                    var skins = sp.Value as JObject;
                    if (skins == null) continue;
                    foreach (var sk in skins.Properties())
                    {
                        var j = sk.Value as JObject;
                        if (j == null) continue;
                        var t = new VanillaTweak();
                        var d = j["fuzzDroopiness"] as JArray;
                        if (d != null && d.Count == 3) t.Droop = new Vector3((float)d[0], (float)d[1], (float)d[2]);
                        var ir = j["iridescence"];
                        if (ir != null && ir.Type == JTokenType.Boolean) t.Iridescence = (bool)ir;
                        var st = j["iridescenceStrength"];
                        if (st != null && (st.Type == JTokenType.Float || st.Type == JTokenType.Integer)) t.IridescenceStrength = (float)st;
                        var fl = j["furLength"];
                        if (fl != null && (fl.Type == JTokenType.Float || fl.Type == JTokenType.Integer)) t.FurLength = (float)fl;
                        var fc = j["furCoverage"];
                        if (fc != null && fc.Type == JTokenType.String) { string v = ((string)fc).ToLowerInvariant(); if (v == "baby" || v == "body" || v == "none") t.FurCoverage = v; }
                        if (!t.Empty) Map[sp.Name + "|" + sk.Name] = t;
                    }
                }
            }
            catch (Exception ex) { SkinModBase.Log.Warning("vanilla-tweaks.json could not be read (" + ex.Message + "). Vanilla skins are unchanged."); }
        }

        private static void Save()
        {
            var root = new JObject();
            foreach (var kv in Map.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (kv.Value.Empty) continue;
                int bar = kv.Key.IndexOf('|');
                string sp = kv.Key.Substring(0, bar), skin = kv.Key.Substring(bar + 1);
                var spObj = root[sp] as JObject;
                if (spObj == null) { spObj = new JObject(); root[sp] = spObj; }
                var j = new JObject();
                var t = kv.Value;
                if (t.Droop.HasValue) j["fuzzDroopiness"] = new JArray(t.Droop.Value.x, t.Droop.Value.y, t.Droop.Value.z);
                if (t.Iridescence.HasValue) j["iridescence"] = t.Iridescence.Value;
                if (t.IridescenceStrength.HasValue) j["iridescenceStrength"] = Math.Round(t.IridescenceStrength.Value, 2);
                if (t.FurLength.HasValue) j["furLength"] = Math.Round(t.FurLength.Value, 2);
                if (t.FurCoverage != null) j["furCoverage"] = t.FurCoverage;
                spObj[skin] = j;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            if (root.Count == 0) { if (File.Exists(FilePath)) File.Delete(FilePath); }
            else File.WriteAllText(FilePath, root.ToString(Formatting.Indented));
        }

        /// <summary>The tweak for an animal's skin slot, or null (custom skins and untouched vanilla skins).</summary>
        public static VanillaTweak For(BaseAnimalData sp, int idx)
        {
            if (!Any || sp == null || sp.skinsData == null || idx < 0 || idx >= sp.skinsData.Count) return null;
            var skin = sp.skinsData[idx];
            if (skin == null || SkinInjector.IsOurs(skin)) return null;
            VanillaTweak t;
            return Map.TryGetValue(Key(sp, skin), out t) ? t : null;
        }

        public static VanillaTweak Get(BaseAnimalData sp, AnimalSkinData skin)
        {
            if (!_loaded) Load();
            VanillaTweak t;
            return Map.TryGetValue(Key(sp, skin), out t) ? t : new VanillaTweak();
        }

        public static string Change(BaseAnimalData sp, AnimalSkinData skin, Action<VanillaTweak> change)
        {
            try
            {
                if (!_loaded) Load();
                var t = Get(sp, skin);
                change(t);
                if (t.Empty) Map.Remove(Key(sp, skin)); else Map[Key(sp, skin)] = t;
                Save();
                return null;
            }
            catch (Exception ex) { return "Could not save vanilla-tweaks.json: " + ex.Message; }
        }
    }
}

namespace PKSkinLoader
{
    /// <summary>One animal's own look (overrides its skin's values). Null = use the skin's.</summary>
    internal class AnimalTweak
    {
        public float? FurLength;          // multiplier on top of the skin's
        public string FurCoverage;        // baby / body / none
        public Vector3? Droop;
        public float? IridescenceStrength;
        public string Name;               // just for reading the file
        public string InheritedFrom;      // "Mother & Father" names when it came from its parents
        public bool Empty { get { return FurLength == null && FurCoverage == null && Droop == null && IridescenceStrength == null; } }
    }

    /// <summary>
    /// Per-animal looks, kept per park: a small file inside the save folder (written when the game saves, read when it loads),
    /// so unsaved changes behave like the rest of the game. Babies inherit a mix of their parents' looks.
    /// </summary>
    internal static class AnimalTweaks
    {
        private const string FileName = "PKSkinLoader-animals.json";
        private static readonly Dictionary<ulong, AnimalTweak> Map = new Dictionary<ulong, AnimalTweak>();
        private static readonly HashSet<ulong> Checked = new HashSet<ulong>();
        private static readonly System.Random Rng = new System.Random();
        private static bool _hooked;

        public static int Count { get { return Map.Count; } }

        public static void Init()
        {
            if (_hooked) return;
            _hooked = true;
            try { SaveLoadManager.OnSaveCompleted += OnSaved; }
            catch (Exception ex) { SkinModBase.Log.Warning("[Animals] Can't hook game saves (" + ex.Message + "); per-animal looks won't be saved."); }
        }

        private static string SidecarPath(string savePath)
        {
            if (string.IsNullOrEmpty(savePath)) return null;
            if (Directory.Exists(savePath)) return Path.Combine(savePath, FileName);
            // zipped / unusual saves: keep it next to the mod's own data instead
            string dir = Path.Combine(Path.Combine(Path.Combine(Directory.GetCurrentDirectory(), "UserData"), "PKSkinLoader"), "Animals");
            return Path.Combine(dir, Path.GetFileNameWithoutExtension(savePath) + ".json");
        }

        public static ulong IdOf(IAnimal a)
        {
            var an = a as Animal;
            if (an == null) return 0;
            try { return an.UniqueID; } catch { return 0; }
        }

        /// <summary>Called when a save starts loading (or a new park starts).</summary>
        public static void OnLoading(string savePath, bool newGame)
        {
            Map.Clear();
            Checked.Clear();
            if (newGame) return;
            string p = SidecarPath(savePath);
            try
            {
                if (p == null || !File.Exists(p)) return;
                var root = JObject.Parse(File.ReadAllText(p));
                var animals = root["animals"] as JObject;
                if (animals == null) return;
                foreach (var prop in animals.Properties())
                {
                    ulong id;
                    var j = prop.Value as JObject;
                    if (j == null || !ulong.TryParse(prop.Name, out id)) continue;
                    var t = new AnimalTweak();
                    var v = j["furLength"]; if (v != null && v.Type != JTokenType.Null) t.FurLength = (float)v;
                    v = j["furCoverage"]; if (v != null && v.Type == JTokenType.String) t.FurCoverage = (string)v;
                    var d = j["fuzzDroopiness"] as JArray; if (d != null && d.Count == 3) t.Droop = new Vector3((float)d[0], (float)d[1], (float)d[2]);
                    v = j["iridescenceStrength"]; if (v != null && v.Type != JTokenType.Null) t.IridescenceStrength = (float)v;
                    v = j["name"]; if (v != null) t.Name = (string)v;
                    v = j["inheritedFrom"]; if (v != null) t.InheritedFrom = (string)v;
                    if (!t.Empty) { Map[id] = t; Checked.Add(id); }
                }
                SkinModBase.Log.Msg("[Animals] " + Map.Count + " animal(s) with their own look in this park.");
            }
            catch (Exception ex) { SkinModBase.Log.Warning("[Animals] " + FileName + " could not be read (" + ex.Message + ")."); }
        }

        private static void OnSaved(string savePath)
        {
            string p = SidecarPath(savePath);
            if (p == null) return;
            try
            {
                if (Map.Count == 0) { if (File.Exists(p)) File.Delete(p); return; }
                var animals = new JObject();
                foreach (var kv in Map)
                {
                    var t = kv.Value;
                    if (t.Empty) continue;
                    var j = new JObject();
                    if (t.Name != null) j["name"] = t.Name;
                    if (t.FurLength.HasValue) j["furLength"] = Math.Round(t.FurLength.Value, 2);
                    if (t.FurCoverage != null) j["furCoverage"] = t.FurCoverage;
                    if (t.Droop.HasValue) j["fuzzDroopiness"] = new JArray(Math.Round(t.Droop.Value.x, 2), Math.Round(t.Droop.Value.y, 2), Math.Round(t.Droop.Value.z, 2));
                    if (t.IridescenceStrength.HasValue) j["iridescenceStrength"] = Math.Round(t.IridescenceStrength.Value, 2);
                    if (t.InheritedFrom != null) j["inheritedFrom"] = t.InheritedFrom;
                    animals[kv.Key.ToString()] = j;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(p));
                File.WriteAllText(p, new JObject { { "version", 1 }, { "animals", animals } }.ToString(Formatting.Indented));
            }
            catch (Exception ex) { SkinModBase.Log.Warning("[Animals] Could not save per-animal looks (" + ex.Message + ")."); }
        }

        public static AnimalTweak Get(IAnimal a)
        {
            ulong id = IdOf(a);
            AnimalTweak t;
            return id != 0 && Map.TryGetValue(id, out t) ? t : null;
        }

        /// <summary>The animal's own look; for a newborn, inherits from its parents the first time it's seen.</summary>
        public static AnimalTweak For(IAnimal a)
        {
            ulong id = IdOf(a);
            if (id == 0) return null;
            AnimalTweak t;
            if (Map.TryGetValue(id, out t)) return t;
            if (Map.Count == 0 || Checked.Contains(id)) return null;
            try { return Inherit(a as Animal, id); }
            catch (Exception ex)
            {
                Checked.Add(id);
                SkinModBase.Log.Warning("[Genes] Inheriting failed: " + ex);
                return null;
            }
        }

        public static void Change(IAnimal a, Action<AnimalTweak> change)
        {
            ulong id = IdOf(a);
            if (id == 0) return;
            AnimalTweak t;
            if (!Map.TryGetValue(id, out t)) t = new AnimalTweak();
            change(t);
            try { t.Name = a.Name; } catch { }
            if (t.Empty) Map.Remove(id); else Map[id] = t;
            Checked.Add(id);
        }

        // ---------------------------------------------------------------- genes

        public static string Status(IAnimal a)
        {
            ulong id = IdOf(a);
            float age = -1;
            try { age = ((Animal)a).AgeMonths; } catch { }
            return (Checked.Contains(id) ? "checked" : "not checked") + ", age " + age.ToString("0.00");
        }

        /// <summary>"Parents: Dawn (own look) & Dexter" for the panel, or null for animals without known parents.</summary>
        public static string ParentsInfo(IAnimal a)
        {
            var an = a as Animal;
            if (an == null) return null;
            ulong m = 0, f = 0;
            try { m = an.MotherID; f = an.FatherID; } catch { }
            if (m == 0 && f == 0) return null;
            Func<ulong, string> one = id =>
            {
                if (id == 0) return "unknown";
                AnimalTweak t;
                bool has = Map.TryGetValue(id, out t);
                string n = Name(id, t);
                return n + (has ? " (own look)" : "");
            };
            return "Parents: " + one(m) + " & " + one(f);
        }

        private static Animal FindAnimal(ulong id)
        {
            foreach (var a in SkinApplier.Known)
            {
                var an = a as Animal;
                try { if (an != null && an.UniqueID == id) return an; } catch { }
            }
            return null;
        }

        private static AnimalTweak Inherit(Animal child, ulong id)
        {
            if (child == null) { Checked.Add(id); return null; }
            float age = child.AgeMonths;
            // Only newborns/hatchlings inherit (not animals that were already grown). Not remembered as "checked":
            // while a park loads, the age can read wrong for a moment.
            if (age > 2f) return null;

            ulong mId = 0, fId = 0;
            try { mId = child.MotherID; fId = child.FatherID; } catch { }
            // A newborn's parents are filled in a moment after its body appears: try again on its next update.
            if (mId == 0 && fId == 0) return null;
            AnimalTweak m = null, f = null;
            if (mId != 0) Map.TryGetValue(mId, out m);
            if (fId != 0) Map.TryGetValue(fId, out f);
            // Parents without their own look (or not readable yet while the park loads): look again later.
            if (m == null && f == null) return null;
            Checked.Add(id);

            var c = new AnimalTweak();
            c.FurLength = MixMultiplier(m != null ? m.FurLength : null, f != null ? f.FurLength : null);
            c.FurCoverage = Pick(m != null ? m.FurCoverage : null, f != null ? f.FurCoverage : null);
            c.Droop = MixVector(m != null ? m.Droop : null, f != null ? f.Droop : null);
            c.IridescenceStrength = MixValue(m != null ? m.IridescenceStrength : null, f != null ? f.IridescenceStrength : null, 0.1f, 0f, 1f);
            SkinModBase.Log.Msg("[Genes] " + child.Name + " (" + age.ToString("0.0") + " months): mother " + (m != null ? "has" : "has no") + " own look, father "
                + (f != null ? "has" : "has no") + " own look" + (c.Empty ? " - nothing passed on this time." : "."));
            if (c.Empty) return null;

            string mn = Name(mId, m), fn = Name(fId, f);
            c.InheritedFrom = mn + " & " + fn;
            try { c.Name = child.Name; } catch { }
            Map[id] = c;
            SkinModBase.Log.Msg("[Genes] " + (c.Name ?? "A baby") + " inherited its look from " + c.InheritedFrom + ".");
            return c;
        }

        private static string Name(ulong id, AnimalTweak t)
        {
            if (t != null && !string.IsNullOrEmpty(t.Name)) return t.Name;
            var a = id != 0 ? FindAnimal(id) : null;
            try { if (a != null) return a.Name; } catch { }
            return "?";
        }

        private static float Gauss()
        {
            double u1 = 1.0 - Rng.NextDouble(), u2 = Rng.NextDouble();
            return (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2));
        }

        /// <summary>Multipliers (1 = normal): somewhere between the parents, plus a little variation.</summary>
        private static float? MixMultiplier(float? a, float? b)
        {
            if (a == null && b == null) return null;
            float x = a ?? 1f, y = b ?? 1f;
            float v = Mathf.Lerp(x, y, (float)Rng.NextDouble()) * (1f + Gauss() * 0.08f);
            v = Mathf.Clamp(v, 0f, 3f);
            return Mathf.Abs(v - 1f) < 0.03f ? (float?)null : v;
        }

        /// <summary>Values without a neutral default: if only one parent has it, ~50% chance to inherit it.</summary>
        private static float? MixValue(float? a, float? b, float noise, float min, float max)
        {
            if (a == null && b == null) return null;
            if (a == null || b == null) return Rng.NextDouble() < 0.5 ? (a ?? b) : null;
            return Mathf.Clamp(Mathf.Lerp(a.Value, b.Value, (float)Rng.NextDouble()) + Gauss() * noise, min, max);
        }

        private static Vector3? MixVector(Vector3? a, Vector3? b)
        {
            if (a == null && b == null) return null;
            if (a == null || b == null) return Rng.NextDouble() < 0.5 ? (a ?? b) : null;
            var v = Vector3.Lerp(a.Value, b.Value, (float)Rng.NextDouble());
            return v + new Vector3(0, Gauss() * 0.1f, Gauss() * 0.1f);
        }

        private static string Pick(string a, string b)
        {
            if (a == b) return a;
            return Rng.NextDouble() < 0.5 ? a : b;
        }
    }
}
