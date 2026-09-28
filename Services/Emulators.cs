using System.IO;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GameOp.Models;

namespace GameOp.Services;

public sealed record ApplyResult(List<string> Files, List<string> Warnings)
{
    public static ApplyResult Empty() => new([], []);
}

/// <summary>
/// One supported emulator: how to find it, where its configs live, and how to write settings in its format.
/// Settings arrive as {section: {key: value}} in the emulator's own vocabulary (see Data\emulators.json).
/// </summary>
public abstract class EmulatorAdapter
{
    public abstract string Id { get; }
    public abstract string Name { get; }
    /// <summary>Key into emulators.json / gamedb.json. Several adapters may share one (Switch).</summary>
    public virtual string DbKey => Id;
    public abstract string[] ExeNames { get; }
    public abstract bool SupportsPerGame { get; }

    public string? ExePath { get; set; }
    public string? ConfigRoot { get; protected set; }
    public bool IsDetected => ConfigRoot is not null;
    public string ExeDir => ExePath is null ? "" : Path.GetDirectoryName(ExePath)!;

    /// <summary>Locates the config directory. Called after ExePath is set (or null if only user folders exist).</summary>
    public abstract void ResolveConfigRoot();

    public abstract ApplyResult ApplyGlobal(JsonObject settings);

    /// <summary>Writes a per-game profile for every id of the game. Falls back to global where unsupported.</summary>
    public virtual ApplyResult ApplyGame(GameEntry game, JsonObject settings) => ApplyGlobal(settings);

    /// <summary>Ids of games the emulator knows about locally (for highlighting in the library).</summary>
    public virtual IEnumerable<string> ScanInstalledIds() => [];

    protected static string? FirstExisting(params string?[] dirs) =>
        dirs.FirstOrDefault(d => !string.IsNullOrEmpty(d) && Directory.Exists(d));

    protected void RequireRoot()
    {
        if (ConfigRoot is null) throw new InvalidOperationException($"{Name} config folder not found. Run the emulator once, or set its path on the Emulators page.");
    }
}

// ───────────────────────────── PCSX2 ─────────────────────────────
public sealed partial class Pcsx2Adapter : EmulatorAdapter
{
    public override string Id => "pcsx2";
    public override string Name => "PCSX2";
    public override string[] ExeNames => ["pcsx2-qt.exe", "pcsx2-qtx64-avx2.exe", "pcsx2-qtx64.exe", "pcsx2.exe"];
    public override bool SupportsPerGame => true;

    /// <summary>serial → CRCs found in the user's library (ISO scan + existing gamesettings files).</summary>
    private readonly Dictionary<string, HashSet<uint>> _crcs = new(StringComparer.OrdinalIgnoreCase);

    private string Ini => Path.Combine(ConfigRoot!, "inis", "PCSX2.ini");

    public override void ResolveConfigRoot()
    {
        var portable = ExePath is not null &&
                       (File.Exists(Path.Combine(ExeDir, "portable.ini")) || File.Exists(Path.Combine(ExeDir, "portable.txt")));
        ConfigRoot = portable ? ExeDir : FirstExisting(Path.Combine(AppPaths.Documents, "PCSX2"));
    }

    public override ApplyResult ApplyGlobal(JsonObject settings)
    {
        RequireRoot();
        var ini = new IniFile(Ini);
        ini.Apply(settings, Formatters.Lower);
        ini.Save();
        return new([Ini], []);
    }

    public override ApplyResult ApplyGame(GameEntry game, JsonObject settings)
    {
        RequireRoot();
        // Rescan if the game isn't known yet (new ISOs or gamesettings files since the last scan).
        if (!game.Ids.Any(_crcs.ContainsKey)) ScanInstalledIds().ToList();
        var result = ApplyResult.Empty();
        foreach (var serial in game.Ids)
        {
            if (!_crcs.TryGetValue(serial, out var crcs)) continue;
            foreach (var crc in crcs)
            {
                var path = Path.Combine(ConfigRoot!, "gamesettings", $"{serial}_{crc:X8}.ini");
                var ini = new IniFile(path);
                ini.Apply(settings, Formatters.Lower);
                ini.Save();
                result.Files.Add(path);
            }
        }
        if (result.Files.Count == 0)
            result.Warnings.Add($"{game.Title}: not found in your PCSX2 library (.iso). PCSX2 names per-game files by serial + CRC, so add the game folder in PCSX2 and rescan.");
        return result;
    }

