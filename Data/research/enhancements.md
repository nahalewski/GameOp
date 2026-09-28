# Emulator "enhancement" config keys — verified reference

Verified 2026-09-27 against the default branch of each repo (raw.githubusercontent.com + GitHub API trees).
Line numbers are as of that date and will drift. "VERIFIED" = read in source. "UNVERIFIED" = inferred, not read in source.

Repos / branches: PCSX2/pcsx2@master, stenzek/duckstation@master, hrydgard/ppsspp@master,
dolphin-emu/dolphin@master, RPCS3/rpcs3@master, cemu-project/Cemu@main,
xenia-canary/xenia-canary@canary_experimental (default branch, not master), xenia-canary/game-patches@main.

---

## 1. PCSX2 (`inis/PCSX2.ini`, per-game `gamesettings/<SERIAL>_<CRC>.ini`)

Bools are written as `true` / `false`.

| Option | Section | Key | Type | Values | Source |
|---|---|---|---|---|---|
| Widescreen patches (global) | `[EmuCore]` | `EnableWideScreenPatches` | bool | true/false | pcsx2/Pcsx2Config.cpp:2000 |
| No-interlacing patches (global) | `[EmuCore]` | `EnableNoInterlacingPatches` | bool | true/false | pcsx2/Pcsx2Config.cpp:2001 |
| Master patches switch | `[EmuCore]` | `EnablePatches` | bool | default true | Pcsx2Config.cpp:1997, default 1959 |
| CAS mode | `[EmuCore/GS]` | `CASMode` | int enum | 0=Disabled, 1=SharpenOnly, 2=SharpenAndResize | Pcsx2Config.cpp:1067; enum `GSCASMode` Config.h:408 |
| CAS sharpness | `[EmuCore/GS]` | `CASSharpness` | int 0-100 | default 50 | Pcsx2Config.cpp:1068; Config.h:902 |
| Load texture replacements | `[EmuCore/GS]` | `LoadTextureReplacements` | bool | default false | Pcsx2Config.cpp:1034 |
| Async load replacements | `[EmuCore/GS]` | `LoadTextureReplacementsAsync` | bool | default true | Pcsx2Config.cpp:1035 |
| Precache replacements | `[EmuCore/GS]` | `PrecacheTextureReplacements` | bool | default false | Pcsx2Config.cpp:1036 |
| FXAA | `[EmuCore/GS]` | `fxaa` (lowercase) | bool | true/false | Pcsx2Config.cpp:1015 |
| Bilinear texture filtering | `[EmuCore/GS]` | `filter` | int enum `BiFiltering` | 0=Nearest, 1=Forced, 2=PS2 (default in UI), 3=Forced_But_Sprite | Pcsx2Config.cpp:1063; Config.h:314 |
| Trilinear filtering | `[EmuCore/GS]` | `TriFilter` | int enum | -1=Automatic, 0=Off, 1=PS2, 2=Forced | Pcsx2Config.cpp:1087; Config.h:322 |
| Shade Boost on/off | `[EmuCore/GS]` | `ShadeBoost` | bool | true/false | Pcsx2Config.cpp:1016 |
| Shade Boost params | `[EmuCore/GS]` | `ShadeBoost_Brightness`, `ShadeBoost_Contrast`, `ShadeBoost_Saturation`, `ShadeBoost_Gamma` | int | default 50 each | Pcsx2Config.cpp:1091-1094; Config.h:741-744 |

**Per-game widescreen / NI — important:** `Pcsx2Config::ClearInvalidPerGameConfiguration` deletes
`EmuCore/EnableWideScreenPatches` and `EmuCore/EnableNoInterlacingPatches` from per-game inis
("Deprecated in favor of patches", Pcsx2Config.cpp:2145-2149). Per game, enable by patch name instead:

```ini
[Patches]
Enable = Widescreen 16:9
Enable = No-Interlacing
; Disable = <name> removes a patch even if enabled globally
```
Section `Patches`, keys `Enable` / `Disable` (string list — repeated key), names `"Widescreen 16:9"` and
`"No-Interlacing"` (Patch.cpp:115-122, 593-624). The global `EnableWideScreenPatches` flag just auto-adds
the name `Widescreen 16:9` to the enabled list. Cheats use `[Cheats] Enable = <name>` + `[EmuCore] EnableCheats`.

