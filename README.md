# GameOp – ROG Ally Optimizer

A native Windows 11 app (Fluent design, Mica, dark/light) that tunes ROG Ally handhelds for emulation. It sets the device's power per performance level, writes per-game emulator settings from a curated database, switches power automatically per game, and keeps your emulators up to date.

## Download

| Version | File | Notes |
|---|---|---|
| **Installer** | [`releases/v1.0.0/GameOp-Setup-v1.0.0.exe`](releases/v1.0.0/GameOp-Setup-v1.0.0.exe) | Installs to Program Files and adds Start menu and optional desktop shortcuts. Uninstall from Windows Settings. |
| **Portable** | [`releases/v1.0.0/GameOp-Portable-v1.0.0.zip`](releases/v1.0.0/GameOp-Portable-v1.0.0.zip) | Unzip anywhere, even a microSD card. Settings and backups stay in the `UserData` folder next to `GameOp.exe`. |

Both are self-contained (no .NET install needed), 64-bit, Windows 10 2004 / Windows 11. GameOp asks for admin rights because TDP and power settings need them.

---

## All features

### Device detection and specs
- Detects the model from the ASUS model code and APU:
  - **ROG Ally** (RC71L): Ryzen Z1, 740M (4 CU)
  - **ROG Ally** (RC71L): Ryzen Z1 Extreme, 780M (12 CU)
  - **ROG Ally X** (RC72LA): Z1 Extreme, 24 GB LPDDR5X, 80 Wh
  - **ROG Xbox Ally** (RC73YA): Ryzen Z2 A, 8 CU RDNA 2
  - **ROG Xbox Ally X** (RC73XA): Ryzen AI Z2 Extreme, 890M (16 CU)
- Dashboard shows CPU, GPU, RAM, display, battery size, charge level / AC status and the current refresh rate.
- Safe TDP limits per model, clamped so a level can never exceed what the hardware allows.
- You can override the detected model manually. On a non-Ally PC GameOp runs in **preview mode**: emulator configs still work, but hardware control is off.

### Four performance levels, tuned per model
| Level | What it does |
|---|---|
| **Battery Saver** | Low TDP (e.g. 9–10 W), 60 Hz, CPU boost off, Windows "best power efficiency", native-ish resolution |
| **Balanced** | Mid TDP (10–17 W), 120 Hz, 2x upscaling where it's cheap |
| **Performance** | High TDP (15–25 W), Windows "best performance", 1080p-class upscaling |
| **Max Out** | Top TDP (20–35 W), highest resolution scale, anisotropic filtering and accuracy the GPU can handle |

- **TDP control:** sustained (SPL), slow boost (SPPT) and fast boost (FPPT) limits through the ASUS ATKACPI driver, the same interface Armoury Crate uses. RyzenAdj works as an optional fallback.
- **Armoury Crate mode:** Silent / Performance / Turbo, set together with the level.
- **Windows power mode overlay:** best efficiency / balanced / best performance.
- **Processor boost:** off on Battery Saver, aggressive otherwise.
- **Refresh rate:** 60 Hz on Battery Saver, 120 Hz otherwise.
- **Weaker GPUs:** the 4-CU Z1 and the Z2 A automatically get lower resolution scales at Performance and Max Out.
- **Applying a level** can also write every detected emulator's global graphics settings in one click.

### Per-game settings database (137 games, 8 emulator families)
- **Contents:** games are sourced from official emulator sources:
  - RPCS3 wiki + live compatibility API: 32 PS3 games
  - PCSX2 GameIndex + wiki: 31 PS2 games
  - DuckStation gamedb: 11 PS1 games
  - Dolphin's shipped GameSettings: 14 GameCube/Wii games
  - PPSSPP compat.ini: 12 PSP games
  - Cemu's shipped game profiles: 12 Wii U games
  - Xenia Canary compatibility tracker: 11 Xbox 360 games
  - Community compatibility lists: 14 Switch games
- **Per entry:** regional serials / title IDs, compatibility, demand rating, recommended level, required fixes, notes and a source link.
- **Settings are layered:** level baseline → weaker-GPU adjustments → the game's fixes → the game's per-level fixes.
- **Preview:** see exactly which settings will be written before applying.
- **Apply profile** writes the chosen level. **Max out** applies the highest level in one click. **Power only** sets TDP without touching configs.
- **Per-game level:** choose a level per game; it's remembered and used by auto power.
- **Library search:** by title or serial, filter by emulator, or show only games found in your library.
- **Live RPCS3 status:** pulled from rpcs3.net when you open a PS3 game.
- **Updating the database:** download from a URL, import a JSON file, or reset to the built-in version. Downloaded entries override built-in ones.

### Emulator support
| Emulator | System | Global defaults | Per-game profiles | Format |
|---|---|---|---|---|
| PCSX2 | PS2 | ✓ | ✓ (serial + CRC, read from your .iso files) | INI |
| RPCS3 | PS3 | ✓ | ✓ `custom_configs/config_<serial>.yml` | YAML |
| DuckStation | PS1 | ✓ | ✓ `gamesettings/<serial>.ini` | INI |
| PPSSPP | PSP | ✓ | ✓ `<ID>_ppsspp.ini` (seeded from your global config) | INI |
| Dolphin | GC / Wii | ✓ (GFX.ini + Dolphin.ini) | ✓ `GameSettings/<ID>.ini` | INI |
| Cemu | Wii U | ✓ `settings.xml` | ✓ `gameProfiles/<titleid>.ini` | XML / INI |
| Xenia Canary | Xbox 360 | ✓ | global only | TOML |
| Ryujinx | Switch | ✓ `Config.json` | global only | JSON |
| Eden / Citron / Sudachi | Switch | ✓ `qt-config.ini` | ✓ `custom/<TitleID>.ini` | INI |

