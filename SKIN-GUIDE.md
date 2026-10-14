# PK Custom Skin Loader 4.0 — Skin maker guide

## The skin panel (F6)
Press **F6** in game. Browse every custom skin, change its settings with buttons and sliders (saved to skin.json and
applied instantly), switch skins on/off, open folders and templates, and see problems. **+ New skin** creates a skin
for any species from one of its vanilla skins (optionally with your own diffuse). Pick a species to also tweak its
**vanilla skins** (fur, droop, iridescence; stored in `UserData/PKSkinLoader/vanilla-tweaks.json`, saves untouched).
**Compact** shrinks it to the settings column; drag the bottom-right corner to resize. In the nursery it follows the
animal you're previewing.

## Quick start (by hand)
1. In game, place the species you want in your park and press **F9**.
   Templates appear in `UserData/PKSkinLoader/Dump/Templates/<Species>/<slot>_<Skin>/`.
2. Copy a template folder to `Mods/Skins/<Species>/<Your Skin Name>/`.
3. Paint over the PNGs you want to change. **Delete the ones you don't change** — anything missing comes from the base skin.
4. In game press **F5**. Every animal wearing the skin updates instantly. Repeat 3–4.

```
Mods/Skins/
  Tyrannosaurus/
    Ember/
      diffuse.png            colour (both sexes)
      diffuse_female.png     optional: female-only version
      pattern.png            genetics pattern mask
      normal.png             normal map (normal blue-purple; converted automatically)
      extra.png              extra map (red channel above ~230 = eyes)
      fur.png                fur / feather-fuzz mask (black and white works: white = fur, black = bare)
      infant_diffuse.png     babies (any infant_ file missing → the adult one is used)
      iridescence.png        optional: where the shimmer shows (mask)
      iridescence_gradient.png  optional: the colour strip the shimmer cycles through
      thumbnail.png          optional nursery skin card (missing → a crop of your diffuse); F9 exports the vanilla card
      portrait.png           optional picture in the animal's info panel, see below
      skin.json              optional, see below
```

Species folder names: the names from the F8 report, e.g. `Tyrannosaurus`, `Allosaurus Anax`, `Leaellynasaura`.

## Packs and credit
Share your skins as a pack: `Mods/Skins/<Pack>/<Species>/<Skin>/`. The pack folder is part of each skin's identity,
so two creators can both make a `Tyrannosaurus/Ember` and players can install both — each keeps its own slot.
**Don't rename your pack folder after release** (that would move its skins to new slots, like renaming a skin).
Optional `pack.json` in the pack folder:
```json
{ "name": "Moshepit's Raptors", "author": "Moshepit", "version": "1.0" }
```
The nursery shows the author greyed out after the name: "Ember  by Moshepit". `"author"` in a skin.json overrides the pack's.
Players can hide authors with `ShowAuthorInNursery = false`. A `_` in front of the pack folder switches the whole pack off.

## File names
`diffuse`, `normal`, `extra`, `pattern`, `fur` — optionally `infant_` in front and `_male` / `_female` at the end.
Sex fallback: a missing `_female` file uses the male one (and the other way round).

Sizes: 1024 or 2048 is plenty (vanilla skins are 1024–2048). Bigger textures are scaled down to `MaxTextureSize`
(default 2048) when loading, and odd sizes are adjusted to a multiple of 4 so they can always be GPU-compressed.
The console shows when this happens, and F5 prints how much video memory custom skins use.