Patch sources: built-in `resources/patches.zip` (Patch.cpp:117, ~294), user `.pnach` files in the `patches/`
folder named `<SERIAL>_<CRC>.pnach` / `<CRC>.pnach` (Patch.cpp:326-330; folder `[Folders] Patches = patches`, Pcsx2Config.cpp:2330).

**Texture replacement folder:** `<DataRoot>/textures/<SERIAL>/replacements/` (dumps in `.../<SERIAL>/dumps/`).
Root from `[Folders] Textures = textures` (Pcsx2Config.cpp:2307/2335); subdir `replacements`
(GS/Renderers/HW/GSTextureReplacements.cpp:39-40, 264). Wrong-case dir names are tolerated (line ~400).

---

## 2. DuckStation (`settings.ini`, per-game `gamesettings/<SERIAL>.ini`)

Source: src/core/settings.cpp unless noted.

| Option | Section | Key | Type | Values | Source |
|---|---|---|---|---|---|
| Widescreen hack (rendering) | `[GPU]` | `WidescreenHack` | bool | default false | settings.cpp:360 (load), 748 (save) |
| Aspect ratio | `[Display]` | `AspectRatio` | string | `Auto (Game Native)` (default), `Stretch To Fill`, `PAR 1:1`, or any `N:D` e.g. `16:9`, `4:3`, `19:9`, `20:9`, `21:9` | settings.cpp:377, parser 2062-2091 |
| Texture replacements | `[TextureReplacements]` | `EnableTextureReplacements` | bool | default false | settings.cpp:588 |
| VRAM-write (background) replacements | `[TextureReplacements]` | `EnableVRAMWriteReplacements` | bool | default false | settings.cpp:590 |
| Preload replacements | `[TextureReplacements]` | `PreloadTextures` | bool | default false | settings.cpp:592 |
| **Required for texture replacements** | `[GPU]` | `EnableTextureCache` | bool | must be true; also needs a HW renderer | settings.cpp:362; forced off otherwise at 1251 |
| Dithering / true color | `[GPU]` | `DitheringMode` | string | `Unscaled`, `UnscaledShaderBlend`, `Scaled`, `ScaledShaderBlend`, `TrueColor` (default), `TrueColorFull` | settings.cpp:339, names 1764-1766; default settings.h:236 |
| Texture filter | `[GPU]` | `TextureFilter` | string | `Nearest` (default), `Bilinear`, `BilinearBinAlpha`, `JINC2`, `JINC2BinAlpha`, `MonotonicCubic`, `MonotonicCubicBinAlpha`, `AdaptiveDiagonal`, `AdaptiveDiagonalBinAlpha`, `DCCI`, `DCCIBinAlpha`, `xBR`, `xBRBinAlpha`, `SharpBilinear`, `Scale2x`, `Scale3x`, `MMPX`, `MMPXEnhanced`, `MMPXAdvanced` | settings.cpp:331, names 1694-1714 |
| Sprite texture filter | `[GPU]` | `SpriteTextureFilter` | string | same list | settings.cpp:335 |
| Display scaling | `[Display]` | `Scaling` (and `Scaling24Bit`) | string | `Nearest`, `NearestInteger`, `BilinearSmooth`, `BilinearHybrid`, `BilinearSharp`, `BilinearInteger`, `Lanczos` | settings.cpp:391-394, names 2240-2242 |
| Post-processing chain | `[PostProcessing]` | `Enabled` (bool), `StageCount` (uint) | | | src/util/postprocessing.cpp:280, 290 |
| Post-processing stage N | `[PostProcessing/Stage1]`, `[PostProcessing/Stage2]`... | `ShaderName` (string), `StageEnabled` (bool, default true) + shader option keys | | | postprocessing.cpp:244-247, 285, 294 |

