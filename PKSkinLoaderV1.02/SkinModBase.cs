using HarmonyLib;
using MelonLoader;
using PrehistoricKingdom;
using System;
using System.Collections.Generic;
using UnityEngine;

[assembly: MelonInfo(typeof(PKSkinLoader.SkinModBase), "PK Custom Skin Loader", "4.0.0", "Moshepit / MosheFarran")]
[assembly: MelonGame(null, null)]
[assembly: HarmonyDontPatchAll]   // hooks are applied one by one in Compat.ApplyPatches (survives game updates)

namespace PKSkinLoader
{
    internal static class Config
    {
        public static MelonPreferences_Entry<KeyCode> ReloadKey;
        public static MelonPreferences_Entry<bool> CompressTextures;
        public static MelonPreferences_Entry<bool> VerboseLogging;
        public static MelonPreferences_Entry<int> FirstSkinNumber;
        public static MelonPreferences_Entry<bool> CompactRegistryOnNextLaunch;
        public static MelonPreferences_Entry<bool> PrepareForUninstall;
        public static MelonPreferences_Entry<bool> ShowAuthorInNursery;
        public static MelonPreferences_Entry<int> MaxTextureSize;
        public static MelonPreferences_Entry<KeyCode> PanelKey;
        public static MelonPreferences_Entry<float> PanelScale;
        public static MelonPreferences_Entry<bool> PanelCompact;

        public static void Init()
        {
            var cat = MelonPreferences.CreateCategory("PKSkinLoader", "PK Custom Skin Loader");
            ReloadKey = cat.CreateEntry("ReloadKey", KeyCode.F5, "Reload skins key",
                "Re-reads Mods/Skins and updates every animal in game. Edit a PNG, save, press this.", false, false, null);
            PanelKey = cat.CreateEntry("PanelKey", KeyCode.F6, "Skin panel key",
                "Opens the in-game skin panel: browse, edit and create custom skins.", false, false, null);
            PanelScale = cat.CreateEntry("PanelScale", 1f, "Skin panel size",
                "Size of the skin panel (0.5 - 1.3). Drag the panel's bottom-right corner to change it.", false, false, null);
            PanelCompact = cat.CreateEntry("PanelCompact", false, "Compact skin panel",
                "Show only the selected skin's settings (fits next to the nursery).", false, false, null);
            CompressTextures = cat.CreateEntry("CompressTextures", true, "Compress textures",
                "GPU-compress skins like the game's own textures (about 4x less video memory). Turn off only if you see compression artefacts.", false, false, null);
            VerboseLogging = cat.CreateEntry("VerboseLogging", false, "Verbose logging",
                "Log every skin and slot number while loading.", false, false, null);
            FirstSkinNumber = cat.CreateEntry("FirstSkinNumber", 40, "First skin number",
                "Slot number given to the first NEW skin of a species (older skins keep theirs). Kept above Tyrant's range (15+) so both mods can be installed together. Don't change this once you have saves using custom skins.", false, false, null);
            CompactRegistryOnNextLaunch = cat.CreateEntry("CompactRegistryOnNextLaunch", false, "Compact skin registry (TEST SAVES ONLY)",
                "Next time skins are set up: forget slots of removed skins and renumber the rest without gaps. Animals in existing saves may change skin! Switches itself back off.", false, false, null);
            MaxTextureSize = cat.CreateEntry("MaxTextureSize", 2048, "Max texture size",
                "Skin textures larger than this are scaled down when loading (4096 -> 2048 uses 4x less video memory). Raise to 4096 only if your graphics card has plenty of memory.", false, false, null);
            ShowAuthorInNursery = cat.CreateEntry("ShowAuthorInNursery", true, "Show skin authors",
                "Show the artist next to the skin name, e.g. \"Ember  by Moshepit\" with the credit greyed out (from skin.json or pack.json \"author\").", false, false, null);
            PrepareForUninstall = cat.CreateEntry("PrepareForUninstall", false, "Prepare for uninstall",
                "Before removing this mod: set to true, load each save, wait for the console message, SAVE, quit. Animals wearing custom skins are switched to their vanilla base skin so the game can load them without the mod. Set back to false to keep using the mod.", false, false, null);
        }
    }

    public class SkinModBase : MelonMod
    {
        public static MelonLogger.Instance Log;