- **Detection:** finds emulators automatically in Program Files, AppData, EmuDeck, RetroBat, `Emulation` / `Emulators` / `Games` folders on every drive, and any folder you add. You can also point to an exe yourself. Portable and installed configs are both handled.
- **Bulk install:** writes profiles for every database game at once, each at its own level or one forced level.
- **Library scan:** reads RPCS3's `games.yml` and `dev_hdd0`, and your PCSX2 game folders (ISO9660 parsing gives the serial + ELF CRC PCSX2 needs).
- **Surgical edits:** only the keys GameOp sets are changed. Comments, ordering and every other setting are kept.

### Automatic emulator updates
- **Checks:** at start-up and every 6 hours, against each emulator's **official GitHub releases**:
  - PCSX2
  - RPCS3
  - DuckStation
  - PPSSPP
  - Cemu
  - Xenia Canary
- **Auto-install:** installs updates on its own (can be turned off). It skips any emulator that's running and retries next time.
- **Pre-releases:** optional, for PCSX2 nightlies and other preview builds.
- **Checksums:** every download is **SHA-256 verified** before anything changes.
- **Install:** missing emulators can be installed with one click into a folder you choose (default `C:\Emulators`).
- **Your files are never touched:** an update never deletes anything. It only adds or replaces program files from the release, and never overwrites:
  - BIOS dumps (`bios\`, `scph*.bin`, `*.bin`, `*.rom`), PS3 firmware (`PS3UPDAT.PUP`, `dev_flash`)
  - Switch keys and firmware (`keys\`, `*.keys`, `nand\`)
  - saves, memory cards, save states, `dev_hdd0`, cheats, patches, texture packs, shader caches
  - configs next to the exe (`portable.ini/txt`, `settings.ini`, `*.toml`, `config\`, `inis\`, `gamesettings\` …)
- **Rollback:** every replaced program file is backed up first. **Roll back update** restores the previous version.
- **Built-in updaters:** Dolphin, Ryujinx and the yuzu forks use their own updaters; GameOp tells you where to find them.

### Auto power per game
- Watches the foreground window, recognises the running game by its serial / title ID or title in the emulator's window, and switches TDP to that game's level.
- Returns to your selected level about 15 seconds after the game closes.

### System tweaks (every one can be reverted)
- **Windows:** Game Mode on; background recording (Game DVR) off; hardware-accelerated GPU scheduling.
- **Memory Integrity:** optional switch to turn it off, with a clear security warning.
- **Per-level switches:** power mode, CPU boost and refresh rate can each be turned off.
- **Tips:** VRAM (UMA frame buffer) size for your model, drivers, RSR and storage speed.
- **Revert:** original registry values are recorded and restored when you turn a tweak off.

### Safety and backups
- **First change:** each config file is backed up as `*.gameop-original` the first time GameOp changes it.
- **Every write:** also gets a timestamped copy in the backups folder.
- **Restore originals:** one click per emulator puts back every original and removes the profiles GameOp created.
- **Activity log:** on the dashboard and in `gameop.log`.

### Made for a handheld
- **Controller:** D-pad / left stick to move, **A** to select, **B** to go back, **LB / RB** to switch pages (XInput).
- **Layout:** large touch targets and a layout designed for the 7" 1080p screen.

---

## Build from source

Requires the .NET 9 SDK, plus Inno Setup 6 for the installer.

```powershell
.\build.ps1 -Version 1.0.0     # → dist\GameOp-Portable-v1.0.0.zip and dist\GameOp-Setup-v1.0.0.exe
dotnet build -p:NoElevate=true # non-admin build for UI work
dotnet run --project tests\SmokeTest            # config-writer / updater-rule tests
dotnet run --project tests\SmokeTest -- --download  # also tests real DuckStation + PCSX2 updates in a temp folder
```

## Project layout
```
Data/devices.json      ROG Ally models, specs, TDP per level and hardware limits
Data/emulators.json    Per-level baseline settings for each emulator (+ weaker-GPU adjustments)
Data/gamedb.json       Per-game database (built from Data/research/*.json by tools/merge_gamedb.py)
Services/              Detection, TDP/ACPI, config writers, emulator adapters, updater, tweaks
Pages/                 Dashboard, Game Library, Emulators, System Tweaks, Settings
installer/GameOp.iss   Inno Setup script
```

## Adding or correcting games

Edit `Data/research/<emulator>.json` and run `python tools/merge_gamedb.py`, or import a JSON file from Settings:

```json
{ "emulator": "rpcs3", "title": "…", "ids": ["BLUS00000"], "demand": "heavy",
  "recommendedTier": "performance", "settings": { "Video": { "Write Color Buffers": true } },
  "tierSettings": { "battery": { "Video": { "Resolution Scale": 100 } } }, "notes": "…", "source": "https://…" }
```

## Notes
- **Estimates:** demand ratings and recommended levels are estimates for Z1-class hardware; tune them to taste.
- **TDP limits:** the ASUS ACPI power limits match what Armoury Crate uses. Keep Armoury Crate SE installed for the driver, but don't let it fight GameOp by switching modes at the same time.
- **Not affiliated:** GameOp is not affiliated with ASUS, AMD, Microsoft or any emulator project.
