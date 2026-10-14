using MelonLoader;
using PrehistoricKingdom;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace PKSkinLoader
{
    /// <summary>
    /// Phase 0 research tool.
    ///   Report key (default F8): writes a text report of every species, every skin slot,
    ///     every texture the skin uses, and the live shader properties of animals in the park.
    ///   Export key (default F9): saves the vanilla textures as PNG templates for artists.
    /// Everything is written to  UserData/PKSkinLoader/Dump/
    /// </summary>
    public static class SkinDumper
    {
        // ---------------------------------------------------------------- settings

        private static MelonPreferences_Entry<KeyCode> _reportKey;
        private static MelonPreferences_Entry<KeyCode> _exportKey;
        private static MelonPreferences_Entry<string> _exportFilter;

        private static bool _busy;

        public static string DumpDir
        {
            get { return Path.Combine(Path.Combine(Path.Combine(Directory.GetCurrentDirectory(), "UserData"), "PKSkinLoader"), "Dump"); }
        }

        public static void Init()
        {
            var cat = MelonPreferences.CreateCategory("PKSkinLoader_Dev", "PK Skin Loader - Developer Tools");
            _reportKey = cat.CreateEntry("ReportKey", KeyCode.F8, "Report key",
                "Writes a text report of all species, skins, texture slots and shader properties.", false, false, null);
            _exportKey = cat.CreateEntry("ExportTemplatesKey", KeyCode.F9, "Export templates key",
                "Exports vanilla textures as PNG templates.", false, false, null);
            _exportFilter = cat.CreateEntry("ExportFilter", "", "Export filter",
                "Which species to export. Empty = only species currently in your park. " +
                "'ALL' = every species (slow, many GB). Or a comma list of names, e.g. 'Tyrannosaurus, Leaellynasaura'.", false, false, null);
        }

        public static void OnUpdate()
        {
            if (_busy || _reportKey == null) return;

            if (Input.GetKeyDown(_reportKey.Value))
            {
                try { WriteReport(); }
                catch (Exception ex) { SkinModBase.Log.Error("[Dump] Report failed: " + ex); }
            }
            else if (Input.GetKeyDown(_exportKey.Value))
            {
                MelonCoroutines.Start(ExportTemplatesRoutine());
            }
        }

        // ---------------------------------------------------------------- texture slot table

        /// <summary>One texture slot on a skin variation asset, plus the shader property it feeds.</summary>
        private class Slot
        {
            public string FileName;      // name used for template files (and the future skin folder layout)
            public string ShaderProp;    // shader property the game assigns it to (confirmed from game strings)
            public Func<AnimalSkinVariationAsset, AssetReference> Get;

            public Slot(string file, string prop, Func<AnimalSkinVariationAsset, AssetReference> get)
            {
                FileName = file; ShaderProp = prop; Get = get;
            }
        }

        private static readonly Slot[] Slots =
        {
            new Slot("diffuse",          "_AdultDiffuse",      a => a.adultDiffuseMap),
            new Slot("normal",           "_AdultNormal",       a => a.adultNormalMap),
            new Slot("extra",            "_AdultExtraMap",     a => a.adultExtraMap),
            new Slot("pattern",          "_AdultPatternMask",  a => a.adultPatternMap),
            new Slot("fur",              "_AdultFurMask",      a => a.adultFurMap),
            new Slot("infant_diffuse",   "_InfantDiffuse",     a => a.infantDiffuseMap),
            new Slot("infant_normal",    "_InfantNormal",      a => a.infantNormalMap),
            new Slot("infant_extra",     "_InfantExtraMap",    a => a.infantExtraMap),
            new Slot("infant_pattern",   "_InfantPatternMask", a => a.infantPatternMap),
            new Slot("infant_fur",       "_InfantFurMask",     a => a.infantFurMap),
            new Slot("iridescence",          "_IridescenceMask (on the species material; the mod sets it per animal)", a => a.defaultVariationData != null ? a.defaultVariationData.iridescenceMap : null),
            new Slot("iridescence_gradient", "_IridescenceTexture (on the species material; the mod sets it per animal)", a => a.defaultVariationData != null ? a.defaultVariationData.iridescenceGradientMap : null),
        };

        private static readonly Slot PortraitSlot = new Slot("portrait", "portrait (info panel / nursery lists)", a => null);

        /// <summary>One job if male and female share the texture, otherwise name_male + name_female.</summary>
        private static List<KeyValuePair<string, AssetReference>> Pair(string name, AssetReference mr, AssetReference fr)
        {
            string mg = GuidOf(mr), fg = GuidOf(fr);
            var jobs = new List<KeyValuePair<string, AssetReference>>();
            if (mg != null && mg == fg) jobs.Add(new KeyValuePair<string, AssetReference>(name, mr));
            else
            {
                if (mg != null) jobs.Add(new KeyValuePair<string, AssetReference>(name + "_male", mr));
                if (fg != null) jobs.Add(new KeyValuePair<string, AssetReference>(name + "_female", fr));
            }
            return jobs;
        }

        private static bool IsSet(AssetReference r)
        {
            try { return r != null && !string.IsNullOrEmpty(r.AssetGUID) && r.RuntimeKeyIsValid(); }
            catch { return false; }
        }

        private static string GuidOf(AssetReference r)
        {
            return IsSet(r) ? r.AssetGUID : null;
        }

        private static string ShortGuid(AssetReference r)
        {
            string g = GuidOf(r);
            return g == null ? "-" : g.Substring(0, Math.Min(8, g.Length));
        }

        // ---------------------------------------------------------------- report

        private static List<BaseAnimalData> GetAllSpecies()
        {
            var list = Resources.FindObjectsOfTypeAll<BaseAnimalData>()
                .Where(a => a != null && !string.IsNullOrEmpty(a.name))
                .GroupBy(a => a.name).Select(g => g.First())
                .OrderBy(a => a.name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return list;
        }

        internal static bool IsCustomSlot(BaseAnimalData species, int idx)
        {
            return idx >= 0 && idx < species.skinsData.Count && SkinInjector.IsOurs(species.skinsData[idx]);
        }

        public static void WriteReport()
        {
            Directory.CreateDirectory(DumpDir);
            string path = Path.Combine(DumpDir, "report_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt");
            var sb = new StringBuilder();

            sb.AppendLine("PK Skin Loader - Phase 0 report");
            sb.AppendLine("Generated " + DateTime.Now + "  |  Unity " + Application.unityVersion + "  |  Game " + Application.version);
            sb.AppendLine();

            var species = GetAllSpecies();
            sb.AppendLine("=== SPECIES & SKIN SLOTS (" + species.Count + " species) ===");
            sb.AppendLine("Texture columns show the first 8 chars of the Addressables GUID. Same GUID = same texture.");
            sb.AppendLine("Slot tagged [CUSTOM] = injected by this mod, not vanilla.");
            sb.AppendLine();

            foreach (var sp in species)
            {
                int skinCount = sp.skinsData != null ? sp.skinsData.Count : 0;
                sb.AppendLine("## " + sp.name
                    + "   speciesID='" + sp.speciesID + "'  short='" + sp.shortSpeciesID + "'"
                    + "  genus='" + sp.genusName + "'  species='" + sp.speciesName + "'  skins=" + skinCount);

                for (int i = 0; i < skinCount; i++)
                {
                    var skin = sp.skinsData[i];
                    if (skin == null) { sb.AppendLine("   [" + i + "] <null>"); continue; }
                    if (SkinInjector.IsStandIn(skin)) { sb.AppendLine("   [" + i + "] hidden stand-in (copy of slot 0)"); continue; }

                    sb.AppendLine("   [" + i + "] '" + skin.skinName + "'" + (IsCustomSlot(sp, i) ? "  [CUSTOM]" : "")
                        + "  integument=" + skin.skinInteguments
                        + "  size m/f=" + skin.maleSizeMultiplier + "/" + skin.femaleSizeMultiplier
                        + "  growthClamp m/f=" + skin.maleGrowthClamp + "/" + skin.femaleGrowthClamp);

                    var m = skin.maleVariationData;
                    var f = skin.femaleVariationData;
                    sb.AppendLine("        variation assets: male='" + (m != null ? m.name : "null") + "'  female='" + (f != null ? f.name : "null") + "'"
                        + (m != null && m == f ? "  (shared)" : ""));

                    foreach (var slot in Slots)
                    {
                        string mg = m != null ? ShortGuid(slot.Get(m)) : "-";
                        string fg = f != null ? ShortGuid(slot.Get(f)) : "-";
                        if (mg == "-" && fg == "-") continue;
                        sb.AppendLine("        " + slot.FileName.PadRight(22) + (mg == fg ? mg + "  (m=f)" : "m:" + mg + "  f:" + fg));
                    }

                    if (m != null)
                    {
                        sb.AppendLine("        fuzzDroopiness adult=" + m.adultFuzzDroopiness + " infant=" + m.infantFuzzDroopiness
                            + "  iridescence=" + (m.defaultVariationData != null && m.defaultVariationData.iridescence)
                            + (m.defaultVariationData != null ? " strength=" + m.defaultVariationData.iridescenceStrength : "")
                            + "  mutagenHue=" + m.mutagenHueMinMax);
                    }
                }
                sb.AppendLine();
            }

            WriteLiveAnimals(sb);

            File.WriteAllText(path, sb.ToString());
            SkinModBase.Log.Msg("[Dump] Report written: " + path);
        }

        private static List<IAnimal> FindLiveAnimals()
        {
            // Park animals are MonoBehaviours; vivarium animals aren't, so add the ones the skin loader has seen.
            var result = new List<IAnimal>();
            var seen = new HashSet<IAnimal>(RefEq<IAnimal>.Instance);
            foreach (var mb in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>())
            {
                var a = mb as IAnimal;
                if (a != null && seen.Add(a)) result.Add(a);
            }
            foreach (var a in SkinApplier.Known)
            {
                if (a == null || !seen.Add(a)) continue;
                try { if (a.Transform == null) continue; } catch { continue; }
                result.Add(a);
            }
            return result;
        }

        private static void WriteLiveAnimals(StringBuilder sb)
        {
            var animals = FindLiveAnimals();
            sb.AppendLine("=== LIVE ANIMALS IN SCENE (" + animals.Count + ") ===");
            sb.AppendLine("One example per species + skin + age group. Shows every shader property and its current value.");
            sb.AppendLine("'BLOCK' = value overridden per-renderer via MaterialPropertyBlock (this is how our mod applies skins).");
            sb.AppendLine();

            var seen = new HashSet<string>();
            var shadersDumped = new HashSet<string>();

            foreach (var a in animals)
            {
                string key;
                string header;
                try
                {
                    var data = a.BaseData;
                    bool infant = a.AgeMonths < 12f;
                    key = (data != null ? data.name : "?") + "|" + a.CurrentSkinIDX + "|" + infant;
                    if (!seen.Add(key)) continue;

                    header = "## " + (data != null ? data.name : "?") + "  '" + a.Name + "'"
                        + "  sex=" + a.Sex + "  ageMonths=" + a.AgeMonths.ToString("0.0")
                        + "  skinIdx=" + a.CurrentSkinIDX + "  skinType=" + a.SkinType + "  mode=" + a.Mode;
                }
                catch (Exception ex)
                {
                    sb.AppendLine("## <error reading animal: " + ex.Message + ">");
                    continue;
                }

                sb.AppendLine(header);

                var comp = a as Component;
                if (comp == null) { sb.AppendLine(); continue; }
                sb.AppendLine("   renderers: " + string.Join(", ", comp.GetComponentsInChildren<Renderer>(true).Where(x => x != null)
                    .Select(x => x.name + "(" + x.sharedMaterials.Length + " mat)").ToArray()));

                var block = new MaterialPropertyBlock();
                var materialsDone = new HashSet<Material>();

                foreach (var r in comp.GetComponentsInChildren<Renderer>(true))
                {
                    if (r == null) continue;
                    var mats = r.sharedMaterials;
                    bool hasBlock = r.HasPropertyBlock();
                    if (hasBlock) r.GetPropertyBlock(block); else block.Clear();

                    for (int mi = 0; mi < mats.Length; mi++)
                    {
                        var mat = mats[mi];
                        if (mat == null || mat.shader == null) continue;
                        if (!materialsDone.Add(mat)) continue;   // LODs share materials; dump each once

                        var sh = mat.shader;
                        sb.AppendLine("   renderer '" + r.name + "' (" + r.GetType().Name + ")  material '" + mat.name + "'  shader '" + sh.name + "'");

                        bool fullDump = shadersDumped.Add(sh.name);
                        int count = sh.GetPropertyCount();
                        for (int p = 0; p < count; p++)
                        {
                            string pname = sh.GetPropertyName(p);
                            int pid = sh.GetPropertyNameId(p);
                            var ptype = sh.GetPropertyType(p);

                            if (ptype == ShaderPropertyType.Texture)
                            {
                                Texture t = mat.GetTexture(pid);
                                string line = "      TEX  " + pname.PadRight(28) + DescribeTex(t);
                                if (hasBlock && block.HasTexture(pid))
                                    line += "   BLOCK -> " + DescribeTex(block.GetTexture(pid));
                                sb.AppendLine(line);
                            }
                            else if (fullDump)
                            {
                                string val;
                                switch (ptype)
                                {
                                    case ShaderPropertyType.Color: val = mat.GetColor(pid).ToString(); break;
                                    case ShaderPropertyType.Vector: val = mat.GetVector(pid).ToString(); break;
                                    case ShaderPropertyType.Int: val = mat.GetInt(pid).ToString(); break;
                                    default: val = mat.GetFloat(pid).ToString(); break;
                                }
                                sb.AppendLine("      " + ptype.ToString().ToUpper().PadRight(5) + pname.PadRight(28) + val);
                            }
                        }
                        if (!fullDump) sb.AppendLine("      (non-texture properties already listed above for this shader)");
                        // fur diagnostics for every material (values can differ per species/skin)
                        var fur = new StringBuilder("      FUR ");
                        foreach (var pn in new[] { "_Length", "_Droopage", "_DroopDirection", "_FurMasking", "_FurDisplaceMinimum", "_FurDispTexScale",
                                                   "_Maturity", "_IntegumentMaturity", "_FEATURETESSELLATION", "_TessellationEdgeLength" })
                        {
                            if (!mat.HasProperty(pn)) continue;
                            int id = Shader.PropertyToID(pn);
                            string mv, bv = null;
                            if (pn == "_DroopDirection" || pn == "_FurDispTexScale")
                            {
                                mv = mat.GetVector(id).ToString();
                                if (hasBlock && block.HasVector(id)) bv = block.GetVector(id).ToString();
                            }
                            else
                            {
                                mv = mat.GetFloat(id).ToString("0.###");
                                if (hasBlock && block.HasFloat(id)) bv = block.GetFloat(id).ToString("0.###");
                            }
                            fur.Append(pn + "=" + mv + (bv != null ? "[BLOCK " + bv + "]" : "") + "  ");
                        }
                        sb.AppendLine(fur.ToString());
                        sb.AppendLine("      KEYWORDS " + string.Join(" ", mat.shaderKeywords ?? new string[0]) + "   renderQueue=" + mat.renderQueue);
                    }
                }
                sb.AppendLine();
            }
        }

        private static string DescribeTex(Texture t)
        {
            if (t == null) return "null";
            string fmt = "";
            try { fmt = t.graphicsFormat + (GraphicsFormatUtility.IsSRGBFormat(t.graphicsFormat) ? " sRGB" : " linear"); } catch { }
            return "'" + t.name + "' " + t.width + "x" + t.height + " " + fmt;
        }

        // ---------------------------------------------------------------- template export

        private static HashSet<string> ResolveExportSpecies(List<BaseAnimalData> all)
        {
            string filter = (_exportFilter.Value ?? "").Trim();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (filter.Equals("ALL", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var s in all) names.Add(s.name);
            }
            else if (filter.Length == 0)
            {
                foreach (var a in FindLiveAnimals())
                {
                    try { if (a.BaseData != null) names.Add(a.BaseData.name); } catch { }
                }
            }
            else
            {
                var wanted = filter.Split(',').Select(w => w.Trim().Replace(" ", "").Replace("_", "").ToLowerInvariant())
                                   .Where(w => w.Length > 0).ToList();
                foreach (var s in all)
                {
                    string n = s.name.Replace(" ", "").Replace("_", "").ToLowerInvariant();
                    if (wanted.Any(w => n.StartsWith(w))) names.Add(s.name);
                }
            }
            return names;
        }

        public static bool Busy { get { return _busy; } }

        /// <summary>Exports one species' templates (skin panel). Skips the work if they're already there.</summary>
        public static IEnumerator ExportSpecies(BaseAnimalData sp)
        {
            while (_busy) yield return null;
            bool complete = true;
            for (int i = 0; i < sp.skinsData.Count; i++)
            {
                var skin = sp.skinsData[i];
                if (skin == null || IsCustomSlot(sp, i)) continue;
                if (!File.Exists(Path.Combine(TemplateDir(sp, i), "_manifest.txt"))) { complete = false; break; }
            }
            if (complete) yield break;
            var only = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { sp.name };
            yield return ExportTemplatesRoutine(only);
        }

        /// <summary>Dump/Templates/&lt;Species&gt;/&lt;nn&gt;_&lt;Skin&gt;/ for a vanilla skin.</summary>
        public static string TemplateDir(BaseAnimalData sp, int idx)
        {
            var skin = sp.skinsData[idx];
            return Path.Combine(Path.Combine(Path.Combine(DumpDir, "Templates"), Safe(sp.name)), idx.ToString("00") + "_" + Safe(skin.skinName));
        }

        private static IEnumerator ExportTemplatesRoutine()
        {
            return ExportTemplatesRoutine(null);
        }

        private static IEnumerator ExportTemplatesRoutine(HashSet<string> only)
        {
            _busy = true;
            var log = SkinModBase.Log;

            var all = GetAllSpecies();
            var targets = only ?? ResolveExportSpecies(all);
            if (targets.Count == 0)
            {
                log.Warning("[Dump] No species to export. Place some animals in your park, or set ExportFilter in UserData/MelonPreferences.cfg.");
                _busy = false;
                yield break;
            }

            string root = Path.Combine(DumpDir, "Templates");
            log.Msg("[Dump] Exporting templates for " + targets.Count + " species to " + root + " ...");

            var savedByGuid = new Dictionary<string, string>();   // reuse: copy file instead of reloading
            int saved = 0, failed = 0;

            foreach (var sp in all)
            {
                if (!targets.Contains(sp.name) || sp.skinsData == null) continue;

                for (int i = 0; i < sp.skinsData.Count; i++)
                {
                    var skin = sp.skinsData[i];
                    if (skin == null || IsCustomSlot(sp, i)) continue;

                    string skinDir = Path.Combine(Path.Combine(root, Safe(sp.name)), i.ToString("00") + "_" + Safe(skin.skinName));
                    Directory.CreateDirectory(skinDir);
                    var manifest = new StringBuilder();
                    manifest.AppendLine(sp.name + " / slot " + i + " / '" + skin.skinName + "' / " + skin.skinInteguments);

                    var m = skin.maleVariationData;
                    var f = skin.femaleVariationData;

                    // Textures, then portraits (so artists get the exact size and framing to paint over).
                    var work = new List<KeyValuePair<Slot, List<KeyValuePair<string, AssetReference>>>>();
                    foreach (var slot in Slots)
                    {
                        var mr = m != null ? slot.Get(m) : null;
                        var fr = f != null ? slot.Get(f) : null;
                        work.Add(new KeyValuePair<Slot, List<KeyValuePair<string, AssetReference>>>(slot, Pair(slot.FileName, mr, fr)));
                    }
                    if (skin.portraits != null)
                    {
                        var pj = Pair("portrait", skin.portraits.adultMale, skin.portraits.adultFemale);
                        if (IsSet(skin.portraits.infant)) pj.Add(new KeyValuePair<string, AssetReference>("portrait_infant", skin.portraits.infant));
                        work.Add(new KeyValuePair<Slot, List<KeyValuePair<string, AssetReference>>>(PortraitSlot, pj));
                    }

                    foreach (var w in work)
                    {
                        var slot = w.Key;
                        foreach (var job in w.Value)
                        {
                            string file = Path.Combine(skinDir, job.Key + ".png");
                            string guid = job.Value.AssetGUID;

                            string existing;
                            if (savedByGuid.TryGetValue(guid, out existing))
                            {
                                try { File.Copy(existing, file, true); } catch { }
                                manifest.AppendLine(job.Key.PadRight(28) + "same texture as " + existing.Substring(root.Length));
                                continue;
                            }

                            AsyncOperationHandle<Texture2D> handle;
                            try { handle = Addressables.LoadAssetAsync<Texture2D>(job.Value.RuntimeKey); }
                            catch (Exception ex)
                            {
                                failed++;
                                manifest.AppendLine(job.Key.PadRight(28) + "LOAD ERROR " + ex.Message);
                                continue;
                            }

                            while (!handle.IsDone) yield return null;

                            if (handle.Status == AsyncOperationStatus.Succeeded && handle.Result != null)
                            {
                                var tex = handle.Result;
                                try
                                {
                                    SaveTextureAsPng(tex, file, slot.FileName.EndsWith("normal"));
                                    savedByGuid[guid] = file;
                                    saved++;
                                    manifest.AppendLine(job.Key.PadRight(28) + "'" + tex.name + "'  " + tex.width + "x" + tex.height
                                        + "  " + tex.graphicsFormat + (GraphicsFormatUtility.IsSRGBFormat(tex.graphicsFormat) ? "  sRGB" : "  linear")
                                        + "  -> " + slot.ShaderProp);
                                }
                                catch (Exception ex)
                                {
                                    failed++;
                                    manifest.AppendLine(job.Key.PadRight(28) + "SAVE ERROR " + ex.Message);
                                }
                            }
                            else
                            {
                                failed++;
                                manifest.AppendLine(job.Key.PadRight(28) + "LOAD FAILED " + handle.OperationException);
                            }

                            Addressables.Release(handle);
                            yield return null;   // spread work over frames so the game stays responsive
                        }
                    }

                    // The nursery skin card (a plain sprite, not an Addressables texture).
                    if (skin.skinThumbnail != null)
                    {
                        try
                        {
                            var card = skin.skinThumbnail;
                            SaveSpriteAsPng(card, Path.Combine(skinDir, "thumbnail.png"));
                            saved++;
                            manifest.AppendLine("thumbnail".PadRight(28) + "'" + card.name + "'  " + (int)card.rect.width + "x" + (int)card.rect.height + "  -> nursery skin card");
                        }
                        catch (Exception ex) { failed++; manifest.AppendLine("thumbnail".PadRight(28) + "SAVE ERROR " + ex.Message); }
                    }

                    File.WriteAllText(Path.Combine(skinDir, "_manifest.txt"), manifest.ToString());
                }
                log.Msg("[Dump]   " + sp.name + " done (" + saved + " textures so far)");
            }

            log.Msg("[Dump] Template export finished: " + saved + " saved, " + failed + " failed. Folder: " + root);
            _busy = false;
        }

        /// <summary>A sprite's own rectangle (sprites can sit in a shared atlas) as a PNG.</summary>
        private static void SaveSpriteAsPng(Sprite sp, string path)
        {
            var src = sp.texture;
            Rect r = sp.textureRect;
            bool srgb = GraphicsFormatUtility.IsSRGBFormat(src.graphicsFormat);
            var rt = RenderTexture.GetTemporary(src.width, src.height, 0, RenderTextureFormat.ARGB32,
                srgb ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear);
            var prev = RenderTexture.active;
            Texture2D readable = null;
            try
            {
                Graphics.Blit(src, rt);
                RenderTexture.active = rt;
                int w = Mathf.RoundToInt(r.width), h = Mathf.RoundToInt(r.height);
                readable = new Texture2D(w, h, TextureFormat.RGBA32, false, !srgb);
                readable.ReadPixels(new Rect(r.x, r.y, w, h), 0, 0);
                readable.Apply(false);
                File.WriteAllBytes(path, ImageConversion.EncodeToPNG(readable));
            }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
                if (readable != null) UnityEngine.Object.Destroy(readable);
            }
        }

        /// <summary>Copies a (possibly compressed, non-readable) GPU texture into a PNG, preserving sRGB/linear encoding.</summary>
        private static void SaveTextureAsPng(Texture2D src, string path, bool unpackNormal)
        {
            bool srgb = GraphicsFormatUtility.IsSRGBFormat(src.graphicsFormat);
            var rt = RenderTexture.GetTemporary(src.width, src.height, 0, RenderTextureFormat.ARGB32,
                srgb ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear);
            var prev = RenderTexture.active;
            Texture2D readable = null;
            try
            {
                Graphics.Blit(src, rt);
                RenderTexture.active = rt;
                readable = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false, !srgb);
                readable.ReadPixels(new Rect(0, 0, src.width, src.height), 0, 0);
                readable.Apply(false);
                if (unpackNormal) UnpackNormal(readable);
                File.WriteAllBytes(path, ImageConversion.EncodeToPNG(readable));
            }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
                if (readable != null) UnityEngine.Object.Destroy(readable);
            }
        }

        /// <summary>
        /// The game stores normal maps packed (R=1, G=Y, A=X), which looks grey. Artists expect the blue-purple
        /// layout (R=X, G=Y, B=Z), so templates are converted; the loader converts back automatically.
        /// </summary>
        private static void UnpackNormal(Texture2D t)
        {
            var px = t.GetPixels32();
            for (int i = 0; i < px.Length; i++)
            {
                float x = px[i].a / 255f * 2f - 1f;
                float y = px[i].g / 255f * 2f - 1f;
                float z = Mathf.Sqrt(Mathf.Max(0f, 1f - x * x - y * y));
                px[i] = new Color32((byte)Mathf.RoundToInt((x * 0.5f + 0.5f) * 255f),
                                    px[i].g,
                                    (byte)Mathf.RoundToInt((z * 0.5f + 0.5f) * 255f), 255);
            }
            t.SetPixels32(px);
            t.Apply(false);
        }

        private static string Safe(string s)
        {
            if (string.IsNullOrEmpty(s)) return "unnamed";
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s.Trim();
        }
    }
}
