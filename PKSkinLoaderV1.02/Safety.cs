using HarmonyLib;
using MelonLoader;
using PrehistoricKingdom;
using PrehistoricKingdom.Modular;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace PKSkinLoader
{
    /// <summary>
    /// Game-update resilience. Every hook is applied on its own (instead of MelonLoader's all-or-nothing patching),
    /// so when a game update renames something, only that feature switches off - with a clear message - and the
    /// rest keeps protecting saves. Also remembers the game version to say when the game was updated.
    /// </summary>
    internal static class Compat
    {
        /// <summary>Hooks that keep saves safe. If one fails, the console says so loudly.</summary>
        private static readonly Dictionary<string, string> Critical = new Dictionary<string, string>
        {
            { "Patch_AnimalManager_Load", "set up custom skins before park animals load" },
            { "Patch_VivariumAnimal_Load", "set up custom skins before vivarium animals load" },
            { "Patch_Animal_SkinData", "fall back to a vanilla skin when a saved skin slot is missing (park)" },
            { "Patch_VivariumAnimal_SkinData", "fall back to a vanilla skin when a saved skin slot is missing (vivarium)" },
        };

        public static readonly List<string> Failed = new List<string>();
        public static bool CriticalFailed;

        public static void ApplyPatches(HarmonyLib.Harmony harmony)
        {
            int ok = 0;
            var types = typeof(Compat).Assembly.GetTypes()
                .Where(t => t.GetCustomAttributes(typeof(HarmonyPatch), false).Length > 0)
                .OrderBy(t => t.Name);

            foreach (var t in types)
            {
                try
                {
                    harmony.CreateClassProcessor(t).Patch();
                    ok++;
                }
                catch (Exception ex)
                {
                    string why;
                    bool critical = Critical.TryGetValue(t.Name, out why);
                    if (critical) CriticalFailed = true;
                    Failed.Add(t.Name);
                    string msg = "[Compat] Hook '" + t.Name + "' could not be applied (" + ex.GetBaseException().Message + ").";
                    if (critical) SkinModBase.Log.Error(msg + " It is needed to " + why + ".");
                    else SkinModBase.Log.Warning(msg + " That feature is off; everything else still works.");
                }
            }

            string game = Application.version;
            string last = ReadLastVersion();
            if (last != null && last != game)
                SkinModBase.Log.Msg("[Compat] Game updated from " + last + " to " + game + ".");
            WriteLastVersion(game);

            if (Failed.Count == 0)
                SkinModBase.Log.Msg("[Compat] All " + ok + " hooks OK on game " + game + " (last verified by the mod author on " + SkinModBase.VerifiedGameVersion + ").");
            else
                SkinModBase.Log.Warning("[Compat] " + ok + " hooks OK, " + Failed.Count + " failed on game " + game
                    + " (mod verified on " + SkinModBase.VerifiedGameVersion + "). "
                    + (CriticalFailed
                        ? "Save protection is reduced - avoid saving over important parks until the mod is updated."
                        : "Saves are still protected."));
        }

        private static string VersionFile
        {
            get { return Path.Combine(Path.Combine(Path.Combine(Directory.GetCurrentDirectory(), "UserData"), "PKSkinLoader"), "last-game-version.txt"); }
        }

        private static string ReadLastVersion()
        {
            try { return File.Exists(VersionFile) ? File.ReadAllText(VersionFile).Trim() : null; } catch { return null; }
        }

        private static void WriteLastVersion(string v)
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(VersionFile)); File.WriteAllText(VersionFile, v); } catch { }
        }
    }

    /// <summary>
    /// "Prepare for uninstall": without this mod the game can't load an animal whose saved skin slot doesn't exist
    /// (the same crash as the old corrupted-T. rex bug). With PrepareForUninstall on, every animal wearing a custom
    /// skin is switched to that skin's vanilla base after a save loads. The player then saves and removes the mod.
    /// </summary>
    internal static class UninstallPrep
    {
        private static bool _ran;

        public static bool Active { get { return Config.PrepareForUninstall != null && Config.PrepareForUninstall.Value; } }

        public static void OnParkLoaded()
        {
            if (!Active || _ran) return;
            _ran = true;
            MelonCoroutines.Start(Run());
        }

        /// <summary>The vanilla slot an animal on this slot should move to (its base skin, or the first skin).</summary>
        private static int VanillaFor(BaseAnimalData data, int idx)
        {
            if (data == null || data.skinsData == null || idx < 0 || idx >= data.skinsData.Count) return 0;
            var skin = data.skinsData[idx];
            if (!SkinInjector.IsOurs(skin)) return idx;   // already vanilla (or another mod's)
            var def = SkinInjector.DefFor(data, idx);
            if (def != null && def.BaseSkin != null)
            {
                int b = data.skinsData.IndexOf(def.BaseSkin);
                if (b >= 0) return b;
            }
            return 0;
        }

        private static IEnumerator Run()
        {
            yield return new WaitForSeconds(3f);   // let vivariums and visuals finish loading

            int park = 0, viv = 0;
            foreach (var a in UnityEngine.Object.FindObjectsOfType<Animal>(true))
            {
                try { if (Switch(a)) park++; }
                catch (Exception ex) { SkinModBase.ErrorOnce("uninst-park", "[Uninstall] " + ex.Message); }
            }
            foreach (var module in AllVivariums())
            {
                List<VivariumAnimal> list = null;
                try { list = module.Animals; } catch { }
                if (list == null) continue;
                foreach (var va in list.ToArray())
                {
                    try { if (Switch(va)) viv++; }
                    catch (Exception ex) { SkinModBase.ErrorOnce("uninst-viv", "[Uninstall] " + ex.Message); }
                }
            }

            SkinModBase.Log.Warning("==============================================================");
            SkinModBase.Log.Warning("[Uninstall] " + park + " park and " + viv + " vivarium animal(s) switched to their vanilla base skin.");
            SkinModBase.Log.Warning("[Uninstall] Now SAVE the game. Then you can remove the mod safely.");
            SkinModBase.Log.Warning("[Uninstall] Not converted: eggs/embryos still incubating and the nursery queue -");
            SkinModBase.Log.Warning("[Uninstall] let eggs hatch (or cancel them) and clear the queue before uninstalling.");
            SkinModBase.Log.Warning("==============================================================");
        }

        /// <summary>VivariumManager.AllVivariums, read by reflection (its list type comes from the game's own library).</summary>
        private static List<ModularModuleVivarium> AllVivariums()
        {
            var result = new List<ModularModuleVivarium>();
            try
            {
                object list = AccessTools.Field(typeof(VivariumManager), "AllVivariums").GetValue(null);
                if (list == null) return result;
                var e = list as IEnumerable;
                if (e != null)
                {
                    foreach (var o in e) { var m = o as ModularModuleVivarium; if (m != null) result.Add(m); }
                    return result;
                }
                var t = list.GetType();
                int count = (int)AccessTools.Property(t, "Count").GetValue(list, null);
                var item = AccessTools.Property(t, "Item") ?? t.GetProperties().FirstOrDefault(p => p.GetIndexParameters().Length == 1);
                for (int i = 0; i < count && item != null; i++)
                {
                    var m = item.GetValue(list, new object[] { i }) as ModularModuleVivarium;
                    if (m != null) result.Add(m);
                }
            }
            catch (Exception ex) { SkinModBase.ErrorOnce("uninst-vivlist", "[Uninstall] Could not list vivariums: " + ex.Message); }
            return result;
        }

        private static bool Switch(IAnimal a)
        {
            var data = a.BaseData;
            int idx = a.CurrentSkinIDX;
            int target = VanillaFor(data, idx);
            if (target == idx) return false;

            bool visible = false;
            try { visible = a.Transform != null; } catch { }
            if (visible)
            {
                try { AnimalVisuals.SetAnimalVisuals(a, target, a.SkinVariationRuntime, null, true); }
                catch { a.CurrentSkinIDX = target; }
            }
            else a.CurrentSkinIDX = target;
            return true;
        }

        /// <summary>While preparing for uninstall, custom skins aren't offered in the nursery.</summary>
        public static bool HideInNursery(AnimalSkinData skin)
        {
            return Active && SkinInjector.IsOurs(skin);
        }
    }
}
