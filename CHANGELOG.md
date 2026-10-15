# Changelog

## 4.1.0
- **Works with other mods**: other mods can now add their own sections to the skin panel (F6), in the panel's own style.
  First one: **Unclamped Skin Colors 1.1** adds a Colour section to Animal mode - change any animal's hue, saturation
  and value live, with per-skin limits.
- For mod makers: `PKSkinLoader.PanelApi` (animal and skin sections, sliders with custom labels, small row buttons,
  redraw, toasts, refreshing an animal). See README.
- Nothing else changed: skins, slot numbers and saves work exactly as in 4.0. Just replace the DLL.

## 4.0.0
A full rewrite of the skin loader, made with the help of AI (Claude by Anthropic; earlier versions with Gemini) - see README.

**Skins**
- Every texture slot: diffuse, normal, extra, pattern, fur, all infant versions, iridescence mask and gradient,
  male/female versions, nursery card (`thumbnail.png`) and info-panel portrait (`portrait.png`).
- Skin folders (`Mods\Skins\<Species>\<Skin>\`) with an optional `skin.json`; old flat files still work.
- Packs with author credit in the nursery; skins with the same name from different packs can both be installed.
- Choose the vanilla skin your skin builds on (crests, spikes, feathers, fur come from it).
- Patterns are made from your diffuse automatically, so colour sliders, random colours and mutations follow your
  markings (mouth, claws and eyes stay untinted). Generated patterns are saved for touching up.
- Iridescence on/off and strength, fur droop, fur length, fur coverage (baby fuzz, whole body, bald).
- Black-and-white fur maps; fur on normally bald and armoured species (experimental).

**In game**
- Skin panel (F6): browse, edit settings live, switch skins on/off, create new skins (optionally from your own
  diffuse), problems list, compact mode, resizable, follows the nursery.
- Vanilla skin tweaks (fur, droop, shimmer) without touching game files.
- Individual animals: their own coat, droop and shimmer, saved with the park; babies inherit a mix of their parents' looks.
- Live reload (F5) updates every animal; F8 report and F9 template export for skin makers.

**Safety**
- Skins keep a permanent slot number; removed skins leave a hidden placeholder so saves never break
  (fixes the old "corrupted animal" bug where skins were added after a save had already started loading).
- Prepare-for-uninstall option, game-update check on start, duplicate-install protection.
- Textures are scaled to `MaxTextureSize` (default 2048) and GPU-compressed; video memory use is shown in the console.
- Nursery skin cards are cached for fast start-up.
- Works alongside Tyrant (new skins are numbered from 40).
