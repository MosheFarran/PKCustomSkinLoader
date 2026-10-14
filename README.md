# PK Custom Skin Loader 4.0

Custom skins for **Prehistoric Kingdom** — made in your favourite art program, edited live in game.
By MosheFarran (Moshepit).

## What it does
- **Custom skins for any species**: diffuse, pattern, normal, fur, infant textures, iridescence, nursery card and portrait.
- **In-game skin panel (F6)**: browse skins, change their settings with sliders, create new skins, switch them on/off.
- **Fur for everyone**: change fur length, give bald dinosaurs fuzz or a whole winter coat, or make fuzzy ones bald.
- **Individual animals**: give one animal its own coat — and its babies inherit a mix of both parents' looks.
- **Vanilla skins** can get fur, droop and shimmer tweaks too (only in your game; game files stay untouched).
- **Save-safe**: skins keep their slot forever, removed skins never break saves, and there's a safe uninstall option.
- Live reload (F5), automatic texture compression, works alongside Tyrant.

## Made with AI assistance
In the spirit of transparency: this mod was made with the help of AI. The earlier versions were built by MosheFarran
(Moshepit) together with Google's Gemini; version 4 was developed with Claude (by Anthropic). The AI wrote the code and
documentation and helped research how the game works. Moshepit came up with the features, guided the work, read and
checked the code along the way, and tested everything in game.

## Requirements
- Prehistoric Kingdom (tested on 1.17.123)
- **MelonLoader 0.7.x** installed in the game

## Install
Download the zip from the Releases page (or Nexus).
1. Copy the `Mods` folder from the zip into your game folder
   (`...\steamapps\common\Prehistoric Kingdom\`), so you get `Mods\PKCustomSkinLoader.dll` and `Mods\Skins\`.
2. Start the game. The MelonLoader console shows `PK Custom Skin Loader 4.0.0` and `[Compat] All … hooks OK`.

### Updating from an older version (v3 / test builds)
- **Install v4 before you remove or rename any skins** — v4 takes over your existing skin numbers so saves keep working.
- Delete the old DLL (for example `Mods\PKSkinLoaderV1.02.dll`). If you forget, v4 switches the old copy off and
  tells you in the console which file to delete.
- Old flat-file skins (`Mods\Skins\Species_Name.png`) keep working.

## Keys
| Key | |
|---|---|
| **F6** | Skin panel |
| **F5** | Reload skins (after editing a PNG or skin.json) |
| F8 | Write a report of all species and skins (for skin makers) |
| F9 | Export vanilla textures as templates (for skin makers) |

Keys and options are in `UserData\MelonPreferences.cfg` under `[PKSkinLoader]` (they appear after the first time you quit the game).

## Making skins
See **SKIN-GUIDE.md**. The quickest way: press **F6 → + New skin**, pick a species and a vanilla skin to start from,
paint the diffuse it opens, press F5.
Try the examples: rename `Mods\Skins\_Example Pack` to `Example Pack`.

## Uninstalling (important!)
Saves remember skins by number. Without the mod, the game can't load an animal wearing a custom skin.
1. In `UserData\MelonPreferences.cfg` set `PrepareForUninstall = true`.
2. Load each save you want to keep, wait for the `[Uninstall]` message in the console, then **save**.
3. Let eggs hatch (or cancel them) and clear the nursery queue first — those aren't converted.
4. Quit and delete `Mods\PKCustomSkinLoader.dll`.

## After a game update
Check the `[Compat]` line in the console when the game starts. If a hook failed, that feature switches off and the rest
keeps working; a red line means save protection is reduced — don't overwrite important saves until the mod is updated.

## For developers: building from source
The mod is a MelonLoader mod (C#, .NET Framework 4.8 project, MelonLoader's net35 libraries).
1. Install Prehistoric Kingdom and MelonLoader 0.7.x, start the game once.
2. Open `PKSkinLoaderv1.02.slnx` in Visual Studio 2022.
3. If your game isn't in `D:\Steam Games\steamapps\common\Prehistoric Kingdom`, set `<GameDir>` at the top of
   `PKSkinLoaderV1.02/PKSkinLoaderV1.02.csproj` (or build with `/p:GameDir="C:\...\Prehistoric Kingdom"`).
   References point at the game's own DLLs; nothing from the game is in this repository.
4. Build. The DLL is `bin\<Configuration>\PKCustomSkinLoader.dll`; when the game folder exists it's also copied to
   its `Mods` folder automatically (close the game first).

### What the code does (where to look)
| File | |
|---|---|
| `SkinModBase.cs` | Entry point, settings, F5 reload, duplicate-install guard |
| `Safety.cs` | Applies every Harmony hook separately (game-update check), prepare-for-uninstall |
| `SkinSources.cs` | Reads `Mods/Skins` (folders, packs, skin.json, flat files) |
| `SkinRegistry.cs` | Permanent skin slot numbers (`skin-registry.json`) |
| `SkinInjector.cs` | Adds skins to the game's species data before saves load |
| `SkinApplier.cs` | Loads textures and applies them to animals (MaterialPropertyBlocks), fur, patterns |
| `Patches.cs` | Harmony hooks into the game |
| `NurseryUI.cs` | Nursery skin cards and preview |
| `SkinPanel*.cs` | The F6 panel, vanilla tweaks, individual animals and inheritance |
| `SkinDumper.cs` | F8 report and F9 template export |

The mod only reads and writes files inside the game folder (`Mods/Skins`, `UserData`) and the park's save folder
(`PKSkinLoader-animals.json`). It makes no network connections.

## Files the mod writes
- `UserData\PKSkinLoader\skin-registry.json` — skin numbers. **Don't delete it.**
- `UserData\PKSkinLoader\vanilla-tweaks.json` — your vanilla skin tweaks.
- `PKSkinLoader-animals.json` inside each save folder — individual animals' looks.
- `UserData\PKSkinLoader\Cache` and `Dump` — safe to delete.

Please don't redistribute the game's own textures (F9 templates are for painting over, not for sharing as-is).

## License
MIT - see LICENSE. Copyright (c) 2026 MosheFarran (Moshepit).