    public override IEnumerable<string> ScanInstalledIds()
    {
        if (ConfigRoot is null) return [];
        _crcs.Clear();

        var gs = Path.Combine(ConfigRoot, "gamesettings");
        if (Directory.Exists(gs))
            foreach (var f in Directory.EnumerateFiles(gs, "*.ini"))
            {
                var m = GameSettingsName().Match(Path.GetFileNameWithoutExtension(f));
                if (m.Success) AddCrc(m.Groups[1].Value, Convert.ToUInt32(m.Groups[2].Value, 16));
            }

        if (File.Exists(Ini))
        {
            var ini = new IniFile(Ini);
            var dirs = ini.GetAll("GameList", "RecursivePaths").Select(p => (p, true))
                .Concat(ini.GetAll("GameList", "Paths").Select(p => (p, false)));
            foreach (var (dir, recursive) in dirs)
            {
                if (!Directory.Exists(dir)) continue;
                IEnumerable<string> isos;
                try { isos = Directory.EnumerateFiles(dir, "*.iso", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly).Take(2000).ToList(); }
                catch { continue; }
                foreach (var iso in isos)
                    if (Ps2Iso.Read(iso) is { } id) AddCrc(id.Serial, id.Crc);
            }
        }
        return _crcs.Keys.ToList();
    }

    private void AddCrc(string serial, uint crc)
    {
        if (!_crcs.TryGetValue(serial, out var set)) _crcs[serial] = set = [];
        set.Add(crc);
    }

    [GeneratedRegex(@"^([A-Z]{4}-\d{5})_([0-9A-Fa-f]{8})$")]
    private static partial Regex GameSettingsName();
}

// ───────────────────────────── RPCS3 ─────────────────────────────
public sealed partial class Rpcs3Adapter : EmulatorAdapter
{
    public override string Id => "rpcs3";
    public override string Name => "RPCS3";
    public override string[] ExeNames => ["rpcs3.exe"];
    public override bool SupportsPerGame => true;

    // Newer builds keep configs in <rpcs3>\config\, older ones in the root.
    private string ConfigDir => Directory.Exists(Path.Combine(ConfigRoot!, "config")) ? Path.Combine(ConfigRoot!, "config") : ConfigRoot!;

    public override void ResolveConfigRoot() =>
        ConfigRoot = ExePath is not null && Directory.Exists(ExeDir) ? ExeDir : null;

    public override ApplyResult ApplyGlobal(JsonObject settings)
    {
        RequireRoot();
        var path = Path.Combine(ConfigDir, "config.yml");
        var yml = new YamlFile(path);
        yml.Apply(settings);
        yml.Save();
        return new([path], []);
    }

    public override ApplyResult ApplyGame(GameEntry game, JsonObject settings)
    {
        RequireRoot();
        var result = ApplyResult.Empty();
        foreach (var id in game.Ids)
        {
            var path = Path.Combine(ConfigDir, "custom_configs", $"config_{id}.yml");
            var yml = new YamlFile(path);
            yml.Apply(settings);
            yml.Save();
            result.Files.Add(path);
        }
        return result;
    }

    public override IEnumerable<string> ScanInstalledIds()
    {
        if (ConfigRoot is null) yield break;
        foreach (var gamesYml in new[] { Path.Combine(ConfigDir, "games.yml"), Path.Combine(ConfigRoot, "games.yml") }.Distinct())
        {
            if (!File.Exists(gamesYml)) continue;
            foreach (var line in File.ReadLines(gamesYml))
            {
                var m = Serial().Match(line);
                if (m.Success) yield return m.Groups[1].Value;
            }
        }
        var hdd = Path.Combine(ConfigRoot, "dev_hdd0", "game");
        if (Directory.Exists(hdd))
            foreach (var d in Directory.EnumerateDirectories(hdd))
            {
                var name = Path.GetFileName(d);
                if (Serial().IsMatch(name + ":")) yield return name;
            }
    }

    [GeneratedRegex(@"^([A-Z]{4}\d{5}):")]
    private static partial Regex Serial();
}

// ───────────────────────────── DuckStation ─────────────────────────────
public sealed class DuckStationAdapter : EmulatorAdapter
{
    public override string Id => "duckstation";
    public override string Name => "DuckStation";
    public override string[] ExeNames => ["duckstation-qt-x64-ReleaseLTCG.exe", "duckstation-qt.exe", "duckstation.exe"];
    public override bool SupportsPerGame => true;

