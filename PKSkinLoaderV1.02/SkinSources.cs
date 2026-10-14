using Newtonsoft.Json.Linq;
using PrehistoricKingdom;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;

namespace PKSkinLoader
{
    // =====================================================================================
    //  Texture slots
    // =====================================================================================

    /// <summary>The 10 skin texture slots of PrehistoricKingdom/AnimalShader (confirmed with the F8 report).</summary>
    internal static class TexSlots
    {
        public const int Count = 12;
        public const int Diffuse = 0, Normal = 1, Extra = 2, Pattern = 3, Fur = 4;
        public const int InfantOffset = 5;
        public const int Iridescence = 10, IridescenceGradient = 11;   // not per age

        /// <summary>File names artists use (also what the F9 template export writes).</summary>
        public static readonly string[] FileNames =
        {
            "diffuse", "normal", "extra", "pattern", "fur",
            "infant_diffuse", "infant_normal", "infant_extra", "infant_pattern", "infant_fur",
            "iridescence", "iridescence_gradient"
        };

        public static readonly string[] ShaderProps =
        {
            "_AdultDiffuse", "_AdultNormal", "_AdultExtraMap", "_AdultPatternMask", "_AdultFurMask",
            "_InfantDiffuse", "_InfantNormal", "_InfantExtraMap", "_InfantPatternMask", "_InfantFurMask",
            "_IridescenceMask", "_IridescenceTexture"
        };

        private static int[] _ids;
        public static int PropId(int slot)
        {
            if (_ids == null)
            {
                _ids = new int[Count];
                for (int i = 0; i < Count; i++) _ids[i] = Shader.PropertyToID(ShaderProps[i]);
            }
            return _ids[slot];
        }

        public static int Kind(int slot) { return slot < Iridescence ? slot % InfantOffset : slot; }
        public static bool IsInfant(int slot) { return slot >= InfantOffset && slot < Iridescence; }
        public static bool IsNormalMap(int slot) { return Kind(slot) == Normal; }

        /// <summary>Diffuse and pattern are colour (sRGB) in the game; normal, extra and fur are data (linear).</summary>
        public static bool IsLinear(int slot)
        {
            int k = Kind(slot);
            return k == Normal || k == Extra || k == Fur || k == Iridescence;
        }

        private static readonly Dictionary<string, int> KindAliases = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            { "diffuse", Diffuse }, { "albedo", Diffuse }, { "basecolor", Diffuse }, { "color", Diffuse }, { "colour", Diffuse },
            { "normal", Normal }, { "nrm", Normal },
            { "extra", Extra },
            { "pattern", Pattern }, { "mask", Pattern },
            { "fur", Fur }, { "fuzz", Fur },
        };

        private static readonly Regex FileRegex = new Regex(
            @"^(?<infant>infant[_ ]?)?(?<kind>[a-z]+)(?:[_ ](?<sex>male|female|m|f))?$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>Parses e.g. "infant_pattern_female" → slot 8, sex false. Returns false for unknown names.</summary>
        public static bool TryParseFileName(string nameWithoutExt, out int slot, out bool? isMale)
        {
            slot = -1; isMale = null;
            string n = nameWithoutExt.Trim().ToLowerInvariant().Replace(' ', '_');
            if (n == "iridescence" || n == "iridescence_mask") { slot = Iridescence; return true; }
            if (n == "iridescence_gradient" || n == "iridescence_texture") { slot = IridescenceGradient; return true; }
            var m = FileRegex.Match(nameWithoutExt.Trim());
            if (!m.Success) return false;
            int kind;
            if (!KindAliases.TryGetValue(m.Groups["kind"].Value, out kind)) return false;
            slot = kind + (m.Groups["infant"].Success ? InfantOffset : 0);
            if (m.Groups["sex"].Success)
            {
                string s = m.Groups["sex"].Value.ToLowerInvariant();
                isMale = s == "male" || s == "m";
            }
            return true;
        }
    }

    // =====================================================================================
    //  A skin as found on disk
    // =====================================================================================