        /// <summary>Game version this build was last checked against (shown in the startup compatibility line).</summary>
        public const string VerifiedGameVersion = "1.17.123";
        public static int MainThreadId;

        /// <summary>The game also reads skins from worker threads (parallel jobs); Unity objects may only be touched on the main thread.</summary>
        public static bool OnMainThread { get { return System.Threading.Thread.CurrentThread.ManagedThreadId == MainThreadId; } }

        private static readonly HashSet<string> _onceKeys = new HashSet<string>();
        /// <summary>Logs an error only once per key, so a failing hook can't flood the console every frame.</summary>
        public static void ErrorOnce(string key, string msg)
        {
            lock (_onceKeys) { if (!_onceKeys.Add(key)) return; }
            Log.Error(msg + " (further errors of this kind are hidden)");
        }
        public static bool Verbose { get { return Config.VerboseLogging != null && Config.VerboseLogging.Value; } }

        /// <summary>The release file name. An older copy under another name (e.g. v3's PKSkinLoaderV1.02.dll) is switched off.</summary>
        public const string ReleaseFileName = "PKCustomSkinLoader.dll";

        private static Version VersionOf(MelonBase m)
        {
            try { return new Version(m.Info.Version); } catch { return new Version(0, 0); }
        }

        private static string FileOf(MelonBase m)
        {
            try { return System.IO.Path.GetFileName(m.MelonAssembly.Location); } catch { return "?"; }
        }

        /// <summary>Which of two copies keeps running: newest version, then the release file name, then file name order.</summary>
        private static bool Beats(MelonBase a, MelonBase b)
        {
            int v = VersionOf(a).CompareTo(VersionOf(b));
            if (v != 0) return v > 0;
            bool ar = string.Equals(FileOf(a), ReleaseFileName, StringComparison.OrdinalIgnoreCase);
            bool br = string.Equals(FileOf(b), ReleaseFileName, StringComparison.OrdinalIgnoreCase);
            if (ar != br) return ar;
            return string.CompareOrdinal(FileOf(a), FileOf(b)) < 0;
        }

        /// <summary>
        /// Two copies of the skin loader in Mods (an old file left behind after updating) would both inject skins and patch
        /// the game. Only one may run: the other is switched off and the console says which file to delete.
        /// Returns false when this copy is the one switched off.
        /// </summary>
        private bool ResolveDuplicates()
        {
            foreach (var m in new List<MelonBase>(MelonBase.RegisteredMelons))
            {
                if (ReferenceEquals(m, this) || m.Info == null || m.Info.Name != Info.Name) continue;
                if (Beats(this, m))
                {
                    LoggerInstance.Error("==============================================================");
                    LoggerInstance.Error("Two copies of PK Custom Skin Loader are installed. Switched off the old one: Mods/" + FileOf(m) + " (v" + m.Info.Version + ").");
                    LoggerInstance.Error("Please DELETE Mods/" + FileOf(m) + " - keep only Mods/" + FileOf(this) + ".");
                    LoggerInstance.Error("==============================================================");
                    try { m.Unregister("Replaced by " + FileOf(this) + " v" + Info.Version, false); } catch { }
                }
                else
                {
                    LoggerInstance.Error("This copy (Mods/" + FileOf(this) + ", v" + Info.Version + ") is switched off: Mods/" + FileOf(m)
                        + " (v" + m.Info.Version + ") is newer. Please DELETE Mods/" + FileOf(this) + ".");
                    try { Unregister("Duplicate of " + FileOf(m), false); } catch { }
                    return false;
                }
            }
            return true;
        }

        public override void OnLateInitializeMelon()
        {
            ResolveDuplicates();   // copies that loaded after this one
        }

        public override void OnInitializeMelon()
        {
            if (!ResolveDuplicates()) return;
            Log = LoggerInstance;
            MainThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
            Config.Init();
            Compat.ApplyPatches(HarmonyInstance ?? new HarmonyLib.Harmony("PKSkinLoader"));
            SkinDumper.Init();
            AnimalTweaks.Init();

            SkinScanner.Scan();
            ReportScan();
            Log.Msg("Ready. Skins folder: Mods/Skins  |  " + Config.ReloadKey.Value + " = reload skins  |  " + Config.PanelKey.Value + " = skin panel.");
            if (Config.PrepareForUninstall.Value)
                Log.Warning("PrepareForUninstall is ON: load a save, wait for the [Uninstall] message, then save. Custom skins are hidden in the nursery.");
        }

