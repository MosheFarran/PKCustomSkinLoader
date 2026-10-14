using PrehistoricKingdom;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace PKSkinLoader
{
    /// <summary>
    /// Loads skin PNGs once and caches them. Each slot gets the same colour space as the game's own textures
    /// (diffuse/pattern sRGB, normal/extra/fur linear), normal maps are converted to the game's packed layout,
    /// and textures are GPU-compressed like vanilla ones (configurable).
    /// </summary>
    internal static class TextureStore
    {
        private class Entry
        {
            public Texture2D Tex;
            public DateTime Stamp;
        }

        private static readonly Dictionary<string, Entry> Cache = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Entry> Thumbs = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<Texture2D> Retired = new List<Texture2D>();
        public static int LoadedThisRound;

        /// <summary>A skin texture for one slot/sex. Diffuse slots may take their cut-out transparency from the base skin.</summary>
        private static Texture2D _blankPattern, _solidPattern;

        private static Texture2D FlatPattern(ref Texture2D cache, byte red, string name)
        {
            if (cache != null) return cache;
            cache = new Texture2D(4, 4, TextureFormat.RGBA32, false, false) { name = name };
            var px = new Color32[16];
            for (int i = 0; i < px.Length; i++) px[i] = new Color32(red, 0, 255, 255);
            cache.SetPixels32(px);
            cache.Apply(false, true);
            return cache;
        }

        public static Texture2D For(SkinDef def, int slot, bool isMale)
        {
            // "pattern": "none"  → red 0 everywhere: no pattern, colour sliders/mutations have nothing to tint.
            // "pattern": "solid" → red 255 everywhere: no vanilla pattern, sliders/mutations tint the whole body.
            // "pattern": "diffuse" → mask generated from the skin's own diffuse (dark → colour A, light → colour B).
            // No "pattern" setting: a skin with its own diffuse but no pattern.png gets one made from the diffuse
            // (the base skin's vanilla markings wouldn't line up with yours). "pattern": "base" keeps the vanilla one.
            string mode = def.Source.PatternMode;
            if (mode == null && TexSlots.Kind(slot) == TexSlots.Pattern && def.FileFor(slot, isMale) == null
                && def.FileFor(slot - TexSlots.Pattern + TexSlots.Diffuse, isMale) != null)
                mode = "diffuse";
            if (mode == "base" && TexSlots.Kind(slot) == TexSlots.Pattern) return null;

            if (mode == "diffuse" && TexSlots.Kind(slot) == TexSlots.Pattern)
            {
                string diff = def.FileFor(slot - TexSlots.Pattern + TexSlots.Diffuse, isMale);
                AssetReference basePattern = null;
                if (def.BaseSkin != null)
                {
                    var bv = isMale ? (def.BaseSkin.maleVariationData ?? def.BaseSkin.femaleVariationData)
                                    : (def.BaseSkin.femaleVariationData ?? def.BaseSkin.maleVariationData);
                    if (bv != null) basePattern = TexSlots.IsInfant(slot) ? bv.infantPatternMap : bv.adultPatternMap;
                }
                return diff == null ? null : Get(diff, slot, basePattern, false, true);
            }
            if ((mode == "none" || mode == "solid") && TexSlots.Kind(slot) == TexSlots.Pattern)
                return mode == "solid" ? FlatPattern(ref _solidPattern, 255, "PKSkin solid pattern")
                                                         : FlatPattern(ref _blankPattern, 0, "PKSkin blank pattern");
            string path = def.FileFor(slot, isMale);
            if (path == null) return null;
            AssetReference alphaFrom = null;
            if (TexSlots.Kind(slot) == TexSlots.Diffuse && def.Source.Cutout != "own" && def.BaseSkin != null)
            {
                var v = isMale ? (def.BaseSkin.maleVariationData ?? def.BaseSkin.femaleVariationData)
                               : (def.BaseSkin.femaleVariationData ?? def.BaseSkin.maleVariationData);
                if (v != null) alphaFrom = TexSlots.IsInfant(slot) ? v.infantDiffuseMap : v.adultDiffuseMap;
            }
            return Get(path, slot, alphaFrom, def.Source.Cutout == "base");
        }

        public static Texture2D Get(string path, int slot) { return Get(path, slot, null, false); }

        public static Texture2D Get(string path, int slot, AssetReference alphaFrom, bool forceBaseAlpha)
        {
            return Get(path, slot, alphaFrom, forceBaseAlpha, false);
        }

        public static Texture2D Get(string path, int slot, AssetReference alphaFrom, bool forceBaseAlpha, bool patternFromDiffuse)
        {
            if (path == null) return null;
            bool linear = TexSlots.IsLinear(slot);
            bool normal = TexSlots.IsNormalMap(slot);
            string alphaGuid = null;
            try { if (alphaFrom != null && alphaFrom.RuntimeKeyIsValid()) alphaGuid = alphaFrom.AssetGUID; } catch { }
            string key = path + (normal ? "|n" : linear ? "|l" : "|s") + (alphaGuid != null ? "|a" + (forceBaseAlpha ? "!" : "") + alphaGuid : "")
                         + (patternFromDiffuse ? "|pd" : "");

            // Cached textures are returned without touching the disk (this runs on every visual update);
            // F5 calls EvictChanged() to drop the ones whose file changed.
            Entry e;
            if (Cache.TryGetValue(key, out e) && e.Tex != null) return e.Tex;

            DateTime stamp;
            try { stamp = File.GetLastWriteTimeUtc(path); }
            catch { return null; }

            var tex = Load(path, linear, normal, !patternFromDiffuse, alphaGuid != null ? alphaFrom : null, forceBaseAlpha, patternFromDiffuse,
                           TexSlots.Kind(slot) == TexSlots.Fur);
            if (tex == null) return null;
            Cache[key] = new Entry { Tex = tex, Stamp = stamp };
            LoadedThisRound++;
            return tex;
        }

        /// <summary>F5: forget textures whose file changed or disappeared (they're destroyed after animals get the new ones).</summary>
        public static void EvictChanged()
        {
            var gone = new List<string>();
            foreach (var kv in Cache)
            {
                string path = kv.Key.Substring(0, kv.Key.IndexOf('|'));
                DateTime stamp;
                try { stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue; }
                catch { stamp = DateTime.MinValue; }
                if (stamp != kv.Value.Stamp) gone.Add(kv.Key);
            }
            foreach (var k in gone)
            {
                if (Cache[k].Tex != null) Retired.Add(Cache[k].Tex);
                Cache.Remove(k);
            }
        }

        /// <summary>Call after re-applying textures to every animal (F5): frees textures that were replaced.</summary>
        public static void DestroyRetired()
        {
            foreach (var t in Retired) if (t != null) UnityEngine.Object.Destroy(t);
            Retired.Clear();
        }

        private static Texture2D Load(string path, bool linear, bool normal, bool validate, AssetReference alphaFrom, bool forceBaseAlpha, bool patternFromDiffuse)
        {
            return Load(path, linear, normal, validate, alphaFrom, forceBaseAlpha, patternFromDiffuse, false);
        }

        private static Texture2D Load(string path, bool linear, bool normal, bool validate, AssetReference alphaFrom, bool forceBaseAlpha, bool patternFromDiffuse, bool fur)
        {
            Texture2D decoded = null;
            try
            {
                decoded = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!ImageConversion.LoadImage(decoded, File.ReadAllBytes(path), false))
                {
                    Warn(path, "is not a readable PNG.");
                    return null;
                }

                int w = decoded.width, h = decoded.height;
                var pixels = decoded.GetPixels32();

                // Safety net for video memory: cap the size (MaxTextureSize) and make sure the result can be
                // GPU-compressed (both sides a multiple of 4). Whatever artists drop in, the cost stays bounded.
                int max = Math.Max(256, Math.Min(8192, Config.MaxTextureSize.Value));
                float scale = Math.Min(1f, (float)max / Math.Max(w, h));
                int w2 = Math.Max(4, ((int)(w * scale)) / 4 * 4);
                int h2 = Math.Max(4, ((int)(h * scale)) / 4 * 4);
                if (w2 != w || h2 != h)
                {
                    if (validate)
                    {
                        if (scale < 1f) Info(path, w + "x" + h + " loaded at " + w2 + "x" + h2 + " (MaxTextureSize = " + max + ").");
                        else Info(path, w + "x" + h + " resized to " + w2 + "x" + h2 + " so it can be compressed (sizes must be a multiple of 4).");
                    }
                    pixels = Resample(pixels, w, h, w2, h2);
                    w = w2; h = h2;
                }
                else if (validate && (!IsPow2(w) || !IsPow2(h)))
                    Warn(path, w + "x" + h + " works, but power-of-two sizes (1024, 2048) look best at a distance.");

                if (normal) PackNormals(pixels, path);
                if (fur) FurFromGreyscale(pixels, path);
                if (patternFromDiffuse)
                {
                    DiffuseToPattern(pixels, w, h, alphaFrom, path);   // alphaFrom = the base skin's pattern here
                    ExportGeneratedPattern(pixels, w, h, path);
                }
                else if (alphaFrom != null && (forceBaseAlpha || IsOpaque(pixels))) CopyBaseAlpha(pixels, w, h, alphaFrom, path);

                // Re-create with the right colour space (LoadImage can't be trusted to keep it) and mipmaps.
                var tex = new Texture2D(w, h, TextureFormat.RGBA32, true, linear);
                tex.name = "PKSkin " + Path.GetFileName(Path.GetDirectoryName(path)) + "/" + Path.GetFileNameWithoutExtension(path);
                tex.SetPixels32(pixels);
                tex.Apply(true, false);
                if (Config.CompressTextures.Value) tex.Compress(true);
                tex.Apply(false, true);   // upload, free the CPU copy
                return tex;
            }
            catch (Exception ex)
            {
                Warn(path, "could not be loaded: " + ex.Message);
                return null;
            }
            finally
            {
                if (decoded != null) UnityEngine.Object.Destroy(decoded);
            }
        }

        /// <summary>
        /// The game's normal maps are "packed" (R=255, G=Y, B=Y, A=X - the F9 export shows them as grey).
        /// Artists paint the usual blue-purple map (R=X, G=Y, B=Z), so convert unless it's already packed.
        /// </summary>
        private static void PackNormals(Color32[] px, string path)
        {
            int step = Math.Max(1, px.Length / 4096), samples = 0, redHigh = 0, alphaVaries = 0;
            for (int i = 0; i < px.Length; i += step)
            {
                samples++;
                if (px[i].r >= 250) redHigh++;
                if (px[i].a < 250) alphaVaries++;
            }
            if (redHigh == samples && alphaVaries > 0) return;   // already in the game's layout

            for (int i = 0; i < px.Length; i++)
            {
                var p = px[i];
                px[i] = new Color32(255, p.g, p.g, p.r);
            }
        }

        /// <summary>
        /// Pattern mask from a diffuse, laid out like the game's own masks:
        ///   red   = the skin's markings (its brightness), contrast matched to the base skin's pattern so colour
        ///           sliders and mutations react as strongly as on vanilla skins;
        ///           parts the base pattern leaves untinted (black: mouth, claws, teeth, eyes) stay untinted;
        ///   green = the base pattern's secondary-colour areas (mouth, eye skin, wattles);
        ///   blue  = 255, like every vanilla mask.
        /// Without a base pattern it falls back to plain brightness.
        /// </summary>
        private static void DiffuseToPattern(Color32[] px, int w, int h, AssetReference basePattern, string path)
        {
            var lum = new byte[px.Length];
            for (int i = 0; i < px.Length; i++)
            {
                var p = px[i];
                lum[i] = (byte)((p.r * 54 + p.g * 183 + p.b * 19) >> 8);
            }

            Color32[] b = basePattern != null ? ReadBase(basePattern, w, h, path, "pattern") : null;
            if (b == null || b.Length != px.Length)
            {
                for (int i = 0; i < px.Length; i++) px[i] = new Color32(lum[i], 0, 255, 255);
                return;
            }

            // Contrast: map the diffuse's brightness distribution onto the base pattern's (tinted area only).
            var hs = new double[256]; var ht = new double[256];
            for (int i = 0; i < px.Length; i++)
                if (b[i].r >= 12) { hs[lum[i]]++; ht[b[i].r]++; }
            var lut = new byte[256];
            double ns = 0, nt = 0;
            for (int v = 0; v < 256; v++) { ns += hs[v]; nt += ht[v]; }
            if (ns > 0 && nt > 0)
            {
                double cs = 0; int t = 0; double ct = ht[0] / nt;
                for (int v = 0; v < 256; v++)
                {
                    double q = (cs + hs[v] * 0.5) / ns;   // midpoint of this brightness in the diffuse
                    cs += hs[v];
                    while (t < 255 && ct < q) { t++; ct += ht[t] / nt; }
                    lut[v] = (byte)t;
                }
            }
            else for (int v = 0; v < 256; v++) lut[v] = (byte)v;

            for (int i = 0; i < px.Length; i++)
            {
                float k = Mathf.Clamp01((b[i].r - 4) / 16f);   // 0 where the game doesn't tint, soft edge
                byte red = (byte)Mathf.RoundToInt(lut[lum[i]] * k + b[i].r * (1f - k));
                px[i] = new Color32(red, b[i].g, 255, 255);
            }
        }

        /// <summary>
        /// Saves a generated pattern to UserData/PKSkinLoader/Dump/GeneratedPatterns/ (same folders and names as
        /// the skin, "diffuse" → "pattern"), so artists can touch it up and use it as their own pattern.png.
        /// Only rewritten when the diffuse is newer.
        /// </summary>
        private static void ExportGeneratedPattern(Color32[] px, int w, int h, string diffusePath)
        {
            Texture2D t = null;
            try
            {
                string outPath = GeneratedPatternPath(diffusePath);
                if (File.Exists(outPath) && File.GetLastWriteTimeUtc(outPath) >= File.GetLastWriteTimeUtc(diffusePath)) return;

                Directory.CreateDirectory(Path.GetDirectoryName(outPath));
                t = new Texture2D(w, h, TextureFormat.RGBA32, false, false);
                t.SetPixels32(px);
                File.WriteAllBytes(outPath, ImageConversion.EncodeToPNG(t));
                Info(diffusePath, "pattern made from this diffuse saved to " + outPath.Substring(SkinDumper.DumpDir.Length).TrimStart('\\', '/')
                    + " in the Dump folder (touch it up and use it as pattern.png if you like).");
            }
            catch (Exception ex) { Warn(diffusePath, "generated pattern could not be saved: " + ex.Message); }
            finally { if (t != null) UnityEngine.Object.Destroy(t); }
        }

        /// <summary>Where the pattern generated from this diffuse is saved (Dump/GeneratedPatterns/..., "diffuse" → "pattern").</summary>
        public static string GeneratedPatternPath(string diffusePath)
        {
            string rel = SkinScanner.Rel(diffusePath);
            string dir = Path.GetDirectoryName(rel) ?? "";
            string name = Path.GetFileNameWithoutExtension(rel);
            name = name.IndexOf("diffuse", StringComparison.OrdinalIgnoreCase) >= 0
                ? System.Text.RegularExpressions.Regex.Replace(name, "diffuse", "pattern", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                : name + "_pattern";
            return Path.Combine(Path.Combine(Path.Combine(SkinDumper.DumpDir, "GeneratedPatterns"), dir), name + ".png");
        }

        /// <summary>A base-skin texture (Addressables) read back at the given size, or null.</summary>
        internal static Color32[] ReadBase(AssetReference baseRef, int w, int h, string path, string what)
        {
            AsyncOperationHandle<Texture2D> handle = default(AsyncOperationHandle<Texture2D>);
            RenderTexture rt = null;
            Texture2D read = null;
            var prev = RenderTexture.active;
            try
            {
                if (!baseRef.RuntimeKeyIsValid()) return null;
                handle = Addressables.LoadAssetAsync<Texture2D>(baseRef.RuntimeKey);
                var baseTex = handle.WaitForCompletion();
                if (baseTex == null) { Warn(path, "base skin's " + what + " could not be loaded."); return null; }
                rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                Graphics.Blit(baseTex, rt);
                RenderTexture.active = rt;
                read = new Texture2D(w, h, TextureFormat.RGBA32, false, false);
                read.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                read.Apply(false);
                return read.GetPixels32();
            }
            catch (Exception ex)
            {
                Warn(path, "reading the base skin's " + what + " failed (" + ex.Message + ").");
                return null;
            }
            finally
            {
                RenderTexture.active = prev;
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
                if (read != null) UnityEngine.Object.Destroy(read);
                try { if (handle.IsValid()) Addressables.Release(handle); } catch { }
            }
        }

        /// <summary>Fur amount for white in a black-and-white fur painting (same as the generated whole-body coat).</summary>
        public const byte FurWhite = 50;

        /// <summary>
        /// Artists can paint fur.png in black and white (white = fur, black = bare). The game wants the amount in the red
        /// channel with green and blue full, so convert unless it's already in the game's layout.
        /// </summary>
        private static void FurFromGreyscale(Color32[] px, string path)
        {
            int step = Math.Max(1, px.Length / 4096), samples = 0, grey = 0, gameLayout = 0;
            for (int i = 0; i < px.Length; i += step)
            {
                var p = px[i];
                samples++;
                if (p.g >= 250 && p.b >= 250 && p.r < 250) gameLayout++;
                else if (Math.Abs(p.r - p.g) < 12 && Math.Abs(p.g - p.b) < 12) grey++;
            }
            if (gameLayout > samples / 10 || grey < samples * 9 / 10) return;
            for (int i = 0; i < px.Length; i++)
            {
                var p = px[i];
                int lum = (p.r * 54 + p.g * 183 + p.b * 19) >> 8;
                px[i] = new Color32((byte)(lum * FurWhite / 255), 255, 255, 255);
            }
            if (SkinModBase.Verbose) SkinModBase.Log.Msg(SkinScanner.Rel(path) + ": black-and-white fur painting converted.");
        }

        /// <summary>
        /// True when the texture has (almost) no cut-away pixels. The game hides unused model parts (head crests,
        /// spikes, feather cards) with diffuse transparency below 50%; an opaque custom diffuse would show them all.
        /// </summary>
        private static bool IsOpaque(Color32[] px)
        {
            int step = Math.Max(1, px.Length / 65536), samples = 0, cut = 0;
            for (int i = 0; i < px.Length; i += step) { samples++; if (px[i].a < 128) cut++; }
            return cut < samples * 0.005f;
        }

        /// <summary>Replaces the alpha channel with the base skin's diffuse alpha (resized to fit).</summary>
        private static void CopyBaseAlpha(Color32[] px, int w, int h, AssetReference baseRef, string path)
        {
            AsyncOperationHandle<Texture2D> handle = default(AsyncOperationHandle<Texture2D>);
            RenderTexture rt = null;
            Texture2D read = null;
            var prev = RenderTexture.active;
            try
            {
                handle = Addressables.LoadAssetAsync<Texture2D>(baseRef.RuntimeKey);
                var baseTex = handle.WaitForCompletion();
                if (baseTex == null) { Warn(path, "base skin's diffuse could not be loaded for its cut-outs; using your own transparency."); return; }

                rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                Graphics.Blit(baseTex, rt);
                RenderTexture.active = rt;
                read = new Texture2D(w, h, TextureFormat.RGBA32, false, false);
                read.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                read.Apply(false);
                var b = read.GetPixels32();
                for (int i = 0; i < px.Length && i < b.Length; i++) px[i].a = b[i].a;
                if (SkinModBase.Verbose) SkinModBase.Log.Msg(SkinScanner.Rel(path) + ": using the base skin's cut-outs.");
            }
            catch (Exception ex)
            {
                Warn(path, "copying the base skin's cut-outs failed (" + ex.Message + "); using your own transparency.");
            }
            finally
            {
                RenderTexture.active = prev;
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
                if (read != null) UnityEngine.Object.Destroy(read);
                try { if (handle.IsValid()) Addressables.Release(handle); } catch { }
            }
        }

        public static Sprite Thumbnail(SkinDef def)
        {
            var src = def.Source;
            string path = src.ThumbnailPath ?? def.FileFor(TexSlots.Diffuse, true) ?? def.FileFor(TexSlots.Pattern, true);
            if (path == null) return null;

            DateTime stamp;
            try { stamp = File.GetLastWriteTimeUtc(path); } catch { return null; }

            Entry e;
            if (Thumbs.TryGetValue(path, out e) && e.Stamp == stamp && e.Tex != null)
                return MakeSprite(e.Tex);

            int maxW = src.ThumbnailPath != null ? 512 : 256;
            var sw = System.Diagnostics.Stopwatch.StartNew();

            // Disk cache: decoding a full 2048 diffuse just for a card is the slow part of starting the game.
            string cacheFile = CardCacheFile(path, stamp, maxW);
            CardsUsed.Add(Path.GetFileName(cacheFile));
            if (File.Exists(cacheFile))
            {
                Texture2D cached = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                try
                {
                    if (ImageConversion.LoadImage(cached, File.ReadAllBytes(cacheFile), true))
                    {
                        cached.name = "PKSkin thumbnail " + def.DisplayName;
                        if (e != null && e.Tex != null) UnityEngine.Object.Destroy(e.Tex);
                        Thumbs[path] = new Entry { Tex = cached, Stamp = stamp };
                        CardsFromCache++;
                        CardMs += sw.ElapsedMilliseconds;
                        return MakeSprite(cached);
                    }
                }
                catch { }
                UnityEngine.Object.Destroy(cached);   // unreadable cache file: make it again below
            }

            Texture2D decoded = null;
            try
            {
                decoded = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!ImageConversion.LoadImage(decoded, File.ReadAllBytes(path), false)) return null;

                // Downscale to 256px so a 4K diffuse doesn't sit in memory just for the nursery card.
                // (keeps the picture's shape, so a wide portrait isn't squashed)
                int tw = Math.Min(maxW, decoded.width);
                int th = Math.Max(1, (int)Math.Round((double)tw * decoded.height / decoded.width));
                var rt = RenderTexture.GetTemporary(tw, th, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                var prev = RenderTexture.active;
                Graphics.Blit(decoded, rt);
                RenderTexture.active = rt;
                var small = new Texture2D(tw, th, TextureFormat.RGBA32, false, false);
                small.ReadPixels(new Rect(0, 0, tw, th), 0, 0);
                small.Apply(false, false);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(cacheFile));
                    File.WriteAllBytes(cacheFile, ImageConversion.EncodeToPNG(small));
                }
                catch { }   // no cache this time; the card still works
                small.Apply(false, true);
                CardsMade++;
                CardMs += sw.ElapsedMilliseconds;
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
                small.name = "PKSkin thumbnail " + def.DisplayName;

                if (e != null && e.Tex != null) UnityEngine.Object.Destroy(e.Tex);
                Thumbs[path] = new Entry { Tex = small, Stamp = stamp };
                return MakeSprite(small);
            }
            catch (Exception ex)
            {
                Warn(path, "thumbnail could not be made: " + ex.Message);
                return null;
            }
            finally
            {
                if (decoded != null) UnityEngine.Object.Destroy(decoded);
            }
        }

        // ---------------------------------------------------------------- card cache

        private static readonly HashSet<string> CardsUsed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static int CardsFromCache, CardsMade;
        private static long CardMs;

        private static string CardCacheDir
        {
            get { return Path.Combine(Path.Combine(Path.Combine(Directory.GetCurrentDirectory(), "UserData"), "PKSkinLoader"), Path.Combine("Cache", "Cards")); }
        }

        /// <summary>Name changes whenever the source file changes, so a stale card is never shown.</summary>
        private static string CardCacheFile(string path, DateTime stamp, int maxW)
        {
            string id = path.ToLowerInvariant() + "|" + stamp.Ticks + "|" + maxW + "|v1";
            using (var md5 = System.Security.Cryptography.MD5.Create())
            {
                var h = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(id));
                return Path.Combine(CardCacheDir, BitConverter.ToString(h).Replace("-", "").ToLowerInvariant() + ".png");
            }
        }

        /// <summary>After skins are placed: report, and delete cached cards no skin uses any more.</summary>
        public static void FinishCards()
        {
            if (CardsMade > 0)
                SkinModBase.Log.Msg("[Cards] " + (CardsMade + CardsFromCache) + " skin card(s) in " + CardMs + " ms: "
                    + CardsFromCache + " from cache, " + CardsMade + " made (cached for next time).");
            else if (SkinModBase.Verbose && CardsFromCache > 0)
                SkinModBase.Log.Msg("[Cards] " + CardsFromCache + " skin card(s) from cache in " + CardMs + " ms.");
            CardsMade = CardsFromCache = 0; CardMs = 0;

            try
            {
                if (!Directory.Exists(CardCacheDir)) return;
                foreach (var f in Directory.GetFiles(CardCacheDir, "*.png"))
                    if (!CardsUsed.Contains(Path.GetFileName(f))) { try { File.Delete(f); } catch { } }
            }
            catch { }
        }

        private static Sprite MakeSprite(Texture2D t)
        {
            return Sprite.Create(t, new Rect(0, 0, t.width, t.height), new Vector2(0.5f, 0.5f), 100f);
        }

        private static bool IsPow2(int v) { return v > 0 && (v & (v - 1)) == 0; }

        /// <summary>
        /// Resizes on the CPU (exact for every colour space and for packed normals): box filter for whole-number
        /// shrink factors (4096 → 2048), bilinear otherwise.
        /// </summary>
        public static Color32[] ResampleTo(Color32[] src, int w, int h, int w2, int h2) { return Resample(src, w, h, w2, h2); }

        private static Color32[] Resample(Color32[] src, int w, int h, int w2, int h2)
        {
            var dst = new Color32[w2 * h2];
            if (w % w2 == 0 && h % h2 == 0)
            {
                int fx = w / w2, fy = h / h2, n = fx * fy;
                for (int y = 0; y < h2; y++)
                    for (int x = 0; x < w2; x++)
                    {
                        int r = 0, g = 0, b = 0, a = 0;
                        for (int yy = 0; yy < fy; yy++)
                        {
                            int row = (y * fy + yy) * w + x * fx;
                            for (int xx = 0; xx < fx; xx++)
                            {
                                var p = src[row + xx];
                                r += p.r; g += p.g; b += p.b; a += p.a;
                            }
                        }
                        dst[y * w2 + x] = new Color32((byte)(r / n), (byte)(g / n), (byte)(b / n), (byte)(a / n));
                    }
                return dst;
            }
            for (int y = 0; y < h2; y++)
            {
                float sy = (y + 0.5f) * h / h2 - 0.5f;
                int y0 = Mathf.Clamp((int)Math.Floor(sy), 0, h - 1), y1 = Math.Min(y0 + 1, h - 1);
                float ty = Mathf.Clamp01(sy - y0);
                for (int x = 0; x < w2; x++)
                {
                    float sx = (x + 0.5f) * w / w2 - 0.5f;
                    int x0 = Mathf.Clamp((int)Math.Floor(sx), 0, w - 1), x1 = Math.Min(x0 + 1, w - 1);
                    float tx = Mathf.Clamp01(sx - x0);
                    dst[y * w2 + x] = Color32.Lerp(Color32.Lerp(src[y0 * w + x0], src[y0 * w + x1], tx),
                                                   Color32.Lerp(src[y1 * w + x0], src[y1 * w + x1], tx), ty);
                }
            }
            return dst;
        }

        /// <summary>Approximate video memory of the custom textures currently loaded (with mipmaps).</summary>
        public static long MemoryBytes(out int count)
        {
            long total = 0; count = 0;
            foreach (var e in Cache.Values)
            {
                var t = e.Tex;
                if (t == null) continue;
                count++;
                long px = (long)t.width * t.height;
                string f = t.format.ToString();
                long bytes = f.Contains("DXT1") || f.Contains("BC1") ? px / 2 : f.Contains("DXT5") || f.Contains("BC3") || f.Contains("BC7") ? px : px * 4;
                total += bytes * 4 / 3;   // + mipmaps
            }
            return total;
        }

        private static readonly HashSet<string> Informed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static void Info(string path, string msg)
        {
            string line = SkinScanner.Rel(path) + ": " + msg;
            if (Informed.Add(line)) SkinModBase.Log.Msg(line);
        }

        private static readonly HashSet<string> Warned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static void Warn(string path, string msg)
        {
            string line = SkinScanner.Rel(path) + ": " + msg;
            if (Warned.Add(line)) SkinModBase.Log.Warning(line);
        }

        public static void ResetWarnings() { Warned.Clear(); }
        public static IEnumerable<string> Problems { get { return Warned; } }
    }

    /// <summary>Puts a custom skin's textures on an animal (park, vivarium or nursery preview).</summary>
    /// <summary>
    /// Optional portraits (animal info panel, nursery lists, nests). Missing variants fall back like this:
    /// own sex → any sex → (babies) the adult portrait. A missing mutation portrait (albino, ...) and a skin with
    /// no portrait at all show the base skin's vanilla portrait, as before.
    /// </summary>
    internal static class PortraitStore
    {
        private class Entry { public Sprite Sprite; public DateTime Stamp; }
        private static readonly Dictionary<string, Entry> Cache = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> Failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private const int MaxSize = 1024;

        public static Sprite For(SkinDef def, AnimalSkinType type, Sex sex, AnimalGrowthStage stage)
        {
            var p = def.Source.Portraits;
            if (p.Count == 0) return null;
            string mut = type.ToString().ToLowerInvariant();
            string sx = sex == Sex.Male ? "m" : "f";
            bool infant = stage == AnimalGrowthStage.Infant;   // adolescents use adult portraits, like the game

            string path = null;
            foreach (var st in infant ? new[] { "i", "a" } : new[] { "a" })
            {
                if (p.TryGetValue(st + "|" + sx + "|" + mut, out path)) break;
                if (p.TryGetValue(st + "||" + mut, out path)) break;
                if (p.TryGetValue(st + "|" + (sx == "m" ? "f" : "m") + "|" + mut, out path)) break;
            }
            return path == null ? null : Load(path);
        }

        private static Sprite Load(string path)
        {
            Entry e;
            if (Cache.TryGetValue(path, out e) && e.Sprite != null) return e.Sprite;
            if (Failed.Contains(path)) return null;

            Texture2D tex = null;
            try
            {
                tex = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
                if (!ImageConversion.LoadImage(tex, File.ReadAllBytes(path), false)) throw new Exception("not a readable PNG");
                if (tex.width > MaxSize || tex.height > MaxSize)
                {
                    float k = (float)MaxSize / Math.Max(tex.width, tex.height);
                    int w2 = Math.Max(1, (int)(tex.width * k)), h2 = Math.Max(1, (int)(tex.height * k));
                    var px = TextureStore.ResampleTo(tex.GetPixels32(), tex.width, tex.height, w2, h2);
                    tex.Reinitialize(w2, h2);
                    tex.SetPixels32(px);
                }
                tex.wrapMode = TextureWrapMode.Clamp;
                tex.name = "PKSkin portrait " + Path.GetFileName(path);
                tex.Apply(false, true);
                // same settings as the game's own portraits
                var sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), Vector2.zero, 16f);
                Cache[path] = new Entry { Sprite = sprite, Stamp = File.GetLastWriteTimeUtc(path) };
                return sprite;
            }
            catch (Exception ex)
            {
                if (tex != null) UnityEngine.Object.Destroy(tex);
                Failed.Add(path);
                SkinModBase.Log.Warning(SkinScanner.Rel(path) + ": portrait could not be loaded (" + ex.Message + "). Showing the vanilla portrait.");
                return null;
            }
        }

        /// <summary>F5: forget portraits whose file changed. Old ones aren't destroyed (an open panel may still show one).</summary>
        public static void EvictChanged()
        {
            Failed.Clear();
            foreach (var k in Cache.Keys.ToList())
            {
                DateTime stamp;
                try { stamp = File.Exists(k) ? File.GetLastWriteTimeUtc(k) : DateTime.MinValue; } catch { stamp = DateTime.MinValue; }
                if (stamp != Cache[k].Stamp) Cache.Remove(k);
            }
        }
    }

    internal static class SkinApplier
    {
        private static readonly MaterialPropertyBlock Block = new MaterialPropertyBlock();
        private static readonly int IridStrengthId = Shader.PropertyToID("_IridescenceStrength");
        private static readonly int DroopId = Shader.PropertyToID("_DroopDirection");
        private static readonly int MaturityId = Shader.PropertyToID("_Maturity");
        private static readonly Texture2D[] Tex = new Texture2D[TexSlots.Count];

        /// <summary>Every animal we've seen being textured, so F5 can update them all (includes vivarium animals).</summary>
        public static readonly HashSet<IAnimal> Known = new HashSet<IAnimal>(RefEq<IAnimal>.Instance);
        private static readonly Dictionary<IAnimal, SkinDef> Applied = new Dictionary<IAnimal, SkinDef>(RefEq<IAnimal>.Instance);

        public static NurseryMenuV2 Nursery;   // set by the nursery patches

        /// <summary>Called from the game's own texture-assignment points (see Patches).</summary>
        public static void Apply(IAnimal animal)
        {
            try { ApplyUnsafe(animal); }
            catch (Exception ex) { SkinModBase.ErrorOnce("apply", "Applying a custom skin failed: " + ex); }
        }

        private static int _pruneAt = 512;

        private static void ApplyUnsafe(IAnimal animal)
        {
            if (animal == null) return;
            BaseAnimalData data; int idx;
            try { data = animal.BaseData; idx = animal.CurrentSkinIDX; }
            catch { return; }
            if (data == null) return;

            Known.Add(animal);
            if (Known.Count > _pruneAt) { Prune(); _pruneAt = Math.Max(512, Known.Count * 2); }
            var def = SkinInjector.DefFor(data, idx);
            bool male = IsMale(animal);
            if (def == null)
            {
                Applied.Remove(animal);
                if (!ApplyVanillaTweak(animal, data, idx, male)) Tweaked.Remove(animal);
            }
            else ApplyDef(animal, def, male);

            var own = AnimalTweaks.For(animal);
            if (own != null) ApplyAnimalTweak(animal, own, data, idx, def, male);
        }

        private static readonly HashSet<IAnimal> Tweaked = new HashSet<IAnimal>(RefEq<IAnimal>.Instance);
        private static readonly int LengthId = Shader.PropertyToID("_Length");
        private static readonly int DroopageId = Shader.PropertyToID("_Droopage");

        /// <summary>
        /// Adults that are normally bald get droop strength 0 from the game, so a droop direction does nothing.
        /// When a droop is set, make sure the strength is the fuzzy species' usual 0.5.
        /// </summary>
        public static void EnsureDroopage(Renderer r, MaterialPropertyBlock b)
        {
            var mat = r.sharedMaterial;
            float cur = b.HasFloat(DroopageId) ? b.GetFloat(DroopageId) : (mat != null && mat.HasProperty(DroopageId) ? mat.GetFloat(DroopageId) : 0.5f);
            if (cur < 0.05f) b.SetFloat(DroopageId, 0.5f);
        }

        private static AnimalSkinVariationAsset BaseVariationOf(AnimalSkinData skin, bool isMale)
        {
            if (skin == null) return null;
            return isMale ? (skin.maleVariationData ?? skin.femaleVariationData) : (skin.femaleVariationData ?? skin.maleVariationData);
        }

        /// <summary>Fur length (multiplier on the species' own value) and coverage (adult fur mask), per renderer.</summary>
        public static void SetFur(Renderer r, MaterialPropertyBlock b, string coverage, float? lengthMult, AnimalSkinVariationAsset v)
        {
            if (coverage == "baby" || coverage == "body" || coverage == "own") FurMaterials.AddFurLayer(r);
            var mat = r.sharedMaterial;
            if (mat != null && mat.HasProperty(LengthId))
                b.SetFloat(LengthId, mat.GetFloat(LengthId) * (lengthMult ?? 1f));   // always set, so removing a setting goes back to normal
            if (coverage == null) return;
            Texture2D t = null;
            switch (coverage)
            {
                case "baby": t = FurStore.Baby(v); break;
                case "body": t = FurStore.WholeBody(v); break;
                case "none":
                    t = FurStore.Bald;
                    b.SetTexture(TexSlots.PropId(TexSlots.Fur + TexSlots.InfantOffset), t);
                    break;
            }
            if (t != null || (coverage == "own" && FurMaterials.IsConverted(r.sharedMaterial)))
            {
                if (t != null) b.SetTexture(TexSlots.PropId(TexSlots.Fur), t);
                // bald species have no droop: new fur would stick straight out. Let it lie back unless a droop is set.
                if (!b.HasVector(DroopId) || ((Vector3)b.GetVector(DroopId)).sqrMagnitude < 0.0001f)
                    b.SetVector(DroopId, new Vector4(0f, -0.5f, 1f, 0f));
                EnsureDroopage(r, b);
            }
        }

        /// <summary>Puts back materials the fur-layer experiment replaced (before re-applying settings).</summary>
        public static void RestoreMaterials(IAnimal animal)
        {
            foreach (var r in GetAnimalRenderers(animal)) if (r != null) FurMaterials.Restore(r);
        }

        /// <summary>False only when the animal's material has the fur layer switched off ("TessellationOff").</summary>
        public static bool CanHaveFur(IAnimal animal)
        {
            bool any = false;
            foreach (var r in GetAnimalRenderers(animal))
            {
                if (r == null || !UsesSkinShader(r)) continue;
                var m = r.sharedMaterial;
                if (m == null) continue;
                any = true;
                if (m.name.IndexOf("TessellationOff", StringComparison.OrdinalIgnoreCase) < 0) return true;
            }
            return !any;
        }

        /// <summary>Skin-panel tweaks on a vanilla skin (fur droop, iridescence), set live like skin.json values.</summary>
        public static bool ApplyVanillaTweak(IAnimal animal, BaseAnimalData data, int idx, bool isMale)
        {
            var t = VanillaTweaks.For(data, idx);
            if (t == null) return false;
            var skin = data.skinsData[idx];
            var bv = isMale ? (skin.maleVariationData ?? skin.femaleVariationData) : (skin.femaleVariationData ?? skin.maleVariationData);
            float baseStrength = bv != null && bv.defaultVariationData != null ? bv.defaultVariationData.iridescenceStrength : 1f;

            float? irid = null;
            if (t.Iridescence == false) irid = 0f;
            else if (t.IridescenceStrength.HasValue) irid = t.IridescenceStrength.Value;
            else if (t.Iridescence == true) irid = baseStrength;
            Vector3 adult = t.Droop ?? Vector3.zero;
            Vector3 infant = bv != null ? bv.infantFuzzDroopiness : adult;

            var renderers = GetAnimalRenderers(animal);
            int n = 0;
            foreach (var r in renderers)
            {
                if (r == null || !UsesSkinShader(r)) continue;
                r.GetPropertyBlock(Block);
                if (irid.HasValue) Block.SetFloat(IridStrengthId, irid.Value);
                if (t.Droop.HasValue)
                {
                    float m = Mathf.Clamp01(Block.GetFloat(MaturityId));
                    Block.SetVector(DroopId, Vector3.Lerp(infant, adult, m));
                    EnsureDroopage(r, Block);
                }
                SetFur(r, Block, t.FurCoverage, t.FurLength, bv);
                r.SetPropertyBlock(Block);
                n++;
            }
            if (n > 0) Tweaked.Add(animal);
            return n > 0;
        }

        /// <summary>One animal's own look, on top of its skin's (fur length multiplies the skin's).</summary>
        private static void ApplyAnimalTweak(IAnimal animal, AnimalTweak t, BaseAnimalData data, int idx, SkinDef def, bool isMale)
        {
            float skinLen = 1f;
            AnimalSkinVariationAsset v;
            if (def != null)
            {
                skinLen = def.Source.FurLength ?? 1f;
                v = BaseVariationOf(def.BaseSkin, isMale);
            }
            else
            {
                var vt = VanillaTweaks.For(data, idx);
                if (vt != null && vt.FurLength.HasValue) skinLen = vt.FurLength.Value;
                v = BaseVariationOf(data.skinsData[idx], isMale);
            }
            foreach (var r in GetAnimalRenderers(animal))
            {
                if (r == null || !UsesSkinShader(r)) continue;
                r.GetPropertyBlock(Block);
                if (t.IridescenceStrength.HasValue) Block.SetFloat(IridStrengthId, t.IridescenceStrength.Value);
                if (t.Droop.HasValue)
                {
                    float m = Mathf.Clamp01(Block.GetFloat(MaturityId));
                    Vector3 infant = v != null ? v.infantFuzzDroopiness : t.Droop.Value;
                    Block.SetVector(DroopId, Vector3.Lerp(infant, t.Droop.Value, m));
                    EnsureDroopage(r, Block);
                }
                if (t.FurLength.HasValue || t.FurCoverage != null)
                    SetFur(r, Block, t.FurCoverage, skinLen * (t.FurLength ?? 1f), v);
                r.SetPropertyBlock(Block);
            }
        }

        /// <summary>Panel changed one animal: rebuild just its look.</summary>
        public static void Refresh(IAnimal a)
        {
            try
            {
                RestoreMaterials(a);
                AnimalVisuals.SetAnimalVisuals(a, a.CurrentSkinIDX, a.SkinVariationRuntime, null, true);
            }
            catch { }
            Apply(a);
        }

        /// <summary>Live animals wearing this skin slot (vanilla skins in the skin panel).</summary>
        public static int CountWearing(BaseAnimalData data, int idx)
        {
            Prune();
            int n = 0;
            foreach (var a in Known)
            {
                try { if (a.BaseData == data && a.CurrentSkinIDX == idx) n++; } catch { }
            }
            return n;
        }

        public static int ApplyDef(IAnimal animal, SkinDef def, bool isMale)
        {
            var renderers = GetAnimalRenderers(animal);
            if (renderers.Length == 0) return 0;

            bool any = def.Source.IridescenceStrength.HasValue || def.Source.Iridescence.HasValue
                       || def.Source.FuzzDroopiness.HasValue || def.Source.InfantFuzzDroopiness.HasValue
                       || def.Source.FurLength.HasValue || def.Source.FurCoverage != null;
            for (int s = 0; s < TexSlots.Count; s++)
            {
                Tex[s] = TextureStore.For(def, s, isMale);
                if (Tex[s] != null) any = true;
            }
            if (!any) return 0;

            // skin.json iridescence strength is applied live (the game only copies it into animals when they're created)
            float? irid = def.Source.Iridescence == false ? 0f : def.Source.IridescenceStrength;

            // skin.json fuzz droop, applied live (the game only re-reads it on its own growth updates)
            bool droop = def.Source.FuzzDroopiness.HasValue || def.Source.InfantFuzzDroopiness.HasValue;
            Vector3 adultDroop = Vector3.zero, infantDroop = Vector3.zero;
            if (droop)
            {
                var bv = def.BaseSkin == null ? null : (isMale ? (def.BaseSkin.maleVariationData ?? def.BaseSkin.femaleVariationData)
                                                               : (def.BaseSkin.femaleVariationData ?? def.BaseSkin.maleVariationData));
                adultDroop = def.Source.FuzzDroopiness ?? (bv != null ? bv.adultFuzzDroopiness : Vector3.zero);
                infantDroop = def.Source.InfantFuzzDroopiness ?? (bv != null ? bv.infantFuzzDroopiness : adultDroop);
            }

            int n = 0;
            foreach (var r in renderers)
            {
                if (r == null || !UsesSkinShader(r)) continue;   // skip outlines, markers, effects attached to the animal
                r.GetPropertyBlock(Block);
                for (int s = 0; s < TexSlots.Count; s++)
                    if (Tex[s] != null) Block.SetTexture(TexSlots.PropId(s), Tex[s]);
                if (irid.HasValue) Block.SetFloat(IridStrengthId, irid.Value);
                if (droop)
                {
                    // same blend the game uses: infant droop → adult droop by growth
                    float m = Mathf.Clamp01(Block.GetFloat(MaturityId));
                    Block.SetVector(DroopId, Vector3.Lerp(infantDroop, adultDroop, m));
                    EnsureDroopage(r, Block);
                }
                // "own" = the skin brings its own fur.png (gives bald species a fur layer too)
                SetFur(r, Block, def.Source.FurCoverage ?? (Tex[TexSlots.Fur] != null ? "own" : null), def.Source.FurLength, BaseVariationOf(def.BaseSkin, isMale));
                r.SetPropertyBlock(Block);
                n++;
            }
            Applied[animal] = def;
            return n;
        }

        private static bool UsesSkinShader(Renderer r)
        {
            var mats = r.sharedMaterials;
            for (int i = 0; i < mats.Length; i++)
                if (mats[i] != null && mats[i].HasProperty(TexSlots.PropId(TexSlots.Diffuse))) return true;
            return false;
        }

        private static void Prune()
        {
            var dead = new List<IAnimal>();
            foreach (var a in Known) if (!IsAlive(a)) dead.Add(a);
            foreach (var d in dead) { Known.Remove(d); Applied.Remove(d); }
        }

        /// <summary>Loads (decodes + compresses) a skin's textures ahead of time, e.g. during the loading screen.</summary>
        public static int Preload(BaseAnimalData data, int idx, bool isMale)
        {
            var def = SkinInjector.DefFor(data, idx);
            if (def == null) return 0;
            int before = TextureStore.LoadedThisRound;
            for (int s = 0; s < TexSlots.Count; s++) TextureStore.For(def, s, isMale);
            return TextureStore.LoadedThisRound - before;
        }

        public static bool IsMale(IAnimal animal)
        {
            try
            {
                // Nursery preview models: the virtual animal being designed is the source of truth.
                if (animal.Mode.ToString() == "Preview" && Nursery != null)
                {
                    var va = Nursery.CurrentPreviewVirtualAnimalSafe();
                    if (va != null) return !va.Sex.ToString().Equals("Female", StringComparison.OrdinalIgnoreCase);
                }
                return !animal.Sex.ToString().Equals("Female", StringComparison.OrdinalIgnoreCase);
            }
            catch { return true; }
        }

        /// <summary>
        /// Renderers belonging to one animal. Park animals are MonoBehaviours; vivarium animals (Yi, etc.)
        /// are plain C# objects, so for those we go through IAnimal.Transform.
        /// </summary>
        public static Renderer[] GetAnimalRenderers(IAnimal animal)
        {
            if (animal == null) return new Renderer[0];
            Transform root = null;
            var comp = animal as Component;
            if (comp != null) root = comp.transform;
            if (root == null)
            {
                try { root = animal.Transform; } catch { }
            }
            if (root == null) return new Renderer[0];
            return root.GetComponentsInChildren<Renderer>(true);
        }

        private static bool IsAlive(IAnimal a)
        {
            var uo = a as UnityEngine.Object;
            if (!ReferenceEquals(uo, null)) return uo != null;   // destroyed park animal
            try { return a.Transform != null; } catch { return false; }
        }

        /// <summary>Re-applies to every known animal (F5). Animals whose custom skin disappeared get the game to reload theirs.</summary>
        /// <summary>How many live animals wear each skin (by its source), for the skin panel.</summary>
        public static Dictionary<SkinSource, int> WearingCounts()
        {
            Prune();
            var r = new Dictionary<SkinSource, int>();
            foreach (var kv in Applied)
            {
                if (kv.Value == null || kv.Value.Source == null) continue;
                int n; r.TryGetValue(kv.Value.Source, out n); r[kv.Value.Source] = n + 1;
            }
            return r;
        }

        public static int ReapplyAll()
        {
            int count = 0;
            Prune();
            foreach (var a in new List<IAnimal>(Known))
            {
                SkinDef before;
                Applied.TryGetValue(a, out before);

                BaseAnimalData data; int idx;
                try { data = a.BaseData; idx = a.CurrentSkinIDX; } catch { continue; }
                var now = SkinInjector.DefFor(data, idx);
                try { RestoreMaterials(a); } catch { }

                if (now != null)
                {
                    // Base skin changed (skin panel / skin.json): let the game re-assign the base textures first.
                    if (before != null && (!ReferenceEquals(before.BaseSkin, now.BaseSkin) || before.Source.FurCoverage != now.Source.FurCoverage))
                    {
                        try { AnimalVisuals.SetAnimalVisuals(a, idx, a.SkinVariationRuntime, null, true); } catch { }
                    }
                    if (ApplyDef(a, now, IsMale(a)) > 0) count++;
                }
                else if (before == null)
                {
                    // vanilla skin: refresh if it has (or had) panel tweaks, so removed tweaks go back to normal
                    if (Tweaked.Contains(a) || VanillaTweaks.For(data, idx) != null)
                    {
                        Tweaked.Remove(a);
                        try { AnimalVisuals.SetAnimalVisuals(a, idx, a.SkinVariationRuntime, null, true); count++; } catch { }
                        ApplyVanillaTweak(a, data, idx, IsMale(a));
                    }
                }
                else
                {
                    Applied.Remove(a);
                    try { AnimalVisuals.SetAnimalVisuals(a, idx, a.SkinVariationRuntime, null, true); }
                    catch (Exception ex) { SkinModBase.Log.Warning("Could not restore the vanilla skin on '" + a.Name + "': " + ex.Message); }
                }
            }
            return count;
        }
    }

    /// <summary>Fur masks for "furCoverage": the baby's fuzz, a generated whole-body mask, or none.</summary>
    internal static class FurStore
    {
        private static readonly Dictionary<string, Texture2D> Cache = new Dictionary<string, Texture2D>();
        private static Texture2D _bald;

        /// <summary>F5: generated coats are made (and saved) again the next time they're used.</summary>
        public static void ForgetGenerated()
        {
            foreach (var k in Cache.Keys.Where(k => k.StartsWith("body|")).ToList()) Cache.Remove(k);
        }

        public static Texture2D Bald
        {
            get
            {
                if (_bald != null) return _bald;
                _bald = new Texture2D(4, 4, TextureFormat.RGBA32, false, true) { name = "PKSkin bald" };
                var px = new Color32[16];
                _bald.SetPixels32(px);
                _bald.Apply(false, true);
                return _bald;
            }
        }

        private static string Safe(string n)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) n = n.Replace(c, '_');
            return n;
        }

        private static string Guid(AssetReference r)
        {
            try { return r != null && r.RuntimeKeyIsValid() ? r.AssetGUID : null; } catch { return null; }
        }

        public static Texture2D Baby(AnimalSkinVariationAsset v)
        {
            if (v == null) return null;
            string g = Guid(v.infantFurMap);
            if (g == null) return null;
            Texture2D t;
            if (Cache.TryGetValue("baby|" + g, out t) && t != null) return t;
            try
            {
                // kept loaded for the session (small, and animals keep using it)
                t = Addressables.LoadAssetAsync<Texture2D>(v.infantFurMap.RuntimeKey).WaitForCompletion();
            }
            catch (Exception ex) { SkinModBase.ErrorOnce("fur-baby", "[Fur] baby fuzz could not be loaded: " + ex.Message); t = null; }
            Cache["baby|" + g] = t;
            return t;
        }

        /// <summary>Fuzz everywhere except where the game leaves skin untinted (mouth, claws, teeth) and the eyes.</summary>
        public static Texture2D WholeBody(AnimalSkinVariationAsset v)
        {
            if (v == null) return null;
            string key = "body|" + Guid(v.adultPatternMap) + "|" + Guid(v.adultExtraMap);
            Texture2D t;
            if (Cache.TryGetValue(key, out t) && t != null) return t;
            const int size = 512;
            Color32[] pat = v.adultPatternMap != null ? TextureStore.ReadBase(v.adultPatternMap, size, size, "fur", "pattern") : null;
            Color32[] extra = v.adultExtraMap != null ? TextureStore.ReadBase(v.adultExtraMap, size, size, "fur", "extra map") : null;
            var bare = new bool[size * size];
            for (int i = 0; i < bare.Length; i++)
            {
                bool skinOnly = pat != null && pat[i].r < 12;          // mouth, claws, teeth
                bool eye = extra != null && extra[i].r > 240;          // eyes (read through sRGB, so a bit higher than 230)
                bare[i] = skinOnly || eye;
            }
            // grow the bare areas a little, so eyes and mouths aren't hidden under fur at the edges
            const int grow = 5;
            var bare2 = new bool[bare.Length];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    bool b = false;
                    for (int dy = -grow; dy <= grow && !b; dy++)
                    {
                        int yy = y + dy; if (yy < 0 || yy >= size) continue;
                        for (int dx = -grow; dx <= grow; dx++)
                        {
                            int xx = x + dx; if (xx < 0 || xx >= size) continue;
                            if (bare[yy * size + xx]) { b = true; break; }
                        }
                    }
                    bare2[y * size + x] = b;
                }
            var px = new Color32[size * size];
            for (int i = 0; i < px.Length; i++) px[i] = new Color32((byte)(bare2[i] ? 0 : TextureStore.FurWhite), 255, 255, 255);

            // Save a black-and-white version to paint on (white = fur) plus a guide that shows the skin underneath,
            // so artists can find the horns/spikes and paint them black, then use it as their skin's fur.png.
            try
            {
                string dir = Path.Combine(SkinDumper.DumpDir, "GeneratedFur");
                Directory.CreateDirectory(dir);
                string name = Safe(v.name);
                const int gs = 1024;
                var paint = new Color32[gs * gs];
                var guide = new Color32[gs * gs];
                Color32[] diff = v.adultDiffuseMap != null ? TextureStore.ReadBase(v.adultDiffuseMap, gs, gs, "fur", "diffuse") : null;
                for (int y = 0; y < gs; y++)
                    for (int x = 0; x < gs; x++)
                    {
                        int i = y * gs + x;
                        bool b = bare2[(y * size / gs) * size + (x * size / gs)];
                        paint[i] = b ? new Color32(0, 0, 0, 255) : new Color32(255, 255, 255, 255);
                        var d = diff != null ? diff[i] : new Color32(128, 128, 128, 255);
                        guide[i] = b ? new Color32((byte)(d.r / 3 + 120), (byte)(d.g / 3), (byte)(d.b / 3), 255)   // bare = red
                                     : new Color32(d.r, d.g, d.b, 255);
                    }
                var tmp = new Texture2D(gs, gs, TextureFormat.RGBA32, false, false);
                tmp.SetPixels32(paint); tmp.Apply(false);
                File.WriteAllBytes(Path.Combine(dir, name + "_fur.png"), ImageConversion.EncodeToPNG(tmp));
                tmp.SetPixels32(guide); tmp.Apply(false);
                File.WriteAllBytes(Path.Combine(dir, name + "_fur_guide.png"), ImageConversion.EncodeToPNG(tmp));
                UnityEngine.Object.Destroy(tmp);
                SkinModBase.Log.Msg("[Fur] Whole-body coat for '" + v.name + "' saved to Dump/GeneratedFur/" + name + "_fur.png (+ _fur_guide.png).");
            }
            catch (Exception ex)
            {
                SkinModBase.Log.Warning("[Fur] Could not save the fur map for '" + v.name + "' (" + ex.Message + "). Is the file open in another program?");
            }

            t = new Texture2D(size, size, TextureFormat.RGBA32, true, true) { name = "PKSkin whole-body fur" };
            t.SetPixels32(px);
            t.Apply(true, true);
            Cache[key] = t;
            return t;
        }
    }

    /// <summary>
    /// EXPERIMENT: species whose material has the fur layer switched off (armoured dinos: "TessellationOff") get a copy
    /// of their material with the game's fur feature switched on, the same switches fur species use.
    /// </summary>
    internal static class FurMaterials
    {
        private static readonly Dictionary<Material, Material> Converted = new Dictionary<Material, Material>();
        private static readonly Dictionary<Material, Material> Original = new Dictionary<Material, Material>();

        public static bool IsConverted(Material m) { return m != null && Original.ContainsKey(m); }

        public static bool IsFurOff(Material m)
        {
            return m != null && !Original.ContainsKey(m) && m.IsKeywordEnabled("_FEATURETESSELLATION_OFF");
        }

        private static Material Convert(Material src)
        {
            Material m;
            if (Converted.TryGetValue(src, out m) && m != null) return m;
            m = new Material(src) { name = src.name + " +fur (PKSkin)" };
            m.DisableKeyword("_FEATURETESSELLATION_OFF");
            m.DisableKeyword("_FURMASKING_NONE");
            m.EnableKeyword("_FEATURETESSELLATION_FUR");
            m.EnableKeyword("_FURMASKING_TEXTURE");
            m.EnableKeyword("_FEATURETESSELATION");
            Set(m, "_FEATURETESSELLATION", 1f);
            Set(m, "_FurMasking", 1f);
            Set(m, "_Length", 1f);
            Set(m, "_Droopage", 0.5f);
            Set(m, "_FurDisplaceMinimum", -1f);
            Set(m, "_TessellationEdgeLength", 1f);
            if (m.HasProperty("_FurDispTexScale")) m.SetVector("_FurDispTexScale", new Vector4(6, 6, 0, 0));
            if (m.HasProperty("_DroopDirection")) m.SetVector("_DroopDirection", new Vector4(0, -0.5f, 1, 0));
            Converted[src] = m;
            Original[m] = src;
            SkinModBase.Log.Msg("[Fur] Experimental fur layer added to '" + src.name + "'.");
            return m;
        }

        private static void Set(Material m, string p, float v) { if (m.HasProperty(p)) m.SetFloat(p, v); }

        public static void AddFurLayer(Renderer r)
        {
            var mats = r.sharedMaterials;
            bool changed = false;
            for (int i = 0; i < mats.Length; i++)
                if (IsFurOff(mats[i])) { mats[i] = Convert(mats[i]); changed = true; }
            if (changed) r.sharedMaterials = mats;
        }

        public static void Restore(Renderer r)
        {
            var mats = r.sharedMaterials;
            bool changed = false;
            for (int i = 0; i < mats.Length; i++)
            {
                Material o;
                if (mats[i] != null && Original.TryGetValue(mats[i], out o)) { mats[i] = o; changed = true; }
            }
            if (changed) r.sharedMaterials = mats;
        }
    }

    internal static class NurseryExt
    {
        public static VirtualAnimal CurrentPreviewVirtualAnimalSafe(this NurseryMenuV2 n)
        {
            try { return HarmonyLib.Traverse.Create(n).Field("currentPreviewVirtualAnimal").GetValue<VirtualAnimal>(); }
            catch { return null; }
        }
    }
}