    internal class SkinSource
    {
        public bool Legacy;              // old flat-file format in Mods/Skins/*.png
        public string Location;          // folder path, or legacy group name (for messages)
        public string SpeciesText;       // folder skins: the species folder name
        public string LegacyName;        // legacy: e.g. "Tyrannosaurus_pigeontest"
        public string Id;                // folder skins: skin.json "id" or folder name
        public string Name;              // folder skins: skin.json "name" or folder name
        public string Author;
        public string Pack;              // pack folder (Mods/Skins/<Pack>/<Species>/<Skin>/), part of the skin's identity
        public string PackName, PackAuthor, PackVersion;   // from pack.json (optional)
        public string Base;              // skin.json "base": vanilla skin name or number
        public bool Feathered;
        public bool Disabled;            // folder (or species folder) starts with "_"
        public string Cutout = "auto";   // diffuse transparency: auto (own if painted, else base's) | base | own
        public string PatternMode;       // null (= "diffuse" unless pattern.png) | "base" (vanilla) | "none" (no pattern, no tint) | "solid" (whole body) | "diffuse" (made from your diffuse)
        public bool? Iridescence;        // override the base skin's iridescence switch
        public float? IridescenceStrength;
        public Vector3? FuzzDroopiness, InfantFuzzDroopiness;
        public float? FurLength;         // multiplies the species' fur length (1 = normal)
        public string FurCoverage;       // null (skin's own) | "baby" (the baby's fuzz on adults) | "body" (whole body) | "none" (bald)

        /// <summary>Needs its own copy of the game's skin settings asset.</summary>
        public bool HasVariationOverrides
        {
            get { return Iridescence.HasValue || IridescenceStrength.HasValue || FuzzDroopiness.HasValue || InfantFuzzDroopiness.HasValue; }
        }
        public string ThumbnailPath;

        /// <summary>Optional portraits (info panel, animal lists): key "a|m|base" = stage (a/i) | sex (m/f/empty) | mutation.</summary>
        public readonly Dictionary<string, string> Portraits = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>[slot, 0=male 1=female] → file path</summary>
        public readonly string[,] Files = new string[TexSlots.Count, 2];

        public bool HasAnyTexture
        {
            get
            {
                foreach (var f in Files) if (f != null) return true;
                return false;
            }
        }

        public void SetFile(int slot, bool? isMale, string path, bool overwrite)
        {
            if (isMale != false && (overwrite || Files[slot, 0] == null)) Files[slot, 0] = path;
            if (isMale != true && (overwrite || Files[slot, 1] == null)) Files[slot, 1] = path;
        }
    }

    /// <summary>A skin resolved for one species: what gets a slot number.</summary>
    internal class SkinDef
    {
        public SkinSource Source;
        public BaseAnimalData Species;
        public string Key;               // stable identity within the species (registry key)
        public string DisplayName;
        public AnimalSkinData BaseSkin;  // vanilla skin it was cloned from (set when injected)

        /// <summary>
        /// The file for a slot and sex, with fallbacks:
        /// own sex → other sex → (infant slots only) the adult file for own sex → adult file for other sex.
        /// Returns null if the skin doesn't provide that slot (the base skin's texture stays).
        /// </summary>
        public string FileFor(int slot, bool isMale)
        {
            int s = isMale ? 0 : 1;
            string f = Source.Files[slot, s] ?? Source.Files[slot, 1 - s];
            if (f == null && TexSlots.IsInfant(slot))
            {
                int adult = slot - TexSlots.InfantOffset;
                f = Source.Files[adult, s] ?? Source.Files[adult, 1 - s];
            }
            return f;
        }
    }

    // =====================================================================================
    //  Scanning Mods/Skins
    // =====================================================================================

    internal static class SkinScanner
    {
        public static List<SkinSource> Sources = new List<SkinSource>();
        public static List<string> Warnings = new List<string>();

        public static string SkinsDir
        {
            get { return Path.Combine(Path.Combine(Directory.GetCurrentDirectory(), "Mods"), "Skins"); }
        }