        public override void OnUpdate()
        {
            // Backup trigger: set skins up as soon as the animal database exists (normally done right before a save loads).
            if (!SkinInjector.Injected && Time.frameCount % 60 == 0) SkinInjector.EnsureInjected("game started", false);
            SkinDumper.OnUpdate();
            if (Config.ReloadKey != null && Input.GetKeyDown(Config.ReloadKey.Value)) Reload();
            SkinPanel.OnUpdate();
        }

        private static void ReportScan()
        {
            int folder = 0, legacy = 0, disabled = 0;
            foreach (var s in SkinScanner.Sources)
            {
                if (s.Disabled) disabled++;
                else if (s.Legacy) legacy++;
                else folder++;
            }
            Log.Msg("Found " + folder + " skin folder(s), " + legacy + " flat-file skin(s)" + (disabled > 0 ? ", " + disabled + " disabled" : "") + ".");
            foreach (var w in SkinScanner.Warnings) Log.Warning(w);
        }

        /// <summary>F5: re-scan the folder, reload changed textures, re-place skins, repaint every animal.</summary>
        public static void Reload()
        {
            try
            {
                var t0 = DateTime.Now;
                TextureStore.LoadedThisRound = 0;
                TextureStore.ResetWarnings();
                TextureStore.EvictChanged();
                PortraitStore.EvictChanged();
                FurStore.ForgetGenerated();
                SkinScanner.Scan();
                ReportScan();
                VanillaTweaks.Load();

                SkinInjector.MarkForRebuild();
                SkinInjector.EnsureInjected("reload", true);

                int animals = SkinApplier.ReapplyAll();
                TextureStore.DestroyRetired();
                if (SkinApplier.Nursery != null) NurseryUI.Refresh(SkinApplier.Nursery);

                int texCount;
                long bytes = TextureStore.MemoryBytes(out texCount);
                Log.Msg("Reloaded in " + (int)(DateTime.Now - t0).TotalMilliseconds + " ms: " + TextureStore.LoadedThisRound
                    + " texture(s) (re)loaded, " + animals + " animal(s) updated. Custom skins use ~" + (bytes / (1024 * 1024))
                    + " MB of video memory (" + texCount + " textures, max size " + Config.MaxTextureSize.Value + ").");
                SkinPanel.OnReloaded();
            }
            catch (Exception ex)
            {
                Log.Error("Reload failed: " + ex);
            }
        }

        // =====================================================================================
        //  v3 naming rules - unchanged, so flat-file skins keep their names and slot numbers.
        // =====================================================================================

        public static bool CheckIsFeathered(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string lower = name.ToLowerInvariant();
            return lower.Contains("feathered") || lower.Contains("feathers") || lower.Contains("fluff");
        }