## Portraits (optional)
(Not the nursery skin card — that's `thumbnail.png`. Cards are square like the vanilla ones, but the nursery shows them
a bit narrower: keep the important part inside the middle ~80% of the width.)
`portrait.png` replaces the animal's picture in its info panel (top right when you select it), the nursery lists and nests. Without one, the base
skin's vanilla portrait shows, as before. F9 exports the vanilla portraits too (`portrait.png`, `portrait_infant.png`),
so you get the exact size and framing to paint over.
- `portrait_infant.png` for babies (missing → the adult portrait). Adolescents use the adult one, like the game.
- `portrait_male.png` / `portrait_female.png` if the sexes look different.
- `portrait_albino.png`, `_melanistic`, `_leucistic` for mutations (missing → the base skin's vanilla mutation portrait).
- Combine in any order: `portrait_infant_female_albino.png`. Flat files: `Species_Name_portrait.png`.

## skin.json (all optional)
```json
{
  "name": "Ember",
  "id": "ember",
  "base": "Molten",
  "feathered": false,
  "author": "Moshepit"
}
```
- `name` — shown in the nursery (default: folder name).
- `id` — the skin's permanent identity. Set it if you rename the folder, so saves keep the skin.
- `base` — vanilla skin to start from (name or number). Decides integument, size and anything you don't paint.
- `feathered` — without `base`: start from a feathered vanilla skin.
- `cutout` — transparency of the diffuse (see below): `"auto"` (default), `"base"` or `"own"`.
- `pattern` — what the colour sliders, random variation and mutations paint on. If you leave it out:
  your `pattern.png` if you made one, otherwise one made from your diffuse (`"diffuse"`).
  `"base"`: the base skin's vanilla pattern (its markings won't match your painting);
  `"diffuse"`: made from your own diffuse (dark areas → pattern colour A, light → colour B), so the
  random colour variation and sliders follow YOUR markings. Mouth, claws, teeth and eyes stay untinted like on the
  base skin, and the contrast is matched to the base skin so mutations aren't extreme. Each generated mask is also
  saved to `UserData/PKSkinLoader/Dump/GeneratedPatterns/` — a clean starting point to touch up and use as `pattern.png`;
  `"solid"`: no pattern, but the nursery colour sliders and mutations tint the whole body evenly;
  `"none"`: no pattern and no tinting at all (sliders/mutations have nothing to colour).
  The pattern mask is the "canvas" for colours: red = where the tint and pattern colours A/B apply, green = secondary colour.
- `iridescence` — `true`/`false`: whether NEW animals get the shimmer (the game decides this when an animal is created).
  `iridescenceStrength` — applied live to every animal wearing the skin (F5 updates it). Small values go a long way:
  `0` = off, `0.05` = subtle, `0.35` = strong.
- `fuzzDroopiness` / `infantFuzzDroopiness` — `[sideways, up/down, along the body]`, applied live with F5.
  The middle number does the visible work: negative = fuzz hangs down (vanilla ≈ -0.5 to -1.5, Velociraptor 'Mirage' -10).
  Keep the first at 0 (vanilla does, so both sides stay symmetric); the last only sweeps the fuzz slightly toward the tail.
  F8 report shows each vanilla skin's values.
- `"enabled": false` — switch the skin off (same as a `_` in front of the folder name).

## Painting your own pattern.png
Easiest: start from the F9 template's `pattern.png` or a generated one (see `"pattern": "diffuse"` above).
- **Red** = your markings in greyscale: dark → pattern colour A, light → colour B; the colour sliders and mutations
  tint everything that isn't black. Paint **pure black** where nothing may be tinted (mouth, claws, teeth, eyes).
- **Green** = secondary-colour areas (vanilla uses it for mouth, eye skin, wattles). Easiest: keep the template's green.
- **Blue** = always fully white.
- Save it as a normal (sRGB) PNG. Two colours of the same brightness look the same to the game — if your diffuse
  has those, make them lighter/darker in the red channel to keep them apart.

## Choosing a base: crests, spikes, feathers and fur
Many species hide parts of their model with **diffuse transparency** (alpha below 50% = invisible):
Dilophosaurus head crests/spikes, Gallimimus feather shapes, etc. Each vanilla skin shows different parts.
- `"base"` picks the vanilla skin your skin builds on. Everything you don't paint comes from it:
  its crest/spike cut-outs, its fur mask (fur length), normal, extra map, iridescence.