    public override void ResolveConfigRoot()
    {
        if (ExePath is not null && (File.Exists(Path.Combine(ExeDir, "portable.txt")) || File.Exists(Path.Combine(ExeDir, "settings.ini"))))
        { ConfigRoot = ExeDir; return; }
        var candidates = new[] { Path.Combine(AppPaths.Documents, "DuckStation"), Path.Combine(AppPaths.LocalAppData, "DuckStation") };
        ConfigRoot = candidates.FirstOrDefault(c => File.Exists(Path.Combine(c, "settings.ini"))) ?? FirstExisting(candidates);
    }

    public override ApplyResult ApplyGlobal(JsonObject settings)
    {
        RequireRoot();
        var path = Path.Combine(ConfigRoot!, "settings.ini");
        var ini = new IniFile(path);
        ini.Apply(settings, Formatters.Lower);
        ini.Save();
        return new([path], []);
    }

    public override ApplyResult ApplyGame(GameEntry game, JsonObject settings)
    {
        RequireRoot();
        var result = ApplyResult.Empty();
        foreach (var id in game.Ids)
        {
            var path = Path.Combine(ConfigRoot!, "gamesettings", $"{id}.ini");
            var ini = new IniFile(path);
            ini.Apply(settings, Formatters.Lower);
            ini.Save();
            result.Files.Add(path);
        }
        return result;
    }
}

// ───────────────────────────── PPSSPP ─────────────────────────────
public sealed class PpssppAdapter : EmulatorAdapter
{
    public override string Id => "ppsspp";
    public override string Name => "PPSSPP";
    public override string[] ExeNames => ["PPSSPPWindows64.exe", "PPSSPPWindows.exe"];
    public override bool SupportsPerGame => true;

    public override void ResolveConfigRoot()
    {
        var portable = ExePath is null ? null : Path.Combine(ExeDir, "memstick", "PSP", "SYSTEM");
        var installed = Path.Combine(AppPaths.Documents, "PPSSPP", "PSP", "SYSTEM");
        if (portable is not null && !File.Exists(Path.Combine(ExeDir, "installed.txt")) && Directory.Exists(Path.Combine(ExeDir, "memstick")))
            ConfigRoot = portable;
        else
            ConfigRoot = FirstExisting(installed, portable);
    }

    public override ApplyResult ApplyGlobal(JsonObject settings)
    {
        RequireRoot();
        var path = Path.Combine(ConfigRoot!, "ppsspp.ini");
        var ini = new IniFile(path);
        ini.Apply(settings, Formatters.Title);
        ini.Save();
        return new([path], []);
    }

    public override ApplyResult ApplyGame(GameEntry game, JsonObject settings)
    {
        RequireRoot();
        var result = ApplyResult.Empty();
        var global = Path.Combine(ConfigRoot!, "ppsspp.ini");
        foreach (var id in game.Ids)
        {
            // PPSSPP game configs don't inherit from the global file, so seed new ones from it.
            var path = Path.Combine(ConfigRoot!, $"{id}_ppsspp.ini");
            if (!File.Exists(path) && File.Exists(global))
            {
                Backup.BeforeWrite(path);
                File.Copy(global, path);
            }
            var ini = new IniFile(path);
            ini.Apply(settings, Formatters.Title);
            ini.Save();
            result.Files.Add(path);
        }
        return result;
    }
}

// ───────────────────────────── Dolphin ─────────────────────────────
public sealed class DolphinAdapter : EmulatorAdapter
{
    public override string Id => "dolphin";
    public override string Name => "Dolphin";
    public override string[] ExeNames => ["Dolphin.exe"];
    public override bool SupportsPerGame => true;

    public override void ResolveConfigRoot()
    {
        if (ExePath is not null && File.Exists(Path.Combine(ExeDir, "portable.txt")))
        { ConfigRoot = Path.Combine(ExeDir, "User"); return; }
        var candidates = new[] { Path.Combine(AppPaths.RoamingAppData, "Dolphin Emulator"), Path.Combine(AppPaths.Documents, "Dolphin Emulator") };
        ConfigRoot = candidates.FirstOrDefault(c => Directory.Exists(Path.Combine(c, "Config"))) ?? FirstExisting(candidates);
    }

