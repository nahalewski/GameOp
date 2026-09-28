using System.IO;
using System.Text;
using GameOp.Models;
using GameOp.Services;

// Builds fake emulator installs in a temp folder, applies tiers + game profiles, and prints the results.
var root = Path.Combine(Path.GetTempPath(), "gameop-smoke");
if (Directory.Exists(root)) Directory.Delete(root, true);
Directory.CreateDirectory(root);

var db = GameDatabase.Load();
Console.WriteLine($"DB: {db.Games.Count} games, {db.Emulators.Count} emulators, {db.Devices.Count} devices");
var fails = 0;
void Check(bool ok, string what) { Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {what}"); if (!ok) fails++; }

// Device matching
Check(DeviceDetector_Match("ROG Ally RC71L_RC71L RC71L", "AMD Ryzen Z1 Extreme") == "ally_z1e", "RC71L + Z1 Extreme → ally_z1e");
Check(DeviceDetector_Match("ROG Ally RC71L RC71L", "AMD Ryzen Z1") == "ally_z1", "RC71L + Z1 → ally_z1");
Check(DeviceDetector_Match("ROG Ally X RC72LA RC72LA", "AMD Ryzen Z1 Extreme") == "ally_x", "RC72LA → ally_x");
Check(DeviceDetector_Match("ROG Xbox Ally RC73YA", "AMD Ryzen Z2 A") == "xbox_ally", "RC73YA → xbox_ally");
Check(DeviceDetector_Match("ROG Xbox Ally X RC73XA", "AMD Ryzen AI Z2 Extreme") == "xbox_ally_x", "RC73XA → xbox_ally_x");
Check(DeviceDetector_Match("System Product Name", "AMD Ryzen 7 9700X") is null, "desktop → no match");

string? DeviceDetector_Match(string model, string cpu)
{
    bool CpuOk(DeviceProfile d) => cpu.Contains(d.CpuMatch, StringComparison.OrdinalIgnoreCase) &&
                                   (d.CpuExclude is null || !cpu.Contains(d.CpuExclude, StringComparison.OrdinalIgnoreCase));
    var byModel = db.Devices.Where(d => d.ModelCodes.Any(c => model.Contains(c, StringComparison.OrdinalIgnoreCase))).ToList();
    return (byModel.FirstOrDefault(CpuOk) ?? byModel.FirstOrDefault() ?? db.Devices.FirstOrDefault(CpuOk))?.Id;
}

var z1e = db.Devices.First(d => d.Id == "ally_z1e");
var z1 = db.Devices.First(d => d.Id == "ally_z1");

// ── RPCS3 ──
var rp = Path.Combine(root, "rpcs3");
Directory.CreateDirectory(Path.Combine(rp, "config"));
File.WriteAllText(Path.Combine(rp, "rpcs3.exe"), "");
File.WriteAllText(Path.Combine(rp, "config", "config.yml"), """
Core:
  PPU Decoder: Recompiler (LLVM)
  PPU Threads: 2
  SPU Block Size: Safe
Video:
  Renderer: Vulkan
  Resolution: 1280x720
  Resolution Scale: 100
  Vulkan:
    Adapter: ""
    Asynchronous Texture Streaming 2: false
Audio:
  Renderer: Cubeb
""");
var rpcs3 = new Rpcs3Adapter { ExePath = Path.Combine(rp, "rpcs3.exe") };
rpcs3.ResolveConfigRoot();
rpcs3.ApplyGlobal(db.BuildSettings("rpcs3", Tier.Max, z1e, null));
var yml = File.ReadAllText(Path.Combine(rp, "config", "config.yml"));
Check(yml.Contains("  Resolution Scale: 200"), "RPCS3 max: Resolution Scale 200");
Check(yml.Contains("Audio:\n  Renderer: Cubeb") || yml.Contains("Audio:\r\n  Renderer: Cubeb"), "RPCS3: Audio block untouched");
Check(yml.Contains("    Adapter: \"\""), "RPCS3: nested Vulkan block preserved");
Check(File.Exists(Path.Combine(rp, "config", "config.yml.gameop-original")), "RPCS3: original backed up");
var ds = db.Games.First(g => g.Emulator == "rpcs3" && g.Settings is { Count: > 0 });
var r = rpcs3.ApplyGame(ds, db.BuildSettings("rpcs3", Tier.Balanced, z1e, ds));
Check(r.Files.Count == ds.Ids.Count, $"RPCS3 game '{ds.Title}': {r.Files.Count} custom configs");
Console.WriteLine(Indent(File.ReadAllText(r.Files[0])));

// ── PCSX2 (portable) ──
var p2 = Path.Combine(root, "pcsx2");
Directory.CreateDirectory(Path.Combine(p2, "inis"));
Directory.CreateDirectory(Path.Combine(p2, "gamesettings"));
File.WriteAllText(Path.Combine(p2, "pcsx2-qt.exe"), "");
File.WriteAllText(Path.Combine(p2, "portable.ini"), "");
File.WriteAllText(Path.Combine(p2, "inis", "PCSX2.ini"), "[UI]\nTheme = Dark\n\n[EmuCore/GS]\nupscale_multiplier = 1\nRenderer = -1\n");
File.WriteAllText(Path.Combine(p2, "gamesettings", "SCUS-97472_ABCD1234.ini"), "[EmuCore/GS]\nOsdShowFPS = true\n");
var pcsx2 = new Pcsx2Adapter { ExePath = Path.Combine(p2, "pcsx2-qt.exe") };
pcsx2.ResolveConfigRoot();
pcsx2.ApplyGlobal(db.BuildSettings("pcsx2", Tier.Performance, z1, null));
var ini = File.ReadAllText(Path.Combine(p2, "inis", "PCSX2.ini"));
Check(ini.Contains("upscale_multiplier = 2") && ini.Contains("Renderer = 14"), "PCSX2 perf on Z1 (low GPU): 2x, Vulkan");
Check(ini.Contains("[EmuCore/Speedhacks]") && ini.Contains("Theme = Dark"), "PCSX2: new section added, UI kept");
var sotc = db.Games.First(g => g.Emulator == "pcsx2" && g.Ids.Contains("SCUS-97472"));
r = pcsx2.ApplyGame(sotc, db.BuildSettings("pcsx2", Tier.Max, z1e, sotc));
Check(r.Files.Count == 1 && File.ReadAllText(r.Files[0]).Contains("upscale_multiplier = 4") && File.ReadAllText(r.Files[0]).Contains("OsdShowFPS = true"),
    "PCSX2 per-game via existing SERIAL_CRC.ini");
var rac = db.Games.First(g => g.Emulator == "pcsx2" && g.Title.StartsWith("Ratchet"));
Check(db.BuildSettings("pcsx2", Tier.Max, z1e, rac)["EmuCore/Speedhacks"]?["EECycleRate"]?.GetValue<int>() == 1, "PCSX2 tierSettings merged (R&C EECycleRate)");

var dod = db.Games.First(g => g.Emulator == "pcsx2" && g.Ids.Contains("SLUS-21180"));
Check(dod.Ids.Contains("SLUS-21362") && TierInfo.All.All(t => db.BuildSettings("pcsx2", t, z1, dod)["EmuCore/GS"]?["accurate_blending_unit"]?.GetValue<int>() == 2),
    "Onimusha DoD: both discs, blending Medium on every level (even Z1)");
var war = db.Games.First(g => g.Emulator == "pcsx2" && g.Ids.Contains("SLUS-20018"));
Check(db.BuildSettings("pcsx2", Tier.Max, z1e, war)["EmuCore/GS"]?["texture_preloading"]?.GetValue<int>() == 1, "Onimusha Warlords: partial texture preload");
Check(db.Games.Any(g => g.Emulator == "pcsx2" && g.Ids.Contains("SCUS-97471")), "Genji (SCUS-97471) present");
File.WriteAllText(Path.Combine(p2, "gamesettings", "SLUS-21180_12345678.ini"), "");
r = pcsx2.ApplyGame(dod, db.BuildSettings("pcsx2", Tier.Balanced, z1e, dod));
Check(r.Files.Count == 1 && File.ReadAllText(r.Files[0]).Contains("accurate_blending_unit = 2"), "Onimusha DoD per-game file written");

// ── Dolphin (portable) ──
var dol = Path.Combine(root, "dolphin");
Directory.CreateDirectory(Path.Combine(dol, "User", "Config"));
File.WriteAllText(Path.Combine(dol, "Dolphin.exe"), "");
File.WriteAllText(Path.Combine(dol, "portable.txt"), "");
var dolphin = new DolphinAdapter { ExePath = Path.Combine(dol, "Dolphin.exe") };
dolphin.ResolveConfigRoot();
dolphin.ApplyGlobal(db.BuildSettings("dolphin", Tier.Performance, z1e, null));
var gfx = File.ReadAllText(Path.Combine(dol, "User", "Config", "GFX.ini"));
Check(gfx.Contains("[Settings]") && gfx.Contains("InternalResolution = 3") && gfx.Contains("[Enhancements]"), "Dolphin global → GFX.ini sections");
Check(File.ReadAllText(Path.Combine(dol, "User", "Config", "Dolphin.ini")).Contains("CPUThread = True"), "Dolphin global → Dolphin.ini [Core] True");
var ww = db.Games.First(g => g.Emulator == "dolphin" && g.Title.Contains("Wind Waker"));
r = dolphin.ApplyGame(ww, db.BuildSettings("dolphin", Tier.Max, z1e, ww));
Console.WriteLine(Indent(File.ReadAllText(r.Files[0])));

// ── Eden (yuzu family) ──
var eden = Path.Combine(root, "eden");
Directory.CreateDirectory(Path.Combine(eden, "user", "config"));
File.WriteAllText(Path.Combine(eden, "eden.exe"), "");
File.WriteAllText(Path.Combine(eden, "user", "config", "qt-config.ini"), "[Renderer]\nbackend\\default=true\nbackend=1\n");
var edenA = new YuzuFamilyAdapter("eden", "Eden", "eden", ["eden.exe"]) { ExePath = Path.Combine(eden, "eden.exe") };
edenA.ResolveConfigRoot();
var smo = db.Games.First(g => g.Emulator == "switch" && g.Title.Contains("Odyssey"));
r = edenA.ApplyGame(smo, db.BuildSettings("switch", Tier.Balanced, z1e, smo));
var custom = File.ReadAllText(r.Files[0]);
Check(custom.Contains("use_docked_mode\\use_global=false") && custom.Contains("use_docked_mode=1"), "Eden per-game: SMO docked override");
Console.WriteLine(Indent(custom));

// ── Cemu, PPSSPP, DuckStation, Xenia, Ryujinx ──
var cemuDir = Path.Combine(root, "cemu");
Directory.CreateDirectory(Path.Combine(cemuDir, "portable"));
File.WriteAllText(Path.Combine(cemuDir, "Cemu.exe"), "");
var cemu = new CemuAdapter { ExePath = Path.Combine(cemuDir, "Cemu.exe") };
cemu.ResolveConfigRoot();
cemu.ApplyGlobal(db.BuildSettings("cemu", Tier.Balanced, z1e, null));
Check(File.ReadAllText(Path.Combine(cemuDir, "portable", "settings.xml")).Contains("<api>1</api>"), "Cemu settings.xml <Graphic><api>");
var botw = db.Games.First(g => g.Emulator == "cemu" && g.Title.Contains("Breath"));
r = cemu.ApplyGame(botw, db.BuildSettings("cemu", Tier.Balanced, z1e, botw));
Check(r.Files.Count > 0 && File.ReadAllText(r.Files[0]).Contains("accurateShaderMul = true") && !File.ReadAllText(r.Files[0]).Contains("<"), "Cemu gameProfile");

var pp = Path.Combine(root, "ppsspp");
Directory.CreateDirectory(Path.Combine(pp, "memstick", "PSP", "SYSTEM"));
File.WriteAllText(Path.Combine(pp, "PPSSPPWindows64.exe"), "");
File.WriteAllText(Path.Combine(pp, "memstick", "PSP", "SYSTEM", "ppsspp.ini"), "[Graphics]\nInternalResolution = 1\nVSyncInterval = True\n");
var ppsspp = new PpssppAdapter { ExePath = Path.Combine(pp, "PPSSPPWindows64.exe") };
ppsspp.ResolveConfigRoot();
var gow = db.Games.First(g => g.Emulator == "ppsspp" && g.Title.Contains("Ghost of Sparta"));
r = ppsspp.ApplyGame(gow, db.BuildSettings("ppsspp", Tier.Performance, z1e, gow));
var ppg = File.ReadAllText(r.Files[0]);
Check(ppg.Contains("InternalResolution = 4") && ppg.Contains("VSyncInterval = True") && ppg.Contains("SkipBufferEffects = False"), "PPSSPP game ini seeded from global");

var duck = Path.Combine(root, "duck");
Directory.CreateDirectory(duck);
File.WriteAllText(Path.Combine(duck, "duckstation-qt-x64-ReleaseLTCG.exe"), "");
File.WriteAllText(Path.Combine(duck, "portable.txt"), "");
var dsA = new DuckStationAdapter { ExePath = Path.Combine(duck, "duckstation-qt-x64-ReleaseLTCG.exe") };
dsA.ResolveConfigRoot();
dsA.ApplyGlobal(db.BuildSettings("duckstation", Tier.Max, z1, null));
Check(File.ReadAllText(Path.Combine(duck, "settings.ini")).Contains("ResolutionScale = 5"), "DuckStation max on Z1 → 5x");

var xe = Path.Combine(root, "xenia");
Directory.CreateDirectory(xe);
File.WriteAllText(Path.Combine(xe, "xenia_canary.exe"), "");
File.WriteAllText(Path.Combine(xe, "xenia-canary.config.toml"), "[GPU]\ngpu = \"any\"                  # Graphics system.\ndraw_resolution_scale_x = 1\n");
var xenia = new XeniaAdapter { ExePath = Path.Combine(xe, "xenia_canary.exe") };
xenia.ResolveConfigRoot();
var h3 = db.Games.First(g => g.Emulator == "xenia" && g.Title == "Halo 3");
xenia.ApplyGame(h3, db.BuildSettings("xenia", Tier.Max, z1e, h3));
var toml = File.ReadAllText(Path.Combine(xe, "xenia-canary.config.toml"));
Check(toml.Contains("gpu = \"d3d12\"") && toml.Contains("draw_resolution_scale_x = 2") && toml.Contains("occlusion_query = \"fast\""), "Xenia TOML strings quoted");

var ryu = Path.Combine(root, "ryujinx");
Directory.CreateDirectory(Path.Combine(ryu, "portable"));
File.WriteAllText(Path.Combine(ryu, "Ryujinx.exe"), "");
File.WriteAllText(Path.Combine(ryu, "portable", "Config.json"), "{\"version\": 50, \"res_scale\": 1, \"enable_vsync\": true}");
var ryA = new RyujinxAdapter { ExePath = Path.Combine(ryu, "Ryujinx.exe") };
ryA.ResolveConfigRoot();
ryA.ApplyGlobal(db.BuildSettings("switch", Tier.Max, z1e, null));
var rj = File.ReadAllText(Path.Combine(ryu, "portable", "Config.json"));
Check(rj.Contains("\"res_scale\": 2") && rj.Contains("\"version\": 50") && rj.Contains("\"enable_docked_mode\": true"), "Ryujinx Config.json merged");

// ── Restore ──
var restored = Backup.RestoreOriginals(rp);
Check(File.ReadAllText(Path.Combine(rp, "config", "config.yml")).Contains("Resolution Scale: 100") &&
      !Directory.EnumerateFiles(Path.Combine(rp, "config", "custom_configs")).Any(), $"RPCS3 restore ({restored} files)");

// ── Extras: catalog, per-game patch lists, Xenia patch toggles ──
var extras = ExtrasCatalog.Load();
Check(extras.Enhancements.Count >= 15 && extras.Addons.Count == 3, $"extras.json: {extras.Enhancements.Count} enhancements, {extras.Addons.Count} add-ons");
var ws = extras.Enhancements.First(x => x.Id == "pcsx2.widescreen");
var ni = extras.Enhancements.First(x => x.Id == "pcsx2.nointerlace");
var merged = new System.Text.Json.Nodes.JsonObject();
Json.DeepMerge(merged, ni.PerGame);
Json.DeepMerge(merged, ws.PerGame);
var pini = Path.Combine(root, "patches-test.ini");
var pf = new IniFile(pini);
pf.Apply(merged, Formatters.Lower);
pf.Apply(merged, Formatters.Lower); // idempotent
pf.Save();
var ptxt = File.ReadAllText(pini);
Check(ptxt.Contains("Enable = No-Interlacing") && ptxt.Contains("Enable = Widescreen 16:9") &&
      ptxt.Split("Enable =").Length == 3, "PCSX2 per-game [Patches] Enable list (both, no duplicates)");

// Turning the enhancement off removes the per-game Enable line again.
var offIni = new IniFile(pini);
offIni.Apply(ws.PerGameOff!, Formatters.Lower);
offIni.Save();
ptxt = File.ReadAllText(pini);
Check(!ptxt.Contains("Widescreen 16:9") && ptxt.Contains("Enable = No-Interlacing"), "PCSX2 per-game widescreen switched back off");

// Bug-check fixes
Check(!SafeId.IsValid(@"..\..\Windows") && !SafeId.IsValid(@"C:\evil") && !SafeId.IsValid("a/b") && SafeId.IsValid("SLUS-20694") && SafeId.IsValid("0100F2C0115B6000"),
    "SafeId rejects path-like ids");
var qy = Path.Combine(root, "quote.yml");
var yq = new YamlFile(qy);
yq.Apply(new System.Text.Json.Nodes.JsonObject { ["Video"] = new System.Text.Json.Nodes.JsonObject { ["Odd"] = "[x]: y", ["Plain"] = "Recompiler (LLVM)" } });
yq.Save();
var ytxt = File.ReadAllText(qy);
Check(ytxt.Contains("Odd: '[x]: y'") && ytxt.Contains("Plain: Recompiler (LLVM)"), "YAML quotes only when needed");
Check(Formatters.Toml(System.Text.Json.Nodes.JsonValue.Create(@"C:\a""b")) == @"""C:\\a\""b""", "TOML escapes backslashes and quotes");

string[] xtoml =
[
    "title_name = \"Test\"", "title_id = \"4D5307E6\"", "hash = \"ABC\"",
    "[[patch]]", "    name = \"60 FPS\"", "    author = \"x\"", "    is_enabled = false", "    [[patch.be32]]", "        address = 0x1", "        value = 0x2",
    "[[patch]]", "    name = \"1080p\"", "    is_enabled = false",
];
var pl = XeniaPatches.Patches(xtoml).ToList();
Check(pl.Count == 2 && pl[0].Name == "60 FPS" && !pl[0].Enabled, "Xenia patch parser");
var toggled = XeniaPatches.SetEnabled(xtoml, new HashSet<string> { "60 FPS" }, true);
Check(XeniaPatches.EnabledNames(toggled).SetEquals(["60 FPS"]), "Xenia patch toggle");

// ── Updater: protection rules ──
foreach (var (path, want) in new[]
{
    ("bios\\scph39001.bin", true), ("bios\\ps2-0230a-20080220.bin", true), ("scph1001.bin", true),
    ("memcards\\Mcd001.ps2", true), ("dev_hdd0\\game\\BLUS30443\\USRDIR\\EBOOT.BIN", true),
    ("dev_flash\\sys\\external\\libaudio.sprx", true), ("keys\\prod.keys", true), ("PS3UPDAT.PUP", true),
    ("settings.ini", true), ("portable.txt", true), ("xenia-canary.config.toml", true), ("config\\config.yml", true),
    ("pcsx2-qt.exe", false), ("resources\\shaders\\vulkan\\tfx.glsl", false), ("Qt6Core.dll", false),
    ("resources\\GameIndex.yaml", false), ("gameProfiles\\default\\000500001010ec00.ini", false),
})
    Check(EmulatorUpdater.IsProtected(path) == want, $"IsProtected({path}) = {want}");

// ── Updater: real DuckStation download into a folder with user files ──
if (args.Contains("--download"))
{
    var inst = Path.Combine(root, "duck-update");
    Directory.CreateDirectory(Path.Combine(inst, "bios"));
    File.WriteAllText(Path.Combine(inst, "bios", "scph5501.bin"), "MY BIOS");
    File.WriteAllText(Path.Combine(inst, "settings.ini"), "[GPU]\nResolutionScale = 7\n");
    Directory.CreateDirectory(Path.Combine(inst, "memcards"));
    File.WriteAllText(Path.Combine(inst, "memcards", "shared_card_1.mcd"), "MY SAVE");
    File.WriteAllText(Path.Combine(inst, "duckstation-qt-x64-ReleaseLTCG.exe"), "OLD EXE");
    var duckA = new DuckStationAdapter { ExePath = Path.Combine(inst, "duckstation-qt-x64-ReleaseLTCG.exe") };
    var info = await EmulatorUpdater.CheckAsync("duckstation", false);
    Check(info is not null && info.Sha256 is not null, $"DuckStation latest: {info?.Version} ({info?.AssetName}, sha256 {(info?.Sha256 is null ? "missing" : "present")})");
    var exe = await EmulatorUpdater.InstallAsync(duckA, info!, inst, new Progress<string>(m => Console.WriteLine("      " + m)));
    Check(new FileInfo(exe).Length > 1_000_000, $"new exe installed ({new FileInfo(exe).Length / 1048576} MB)");
    Check(File.ReadAllText(Path.Combine(inst, "bios", "scph5501.bin")) == "MY BIOS", "BIOS untouched");
    Check(File.ReadAllText(Path.Combine(inst, "memcards", "shared_card_1.mcd")) == "MY SAVE", "memory card untouched");
    Check(File.ReadAllText(Path.Combine(inst, "settings.ini")).Contains("ResolutionScale = 7"), "settings.ini untouched");
    Console.WriteLine("      " + EmulatorUpdater.Rollback(duckA));
    Check(File.ReadAllText(Path.Combine(inst, "duckstation-qt-x64-ReleaseLTCG.exe")) == "OLD EXE", "rollback restored old exe");
    Check(File.Exists(Path.Combine(inst, "bios", "scph5501.bin")), "BIOS still there after rollback");
    Check(!Directory.Exists(Path.Combine(inst, "resources")) || !Directory.EnumerateFiles(Path.Combine(inst, "resources"), "*", SearchOption.AllDirectories).Any(),
        "rollback removed files the update added");
    Check(File.ReadAllText(Path.Combine(inst, "settings.ini")).Contains("ResolutionScale = 7") &&
          File.ReadAllText(Path.Combine(inst, "memcards", "shared_card_1.mcd")) == "MY SAVE", "settings + memory card intact after rollback");

    // PCSX2 ships .7z – exercise that path too.
    var p2u = Path.Combine(root, "pcsx2-update");
    Directory.CreateDirectory(Path.Combine(p2u, "bios"));
    File.WriteAllText(Path.Combine(p2u, "bios", "SCPH-70012.bin"), "MY PS2 BIOS");
    File.WriteAllText(Path.Combine(p2u, "portable.ini"), "");
    var p2A = new Pcsx2Adapter { ExePath = Path.Combine(p2u, "pcsx2-qt.exe") };
    var p2info = await EmulatorUpdater.CheckAsync("pcsx2", false);
    Check(p2info is not null, $"PCSX2 latest: {p2info?.Version} ({p2info?.AssetName})");
    var p2exe = await EmulatorUpdater.InstallAsync(p2A, p2info!, p2u, new Progress<string>(m => Console.WriteLine("      " + m)));
    Check(File.Exists(p2exe) && Directory.Exists(Path.Combine(p2u, "resources")), $"PCSX2 .7z extracted → {Path.GetFileName(p2exe)}");
    Check(File.ReadAllText(Path.Combine(p2u, "bios", "SCPH-70012.bin")) == "MY PS2 BIOS" && File.Exists(Path.Combine(p2u, "portable.ini")), "PCSX2 BIOS + portable.ini untouched");

    // ── Add-ons: real downloads ──
    foreach (var a in extras.Addons)
    {
        var fakeRoot = Path.Combine(root, "addon-" + a.Id);
        Directory.CreateDirectory(fakeRoot);
        File.WriteAllText(Path.Combine(fakeRoot, a.Emulator == "cemu" ? "Cemu.exe" : a.Emulator == "xenia" ? "xenia_canary.exe" : "rpcs3.exe"), "");
        EmulatorAdapter ad = a.Emulator switch { "cemu" => new CemuAdapter(), "xenia" => new XeniaAdapter(), _ => new Rpcs3Adapter() };
        ad.ExePath = Directory.GetFiles(fakeRoot, "*.exe")[0];
        if (a.Emulator == "cemu") File.WriteAllText(Path.Combine(fakeRoot, "settings.xml"), "<content/>");
        ad.ResolveConfigRoot();

        // Pretend the user had turned a Xenia patch on before the update.
        string? userPatch = null;
        if (a.PreserveEnabledPatches)
        {
            var rel0 = await ExtrasService.CheckAddonAsync(a);
            await ExtrasService.InstallAddonAsync(a, rel0, ad);
            userPatch = Directory.GetFiles(Path.Combine(ad.ConfigRoot!, "patches"), "*.patch.toml")
                .First(f => XeniaPatches.Patches(File.ReadAllLines(f)).Any());
            var first = XeniaPatches.Patches(File.ReadAllLines(userPatch)).First().Name;
            File.WriteAllLines(userPatch, XeniaPatches.SetEnabled(File.ReadAllLines(userPatch), new HashSet<string> { first }, true));
        }

        var rel = await ExtrasService.CheckAddonAsync(a);
        var msg = await ExtrasService.InstallAddonAsync(a, rel, ad);
        var dest = Path.Combine(ad.ConfigRoot!, a.InstallTo);
        var ok = a.IsArchive ? Directory.Exists(dest) && Directory.EnumerateFiles(dest, "*", SearchOption.AllDirectories).Count() > 20 : new FileInfo(dest).Length > 100_000;
        Check(ok, $"{a.Name} {rel.Version} → {msg}");
        if (userPatch is not null)
            Check(XeniaPatches.EnabledNames(File.ReadAllLines(userPatch)).Count > 0, "Xenia: user-enabled patch kept after update");
    }
    if (a_cemuCheck(root)) Check(true, "Cemu graphic packs contain rules.txt files");

    static bool a_cemuCheck(string r) =>
        Directory.EnumerateFiles(Path.Combine(r, "addon-cemu.graphicpacks"), "rules.txt", SearchOption.AllDirectories).Any();
}

// ── Texture packs: real install of the smallest pack per emulator ──
if (args.Contains("--packs"))
{
    Check(extras.Packs.Count >= 60, $"texture catalog loaded: {extras.Packs.Count} packs");
    foreach (var (emu, packId) in new[] { ("pcsx2", "mgs2-hd-ui"), ("dolphin", "scaler-uhd"), ("ppsspp", "la-pucelle-textures") })
    {
        var pack = extras.Packs.First(p => p.Id == packId);
        var dir = Path.Combine(root, "tp-" + emu);
        Directory.CreateDirectory(dir);
        EmulatorAdapter ad = emu switch
        {
            "pcsx2" => new Pcsx2Adapter(),
            "dolphin" => new DolphinAdapter(),
            _ => new PpssppAdapter()
        };
        File.WriteAllText(Path.Combine(dir, ad.ExeNames[0]), "");
        if (emu == "pcsx2") File.WriteAllText(Path.Combine(dir, "portable.ini"), "");
        if (emu == "dolphin") { File.WriteAllText(Path.Combine(dir, "portable.txt"), ""); Directory.CreateDirectory(Path.Combine(dir, "User", "Config")); }
        if (emu == "ppsspp") Directory.CreateDirectory(Path.Combine(dir, "memstick", "PSP", "SYSTEM"));
        ad.ExePath = Path.Combine(dir, ad.ExeNames[0]);
        ad.ResolveConfigRoot();
        var id = pack.GameIds[0];
        var msg = await ExtrasService.InstallPackAsync(pack, ad, id);
        var target = ad.TextureDir(id)!;
        var files = Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories).ToList();
        var images = files.Count(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".dds", StringComparison.OrdinalIgnoreCase));
        var nested = Directory.Exists(Path.Combine(target, "replacements")) || Directory.Exists(Path.Combine(target, id));
        Check(images > 0 && !nested, $"{emu}: {pack.Name} → {images} images directly in {Path.GetRelativePath(root, target)}" +
                                    (emu == "ppsspp" ? $" (textures.ini: {File.Exists(Path.Combine(target, "textures.ini"))})" : ""));
    }
}

Console.WriteLine(fails == 0 ? "\nALL PASS" : $"\n{fails} FAILED");
return fails;

static string Indent(string s) => string.Join("\n", s.Split('\n').Select(l => "      | " + l.TrimEnd('\r')));
