using HarmonyLib;
using PrehistoricKingdom;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace PKSkinLoader
{
    internal sealed class RefEq<T> : IEqualityComparer<T> where T : class
    {
        public static readonly RefEq<T> Instance = new RefEq<T>();
        public bool Equals(T x, T y) { return ReferenceEquals(x, y); }
        public int GetHashCode(T obj) { return RuntimeHelpers.GetHashCode(obj); }
    }

    /// <summary>
    /// Optional interop with the Tyrant framework (github.com/ViraLCyclopes/Tyrant), looked up by reflection so this
    /// mod has no dependency on it. Tyrant appends its skins right after the vanilla ones at numbers 15+, filling the
    /// gap with hidden "stand-ins". We only ever replace Tyrant stand-ins, never its real skins.
    /// </summary>
    internal static class TyrantCompat
    {
        private static bool _looked;
        private static MethodInfo _isStandIn, _isAdded;
        private static FieldInfo _done;

        private static void Look()
        {
            if (_looked) return;
            _looked = true;
            try
            {
                // Look only in Tyrant's own assembly (AccessTools.TypeByName logs a scary warning when it's absent).
                Assembly asm = null;
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                    if (a.GetName().Name == "Tyrant.Framework") { asm = a; break; }
                if (asm == null) return;
                var t = asm.GetType("Tyrant.Framework.SkinsModule");
                if (t == null) return;
                _isStandIn = AccessTools.Method(t, "IsStandIn", new[] { typeof(object) });
                _isAdded = AccessTools.Method(t, "IsAdded", new[] { typeof(object) });
                _done = AccessTools.Field(t, "_done");
                SkinModBase.Log.Msg("[Compat] Tyrant framework detected - its skin numbers are respected.");
            }
            catch { }
        }

        public static bool Present { get { Look(); return _isStandIn != null; } }

        /// <summary>False while Tyrant hasn't added its skins yet (we must add ours after it).</summary>
        public static bool Ready
        {
            get
            {
                Look();
                if (_done == null) return true;
                try { return (bool)_done.GetValue(null); } catch { return true; }
            }
        }

        private static bool Call(MethodInfo m, object skin)
        {
            if (m == null || skin == null) return false;
            try { return (bool)m.Invoke(null, new[] { skin }); } catch { return false; }
        }

        public static bool IsStandIn(object skin) { Look(); return Call(_isStandIn, skin); }
        public static bool IsAdded(object skin) { Look(); return Call(_isAdded, skin); }
    }

    /// <summary>
    /// Puts custom skins into each species' skinsData list at their permanent numbers.
    /// Must run before the game loads animals from a save (see Patches), otherwise saved numbers point past the end of the list.
    /// </summary>
    internal static class SkinInjector
    {
        private static readonly HashSet<AnimalSkinData> Ours = new HashSet<AnimalSkinData>(RefEq<AnimalSkinData>.Instance);
        private static readonly HashSet<AnimalSkinData> StandIns = new HashSet<AnimalSkinData>(RefEq<AnimalSkinData>.Instance);
        private static readonly Dictionary<AnimalSkinData, SkinDef> DefBySkin = new Dictionary<AnimalSkinData, SkinDef>(RefEq<AnimalSkinData>.Instance);
        private static readonly Dictionary<BaseAnimalData, int> VanillaCount = new Dictionary<BaseAnimalData, int>(RefEq<BaseAnimalData>.Instance);
        private static readonly Dictionary<BaseAnimalData, Dictionary<int, AnimalSkinData>> Placed = new Dictionary<BaseAnimalData, Dictionary<int, AnimalSkinData>>(RefEq<BaseAnimalData>.Instance);

        public static bool Injected { get; private set; }
        private static bool _rebuild = true;
        private static bool _injecting;

        public static bool IsOurs(AnimalSkinData s) { return s != null && Ours.Contains(s); }
        public static bool IsStandIn(AnimalSkinData s) { return s != null && StandIns.Contains(s); }

        /// <summary>The custom skin an animal wears, or null for vanilla / stand-in skins.</summary>
        public static SkinDef DefFor(BaseAnimalData species, int idx)
        {
            if (species == null || species.skinsData == null || idx < 0 || idx >= species.skinsData.Count) return null;
            var s = species.skinsData[idx];
            SkinDef d;
            return s != null && DefBySkin.TryGetValue(s, out d) ? d : null;
        }

        public static SkinDef DefFor(AnimalSkinData skin)
        {
            SkinDef d;
            return skin != null && DefBySkin.TryGetValue(skin, out d) ? d : null;
        }

        public static IEnumerable<SkinDef> AllDefs { get { return DefBySkin.Values; } }

        /// <summary>Forces a full re-placement next time (after F5 reload).</summary>
        public static void MarkForRebuild() { _rebuild = true; }

        public static List<BaseAnimalData> AllSpecies()
        {
            return Resources.FindObjectsOfTypeAll<BaseAnimalData>()
                .Where(a => a != null && a.skinsData != null && a.skinsData.Count > 0)
                .ToList();
        }

        /// <summary>
        /// Makes sure every custom skin sits at its number. Cheap when nothing changed.
        /// force=false lets it wait for Tyrant to finish first; true (save loading) never waits.
        /// </summary>
        public static void EnsureInjected(string reason, bool force)
        {
            if (_injecting || !SkinModBase.OnMainThread) return;
            if (!force && TyrantCompat.Present && !TyrantCompat.Ready) return;

            var species = AllSpecies();
            if (species.Count == 0) return;   // animal database not loaded yet

            if (!_rebuild && Intact(species)) return;

            _injecting = true;
            try
            {
                Inject(species, reason);
                TextureStore.FinishCards();
                Injected = true;
                _rebuild = false;
            }
            catch (Exception ex)
            {
                SkinModBase.Log.Error("[Injector] Failed (" + reason + "): " + ex);
            }
            finally { _injecting = false; }
        }

        private static bool Intact(List<BaseAnimalData> species)
        {
            foreach (var sp in species)
            {
                Dictionary<int, AnimalSkinData> placed;
                if (!Placed.TryGetValue(sp, out placed)) return false;   // new / reloaded species asset
                foreach (var kv in placed)
                    if (kv.Key >= sp.skinsData.Count || !ReferenceEquals(sp.skinsData[kv.Key], kv.Value)) return false;
            }
            return true;
        }

        private static int VanillaOf(BaseAnimalData sp)
        {
            int v;
            if (VanillaCount.TryGetValue(sp, out v)) return v;
            var list = sp.skinsData;
            v = list.Count;
            for (int i = 0; i < list.Count; i++)
            {
                var s = list[i];
                if (Ours.Contains(s) || TyrantCompat.IsAdded(s) || TyrantCompat.IsStandIn(s)) { v = i; break; }
            }
            VanillaCount[sp] = v;
            return v;
        }

        private static void Inject(List<BaseAnimalData> species, string reason)
        {
            if (!SkinRegistry.Loaded)
            {
                SkinRegistry.Load();
                if (!SkinRegistry.FileExisted) SkinRegistry.MigrateFromV3(species, VanillaOf);
            }

            SkinScanner.WarnUnknownSpecies(species);

            if (Config.CompactRegistryOnNextLaunch.Value)
            {
                var installed = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var sp in species)
                {
                    HashSet<string> set;
                    string k = SkinRegistry.SpeciesKey(sp);
                    if (!installed.TryGetValue(k, out set)) { set = new HashSet<string>(StringComparer.OrdinalIgnoreCase); installed[k] = set; }
                    foreach (var d in SkinScanner.DefsFor(sp, species)) if (!d.Source.Disabled) set.Add(d.Key);
                }
                SkinRegistry.Compact(k =>
                {
                    HashSet<string> s;
                    return installed.TryGetValue(k, out s) ? (ICollection<string>)s : new string[0];
                });
                Config.CompactRegistryOnNextLaunch.Value = false;
                MelonLoader.MelonPreferences.Save();
            }

            DefBySkin.Clear();
            int custom = 0, standIns = 0;

            foreach (var sp in species)
            {
                try
                {
                    int c, s;
                    InjectSpecies(sp, species, out c, out s);
                    custom += c; standIns += s;
                }
                catch (Exception ex)
                {
                    SkinModBase.Log.Error("[Injector] " + sp.name + ": " + ex.Message);
                }
            }

            SkinRegistry.Save();
            SkinModBase.Log.Msg("[Injector] " + custom + " custom skin(s) in place" + (standIns > 0 ? ", " + standIns + " hidden stand-in(s)" : "") + " (" + reason + ").");
        }

        private static void InjectSpecies(BaseAnimalData sp, List<BaseAnimalData> allSpecies, out int customCount, out int standInCount)
        {
            customCount = 0; standInCount = 0;
            var list = sp.skinsData;
            int vanilla = VanillaOf(sp);
            var entry = SkinRegistry.For(sp);
            var defs = SkinScanner.DefsFor(sp, allSpecies);

            // number → what goes there
            var wanted = new SortedDictionary<int, SkinDef>();
            foreach (var d in defs)
            {
                int n = SkinRegistry.NumberFor(sp, d.Key, vanilla);
                if (n < vanilla)
                {
                    SkinModBase.Log.Warning(sp.name + ": slot " + n + " of '" + d.DisplayName + "' is now a vanilla skin (game update?). Moving it to a new slot - animals that wore it will show the vanilla skin.");
                    n = SkinRegistry.Renumber(sp, d.Key, vanilla);
                }
                if (n > SkinRegistry.MaxSlot)
                {
                    SkinModBase.Log.Warning(sp.name + ": slot " + n + " of '" + d.DisplayName + "' is above the limit (" + SkinRegistry.MaxSlot + ") - was skin-registry.json edited by hand? Giving it a new slot.");
                    n = SkinRegistry.Renumber(sp, d.Key, vanilla);
                }
                SkinDef other;
                if (wanted.TryGetValue(n, out other) && other != null)
                {
                    SkinModBase.Log.Warning(sp.name + ": '" + other.DisplayName + "' and '" + d.DisplayName + "' both have slot " + n + " in skin-registry.json. '" + d.DisplayName + "' gets a new slot.");
                    n = SkinRegistry.Renumber(sp, d.Key, vanilla);
                }
                wanted[n] = d.Source.Disabled ? null : d;
            }
            foreach (var kv in entry.Skins) if (!wanted.ContainsKey(kv.Value) && kv.Value >= vanilla && kv.Value <= SkinRegistry.MaxSlot) wanted[kv.Value] = null;  // removed skin
            foreach (var r in entry.Reserved) if (!wanted.ContainsKey(r) && r >= vanilla && r <= SkinRegistry.MaxSlot) wanted[r] = null;

            if (wanted.Count == 0) { Placed[sp] = new Dictionary<int, AnimalSkinData>(); return; }

            var placed = new Dictionary<int, AnimalSkinData>();
            foreach (var kv in wanted)
            {
                int n = kv.Key;
                AnimalSkinData obj;
                if (kv.Value != null)
                {
                    obj = MakeCustom(sp, vanilla, kv.Value);
                    DefBySkin[obj] = kv.Value;
                    customCount++;
                }
                else
                {
                    obj = MakeStandIn(list[0]);
                    standInCount++;
                }

                // Fill any gap before n with hidden stand-ins (keeps the list contiguous).
                while (list.Count < n)
                {
                    var pad = MakeStandIn(list[0]);
                    placed[list.Count] = pad;
                    list.Add(pad);
                }

                if (list.Count == n) list.Add(obj);
                else
                {
                    var existing = list[n];
                    bool replaceable = existing == null || Ours.Contains(existing) || TyrantCompat.IsStandIn(existing);
                    if (!replaceable)
                    {
                        if (kv.Value == null) continue;   // a free number we wanted for a stand-in: just leave the other skin there
                        SkinModBase.Log.Warning(sp.name + ": slot " + n + " is taken by '" + existing.skinName + "' (another mod?). '" + kv.Value.DisplayName + "' gets a new slot.");
                        DefBySkin.Remove(obj);
                        int nn = SkinRegistry.Renumber(sp, kv.Value.Key, vanilla);
                        while (list.Count < nn) { var pad = MakeStandIn(list[0]); placed[list.Count] = pad; list.Add(pad); }
                        if (list.Count == nn) list.Add(obj); else list[nn] = obj;
                        DefBySkin[obj] = kv.Value;
                        placed[nn] = obj;
                        continue;
                    }
                    list[n] = obj;
                }
                placed[n] = obj;
            }
            Placed[sp] = placed;

            foreach (var d in wanted.Values)
                if (d != null && SkinModBase.Verbose) SkinModBase.Log.Msg("  " + sp.name + " #" + SkinRegistry.For(sp).Skins[d.Key] + " '" + d.DisplayName + "'  <- " + d.Source.Location);
        }

        private static AnimalSkinData MakeStandIn(AnimalSkinData first)
        {
            var s = CopyOf(first);
            Ours.Add(s);
            StandIns.Add(s);
            return s;
        }

        private static AnimalSkinData MakeCustom(BaseAnimalData sp, int vanilla, SkinDef def)
        {
            var baseSkin = PickBase(sp, vanilla, def);
            def.BaseSkin = baseSkin;
            var s = CopyOf(baseSkin);
            if (def.Source.HasVariationOverrides)
            {
                var m = OwnVariation(baseSkin.maleVariationData, def);
                s.maleVariationData = m;
                s.femaleVariationData = ReferenceEquals(baseSkin.femaleVariationData, baseSkin.maleVariationData)
                    ? m : OwnVariation(baseSkin.femaleVariationData, def);
            }
            s.mainData = sp;
            s.skinName = def.DisplayName;
            var thumb = TextureStore.Thumbnail(def);
            if (thumb != null) s.skinThumbnail = thumb;
            Ours.Add(s);
            return s;
        }

        /// <summary>
        /// A private copy of the base skin's settings asset with the skin.json overrides applied.
        /// Textures keep pointing at the base skin's (Addressables references are copied as-is).
        /// Iridescence on/strength is rolled when an animal is CREATED, so it affects new animals only.
        /// </summary>
        private static AnimalSkinVariationAsset OwnVariation(AnimalSkinVariationAsset src, SkinDef def)
        {
            if (src == null) return null;
            var v = UnityEngine.Object.Instantiate(src);
            v.name = src.name + " (" + def.DisplayName + ")";
            var o = def.Source;
            if (o.Iridescence.HasValue || o.IridescenceStrength.HasValue)
            {
                if (v.defaultVariationData == null) v.defaultVariationData = new AnimalDefaultSkinVariationData();
                if (o.Iridescence.HasValue) v.defaultVariationData.iridescence = o.Iridescence.Value;
                if (o.IridescenceStrength.HasValue) v.defaultVariationData.iridescenceStrength = o.IridescenceStrength.Value;
            }
            if (o.FuzzDroopiness.HasValue) v.adultFuzzDroopiness = o.FuzzDroopiness.Value;
            if (o.InfantFuzzDroopiness.HasValue) v.infantFuzzDroopiness = o.InfantFuzzDroopiness.Value;
            return v;
        }

        private static AnimalSkinData CopyOf(AnimalSkinData src)
        {
            return new AnimalSkinData
            {
                mainData = src.mainData,
                skinName = src.skinName,
                skinThumbnail = src.skinThumbnail,
                portraits = src.portraits,
                skinInteguments = src.skinInteguments,
                maleGrowthClamp = src.maleGrowthClamp,
                femaleGrowthClamp = src.femaleGrowthClamp,
                maleSizeMultiplier = src.maleSizeMultiplier,
                femaleSizeMultiplier = src.femaleSizeMultiplier,
                maleVariationData = src.maleVariationData,
                femaleVariationData = src.femaleVariationData,
            };
        }

        /// <summary>skin.json "base" (vanilla skin name or number), otherwise v3's choice (feathered vs scaled).</summary>
        private static AnimalSkinData PickBase(BaseAnimalData sp, int vanilla, SkinDef def)
        {
            var list = sp.skinsData;
            string b = def.Source.Base;
            if (!string.IsNullOrEmpty(b))
            {
                int idx;
                if (int.TryParse(b, out idx))
                {
                    if (idx >= 0 && idx < vanilla) return list[idx];
                }
                else
                {
                    for (int i = 0; i < vanilla; i++)
                        if (list[i] != null && string.Equals(list[i].skinName, b, StringComparison.OrdinalIgnoreCase)) return list[i];
                }
                var names = string.Join(", ", Enumerable.Range(0, vanilla).Select(i => i + "='" + (list[i] != null ? list[i].skinName : "?") + "'").ToArray());
                SkinModBase.Log.Warning(def.Source.Location + ": base '" + b + "' is not a vanilla " + sp.name + " skin (" + names + "). Using the default.");
            }
            return SkinModBase.GetOptimalBaseSkin(sp, vanilla, def.Source.Feathered) ?? list[0];
        }
    }
}