- There is **no dedicated TrueColor bool** anymore — true colour is the `DitheringMode=TrueColor` value.
- **No FSR or CAS exists in DuckStation master** (no settings key, no bundled shader; repo tree search for fsr/cas finds nothing).
  The only bundled sharpening shader is `data/resources/shaders/simple-sharpen.glsl` → `ShaderName = simple-sharpen`
  (glsl shaders are looked up as `shaders/{name}.glsl`, postprocessing.cpp:925/966). Other bundled: `reshade/Shaders/anti-aliasing/fxaa.fx`, `edge-smoothing/super-xbr.fx` etc.
  (UNVERIFIED: exact ShaderName format for reshade subfolder shaders; code builds `reshade/Shaders/{name}.fx`, so probably `anti-aliasing/fxaa`.)
- An `[InternalPostProcessing]` section also exists (settings.cpp:181) — not investigated.

**Texture pack folder:** `<DataRoot>/textures/<SERIAL>/replacements/` (older layout without `replacements/`
still accepted). Dumps go in `.../<SERIAL>/dumps/`. Optional per-game `textures/<SERIAL>/config.yaml`.
Multi-disc games fall back to the first disc's serial folder. Root: `[Folders] Textures = textures`.
Source: src/core/gpu_hw_texture_cache.cpp:60, 3523-3545, 3567-3575, 3602-3607; settings.cpp:2906.

---

## 3. PPSSPP (`memstick/PSP/SYSTEM/ppsspp.ini`; per-game `PSP/SYSTEM/<GAMEID>_ppsspp.ini`)

All below are in `[Graphics]` (section name Config.cpp:1155) and are PER_GAME capable.

| Option | Key | Type | Values | Source |
|---|---|---|---|---|
| Texture replacement | `ReplaceTextures` | bool | **default True** | Core/Config.cpp:739 |
| Save new textures (dump) | `SaveNewTextures` | bool | default False | Config.cpp:740 |
| CPU texture upscale level | `TexScalingLevel` | int | 1 = Off (default), 2..5 = 2x..5x (values <=0 clamped to 1, Config.cpp:1603) | Config.cpp:744 |
| CPU texture upscale type | `TexScalingType` | int | 0=xBRZ, 1=Hybrid, 2=Bicubic, 3=Hybrid+Bicubic | Config.cpp:745; GPU/Common/TextureScalerCommon.h:37 |
| Deposterize | `TexDeposterize` | bool | | Config.cpp:746 |
| GPU texture upscale shader | `TextureShader` | string | `Off` (default) or a `Type=Texture` section id from defaultshaders.ini: `Tex2xBRZ`, `Tex4xBRZ`, `TexSpline36`, `TexSpline36_4x`, `TexMMPX`, `TexMMPXAdvanced`, `TexNNEDI3NNS16`, `TexNNEDI3NNS16_4x`, `TexNNEDI3NNS16Single`, `TexNNEDI3NNS16Single4x`, `TexSmiley` | Config.cpp:754 |
| Texture filtering | `TextureFiltering` | int | 1=Auto (default), 2=Nearest, 3=Linear, 4=Auto Max Quality | Config.cpp:703; Core/ConfigValues.h:99-104 |
| Anisotropy | `AnisotropyLevel` | int 0-4 | default 4 (=16x) | Config.cpp:720 |
| (`TexUpscaleType` does not exist — it is `TexScalingType`.) | | | | |

**Post-processing:** not in `[Graphics]`; own sections:
```ini
[PostShaderList]
PostShader1 = FSR-EASU
PostShader2 = FSR-RCAS
[PostShaderSetting]
FSR-RCASSettingCurrentValue1 = 0.500000
```
Chain keys `PostShader1..N` (Config.cpp:1377, 1405-1409, 1507-1512; value `Off` ignored). Setting key format
`<SectionId>SettingCurrentValue<N>` (GPU/Common/PresentationCommon.cpp:484). Values are the **section ids**
(bracketed names) from assets/shaders/defaultshaders.ini:

| Section id (value to write) | Display name | defaultshaders.ini line |
|---|---|---|
| `FSR-EASU` | FSR-EASU (upscale) | 308 |
| `FSR-RCAS` | FSR-RCAS (sharpen); setting 1 Sharpness 0.0-1.0, default 0.5 | 315 |
| `Sharpen` | Sharpen | 77 |
| `FXAA` | FXAA Antialiasing | 2 |
| `UpscaleSharpBilinear`, `UpscaleBicubic`, `UpscaleSpline36`, `5xBR`, `5xBR-lv2`, `4xHqGLSL`, `ColorCorrection`, `Natural`, `NaturalA`, `CRT`, `Scanlines`, `Bloom`, `LCDPersistence`, ... | | various |

No standalone "CAS" shader — RCAS (FSR's sharpener) is the CAS-like option.

**Texture pack folder:** `<memstick>/PSP/TEXTURES/<GAMEID>/` containing `textures.ini` (+ images) or a single
`textures.zip`. Sources: Core/Util/PathUtil.cpp:114-115 (`pspDirectory / "TEXTURES"`),
GPU/Common/TextureReplacer.cpp:46-47, 92-98, 157. GAMEID = disc ID from PARAM.SFO, e.g. `ULUS10041`.
textures.ini `[games]` section can redirect other IDs to the same pack (TextureReplacer.cpp:183).

---

## 4. Dolphin (`User/Config/GFX.ini`; per-game `User/GameSettings/<GAMEID>.ini`)

Source: Source/Core/Core/Config/GraphicsSettings.cpp. Enums stored as ints.

| Option | GFX.ini section | Per-game section | Key | Type / values | Line |
|---|---|---|---|---|---|
| Custom (HD) textures | `[Settings]` | `[Video_Settings]` | `HiresTextures` | bool, default False | 69 |
| Prefetch custom textures | `[Settings]` | `[Video_Settings]` | `CacheHiresTextures` | bool, default False | 70 |
| Widescreen hack | `[Settings]` | `[Video_Settings]` | `wideScreenHack` (lowercase w) | bool | 20 |
| Aspect ratio | `[Settings]` | `[Video_Settings]` | `AspectRatio` | 0=Auto, 1=ForceWide(16:9), 2=ForceStandard(4:3), 3=Stretch, 4=Custom, 5=CustomStretch, 6=Raw | 21; VideoCommon/VideoConfig.h:22-31 |
| Graphics mods | `[Settings]` | `[Video_Settings]` | `EnableMods` | bool | 133 |
| Post-processing shader | `[Enhancements]` | `[Video_Enhancements]` | `PostProcessingShader` | string = filename **without** `.glsl`, "" = off | 148-149; VideoCommon/PostProcessing.cpp:45-50 |
| Arbitrary mipmap detection | `[Enhancements]` | `[Video_Enhancements]` | `ArbitraryMipmapDetection` | bool, default False (CONFIRMED) | 154-155 |
| (threshold) | `[Enhancements]` | | `ArbitraryMipmapDetectionThreshold` | float, default 14.0 | 156-157 |
| Texture filtering | `[Enhancements]` | `[Video_Enhancements]` | `ForceTextureFiltering` | 0=Default, 1=Nearest, 2=Linear | 142-143; VideoConfig.h:51 |
| Anisotropy | `[Enhancements]` | `[Video_Enhancements]` | `MaxAnisotropy` | -1=Default, 0=1x, 1=2x, 2=4x, 3=8x, 4=16x | 144-145; VideoConfig.h:58 |
| Output resampling | `[Enhancements]` | `[Video_Enhancements]` | `OutputResampling` | 0=Default, 1=Bilinear, 2=BSpline, 3=MitchellNetravali, 4=CatmullRom, 5=SharpBilinear, 6=AreaSampling | 146-147; VideoConfig.h:68 |

Per-game section mapping `Video_Settings -> GFX Settings`, `Video_Enhancements -> GFX Enhancements`:
Source/Core/Core/ConfigLoaders/GameConfigLoader.cpp:113-114.

**Built-in FSR / CAS / sharpen shaders: NONE.** Data/Sys/Shaders contains only FXAA.glsl, AutoHDR, PerceptualHDR,
integer_scaling, 16bit/32bit, and novelty filters (sepia, grayscale, toon, bloom...). To offer FSR/CAS you would have to ship
your own `.glsl` into `User/Shaders/`.

**Texture pack folder:** `User/Load/Textures/<GAMEID>/` (6-char ID e.g. `GALE01`), falls back to 3-char
region-free `User/Load/Textures/<GAMEID[0:3]>/`. Also any top-level folder under Load/Textures containing a
`<GAMEID>.txt` / `<first3>.txt` / `all.txt` marker file anywhere inside it is used.
Source: VideoCommon/HiresTextures.cpp:86-87, 201-245; Common/CommonPaths.h:61-62.

---

## 5. RPCS3 (`config/config.yml` on Windows)

Source: rpcs3/Emu/system_config.h, node `Video:` (line 114).

| Option | YAML path | Type | Values | Source |
|---|---|---|---|---|
| Output scaling / FSR | `Video: Output Scaling Mode` | enum string | `Nearest`, `Bilinear` (default), `FidelityFX Super Resolution` | system_config.h:179; strings system_config_types.cpp:700-702 |
| CAS / RCAS sharpening | `Video: FidelityFX CAS Sharpening Intensity` | uint 0-100 | default 50 | system_config.h:183 |
| Resolution scale | `Video: Resolution Scale` | uint 25-800 | default 100 | :166 |
| Anisotropic override | `Video: Anisotropic Filter Override` | uint 0-16 | 0 = auto | :167 |

Both FSR keys are directly under `Video:` (not under `Vulkan:`). FSR only takes effect with the Vulkan renderer (UNVERIFIED in source here; not checked).

Per-game configs: `config/custom_configs/config_<TITLEID>.yml` — UNVERIFIED (not read this pass).

Config location on Windows: `<rpcs3 dir>/config/config.yml` (Emu/System.cpp:505 via `fs::get_config_dir(true)`,
which appends `config/` only on _WIN32, Utilities/File.cpp:2585-2590). Old `<rpcs3 dir>/config.yml` is migrated.

**Patches**
- Patch database: `<rpcs3 dir>/patches/patch.yml` (`get_patches_path()` = `get_config_dir() + "patches/"`, Utilities/bin_patch.cpp:141-144, 893-894).
  Also loads `patches/imported_patch.yml` and `patches/<TITLEID>_patch.yml` (bin_patch.cpp:146-148, 897, 908).
- Download URL: `https://rpcs3.net/compatibility?patch&api=v1&v=1.2` (+ `&sha256=<hash of current patch.yml>` if one exists)
  — rpcs3qt/patch_manager_dialog.cpp:1156-1165; `patch_engine_version = "1.2"` (bin_patch.h:31).
  Response is JSON: `return_code` (0 = new data, 1 = already up to date, <0 = error), `version`, `sha256`, `patch` (YAML text);
  the client verifies sha256 of `patch` then writes it to patches/patch.yml (patch_manager_dialog.cpp:1188-1290).
- Enabled state: `<rpcs3 dir>/config/patch_config.yml` (bin_patch.cpp:128-139, `get_config_dir(true)`). Format written by
  `save_config` (bin_patch.cpp:1642+):

```yaml
<PPU hash e.g. PPU-b8c34f774adb367761706a7f685d4f8d9d355426>:
  "<patch description>":
    "<game title>":
      <SERIAL e.g. BLUS30443>:
        <app version e.g. 01.00>:
          Enabled: true
          Configurable Values:      # only if changed from defaults
            <name>: <value>
```
Keys `Enabled` / `Configurable Values` from bin_patch.h patch_key namespace (lines 11-29). Title/serial/version may be
`All` (patch_key::all). Only entries that are enabled (or have non-default values) are written.

---

## 6. Cemu

- **Download source:** Cemu first queries `https://cemu.info/api2/query_graphicpack_url.php?version=X.Y.Z&t=<unix>`; on failure it uses
  `https://api.github.com/repos/cemu-project/cemu_graphic_packs/releases/latest` and downloads **`assets[0].browser_download_url`**
  (src/gui/wxgui/DownloadGraphicPacksWindow.cpp:117-178). Current latest release (checked via API): tag `Github982`, asset
  `graphicPacks982.zip` (pattern `graphicPacks<build>.zip`), published 2026-09-22.
- **Folder:** extracted into `<UserDataPath>/graphicPacks/downloadedGraphicPacks/<pack dirs>`, with `version.txt` there recording the
  release name (DownloadGraphicPacksWindow.cpp:69, 82, 94, 234-254). Cemu loads **any** folder under `graphicPacks/` containing
  `rules.txt` (recursive; GraphicPack2.cpp:97-111), so custom packs can live in `graphicPacks/<anything>/`.
- **Enable in settings.xml** (src/config/CemuConfig.cpp:339-358 save, 90-120 load):

```xml
<GraphicPack>
  <Entry filename="graphicPacks/downloadedGraphicPacks/BreathOfTheWild/Graphics/rules.txt">
    <Preset>
      <category>Resolution</category>   <!-- omitted when preset has no category -->
      <preset>2560x1440</preset>
    </Preset>
  </Entry>
  <Entry filename="graphicPacks/.../rules.txt" disabled="true"/>
</GraphicPack>
```
  - An `Entry` present **without** `disabled="true"` = enabled (GraphicPack2.cpp:132-149).
  - `filename` is the rules.txt path relative to the user data dir (`GetNormalizedPathString()` = `MakeRelativePath(GetUserDataPath(), rulesPath).lexically_normal()`, GraphicPack2.cpp:592-595); absolute paths also accepted (legacy). Written via fs::path so on Windows Cemu itself will likely write backslashes; lookup compares fs::path objects so either separator should match (UNVERIFIED at runtime).
  - Legacy format with child `<filename>`,`<category>`,`<preset>` elements is still read (CemuConfig.cpp:94-101).
- UserDataPath location (portable folder vs %APPDATA%\Cemu): UNVERIFIED this pass (ActiveSettings not read).

---

## 7. Xenia Canary

- **Repo layout (xenia-canary/game-patches@main):** flat folder `patches/` with ~496 files named
  `<TITLEID> - <Game Name>[ (variant)].patch.toml`, e.g. `patches/415407D7 - Catherine (USA).patch.toml`.
- **Release download:** `https://github.com/xenia-canary/game-patches/releases/download/latest/game-patches.7z` (+ `version.txt`) — tag `latest` (GitHub API).
- **Load location:** `<storage_root>/patches/`. Only files matching regex `^[A-Fa-f0-9]{8}.*\.patch\.toml$` are loaded
  (src/xenia/patcher/patch_db.cc:35-47; patch_db.h:121-122). `storage_root` = exe folder when portable; **on Windows `portable`
  defaults to true** (src/xenia/app/xenia_main.cc:122-130, 487-501), otherwise `<user folder>/Xenia`. So normally `<xenia dir>/patches/`.
  Patcher is built with `storage_root_` (src/xenia/emulator.cc:309).
- **Config key:** `[General] apply_patches = true` in `xenia-canary.config.toml` (default **true**; patch_db.cc:17-18; config name src/xenia/config.cc:34).
- **Enable an individual patch:** set `is_enabled = true` inside its `[[patch]]` table in the .patch.toml (default false; patch_db.cc:257, 268-270).
  File header needs `title_name`, `title_id` (hex string), `hash` (string or array of module hashes) — patches only apply when title_id AND hash match
  (patch_db.cc:72-81, 210-220). Example:

```toml
title_name = "Catherine"
title_id = "415407D7"
hash = "C451BB35FB61698F"
[[patch]]
    name = "1920x1080 Resolution"
    author = "Sowa_95"
    is_enabled = true
    [[patch.be16]]
        address = 0x8204a9a2
        value = 0x0780
```
Note: re-downloading the pack overwrites your `is_enabled` edits, so the app must re-apply them after updating.