    // Per-game INI sections → global file + section.
    private static (string File, string Section) MapGlobal(string section) => section switch
    {
        "Video_Settings" => ("GFX.ini", "Settings"),
        "Video_Enhancements" => ("GFX.ini", "Enhancements"),
        "Video_Hacks" => ("GFX.ini", "Hacks"),
        "Video_Stereoscopy" => ("GFX.ini", "Stereoscopy"),
        _ => ("Dolphin.ini", section)
    };

    public override ApplyResult ApplyGlobal(JsonObject settings)
    {
        RequireRoot();
        var files = new Dictionary<string, IniFile>();
        foreach (var (section, keys) in settings)
        {
            if (keys is not JsonObject obj) continue;
            var (file, target) = MapGlobal(section);
            var path = Path.Combine(ConfigRoot!, "Config", file);
            if (!files.TryGetValue(path, out var ini)) files[path] = ini = new IniFile(path);
            foreach (var (key, value) in obj) ini.Set(target, key, Formatters.Title(value));
        }
        foreach (var ini in files.Values) ini.Save();
        return new(files.Keys.ToList(), []);
    }

    public override ApplyResult ApplyGame(GameEntry game, JsonObject settings)
    {
        RequireRoot();
        var result = ApplyResult.Empty();
        foreach (var id in game.Ids)
        {
            var path = Path.Combine(ConfigRoot!, "GameSettings", $"{id}.ini");
            var ini = new IniFile(path);
            ini.Apply(settings, Formatters.Title);
            ini.Save();
            result.Files.Add(path);
        }
        return result;
    }
}

// ───────────────────────────── Cemu ─────────────────────────────
public sealed class CemuAdapter : EmulatorAdapter
{
    public override string Id => "cemu";
    public override string Name => "Cemu";
    public override string[] ExeNames => ["Cemu.exe"];
    public override bool SupportsPerGame => true;

    public override void ResolveConfigRoot()
    {
        if (ExePath is not null && Directory.Exists(Path.Combine(ExeDir, "portable"))) { ConfigRoot = Path.Combine(ExeDir, "portable"); return; }
        if (ExePath is not null && File.Exists(Path.Combine(ExeDir, "settings.xml"))) { ConfigRoot = ExeDir; return; }
        ConfigRoot = FirstExisting(Path.Combine(AppPaths.RoamingAppData, "Cemu"), ExePath is null ? null : ExeDir);
    }

    // Tier baselines target settings.xml (<content><Graphic>…); game entries target gameProfiles (CPU/Graphics).
    private static bool IsProfileSection(string s) => s is "CPU" or "Graphics" or "General" or "Controller";

    public override ApplyResult ApplyGlobal(JsonObject settings)
    {
        RequireRoot();
        var xmlPart = new JsonObject();
        foreach (var (k, v) in settings) if (!IsProfileSection(k)) xmlPart[k] = v?.DeepClone();
        var path = Path.Combine(ConfigRoot!, "settings.xml");
        XmlConfigFile.Apply(path, xmlPart);
        return new([path], []);
    }

    public override ApplyResult ApplyGame(GameEntry game, JsonObject settings)
    {
        RequireRoot();
        var profile = new JsonObject();
        foreach (var (k, v) in settings) if (IsProfileSection(k)) profile[k] = v?.DeepClone();
        var result = ApplyResult.Empty();
        if (profile.Count == 0)
        {
            result.Warnings.Add($"{game.Title}: no Cemu per-game overrides; resolution is set with graphic packs inside Cemu.");
            return result;
        }
        foreach (var id in game.Ids)
        {
            var path = Path.Combine(ConfigRoot!, "gameProfiles", $"{id.ToLowerInvariant()}.ini");
            var ini = new IniFile(path);
            ini.Apply(profile, Formatters.Lower);
            ini.Save();
            result.Files.Add(path);
        }
        return result;
    }
}

// ───────────────────────────── Xenia Canary ─────────────────────────────
public sealed class XeniaAdapter : EmulatorAdapter
{
    public override string Id => "xenia";
    public override string Name => "Xenia Canary";
    public override string[] ExeNames => ["xenia_canary.exe", "xenia.exe"];
    public override bool SupportsPerGame => false;

    public override void ResolveConfigRoot() => ConfigRoot = ExePath is null ? null : ExeDir;

