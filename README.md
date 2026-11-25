# Vendetta (Stonewards mod) v0.2.0

Each wave, one monster swears a vendetta against one dwarf. It hunts that player over the wall and to the mine entrance, grows stronger with every hit, and if its dwarf stays dead the run is lost. For players, see `thunderstore/README.md`; the rest of this page is for building it.

Tested in solo runs; co-op is still being tested. Made by BaranMods, with much of the code written by AI (Claude). A fan-made mod, not affiliated with the makers of Stonewards. `DISCOVERY.md` explains how to find the game's names again after an update.

## Build
1. .NET SDK 8+ installed. BepInEx 5 is in the game folder.
2. In `Vendetta.csproj`, check `GameDir` and `ProfileDir`.
3. Run `dotnet build -c Release`. This:
   - copies `Vendetta.dll` to the game folder's `BepInEx\plugins\Vendetta\`
   - copies it to your Thunderstore profile (if `ProfileDir` exists)
   - creates `bin\Vendetta-<version>.zip` for **Import local mod**

## Project layout
| Folder | What's in it | Depends on |
|---|---|---|
| `src/Core/` | All the rules: settings and presets, stacks and healing, targeting, the **director** (wave flow, wall leap, mine chase, revive-or-lose, rewards), the **Nemesis** roster and names | Nothing (plain C#, unit-tested) |
| `src/Game/` | The link to Stonewards: **`GameNames.cs`** (the only file you fill in), the bridge (reads and changes the game through reflection), and the Harmony hooks | Game, by name only |
| `src/Unity/` | Plugin entry point, config menu, and the runner (per-frame ticks, marker, banners, debug overlay) | BepInEx, Unity |
| `src/Net/` | `LobbySync`: what other players see, sent as Steam lobby messages | Steamworks.NET |
| `thunderstore/` | Manifest, icon, README and changelog for the package | |

**Design rule:** the core never touches the game. The game talks to the director only through `GameHooks` (events coming in) and `IGameBridge` (actions going out). When Stonewards updates, fixes usually go in `GameNames.cs`. A missing hook switches off only its own feature and logs a warning, so the game never crashes because of Vendetta.

## Settings menus
The settings work with **Stonewards ModSettings** (on Thunderstore) and **BepInEx.ConfigurationManager** (F1). Both read the same BepInEx config, and the menu order comes from `ConfigurationManagerAttributes.Order`.

## Saved files
- `BepInEx\config\<GUID>.cfg`: settings
- `BepInEx\config\<GUID>.nemeses.txt`: active Nemeses, one per line. You can edit or delete it.

## Before publishing on Thunderstore
- [ ] Mechanics hooked up and tested in co-op (DISCOVERY.md)
- [x] Marker, messages and countdowns on other players' screens (`src/Net/LobbySync.cs`; they need the mod too)
- [ ] Tested with a friend
- [x] `PluginGuid` in `Plugin.cs` set to `baranmods.stonewards.vendetta` (don't change it: that resets everyone's settings)
- [ ] `website_url` in `thunderstore/manifest.json` set (a GitHub repo is typical)
- [ ] Version matches in three places: `ModVersion` (csproj), `version_number` (manifest), `PluginVersion` (Plugin.cs). Add a CHANGELOG entry.
- [ ] Choose the **AI Generated** category when uploading. Thunderstore asks mods with significant AI-written code to disclose it.
- [ ] The upload is `bin\Vendetta-<version>.zip`

## Changing a version
Bump the three version numbers above, add a line to `thunderstore\CHANGELOG.md`, and rebuild.