        public static string CleanName(string rawName)
        {
            if (string.IsNullOrEmpty(rawName)) return "";

            string clean = rawName;
            while (clean.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                clean = clean.Substring(0, clean.Length - 4);

            int pipeIdx = clean.IndexOf('|');
            if (pipeIdx >= 0) clean = clean.Substring(0, pipeIdx);

            int parenIdx = clean.IndexOf('(');
            if (parenIdx >= 0) clean = clean.Substring(0, parenIdx);

            int dotIdx = clean.IndexOf('.');
            if (dotIdx >= 0) clean = clean.Substring(0, dotIdx);

            if (clean.StartsWith("t_", StringComparison.OrdinalIgnoreCase)) clean = clean.Substring(2);
            if (clean.StartsWith("tex_", StringComparison.OrdinalIgnoreCase)) clean = clean.Substring(4);

            if (clean.Contains("(Clone)")) clean = clean.Replace("(Clone)", "");

            return clean.Trim();
        }

        public static void ParseGender(string fullName, out string baseNameWithoutGender, out bool? isMale)
        {
            string lower = fullName.ToLowerInvariant();

            if (lower.EndsWith("_male") || lower.EndsWith(" male"))
            {
                baseNameWithoutGender = fullName.Substring(0, fullName.Length - 5).Trim();
                isMale = true;
            }
            else if (lower.EndsWith("_female") || lower.EndsWith(" female"))
            {
                baseNameWithoutGender = fullName.Substring(0, fullName.Length - 7).Trim();
                isMale = false;
            }
            else if (lower.EndsWith("_m") || lower.EndsWith(" m"))
            {
                baseNameWithoutGender = fullName.Substring(0, fullName.Length - 2).Trim();
                isMale = true;
            }
            else if (lower.EndsWith("_f") || lower.EndsWith(" f"))
            {
                baseNameWithoutGender = fullName.Substring(0, fullName.Length - 2).Trim();
                isMale = false;
            }
            else
            {
                baseNameWithoutGender = fullName;
                isMale = null;
            }
        }

        public static bool IsSkinMatchingAnimal(string rawFilename, BaseAnimalData animalData, IEnumerable<BaseAnimalData> allAnimals)
        {
            if (animalData == null || string.IsNullOrEmpty(rawFilename)) return false;

            string normFile = rawFilename.Replace("_", "").Replace(" ", "").ToLowerInvariant();

            BaseAnimalData bestSpeciesMatch = null;
            int longestSpeciesMatchLength = 0;

            foreach (var animal in allAnimals)
            {
                if (animal == null || string.IsNullOrEmpty(animal.name)) continue;

                string fullName = animal.name.Trim();
                string normFull = fullName.Replace("_", "").Replace(" ", "").ToLowerInvariant();

                if (normFile.StartsWith(normFull) && normFull.Length > longestSpeciesMatchLength)
                {
                    bestSpeciesMatch = animal;
                    longestSpeciesMatchLength = normFull.Length;
                }
            }

            if (bestSpeciesMatch != null)
            {
                return animalData == bestSpeciesMatch;
            }

            string targetFullName = animalData.name.Trim();
            string[] parts = targetFullName.Split(new char[] { ' ', '_' }, StringSplitOptions.RemoveEmptyEntries);
            string genus = parts.Length > 0 ? parts[0].ToLowerInvariant() : targetFullName.Replace("_", "").Replace(" ", "").ToLowerInvariant();

            return normFile.StartsWith(genus);
        }

        public static string ExtractDisplayName(string rawFilename, BaseAnimalData animalData)
        {
            if (animalData == null || string.IsNullOrEmpty(rawFilename)) return "Custom";

            string clean = rawFilename.Replace('_', ' ');

            if (clean.StartsWith("Yi Qi", StringComparison.OrdinalIgnoreCase))
                clean = clean.Substring(5).Trim();
            else if (clean.StartsWith("Yi", StringComparison.OrdinalIgnoreCase))
                clean = clean.Substring(2).Trim();
            else
            {
                string[] parts = animalData.name.Trim().Split(new char[] { ' ', '_' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var part in parts)
                {
                    if (string.IsNullOrEmpty(part)) continue;
                    int idx = clean.IndexOf(part, StringComparison.OrdinalIgnoreCase);
                    if (idx >= 0)
                    {
                        clean = clean.Remove(idx, part.Length);
                    }
                }
            }

            clean = clean.Trim();
            if (string.IsNullOrEmpty(clean)) return "Custom";

            System.Globalization.TextInfo ti = System.Globalization.CultureInfo.CurrentCulture.TextInfo;
            return ti.ToTitleCase(clean);
        }

        /// <summary>v3's base-skin choice, limited to the vanilla skins: first feathered skin for feathered customs, else first scaled one.</summary>
        public static AnimalSkinData GetOptimalBaseSkin(BaseAnimalData animalData, int vanillaCount, bool wantsFeathers)
        {
            if (animalData == null || animalData.skinsData == null || animalData.skinsData.Count == 0) return null;
            int n = Math.Min(vanillaCount, animalData.skinsData.Count);
            for (int i = 0; i < n; i++)
            {
                var skin = animalData.skinsData[i];
                if (skin == null) continue;
                string lower = (skin.skinName ?? "").ToLowerInvariant();
                bool isFeathered = skin.skinInteguments == AnimalSkinData.SkinIntegument.Feathers
                                   || lower.Contains("feather") || lower.Contains("brumal");
                if (wantsFeathers == isFeathered) return skin;
            }
            return animalData.skinsData[0];
        }
    }
}