- **Cut-outs are automatic**: if your `diffuse.png` has no transparency, the base skin's cut-outs are used.
  Paint your own alpha to change them (`"cutout": "own"` forces yours, `"base"` always uses the base's).
- **Fur** comes from the fur mask (`fur.png`). Without one, the base skin's is used. F9-export the species to see them.
- **Iridescence**: species with it (Oviraptor, Microraptor, Edmontosaurus annectens, Velociraptor 'Mirage') have their
  mask and gradient on the species material; `iridescence.png` / `iridescence_gradient.png` replace them for your skin.

## Fur and fuzz
- `fur.png`: paint it **black and white** (white = fur, grey = shorter, black = bare). The game's own layout
  (amount in red, green/blue full) works too. A `fur.png` also gives fur to species that are normally bald,
  even armoured ones like Ankylosaurus or Styracosaurus (experimental: the mod switches the game's fur layer on).
- skin.json `"furLength"`: multiplies the species' fur length (1 = normal, 0.3 = short fuzz, 2 = shaggy).
- skin.json `"furCoverage"`: `"baby"` (the babies' fuzz on adults), `"body"` (a generated coat everywhere except
  mouth, claws and eyes), `"none"` (bald). Leave it out for the skin's own fur.
- Fur on a normally bald animal sticks straight out unless you set `fuzzDroopiness`, e.g. `[0, -1, 1]` to lie back.
- The generated coat is saved as `Dump/GeneratedFur/<skin>_fur.png` (black and white) with `_fur_guide.png`
  (the skin with bare parts in red) - paint horns, spikes and scaly feet black and use it as your `fur.png`.
- All of this is in the panel's **Fur** and **Fur droop** sections, live.

## Individual animals and genes
In the F6 panel, **Animal** mode: click an animal in your park and give it its own fur (coverage, length), fur droop and
shimmer. It's saved inside the park's save folder (`PKSkinLoader-animals.json`) when you save the game.
Newborns and hatchlings inherit a mix of their parents' own looks (between mum and dad plus a little variation; a trait
only one parent has is passed on about half the time). Animals that were already grown never change.

## Testing tips
- **Switch a skin off** without deleting it: rename the folder to `_Ember`. Its slot stays reserved.
- **Remove / re-add freely**: a skin always returns to the same slot, so saves keep working.
- Problems (unknown file names, bad sizes, unknown species) are printed in the MelonLoader console on start and on F5.

## Save safety
Each skin gets a permanent slot number in `UserData/PKSkinLoader/skin-registry.json` — **don't delete this file**.
Removed skins leave a hidden stand-in: animals wearing them show the species' first vanilla skin until the skin returns.
For throwaway test saves only: `CompactRegistryOnNextLaunch = true` in `UserData/MelonPreferences.cfg` renumbers everything without gaps.

## Uninstalling the mod (important!)
Saves store skins by slot number. Without the mod, the game can't load an animal whose slot doesn't exist.
1. In `UserData/MelonPreferences.cfg` set `PrepareForUninstall = true`.
2. Start the game, load your save, wait for the `[Uninstall] … switched to their vanilla base skin` message.
3. Let eggs hatch (or cancel them) and clear the nursery queue — those aren't converted.
4. **Save.** Repeat for each save you want to keep, then quit and remove the mod.
(To keep using the mod instead, set it back to `false`.)

## After a game update
The console's `[Compat]` line on start says whether every hook still works. If any failed, that feature is off
but the rest keeps protecting saves; a red line means save protection is reduced — don't overwrite important saves
until the mod is updated.

## Old flat files
v3 files (`Mods/Skins/Species_Name.png`, `Species_Name_pattern.png`, `_male` / `_female`) still work and keep their slots.
Settings for flat files: put `Species_Name.json` next to them (same fields as skin.json, except `name`/`id`,
which would move the skin to a new slot), e.g. `Dilophosaurus_Beans.json` → `{ "base": "Clouded" }`.
New in v4: add `_infant` for baby textures, e.g. `Species_Name_infant.png`, `Species_Name_infant_pattern.png`, `Species_Name_infant_female.png`.

## Settings (`UserData/MelonPreferences.cfg`)
`[PKSkinLoader]` ReloadKey (F5), PanelKey (F6), PanelScale, PanelCompact, CompressTextures, VerboseLogging, FirstSkinNumber, CompactRegistryOnNextLaunch, PrepareForUninstall, ShowAuthorInNursery, MaxTextureSize
`[PKSkinLoader_Dev]` ReportKey (F8), ExportTemplatesKey (F9), ExportFilter

Works alongside Tyrant: new skins are numbered from 40, above Tyrant's range.