    private string ConfigFile => new[] { "xenia-canary.config.toml", "xenia.config.toml" }
        .Select(f => Path.Combine(ConfigRoot!, f)).FirstOrDefault(File.Exists) ?? Path.Combine(ConfigRoot!, "xenia-canary.config.toml");

    public override ApplyResult ApplyGlobal(JsonObject settings)
    {
        RequireRoot();
        var ini = new IniFile(ConfigFile) { Separator = " = " };
        ini.Apply(settings, Formatters.Toml);
        ini.Save();
        return new([ConfigFile], []);
    }
}

// ───────────────────────────── Switch: Ryujinx ─────────────────────────────
public sealed class RyujinxAdapter : EmulatorAdapter
{
    public override string Id => "ryujinx";
    public override string Name => "Ryujinx";
    public override string DbKey => "switch";
    public override string[] ExeNames => ["Ryujinx.exe", "Ryujinx.Ava.exe"];
    public override bool SupportsPerGame => false;

    public override void ResolveConfigRoot()
    {
        if (ExePath is not null && Directory.Exists(Path.Combine(ExeDir, "portable"))) { ConfigRoot = Path.Combine(ExeDir, "portable"); return; }
        ConfigRoot = FirstExisting(Path.Combine(AppPaths.RoamingAppData, "Ryujinx"));
    }

    public override ApplyResult ApplyGlobal(JsonObject settings)
    {
        RequireRoot();
        var s = settings["switch"] as JsonObject ?? [];
        var native = new JsonObject();
        if (s["resolutionScale"] is { } rs) native["res_scale"] = rs.GetValue<int>();
        if (s["docked"] is { } d) native["enable_docked_mode"] = d.GetValue<bool>();
        if (s["anisotropy"] is { } a) native["max_anisotropy"] = a.GetValue<int>() switch { 0 => -1f, var n => (float)n };
        native["graphics_backend"] = "Vulkan";
        native["enable_shader_cache"] = true;
        native["enable_ptc"] = true;
        var path = Path.Combine(ConfigRoot!, "Config.json");
        JsonConfigFile.Apply(path, native);
        return new([path], []);
    }
}

// ───────────────────────────── Switch: yuzu family (Eden, Citron, Sudachi, …) ─────────────────────────────
public sealed class YuzuFamilyAdapter(string id, string name, string folder, string[] exes) : EmulatorAdapter
{
    public override string Id => id;
    public override string Name => name;
    public override string DbKey => "switch";
    public override string[] ExeNames => exes;
    public override bool SupportsPerGame => true;

    public override void ResolveConfigRoot()
    {
        if (ExePath is not null && Directory.Exists(Path.Combine(ExeDir, "user", "config"))) { ConfigRoot = Path.Combine(ExeDir, "user", "config"); return; }
        ConfigRoot = FirstExisting(Path.Combine(AppPaths.RoamingAppData, folder, "config"));
    }

    /// <summary>Neutral switch vocabulary → qt-config.ini [section] key/value.</summary>
    private static IEnumerable<(string Section, string Key, string Value)> Map(JsonObject settings)
    {
        var s = settings["switch"] as JsonObject ?? [];
        if (s["resolutionScale"] is { } rs)
            yield return ("Renderer", "resolution_setup", (rs.GetValue<int>() switch { <= 1 => 2, 2 => 4, 3 => 5, _ => 6 }).ToString());
        if (s["gpuAccuracy"] is { } ga)
            yield return ("Renderer", "gpu_accuracy", ga.GetValue<string>() switch { "high" => "1", "extreme" => "2", _ => "0" });
        if (s["asyncShaders"] is { } asy)
            yield return ("Renderer", "use_asynchronous_shaders", asy.GetValue<bool>() ? "true" : "false");
        if (s["anisotropy"] is { } an)
            yield return ("Renderer", "max_anisotropy", (an.GetValue<int>() switch { 0 => 0, 2 => 2, 4 => 3, 8 => 4, _ => 5 }).ToString());
        if (s["docked"] is { } d)
            yield return ("System", "use_docked_mode", d.GetValue<bool>() ? "1" : "0");
    }

    private static void Write(IniFile ini, JsonObject settings, bool perGame)
    {
        ini.Separator = "=";
        foreach (var (section, key, value) in Map(settings))
        {
            if (perGame) ini.Set(section, $"{key}\\use_global", "false");
            ini.Set(section, $"{key}\\default", "false");
            ini.Set(section, key, value);
        }
    }

