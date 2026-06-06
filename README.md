# LethalPirateClark

> A Backrooms creature. He doesn't chase. He waits. When you look away, he moves. When you look back, he stops.

**Pirate Clark** is a Lethal Company enemy mod based on the *Backrooms: Still Life* entity from Kane Pixels' series — a person the Backrooms copied wrong, wearing a rotted pirate's coat and tricorn, eyes fixed too wide. He reads as set dressing until he doesn't.

**He freezes when watched. He stalks when unobserved. He knocks on doors he can't open. He breaks them down. The player he kills doesn't stay dead — they rise as another Still Life.**

---

## Features

### Phase 1 — the stalker
- **Freeze-when-watched** — he stops moving the instant any player has line-of-sight. He resumes the moment you look away.
- **Acceleration** — the longer he goes unseen, the faster he moves. Base 3.2 m/s, caps at 8 m/s.
- **Light-flicker** — every few hundred milliseconds, the lights in nearby rooms flicker out. When he's about to grab you, they all go dark.
- **Door-knock** — he can't open doors. He knocks three times over ~1.8s, then forces the door open. (Implemented as a Harmony postfix on `EnemyAICollisionDetect.OnTriggerStay`.)
- **Grab kill** — on contact, he grabs and kills via suffocation, with a one-shot eat SFX.

### Phase 2 — the turn
- A player he kills isn't left as a corpse. After ~4 seconds, the body fills with white foam and **rises as a new Still Life** that hunts the survivors.
- Capped by `Conversion.MaxAlive` (default 4) so conversions can't snowball the level.
- The conversion copies are *generic* Still Lifes in Pirate Clark's outfit, not a swappable mesh of the dead player. (Swappable-mesh support is planned.)

---

## Model attribution (required by CC-BY)

