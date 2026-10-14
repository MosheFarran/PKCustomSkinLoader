using Newtonsoft.Json.Linq;
using PrehistoricKingdom;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PKSkinLoader
{
    /// <summary>
    /// Permanent skin slot numbers, saved in UserData/PKSkinLoader/skin-registry.json.
    ///
    /// Saves store an animal's skin as a plain number, so a custom skin must keep its number forever:
    ///  - a skin is identified by species + key (folder name, skin.json "id", or the v3 display name);
    ///  - removing a skin keeps its number (a hidden stand-in fills it); re-adding it gets the same number back;
    ///  - "reserved" numbers are hidden copies of the first vanilla skin (e.g. the old T. rex padding slot).
    /// </summary>
    internal static class SkinRegistry
    {
        public class SpeciesEntry
        {
            public readonly Dictionary<string, int> Skins = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            public readonly SortedSet<int> Reserved = new SortedSet<int>();

            public int MaxNumber
            {
                get
                {
                    int m = -1;
                    foreach (var n in Skins.Values) if (n > m) m = n;
                    if (Reserved.Count > 0 && Reserved.Max > m) m = Reserved.Max;
                    return m;
                }
            }

            public bool IsUsed(int n)
            {
                return Reserved.Contains(n) || Skins.ContainsValue(n);
            }
        }

        /// <summary>Highest slot number allowed (each slot is a nursery card; a typo like 4000 would create thousands).</summary>
        public const int MaxSlot = 255;

        public static readonly Dictionary<string, SpeciesEntry> Species = new Dictionary<string, SpeciesEntry>(StringComparer.OrdinalIgnoreCase);
        public static bool Loaded;
        public static bool FileExisted;
        public static bool Dirty;
        private static bool _readOnly;

        public static string FilePath
        {
            get { return Path.Combine(Path.Combine(Path.Combine(Directory.GetCurrentDirectory(), "UserData"), "PKSkinLoader"), "skin-registry.json"); }
        }

        public static string SpeciesKey(BaseAnimalData sp)
        {
            return string.IsNullOrEmpty(sp.speciesID) ? sp.name : sp.speciesID;
        }

        public static SpeciesEntry For(BaseAnimalData sp)
        {
            string k = SpeciesKey(sp);
            SpeciesEntry e;
            if (!Species.TryGetValue(k, out e)) { e = new SpeciesEntry(); Species[k] = e; }
            return e;
        }

        public static void Load()
        {
            Species.Clear();
            Loaded = true;
            FileExisted = File.Exists(FilePath);
            if (!FileExisted) return;

            try
            {
                var root = JObject.Parse(File.ReadAllText(FilePath));
                var species = root["species"] as JObject;
                if (species == null) return;
                foreach (var prop in species.Properties())
                {
                    var e = new SpeciesEntry();
                    var skins = prop.Value["skins"] as JObject;
                    if (skins != null)
                        foreach (var s in skins.Properties())
                            e.Skins[s.Name] = (int)s.Value;
                    var reserved = prop.Value["reserved"] as JArray;
                    if (reserved != null)
                        foreach (var r in reserved) e.Reserved.Add((int)r);
                    Species[prop.Name] = e;
                }
            }
            catch (Exception ex)
            {
                // Never overwrite a registry we couldn't read: numbers would be lost.
                _readOnly = true;
                SkinModBase.Log.Error("skin-registry.json could not be read (" + ex.Message + "). Skins get temporary numbers this session and the file is left untouched. Fix or delete it.");
            }
        }

        public static void Save()
        {
            if (!Dirty || _readOnly) return;
            try
            {
                var species = new JObject();
                foreach (var kv in Species.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                {
                    if (kv.Value.Skins.Count == 0 && kv.Value.Reserved.Count == 0) continue;
                    var skins = new JObject();
                    foreach (var s in kv.Value.Skins.OrderBy(s => s.Value)) skins[s.Key] = s.Value;
                    var o = new JObject();
                    o["skins"] = skins;
                    if (kv.Value.Reserved.Count > 0) o["reserved"] = new JArray(kv.Value.Reserved.ToArray());
                    species[kv.Key] = o;
                }
                var root = new JObject();
                root["format"] = 1;
                root["note"] = "Permanent skin slot numbers. Saves store skins by number - don't edit numbers by hand unless you know what you're doing.";
                root["species"] = species;

                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, root.ToString());
                if (File.Exists(FilePath)) File.Delete(FilePath);
                File.Move(tmp, FilePath);
                Dirty = false;
            }
            catch (Exception ex)
            {
                SkinModBase.Log.Error("skin-registry.json could not be saved (" + ex.Message + "). New skins may get different numbers next time.");
            }
        }

        /// <summary>Number for a skin key, assigning a new permanent one if needed.</summary>
        public static int NumberFor(BaseAnimalData sp, string key, int vanillaCount)
        {
            var e = For(sp);
            int n;
            if (e.Skins.TryGetValue(key, out n)) return n;

            int next = Math.Max(Config.FirstSkinNumber.Value, vanillaCount);
            foreach (var v in e.Skins.Values) if (v >= next && v <= MaxSlot) next = v + 1;
            foreach (var r in e.Reserved) if (r >= next && r <= MaxSlot) next = r + 1;
            e.Skins[key] = next;
            Dirty = true;
            return next;
        }

        /// <summary>Moves a skin to a fresh number (only used when its number collides with a vanilla/other-mod skin).</summary>
        public static int Renumber(BaseAnimalData sp, string key, int vanillaCount)
        {
            For(sp).Skins.Remove(key);
            Dirty = true;
            return NumberFor(sp, key, vanillaCount);
        }

        // ---------------------------------------------------------------- one-time migration from v3

        /// <summary>
        /// First launch of v4: re-creates the numbers v3 gave its flat-file skins, so existing saves load unchanged.
        /// v3 appended skins per species in file order, reused a slot when a skin with the same name already existed,
        /// and padded Tyrannosaurus to 4 skins (copies of skin 0) before its first custom skin.
        /// </summary>
        public static void MigrateFromV3(List<BaseAnimalData> species, Func<BaseAnimalData, int> vanillaCountOf)
        {
            int total = 0;
            foreach (var sp in species)
            {
                if (sp == null || sp.skinsData == null || sp.skinsData.Count == 0) continue;
                var e = For(sp);
                if (e.Skins.Count > 0 || e.Reserved.Count > 0) continue;   // duplicate species asset already handled

                int vanilla = vanillaCountOf(sp);
                var names = new List<string>();
                for (int i = 0; i < vanilla; i++) names.Add(sp.skinsData[i] != null ? sp.skinsData[i].skinName : null);
                int count = vanilla;

                foreach (var src in SkinScanner.Sources)
                {
                    if (!src.Legacy || !SkinModBase.IsSkinMatchingAnimal(src.LegacyName, sp, species)) continue;
                    string display = SkinModBase.ExtractDisplayName(src.LegacyName, sp);
                    string key = SkinScanner.KeyOf(display);

                    int existing = names.IndexOf(display);
                    if (existing >= 0)
                    {
                        if (existing >= vanilla && !e.Skins.ContainsKey(key)) e.Skins[key] = existing;
                        continue;   // v3 reused that slot (for a vanilla name it just painted over the vanilla skin)
                    }

                    if (sp.name.ToLowerInvariant().Contains("tyrannosaurus"))
                        while (count < 4) { e.Reserved.Add(count); names.Add(null); count++; }

                    e.Skins[key] = count;
                    names.Add(display);
                    count++;
                    total++;
                }
            }
            Dirty = true;
            SkinModBase.Log.Msg("[Registry] First launch: locked in " + total + " existing skin slot(s) from v3 so current saves load unchanged.");
        }

        // ---------------------------------------------------------------- compaction (opt-in, for test saves)

        /// <summary>
        /// Drops numbers of skins that are no longer installed (and reserved numbers), then renumbers the remaining
        /// skins without gaps. This CHANGES numbers: animals in existing saves may show a different skin afterwards.
        /// </summary>
        public static void Compact(Func<string, ICollection<string>> installedKeysOfSpecies)
        {
            int dropped = 0, moved = 0;
            foreach (var kv in Species)
            {
                var installed = installedKeysOfSpecies(kv.Key);
                var keep = kv.Value.Skins.Where(s => installed.Contains(s.Key)).OrderBy(s => s.Value).Select(s => s.Key).ToList();
                dropped += kv.Value.Skins.Count - keep.Count + kv.Value.Reserved.Count;
                var old = new Dictionary<string, int>(kv.Value.Skins, StringComparer.OrdinalIgnoreCase);
                kv.Value.Skins.Clear();
                kv.Value.Reserved.Clear();
                int n = Config.FirstSkinNumber.Value;
                foreach (var key in keep)
                {
                    if (old[key] != n) moved++;
                    kv.Value.Skins[key] = n++;
                }
            }
            Dirty = true;
            SkinModBase.Log.Warning("[Registry] Compacted: removed " + dropped + " unused number(s), renumbered " + moved + " skin(s). Animals in older saves may show different skins.");
        }
    }
}