        public static void Scan()
        {
            Sources = new List<SkinSource>();
            Warnings = new List<string>();
            PackInfoCache.Clear();

            string root = SkinsDir;
            if (!Directory.Exists(root))
            {
                Directory.CreateDirectory(root);
                return;
            }

            ScanLegacy(root);

            foreach (var dir in Directory.GetDirectories(root, "*", SearchOption.AllDirectories))
            {
                try { ScanFolder(root, dir); }
                catch (Exception ex) { Warnings.Add("Could not read skin folder '" + Rel(dir) + "': " + ex.Message); }
            }
        }

        public static string Rel(string path)
        {
            string root = SkinsDir;
            if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) path = path.Substring(root.Length).TrimStart('\\', '/');
            return path.Replace('\\', '/');
        }

        private static void ScanFolder(string root, string dir)
        {
            var pngs = Directory.GetFiles(dir, "*.png");
            string jsonPath = Path.Combine(dir, "skin.json");
            bool hasJson = File.Exists(jsonPath);

            // A skin folder = a folder holding skin.json or at least one recognised texture name.
            bool isSkinFolder = hasJson;
            foreach (var p in pngs)
            {
                int slot; bool? sex;
                if (TexSlots.TryParseFileName(Path.GetFileNameWithoutExtension(p), out slot, out sex)) { isSkinFolder = true; break; }
            }
            if (!isSkinFolder)
            {
                if (pngs.Length > 0)
                    Warnings.Add("'" + Rel(dir) + "' has PNGs but none with a skin texture name (diffuse.png, pattern.png, ...). Skipped.");
                return;
            }

            var parent = Directory.GetParent(dir);
            if (parent == null || string.Equals(parent.FullName.TrimEnd('\\', '/'), root.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            {
                Warnings.Add("'" + Rel(dir) + "' looks like a skin but isn't inside a species folder. Use Mods/Skins/<Species>/<SkinName>/.");
                return;
            }

            string folderName = Path.GetFileName(dir);
            var src = new SkinSource
            {
                Legacy = false,
                Location = Rel(dir),
                SpeciesText = parent.Name.TrimStart('_'),
                Id = folderName.TrimStart('_'),
                Name = folderName.TrimStart('_'),
                Disabled = Rel(dir).Split('/').Any(p => p.StartsWith("_")),   // skin, species or pack folder switched off
            };

            // Packs: anything above the species folder. Same-name skins from different packs get different slots.
            var parts = Rel(dir).Split('/');
            if (parts.Length >= 3)
            {
                src.Pack = string.Join("/", parts.Take(parts.Length - 2).Select(p => p.TrimStart('_')).ToArray());
                ReadPackInfo(src, Path.Combine(root, parts[0]));
            }
            src.Feathered = SkinModBase.CheckIsFeathered(src.Name);

            if (hasJson) ReadSettings(src, jsonPath, true);

            // Specific files (with _male/_female) win over generic ones, whatever the file order.
            var generic = new List<KeyValuePair<int, string>>();
            foreach (var p in pngs)
            {
                string n = Path.GetFileNameWithoutExtension(p);
                if (n.Equals("thumbnail", StringComparison.OrdinalIgnoreCase)) { src.ThumbnailPath = p; continue; }
                if (n.StartsWith("portrait", StringComparison.OrdinalIgnoreCase))
                {
                    string pkey;
                    if (TryParsePortrait(n.Substring("portrait".Length), out pkey)) src.Portraits[pkey] = p;
                    else Warnings.Add(src.Location + "/" + Path.GetFileName(p) + ": unknown portrait name, ignored. Use portrait.png, optionally with "
                        + "_infant, _male/_female and _albino/_melanistic/_leucistic (e.g. portrait_infant_female.png).");
                    continue;
                }

                int slot; bool? sex;
                if (!TexSlots.TryParseFileName(n, out slot, out sex))
                {
                    bool noDiffuse = !pngs.Any(q => Path.GetFileNameWithoutExtension(q).StartsWith("diffuse", StringComparison.OrdinalIgnoreCase));
                    Warnings.Add(src.Location + "/" + Path.GetFileName(p) + ": unknown texture name, ignored. "
                        + (noDiffuse ? "If this is the skin's colour texture, rename it to diffuse.png. " : "")
                        + "Valid names: diffuse, normal, extra, pattern, fur (optionally infant_ in front, _male/_female at the end), thumbnail, portrait.");
                    continue;
                }
                if (sex == null) generic.Add(new KeyValuePair<int, string>(slot, p));
                else src.SetFile(slot, sex, p, true);
            }
            foreach (var g in generic) src.SetFile(g.Key, null, g.Value, false);

            bool settingsOnly = src.FurCoverage != null || src.FurLength.HasValue || src.HasVariationOverrides
                                || src.Files[TexSlots.Fur, 0] != null || src.Files[TexSlots.Fur, 1] != null;
            if (!settingsOnly && src.Files[TexSlots.Diffuse, 0] == null && src.Files[TexSlots.Diffuse, 1] == null
                && src.Files[TexSlots.Pattern, 0] == null && src.Files[TexSlots.Pattern, 1] == null)
            {
                Warnings.Add(src.Location + ": no diffuse.png or pattern.png - this skin will mostly look like its base skin.");
            }

            Sources.Add(src);
        }

        private static readonly Dictionary<string, string[]> PackInfoCache = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Optional pack.json in the pack's top folder: { "name": "...", "author": "...", "version": "..." }.</summary>
        private static void ReadPackInfo(SkinSource src, string packDir)
        {
            string[] info;
            if (!PackInfoCache.TryGetValue(packDir, out info))
            {
                info = new string[3];
                string f = Path.Combine(packDir, "pack.json");
                if (File.Exists(f))
                {
                    try
                    {
                        var j = JObject.Parse(File.ReadAllText(f));
                        info[0] = (string)j["name"]; info[1] = (string)j["author"]; info[2] = (string)j["version"];
                    }
                    catch (Exception ex) { Warnings.Add(Rel(f) + " is not valid JSON (" + ex.Message + ")."); }
                }
                PackInfoCache[packDir] = info;
            }
            src.PackName = info[0]; src.PackAuthor = info[1]; src.PackVersion = info[2];
        }

        /// <summary>
        /// Reads skin.json (folder skins) or Species_Name.json (flat-file skins). Flat files may not change
        /// "name"/"id": those decide the skin's permanent slot.
        /// </summary>
        private static void ReadSettings(SkinSource src, string jsonPath, bool allowIdentity)
        {
            string shown = SkinScanner.Rel(jsonPath);
            try
            {
                var j = JObject.Parse(File.ReadAllText(jsonPath));
                JToken t;
                if (allowIdentity)
                {
                    t = j["id"]; if (t != null && t.Type == JTokenType.String && ((string)t).Trim().Length > 0) src.Id = ((string)t).Trim();
                    t = j["name"]; if (t != null && t.Type == JTokenType.String && ((string)t).Trim().Length > 0) src.Name = ((string)t).Trim();
                }
                else if (j["id"] != null || j["name"] != null)
                {
                    Warnings.Add(shown + ": \"name\"/\"id\" are ignored for flat-file skins (renaming would move the skin to a new slot). Rename the PNGs or use a skin folder.");
                }
                t = j["author"]; if (t != null) src.Author = (string)t;
                t = j["base"]; if (t != null && t.Type != JTokenType.Null) src.Base = t.ToString().Trim();
                t = j["feathered"]; if (t != null && t.Type == JTokenType.Boolean) src.Feathered = (bool)t;
                t = j["enabled"]; if (t != null && t.Type == JTokenType.Boolean && !(bool)t) src.Disabled = true;
                t = j["pattern"];
                if (t != null && t.Type == JTokenType.String)
                {
                    string pm = ((string)t).Trim().ToLowerInvariant();
                    if (pm == "none" || pm == "solid" || pm == "diffuse" || pm == "base") src.PatternMode = pm;
                    else Warnings.Add(shown + ": \"pattern\" accepts \"diffuse\", \"base\", \"solid\" or \"none\" (or use pattern.png for your own).");
                }
                t = j["iridescence"]; if (t != null && t.Type == JTokenType.Boolean) src.Iridescence = (bool)t;
                t = j["iridescenceStrength"];
                if (t != null && (t.Type == JTokenType.Float || t.Type == JTokenType.Integer)) src.IridescenceStrength = (float)t;
                src.FuzzDroopiness = ReadVec3(j["fuzzDroopiness"], shown, "fuzzDroopiness") ?? src.FuzzDroopiness;
                src.InfantFuzzDroopiness = ReadVec3(j["infantFuzzDroopiness"], shown, "infantFuzzDroopiness") ?? src.InfantFuzzDroopiness;
                t = j["furLength"];
                if (t != null && (t.Type == JTokenType.Float || t.Type == JTokenType.Integer)) src.FurLength = Mathf.Clamp((float)t, 0f, 5f);
                t = j["furCoverage"];
                if (t != null && t.Type == JTokenType.String)
                {
                    string fc = ((string)t).Trim().ToLowerInvariant();
                    if (fc == "baby" || fc == "body" || fc == "none") src.FurCoverage = fc;
                    else if (fc != "skin") Warnings.Add(shown + ": \"furCoverage\" must be \"skin\", \"baby\", \"body\" or \"none\".");
                }
                t = j["cutout"];
                if (t != null && t.Type == JTokenType.String)
                {
                    string c = ((string)t).Trim().ToLowerInvariant();
                    if (c == "auto" || c == "base" || c == "own") src.Cutout = c;
                    else Warnings.Add(shown + ": \"cutout\" must be \"auto\", \"base\" or \"own\".");
                }
            }
            catch (Exception ex)
            {
                Warnings.Add(shown + " is not valid JSON (" + ex.Message + "). Using defaults.");
            }
        }

        private static Vector3? ReadVec3(JToken t, string shown, string field)
        {
            if (t == null) return null;
            var a = t as JArray;
            if (a != null && a.Count == 3)
            {
                try { return new Vector3((float)a[0], (float)a[1], (float)a[2]); } catch { }
            }
            Warnings.Add(shown + ": \"" + field + "\" must be three numbers, e.g. [0, -1, -1] (see the F8 report for the base skin's values).");
            return null;
        }

        // ---------------------------------------------------------------- legacy flat files (v3 format)

        /// <summary>
        /// The v3 loader's rules, kept identical so existing skins keep their names and slot numbers:
        /// Mods/Skins/Species_Name[_pattern|_mask][_male|_female|_m|_f].png
        /// </summary>
        private static void ScanLegacy(string root)
        {
            string[] pngFiles = Directory.GetFiles(root, "*.png");
            Array.Sort(pngFiles);   // same (culture) ordering as v3: decides migration order
            var byKey = new Dictionary<string, SkinSource>(StringComparer.OrdinalIgnoreCase);

            foreach (string filePath in pngFiles)
            {
                string rawFilename = Path.GetFileNameWithoutExtension(filePath);
                string clean = SkinModBase.CleanName(rawFilename);

                // Optional portrait: Species_Name_portrait[_infant][_male|_female][_albino|...].png
                var por = LegacyPortrait.Match(clean);
                if (por.Success)
                {
                    string pkey;
                    if (TryParsePortrait(por.Groups["tags"].Value, out pkey))
                        LegacyGroup(byKey, por.Groups["name"].Value.Trim()).Portraits[pkey] = filePath;
                    continue;
                }

                // v4 addition: "_infant" in the trailing tags (any order), e.g. Species_Name_infant.png,
                // Species_Name_infant_pattern.png, Species_Name_pattern_infant_female.png.
                var inf = LegacyInfant.Match(clean);
                if (inf.Success)
                {
                    string name = inf.Groups["name"].Value.Trim();
                    bool? sexI = null; bool patternI = false;
                    foreach (Capture c in inf.Groups["tag"].Captures)
                    {
                        string tag = c.Value.ToLowerInvariant();
                        if (tag == "pattern" || tag == "mask") patternI = true;
                        else if (tag == "male" || tag == "m") sexI = true;
                        else if (tag == "female" || tag == "f") sexI = false;
                    }
                    var grp = LegacyGroup(byKey, name);
                    grp.SetFile((patternI ? TexSlots.Pattern : TexSlots.Diffuse) + TexSlots.InfantOffset, sexI, filePath, sexI != null);
                    continue;
                }

                bool isPattern = clean.EndsWith("_pattern", StringComparison.OrdinalIgnoreCase) ||
                                 clean.EndsWith("_mask", StringComparison.OrdinalIgnoreCase) ||
                                 clean.EndsWith(" pattern", StringComparison.OrdinalIgnoreCase);
                if (isPattern)
                {
                    if (clean.EndsWith("_pattern", StringComparison.OrdinalIgnoreCase))
                        clean = clean.Substring(0, clean.Length - "_pattern".Length).Trim();
                    else if (clean.EndsWith(" pattern", StringComparison.OrdinalIgnoreCase))
                        clean = clean.Substring(0, clean.Length - " pattern".Length).Trim();
                    else if (clean.EndsWith("_mask", StringComparison.OrdinalIgnoreCase))
                        clean = clean.Substring(0, clean.Length - "_mask".Length).Trim();
                }

                string baseKey; bool? isMale;
                SkinModBase.ParseGender(clean, out baseKey, out isMale);

                SkinSource src = LegacyGroup(byKey, baseKey);

                int slot = isPattern ? TexSlots.Pattern : TexSlots.Diffuse;
                src.SetFile(slot, isMale, filePath, isMale != null);
                if (!isPattern && src.ThumbnailPath == null) src.ThumbnailPath = filePath;
            }

            // Optional settings next to the PNGs: Mods/Skins/Species_Name.json
            foreach (var src in byKey.Values)
            {
                string json = Path.Combine(root, src.LegacyName + ".json");
                if (File.Exists(json)) ReadSettings(src, json, false);
            }

            // A portrait without any texture isn't a skin (e.g. a typo in the name part).
            foreach (var src in byKey.Values.Where(x => !x.HasAnyTexture && x.Portraits.Count > 0).ToList())
            {
                Sources.Remove(src);
                Warnings.Add(src.LegacyName + "_portrait.png: no skin called '" + src.LegacyName + "' found, so the portrait is ignored.");
            }
        }

        private static readonly Regex LegacyPortrait = new Regex(@"^(?<name>.+?)[_ ]portrait(?<tags>(?:[_ ][a-z]+)*)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>"_infant_female_albino" (any order) → "i|f|albino". Empty → "a||base".</summary>
        internal static bool TryParsePortrait(string tags, out string key)
        {
            string stage = "a", sex = "", mut = "base";
            key = null;
            foreach (var raw in tags.Split(new[] { '_', ' ', '-' }, StringSplitOptions.RemoveEmptyEntries))
            {
                switch (raw.ToLowerInvariant())
                {
                    case "infant": case "baby": stage = "i"; break;
                    case "adult": stage = "a"; break;
                    case "male": case "m": sex = "m"; break;
                    case "female": case "f": sex = "f"; break;
                    case "albino": mut = "albino"; break;
                    case "melanistic": mut = "melanistic"; break;
                    case "leucistic": mut = "leucistic"; break;
                    default: return false;
                }
            }
            key = stage + "|" + sex + "|" + mut;
            return true;
        }

        private static readonly Regex LegacyInfant = new Regex(
            @"^(?<name>.+?)(?:[_ ](?<tag>pattern|mask|male|female|m|f))*[_ ]infant(?:[_ ](?<tag>pattern|mask|male|female|m|f))*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static SkinSource LegacyGroup(Dictionary<string, SkinSource> byKey, string baseKey)
        {
            SkinSource src;
            if (!byKey.TryGetValue(baseKey, out src))
            {
                src = new SkinSource
                {
                    Legacy = true,
                    Location = baseKey + " (flat files)",
                    LegacyName = baseKey,
                    Feathered = SkinModBase.CheckIsFeathered(baseKey),
                };
                byKey[baseKey] = src;
                Sources.Add(src);
            }
            return src;
        }

        // ---------------------------------------------------------------- resolving per species

        private static string Norm(string s)
        {
            return string.IsNullOrEmpty(s) ? "" : s.Replace("_", "").Replace(" ", "").Replace("-", "").ToLowerInvariant();
        }

        public static bool FolderMatchesSpecies(string folder, BaseAnimalData sp)
        {
            string f = Norm(folder);
            if (f.Length == 0) return false;
            return f == Norm(sp.name) || f == Norm(sp.speciesID) || f == Norm(sp.shortSpeciesID)
                || f == Norm(sp.genusName + sp.speciesName);
        }

        /// <summary>"Ember  by Moshepit" (credit in grey) in the nursery, when an author is known and the setting is on.</summary>
        public static string WithAuthor(string name, SkinSource src)
        {
            string author = !string.IsNullOrEmpty(src.Author) ? src.Author : src.PackAuthor;
            if (string.IsNullOrEmpty(author) || Config.ShowAuthorInNursery == null || !Config.ShowAuthorInNursery.Value) return name;
            // The game's labels support rich text (tested in the nursery): show the credit greyed out.
            string clean = System.Text.RegularExpressions.Regex.Replace(author, "<[^>]*>", "").Trim();
            if (clean.Length == 0) return name;
            return name + "  <color=#9a9a9a>by " + clean + "</color>";
        }

        public static string KeyOf(string idOrName)
        {
            return (idOrName ?? "").Trim().ToLowerInvariant();
        }

        /// <summary>All enabled-or-disabled skins that belong to this species, folder skins winning over legacy ones with the same key.</summary>
        public static List<SkinDef> DefsFor(BaseAnimalData species, List<BaseAnimalData> allSpecies)
        {
            return DefsFor(species, allSpecies, false);
        }

        /// <summary>quiet: don't add duplicate-name warnings (the skin panel asks often).</summary>
        public static List<SkinDef> DefsFor(BaseAnimalData species, List<BaseAnimalData> allSpecies, bool quiet)
        {
            int warnCount = Warnings.Count;
            var r = DefsForImpl(species, allSpecies);
            if (quiet && Warnings.Count > warnCount) Warnings.RemoveRange(warnCount, Warnings.Count - warnCount);
            return r;
        }

        private static List<SkinDef> DefsForImpl(BaseAnimalData species, List<BaseAnimalData> allSpecies)
        {
            var result = new List<SkinDef>();
            var byKey = new Dictionary<string, SkinDef>(StringComparer.OrdinalIgnoreCase);

            foreach (var src in Sources)
            {
                SkinDef def = null;
                if (src.Legacy)
                {
                    if (SkinModBase.IsSkinMatchingAnimal(src.LegacyName, species, allSpecies))
                    {
                        string display = SkinModBase.ExtractDisplayName(src.LegacyName, species);
                        def = new SkinDef { Source = src, Species = species, DisplayName = WithAuthor(display, src), Key = KeyOf(display) };
                    }
                }
                else if (FolderMatchesSpecies(src.SpeciesText, species))
                {
                    def = new SkinDef { Source = src, Species = species, DisplayName = WithAuthor(src.Name, src),
                                        Key = KeyOf(src.Pack != null ? src.Pack + "/" + src.Id : src.Id) };
                }
                if (def == null) continue;

                SkinDef existing;
                if (byKey.TryGetValue(def.Key, out existing))
                {
                    if (existing.Source.Legacy && !src.Legacy)
                    {
                        result.Remove(existing);
                        Warnings.Add(species.name + ": '" + src.Location + "' replaces the flat-file skin '" + existing.Source.LegacyName + "' (same name).");
                    }
                    else
                    {
                        if (!(existing.Source.Legacy && src.Legacy))   // v3 silently merged same-name flat files; keep that quiet
                            Warnings.Add(species.name + ": two skins named '" + def.DisplayName + "' ('" + existing.Source.Location + "' and '" + src.Location + "'). Only the first is used; give one an \"id\" in skin.json.");
                        continue;
                    }
                }
                byKey[def.Key] = def;
                result.Add(def);
            }
            return result;
        }

        /// <summary>Folder skins whose species folder matches no species at all.</summary>
        public static void WarnUnknownSpecies(List<BaseAnimalData> allSpecies)
        {
            foreach (var src in Sources)
            {
                if (src.Legacy) continue;
                bool any = false;
                foreach (var sp in allSpecies) if (FolderMatchesSpecies(src.SpeciesText, sp)) { any = true; break; }
                if (!any)
                    Warnings.Add(src.Location + ": no species called '" + src.SpeciesText + "'. Use the species name from the F8 report (e.g. 'Tyrannosaurus', 'Allosaurus Anax').");
            }
        }
    }
}