**"Pirate Clark (Backrooms)"** by **Slightlyoversizedsweater** on [Sketchfab](https://sketchfab.com/3d-models/pirate-clark-backrooms-e7d8c66f7c4b4b58a4b6e8c6b3d6e8c6) — used under [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/). The Blender source file is included in `model-source/` for transparency.

This mod is **not affiliated with Kane Pixels, Slightlyoversizedsweater, or Zeekerss**. Backrooms content is referenced as fan tribute.

---

## Installation

### Quick install (Thunderstore Mod Manager / r2modman)
1. Install [BepInExPack](https://thunderstore.io/c/lethal-company/p/BepInEx/BepInExPack/) (5.4.2100+)
2. Install [LethalLib](https://thunderstore.io/c/lethal-company/p/Evaisa/LethalLib/) (0.16.0+) — required, this mod registers an enemy via LethalLib
3. Search "LethalPirateClark" in the mod manager and install
4. Launch the game

### Manual install
1. Install BepInExPack and LethalLib (above)
2. Extract `LethalPirateClark_v1.0.1.zip` to your `Lethal Company/` directory
3. Confirm the install looks like:
   ```
   Lethal Company/BepInEx/plugins/LethalPirateClark/
   ├── com.TESTYEE-09.lethalpirateclark.dll
   └── stilllife
   ```
4. Launch the game

---

## Configuration

Edit `BepInEx/config/com.TESTYEE-09.lethalpirateclark.cfg`:

| Section | Key | Default | Description |
|---------|-----|---------|-------------|
| `Spawn` | `Rarity` | `200` | Relative spawn weight on indoor moons. Higher = more common. **200 is the v1.0.1 "I want to actually see him" value** — set to `30-50` for a "feels rare" experience. |
| `Behaviour` | `MoveSpeed` | `3.2` | Base movement speed in m/s when unobserved. Ramps up the longer he stays unseen, capped at 8 m/s. |
| `Conversion` | `Enabled` | `true` | Phase 2: when he kills a player, the corpse rises as a new Still Life. Set to `false` to disable. |
| `Conversion` | `MaxAlive` | `4` | Hard cap on simultaneous Still Lifes (including the original Pirate Clark + any conversions). Prevents the level from snowballing. |

---

## Best moons to test

Pirate Clark is **`isOutsideEnemy: false` and `isDaytimeEnemy: false`** — he only spawns indoors on **Company moons**.

- **`Experimentation`** — smallest Company moon, easiest to find him alone
- **`Titan`** — long hallways, good for testing the door-knock and light-flicker
- **`March`** — medium, good for testing alongside other enemies
- **Skip**: outdoor moons (Zulu, Rend, Artic, etc.) — he won't spawn there

---

## Compatibility

### Verified compatible
- **RugbugRedfern-Skinwalkers** 5.0.0 — different category of mod (client-side voice mimicry on stock enemies). No code-level conflicts.
- **BepInEx-BepInExPack** 5.4.2100
- **Evaisa-LethalLib** 0.16.0 (required dependency)

### Game version
- **Built and tested against Lethal Company v81** (which ships Netcode for GameObjects 1.12.2).
- Earlier game versions may break — `EnemyType.MaxCount` and other field names shift between game updates. Bump this mod's version when the game updates, and the diagnostic in the BepInEx log will tell you what field name changed.

### Bundle build caveat
- The `stilllife` asset bundle is built on **macOS** with Unity 2022.3.9f1 against the game's publicized `Assembly-CSharp.dll`. Asset bundles built for `BuildTarget.StandaloneOSX` are loadable by the Windows game runtime as long as they don't carry game-script references — this bundle's only game-script (`EnemyAICollisionDetect`) is added at runtime by the mod DLL. **This is a common pattern for Mac-built Lethal Company asset bundles and has been verified to work for the v1.0.0+ build.**

---

## Known issues / FAQ

**Q: I see "EnemyType.MaxCount" being skipped in the log.**
A: Field is set as `MaxCount` in current Lethal Company. The mod logs the skip and falls back to the default. Will be fixed in a future version that introspects the actual field name.

**Q: Clark doesn't freeze / accelerate / flicker.**
A: The animator controller depends on the FBX exporting clips named `idle`, `walk`, `grab`, `knock` (case-insensitive). The AI logic runs regardless, but visual states may fall back to a 2-state walk/frozen if the clips aren't found.

**Q: He gets stuck on a door forever.**
A: The knock-then-break routine is triggered by the player or AI approaching a closed door. If the door is locked from the ship's side (player-bought locks), his `OpenOrCloseDoor` call won't bypass that. He should still knock three times. If he doesn't, the BepInEx log will show what API call failed.

**Q: Will he spawn on Ship / Orbstation moons?**
A: No — `Levels.LevelTypes.All` includes all *Company* moons. He won't spawn on the ship interior or on Orb Station, only on Company facility moons.

---

## For modders / how to build from source

Two halves, same as in the repo:

| Half | Where | How to build |
|------|-------|--------------|
| C# behaviour DLL | `src/Plugin/` | `dotnet build -c Release` (requires .NET 8 SDK at `~/.dotnet/dotnet` on this Mac, or anywhere in `PATH` on Windows) |
| Asset bundle | `unity/StillLifeUnity/` | Open in Unity 2022.3.9f1, drop publicized `Assembly-CSharp.dll` into `Assets/Plugins/`, run **StillLife ▸ Build Everything**, grab `AssetBundles/stilllife` |

See [`unity-project-notes/BUILDING_THE_ASSET_BUNDLE.md`](unity-project-notes/BUILDING_THE_ASSET_BUNDLE.md) for the full build walkthrough, including how the Mac editor works around not having the Windows Build Support module installed.

---

## Changelog

See [CHANGELOG.md](CHANGELOG.md).

## License

- **Mod code**: [MIT](LICENSE)
- **Model**: [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/) by Slightlyoversizedsweater
- **"Backrooms" IP**: property of Kane Pixels / Zeekerss — used under fair-use as fan tribute, not endorsed by or affiliated with either

## Credits

- **Programming, AI behaviour, audio wiring, asset-bundle builder**: TESTYEE-09
- **Model**: Slightlyoversizedsweater (CC-BY)
- **Backrooms concept**: Kane Pixels
- **Build pipeline / runtime architecture**: inspired by the Lethal Company modding community on Discord and Thunderstore