    public override ApplyResult ApplyGlobal(JsonObject settings)
    {
        RequireRoot();
        var path = Path.Combine(ConfigRoot!, "qt-config.ini");
        var ini = new IniFile(path);
        Write(ini, settings, perGame: false);
        ini.Save();
        return new([path], []);
    }

    public override ApplyResult ApplyGame(GameEntry game, JsonObject settings)
    {
        RequireRoot();
        var result = ApplyResult.Empty();
        foreach (var titleId in game.Ids)
        {
            var path = Path.Combine(ConfigRoot!, "custom", $"{titleId.ToUpperInvariant()}.ini");
            var ini = new IniFile(path);
            Write(ini, settings, perGame: true);
            ini.Save();
            result.Files.Add(path);
        }
        return result;
    }
}

public static class EmulatorRegistry
{
    public static List<EmulatorAdapter> CreateAll() =>
    [
        new Pcsx2Adapter(),
        new Rpcs3Adapter(),
        new DuckStationAdapter(),
        new PpssppAdapter(),
        new DolphinAdapter(),
        new CemuAdapter(),
        new XeniaAdapter(),
        new RyujinxAdapter(),
        new YuzuFamilyAdapter("eden", "Eden", "eden", ["eden.exe"]),
        new YuzuFamilyAdapter("citron", "Citron", "citron", ["citron.exe"]),
        new YuzuFamilyAdapter("sudachi", "Sudachi", "sudachi", ["sudachi.exe"]),
    ];

    private static IEnumerable<string> SearchRoots(IEnumerable<string> extra)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new List<string>(extra)
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Path.Combine(AppPaths.LocalAppData, "Programs"),
            Path.Combine(AppPaths.RoamingAppData),
            Path.Combine(home, "EmuDeck", "EmulationStation-DE", "Emulators"),
            Path.Combine(home, "Emulators"),
            Path.Combine(home, "Desktop"),
            Path.Combine(home, "Downloads"),
            Path.Combine(home, "Documents"),
        };
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
        {
            roots.Add(Path.Combine(drive.RootDirectory.FullName, "Emulation"));
            roots.Add(Path.Combine(drive.RootDirectory.FullName, "Emulators"));
            roots.Add(Path.Combine(drive.RootDirectory.FullName, "Emulation", "tools"));
            roots.Add(Path.Combine(drive.RootDirectory.FullName, "RetroBat", "emulators"));
            roots.Add(Path.Combine(drive.RootDirectory.FullName, "Games"));
        }
        return roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Finds each emulator's exe (saved path first, then a shallow search of common folders).</summary>
    public static void Detect(IEnumerable<EmulatorAdapter> adapters, AppSettings settings)
    {
        var list = adapters.ToList();
        foreach (var a in list)
        {
            a.ExePath = settings.EmulatorPaths.TryGetValue(a.Id, out var saved) && File.Exists(saved) ? saved : null;
        }

        var wanted = list.Where(a => a.ExePath is null)
            .SelectMany(a => a.ExeNames.Select(n => (n, a)))
            .GroupBy(x => x.n, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(x => x.a).ToList(), StringComparer.OrdinalIgnoreCase);

        foreach (var root in SearchRoots(settings.ExtraSearchRoots))
        {
            if (wanted.Count == 0) break;
            foreach (var file in SafeEnumerateExe(root, depth: 3))
            {
                if (!wanted.TryGetValue(Path.GetFileName(file), out var owners)) continue;
                foreach (var a in owners.Where(a => a.ExePath is null)) a.ExePath = file;
            }
        }

        foreach (var a in list)
        {
            try { a.ResolveConfigRoot(); }
            catch (Exception ex) { Log.Warn($"{a.Name}: {ex.Message}"); }
        }
    }

    private static IEnumerable<string> SafeEnumerateExe(string dir, int depth)
    {
        string[] files = [], dirs = [];
        try { files = Directory.GetFiles(dir, "*.exe"); } catch { }
        foreach (var f in files) yield return f;
        if (depth == 0) yield break;
        try { dirs = Directory.GetDirectories(dir); } catch { }
        foreach (var d in dirs)
        {
            var n = Path.GetFileName(d);
            if (n.StartsWith('.') || n is "Windows" or "WindowsApps" or "node_modules" or "Microsoft" or "Common Files") continue;
            foreach (var f in SafeEnumerateExe(d, depth - 1)) yield return f;
        }
    }
}
