using HarmonyLib;
using PrehistoricKingdom;
using PrehistoricKingdom.Modular;
using PrehistoricKingdom.SaveData.Current;
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace PKSkinLoader
{
    // =====================================================================================
    //  1. Inject BEFORE the game reads saved skin numbers.
    //     (v3 injected too late: Animal.Load → AnimalGenetics.Load → get_SkinData threw
    //      "index out of range", leaving a half-loaded animal - the "corrupted Tarbosaurus".)
    // =====================================================================================

    [HarmonyPatch(typeof(AnimalManager), "Load")]
    internal static class Patch_AnimalManager_Load
    {
        private static void Prefix() { SkinInjector.EnsureInjected("park animals loading", true); }

        /// <summary>Animals now exist: decode their custom textures while the loading screen is still up.</summary>
        private static void Postfix()
        {
            UninstallPrep.OnParkLoaded();
            try
            {
                var sw = Stopwatch.StartNew();
                int textures = 0, custom = 0, total = 0;
                foreach (var a in UnityEngine.Object.FindObjectsOfType<Animal>(true))   // true: animals may still be inactive here
                {
                    total++;
                    if (SkinInjector.DefFor(a.BaseData, a.CurrentSkinIDX) == null) continue;
                    custom++;
                    textures += SkinApplier.Preload(a.BaseData, a.CurrentSkinIDX, !a.Sex.ToString().Equals("Female", StringComparison.OrdinalIgnoreCase));
                }
                int texCount;
                long bytes = TextureStore.MemoryBytes(out texCount);
                SkinModBase.Log.Msg("[Preload] Park: " + custom + " of " + total + " animal(s) wear custom skins; " + textures + " new texture(s) loaded in "
                    + sw.ElapsedMilliseconds + " ms. Vivarium: " + Patch_VivariumAnimal_Load.Preloaded + " texture(s). Custom skins use ~"
                    + (bytes / (1024 * 1024)) + " MB of video memory.");
                Patch_VivariumAnimal_Load.Preloaded = 0;
            }
            catch (Exception ex) { SkinModBase.ErrorOnce("preload-park", "[Preload] " + ex.Message); }
        }
    }

    [HarmonyPatch(typeof(VivariumAnimal), "Load")]
    internal static class Patch_VivariumAnimal_Load
    {
        private static Dictionary<string, BaseAnimalData> _byId;
        public static int Preloaded;

        private static void Prefix(SaveDataVivariumAnimal data)
        {
            SkinInjector.EnsureInjected("vivarium animals loading", true);
            try
            {
                if (data == null || string.IsNullOrEmpty(data.SpeciesID)) return;
                if (_byId == null || !_byId.ContainsKey(data.SpeciesID))
                {
                    _byId = new Dictionary<string, BaseAnimalData>();
                    foreach (var sp in SkinInjector.AllSpecies())
                        if (!string.IsNullOrEmpty(sp.speciesID)) _byId[sp.speciesID] = sp;
                }
                BaseAnimalData species;
                if (_byId.TryGetValue(data.SpeciesID, out species))
                    Preloaded += SkinApplier.Preload(species, data.CurrentSkin, data.Sex == 0);   // Sex: 0 = Male, 1 = Female
            }
            catch (Exception ex) { SkinModBase.ErrorOnce("preload-viv", "[Preload] " + ex.Message); }
        }
    }

    // =====================================================================================
    //  2. Safety net: a saved number that doesn't exist (registry deleted, other mod removed...)
    //     shows the first vanilla skin instead of breaking the animal.
    // =====================================================================================

    /// <summary>
    /// NOTE: get_SkinData is also called from the game's parallel worker jobs, so this must be cheap and
    /// must never create Unity objects off the main thread (EnsureInjected checks that itself).
    /// </summary>
    internal static class SkinIndexGuard
    {
        private static readonly HashSet<string> Warned = new HashSet<string>();

        public static bool Check(BaseAnimalData data, int idx, ref AnimalSkinData result)
        {
            if (data == null) return true;
            var list = data.skinsData;
            if (list == null || list.Count == 0) return true;
            if (idx >= 0 && idx < list.Count) return true;

            if (!SkinInjector.Injected && SkinModBase.OnMainThread)
            {
                SkinInjector.EnsureInjected("early skin lookup", true);
                if (idx >= 0 && idx < list.Count) return true;
            }

            result = list[0];
            bool first;
            lock (Warned) first = Warned.Add(data.name + "#" + idx);
            if (first)
                SkinModBase.Log.Warning(data.name + ": an animal uses skin slot " + idx + ", which doesn't exist. Showing '" + result.skinName + "' instead.");
            return false;
        }
    }

    [HarmonyPatch(typeof(Animal), "SkinData", MethodType.Getter)]
    internal static class Patch_Animal_SkinData
    {
        private static bool Prefix(Animal __instance, ref AnimalSkinData __result)
        {
            try { return SkinIndexGuard.Check(__instance.BaseData, __instance.CurrentSkinIDX, ref __result); }
            catch { return true; }
        }
    }

    [HarmonyPatch(typeof(VivariumAnimal), "SkinData", MethodType.Getter)]
    internal static class Patch_VivariumAnimal_SkinData
    {
        private static bool Prefix(VivariumAnimal __instance, ref AnimalSkinData __result)
        {
            try { return SkinIndexGuard.Check(__instance.BaseData, __instance.CurrentSkinIDX, ref __result); }
            catch { return true; }
        }
    }

    // =====================================================================================
    //  2b. Random skin picks must never land on a hidden stand-in.
    //      The game picks from the whole skinsData list in two places (found by scanning its code):
    //      the nursery's "random skin" option and randomly generated vivarium animals.
    //      (Breeding copies a parent's skin, so it's fine.)
    // =====================================================================================

    internal static class SkinRandom
    {
        [ThreadStatic] public static bool InRandomVivarium;

        public static int PickVisible(BaseAnimalData data, int current)
        {
            if (data == null || data.skinsData == null || data.skinsData.Count == 0) return current;
            var list = data.skinsData;
            if (current >= 0 && current < list.Count && !SkinInjector.IsStandIn(list[current])) return current;

            var ok = new List<int>();
            for (int i = 0; i < list.Count; i++)
                if (list[i] != null && !SkinInjector.IsStandIn(list[i])) ok.Add(i);
            return ok.Count == 0 ? 0 : ok[UnityEngine.Random.Range(0, ok.Count)];
        }
    }

    [HarmonyPatch(typeof(NurseryMenuV2), "GenerateNewAnimal")]
    internal static class Patch_Nursery_GenerateNewAnimal
    {
        private static void Postfix(NurseryMenuV2 __instance, VirtualAnimal __result)
        {
            try
            {
                if (__result == null || __result.OwnAD == null) return;
                int picked = SkinRandom.PickVisible(__result.OwnAD, __result.skinIdx);
                if (picked == __result.skinIdx) return;
                __result.skinIdx = picked;
                // its colour variation was rolled for the old skin - roll again for the new one
                AccessTools.Method(typeof(NurseryMenuV2), "GenerateNewSkinVariation").Invoke(__instance, new object[] { __result });
            }
            catch (Exception ex) { SkinModBase.ErrorOnce("rnd-nursery", "[Random skin] " + ex.Message); }
        }
    }

    [HarmonyPatch(typeof(ModularModuleVivarium), "CreateRandomVivariumAnimalAsync")]
    internal static class Patch_RandomVivariumAnimal
    {
        private static void Prefix() { SkinRandom.InRandomVivarium = true; }
        private static Exception Finalizer(Exception __exception) { SkinRandom.InRandomVivarium = false; return __exception; }
    }

    [HarmonyPatch(typeof(VivariumAnimal), "CurrentSkinIDX", MethodType.Setter)]
    internal static class Patch_VivariumAnimal_SetSkin
    {
        private static void Prefix(VivariumAnimal __instance, ref int value)
        {
            if (!SkinRandom.InRandomVivarium) return;   // only while the game rolls a random vivarium animal
            try { value = SkinRandom.PickVisible(__instance.BaseData, value); } catch { }
        }
    }

    // =====================================================================================
    //  3. Apply our textures right after the game assigns its own (it uses MaterialPropertyBlocks too).
    // =====================================================================================

    [HarmonyPatch(typeof(AnimalVisuals), "AssignTexturesAndSetMaterialVariation")]
    internal static class Patch_AssignTextures
    {
        private static void Postfix(IAnimal animal) { SkinApplier.Apply(animal); }
    }

    [HarmonyPatch(typeof(AnimalVisuals), "SetAnimalMaterialVariation")]
    internal static class Patch_SetMaterialVariation
    {
        private static void Postfix(IAnimal animal) { SkinApplier.Apply(animal); }
    }

    [HarmonyPatch(typeof(AnimalVisuals), nameof(AnimalVisuals.SetAnimalVisuals))]
    internal static class Patch_SetAnimalVisuals
    {
        private static void Postfix(IAnimal animal) { SkinApplier.Apply(animal); }
    }

    [HarmonyPatch(typeof(AnimalVisuals), nameof(AnimalVisuals.LoadAnimalSkinsAndSetMaterial))]
    internal static class Patch_LoadSkinsAndSetMaterial
    {
        private static void Prefix(IAnimal animal, ref Action mainCallback)
        {
            Action original = mainCallback;
            mainCallback = () =>
            {
                try { if (original != null) original(); }
                catch (Exception ex) { SkinModBase.Log.Error("[Material callback] " + ex.Message); }
                SkinApplier.Apply(animal);
            };
        }
    }

    // =====================================================================================
    //  3b. Portraits (info panel, nursery lists, nests). One static method serves all of them.
    // =====================================================================================

    [HarmonyPatch(typeof(BaseAnimalData), nameof(BaseAnimalData.GetPortrait))]
    internal static class Patch_GetPortrait
    {
        private static bool Prefix(AnimalSkinData skinData, AnimalSkinType skinType, Sex sex, AnimalGrowthStage growthStage, ref UnityEngine.Sprite __result)
        {
            try
            {
                if (!SkinInjector.IsOurs(skinData) || !SkinModBase.OnMainThread) return true;
                var def = SkinInjector.DefFor(skinData);
                if (def == null) return true;
                var sprite = PortraitStore.For(def, skinType, sex, growthStage);
                if (sprite == null) return true;   // no portrait for this case: the base skin's vanilla one
                __result = sprite;
                return false;
            }
            catch (Exception ex) { SkinModBase.ErrorOnce("portrait", "[Portrait] " + ex.Message); return true; }
        }
    }

    // =====================================================================================
    //  4. Nursery
    // =====================================================================================

    [HarmonyPatch(typeof(NurseryMenuV2), "SetSelectionMenu")]
    internal static class Patch_Nursery_SetSelectionMenu
    {
        private static void Prefix() { SkinInjector.EnsureInjected("nursery opened", false); }
        private static void Postfix(NurseryMenuV2 __instance) { NurseryUI.Refresh(__instance); }
    }

    [HarmonyPatch(typeof(NurseryMenuV2), "UpdateAnimalPreview")]
    internal static class Patch_Nursery_UpdateAnimalPreview
    {
        private static void Postfix(NurseryMenuV2 __instance)
        {
            NurseryUI.Refresh(__instance);
            try
            {
                var va = __instance.CurrentPreviewVirtualAnimalSafe();
                var comp = UnityEngine.Object.FindObjectOfType<AnimalPreviewComponent>();
                if (va != null && comp != null) NurseryUI.PaintPreview(comp, va);
                if (va != null) SkinPanel.FollowNursery(va.OwnAD, va.skinIdx);
            }
            catch { }
        }
    }

    /// <summary>
    /// The game re-writes fur droop, fur length and iridescence on every growth tick (while not paused), from the
    /// skin's original settings. Put the skin's / panel's values back right after.
    /// </summary>
    [HarmonyPatch]
    internal static class Patch_UpdateGrowth
    {
        private static System.Reflection.MethodBase TargetMethod()
        {
            var t = AccessTools.TypeByName("PrehistoricKingdom.AnimalGrowthManager") ?? AccessTools.TypeByName("AnimalGrowthManager");
            return AccessTools.Method(t, "UpdateGrowth", new Type[] { typeof(AnimalGrowthFlags) });
        }

        private static void Postfix(Animal ___animal)
        {
            if (___animal != null) SkinApplier.Apply(___animal);
        }
    }

    /// <summary>Per-animal looks belong to a park: read them when a save starts loading.</summary>
    [HarmonyPatch]
    internal static class Patch_LoadGame
    {
        private static System.Reflection.MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(SaveLoadManager), "LoadGameDelayed");
        }
        private static void Prefix(string path, bool newGame)
        {
            try { AnimalTweaks.OnLoading(path, newGame); }
            catch (Exception ex) { SkinModBase.ErrorOnce("animals-load", "[Animals] " + ex.Message); }
        }
    }

    [HarmonyPatch(typeof(AnimalPreviewComponent), nameof(AnimalPreviewComponent.CreateAnimalPreview))]
    internal static class Patch_CreateAnimalPreview
    {
        private static void Postfix(AnimalPreviewComponent __instance, VirtualAnimal animal)
        {
            NurseryUI.PaintPreview(__instance, animal);
        }
    }
}
