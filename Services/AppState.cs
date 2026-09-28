using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using GameOp.Models;

namespace GameOp.Services;

/// <summary>App-wide state and the high-level "apply" operations the pages call.</summary>
public sealed partial class AppState : INotifyPropertyChanged
{
    public static AppState Instance { get; } = new();

    public AppSettings Settings { get; private set; } = new();
    public GameDatabase Db { get; private set; } = new();
    public DeviceProfile Device { get; private set; } = new();
    public string HardwareModel { get; private set; } = "";
    public string HardwareCpu { get; private set; } = "";
    public List<EmulatorAdapter> Emulators { get; } = EmulatorRegistry.CreateAll();
    public Dictionary<string, HashSet<string>> InstalledIds { get; } = [];

    private Tier _tier = Tier.Balanced;
    public Tier CurrentTier { get => _tier; private set { _tier = value; OnChanged(); } }

    private string _status = "";
    public string AutoStatus { get => _status; set { _status = value; OnChanged(); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? EmulatorsChanged;
    private void OnChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new(n));

    public void Initialize()
    {
        Settings = Json.Load<AppSettings>(AppPaths.SettingsFile) ?? new AppSettings();
        Db = GameDatabase.Load();
        (HardwareModel, HardwareCpu) = DeviceDetector.ReadHardware();

        var forced = Settings.ForcedDeviceId is { } fid ? Db.Devices.FirstOrDefault(d => d.Id == fid) : null;
        var matched = forced ?? DeviceDetector.Match(Db.Devices, HardwareModel, HardwareCpu);
        if (matched is not null)
        {
            Device = matched;
            // A manually chosen model only gets hardware control on real Ally hardware.
            Device.IsAlly = forced is null ||
                            HardwareModel.Contains("Ally", StringComparison.OrdinalIgnoreCase) ||
                            HardwareModel.Contains("RC7", StringComparison.OrdinalIgnoreCase);
            Log.Ok(Device.IsAlly ? $"Device: {Device.Name} ({Device.Apu})" : $"Simulating {Device.Name}; hardware control disabled");
        }
        else
        {
            var template = Db.Devices.FirstOrDefault(d => d.Id == "ally_z1e") ?? new DeviceProfile();
            Device = new DeviceProfile
            {
                Id = "unknown", Name = "Not a ROG Ally (preview mode)", Apu = HardwareCpu, Cpu = HardwareCpu,
                Gpu = "—", Ram = "—", Display = "—", GpuClass = "high", Tdp = template.Tdp, Limits = template.Limits, IsAlly = false
            };
            Log.Warn($"No ROG Ally detected ({HardwareModel}, {HardwareCpu}). Emulator configs still work; TDP control is disabled.");
        }
        CurrentTier = TierInfo.Parse(Settings.LastTier);
        Log.Info($"Game database {Db.Version}: {Db.Games.Count} games across {Db.Games.Select(g => g.Emulator).Distinct().Count()} emulators");
    }

    public void SaveSettings() => Json.Save(AppPaths.SettingsFile, Settings);

    public async Task DetectEmulatorsAsync()
    {
        await Task.Run(() =>
        {
            EmulatorRegistry.Detect(Emulators, Settings);
            foreach (var e in Emulators.Where(e => e.IsDetected))
            {
                try { InstalledIds[e.Id] = new HashSet<string>(e.ScanInstalledIds(), StringComparer.OrdinalIgnoreCase); }
                catch (Exception ex) { Log.Warn($"{e.Name} library scan: {ex.Message}"); }
            }
        });
        foreach (var e in Emulators.Where(e => e.ExePath is not null)) Settings.EmulatorPaths[e.Id] = e.ExePath!;
        SaveSettings();
        var found = Emulators.Where(e => e.IsDetected).Select(e => e.Name).ToList();
        Log.Info(found.Count == 0 ? "No emulators found yet – set paths on the Emulators page." : $"Found: {string.Join(", ", found)}");
        EmulatorsChanged?.Invoke();
    }

    public bool IsInstalled(GameEntry g) => Emulators.Where(e => e.DbKey == g.Emulator)
        .Any(e => InstalledIds.TryGetValue(e.Id, out var ids) && g.Ids.Any(ids.Contains));

    public Tier TierForGame(GameEntry g) =>
        Settings.GameTiers.TryGetValue(GameKey(g), out var t) ? TierInfo.Parse(t) : g.Recommended;

    public void SetTierForGame(GameEntry g, Tier t)
    {
        Settings.GameTiers[GameKey(g)] = t.Key();
        SaveSettings();
    }

    private static string GameKey(GameEntry g) => $"{g.Emulator}:{g.Ids.FirstOrDefault()}";

    // ─────────── Apply operations ───────────

    /// <summary>Device power (TDP) + Windows per-tier behaviour. Safe to call often.</summary>
    public void ApplyPower(Tier tier, string reason)
    {
        var plan = PowerService.PlanFor(Device, tier);
        Log.Info($"{reason}: {PowerService.ApplyTdp(Device, plan, Settings.RyzenAdjPath)}");
        foreach (var line in TweakService.ApplyTierSystem(tier, Settings)) Log.Info($"  {line}");
    }

    public async Task SetTierAsync(Tier tier, bool includeEmulators)
    {
        CurrentTier = tier;
        Settings.LastTier = tier.Key();
        SaveSettings();
        await Task.Run(() =>
        {
            ApplyPower(tier, tier.Label());
            if (!includeEmulators) return;
            foreach (var e in Emulators.Where(e => e.IsDetected)) ApplyGlobal(e, tier);
        });
    }

    public void ApplyGlobal(EmulatorAdapter e, Tier tier)
    {
        try
        {
            var settings = Db.BuildSettings(e.DbKey, tier, Device, null);
            var r = e.ApplyGlobal(settings);
            Log.Ok($"{e.Name}: {tier.Label()} defaults → {string.Join(", ", r.Files.Select(System.IO.Path.GetFileName))}");
            foreach (var w in r.Warnings) Log.Warn(w);
        }
        catch (Exception ex) { Log.Warn($"{e.Name}: {ex.Message}"); }
    }

    public ApplyResult ApplyGame(EmulatorAdapter e, GameEntry g, Tier tier)
    {
        var settings = Db.BuildSettings(e.DbKey, tier, Device, g);
        var r = e.ApplyGame(g, settings);
        if (r.Files.Count > 0) Log.Ok($"{e.Name}: {g.Title} [{tier.Label()}] → {r.Files.Count} file(s)");
        foreach (var w in r.Warnings) Log.Warn(w);
        return r;
    }

    /// <summary>Writes per-game profiles for every database game of this emulator (each at its own tier unless forced).</summary>
    public (int Games, int Files) InstallAllProfiles(EmulatorAdapter e, Tier? forced)
    {
        int games = 0, files = 0;
        foreach (var g in Db.GamesFor(e.DbKey))
        {
            try
            {
                var settings = Db.BuildSettings(e.DbKey, forced ?? TierForGame(g), Device, g);
                var r = e.ApplyGame(g, settings);
                if (r.Files.Count > 0) { games++; files += r.Files.Count; }
            }
            catch (Exception ex) { Log.Warn($"{e.Name} / {g.Title}: {ex.Message}"); }
        }
        Log.Ok($"{e.Name}: installed profiles for {games} games ({files} files)");
        return (games, files);
    }

    // ─────────── Emulator updates ───────────

    public sealed record UpdateStatus(UpdateState State, UpdateInfo? Info, string Message);

    public Dictionary<string, UpdateStatus> Updates { get; } = [];
    public event Action? UpdatesChanged;
    private DispatcherTimer? _updateTimer;
    private readonly SemaphoreSlim _updateGate = new(1, 1);

    public void StartUpdateTimer()
    {
        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
        _updateTimer.Tick += async (_, _) => await CheckUpdatesAsync(Settings.AutoUpdateEmulators);
        _updateTimer.Start();
    }

    /// <summary>Checks every supported emulator; installs updates for ones that aren't running when <paramref name="autoInstall"/>.</summary>
    public async Task CheckUpdatesAsync(bool autoInstall)
    {
        if (!await _updateGate.WaitAsync(0)) return;
        try
        {
            foreach (var e in Emulators)
            {
                if (!EmulatorUpdater.IsSupported(e.Id))
                {
                    var note = EmulatorUpdater.BuiltInUpdaterNote(e.Id);
                    if (note is not null) Updates[e.Id] = new(UpdateState.Unsupported, null, note);
                    continue;
                }
                if (e.ExePath is null) { Updates.Remove(e.Id); continue; } // not installed: offered as "Install" instead
                try
                {
                    var info = await EmulatorUpdater.CheckAsync(e.Id, Settings.IncludePrereleases);
                    if (info is null) { Updates[e.Id] = new(UpdateState.Failed, null, "No Windows build found in the latest release."); continue; }
                    var state = EmulatorUpdater.Evaluate(e, info, Settings);
                    Updates[e.Id] = new(state, info, state == UpdateState.Available ? $"Update available: {info.Version}" : $"Up to date ({info.Version})");
                    if (state == UpdateState.Available) Log.Info($"{e.Name}: update {info.Version} available");
                }
                catch (Exception ex) { Updates[e.Id] = new(UpdateState.Failed, null, $"Couldn't check: {ex.Message}"); }
            }
            Settings.LastUpdateCheck = DateTime.Now;
            SaveSettings();
            UpdatesChanged?.Invoke();

            if (!autoInstall) return;
            foreach (var e in Emulators.Where(e => Updates.TryGetValue(e.Id, out var u) && u.State == UpdateState.Available))
            {
                if (EmulatorUpdater.IsRunning(e)) { Log.Info($"{e.Name} is running – its update will install next time."); continue; }
                await InstallCoreAsync(e, Updates[e.Id].Info!);
            }
        }
        finally { _updateGate.Release(); }
    }

    /// <summary>Updates an installed emulator in place, or installs a missing one under the install root.</summary>
    public async Task<string> InstallOrUpdateAsync(EmulatorAdapter e)
    {
        await _updateGate.WaitAsync();
        try
        {
            var info = Updates.TryGetValue(e.Id, out var u) && u.Info is not null
                ? u.Info
                : await EmulatorUpdater.CheckAsync(e.Id, Settings.IncludePrereleases)
                  ?? throw new InvalidOperationException("No Windows build found in the latest release.");
            return await InstallCoreAsync(e, info);
        }
        finally { _updateGate.Release(); }
    }

    private async Task<string> InstallCoreAsync(EmulatorAdapter e, UpdateInfo info)
    {
        var dir = e.ExePath is not null ? e.ExeDir : System.IO.Path.Combine(Settings.EmulatorInstallRoot, e.Name);
        Updates[e.Id] = new(UpdateState.Updating, info, $"Installing {info.Version}…");
        UpdatesChanged?.Invoke();
        try
        {
            var progress = new Progress<string>(m => { Updates[e.Id] = new(UpdateState.Updating, info, m); UpdatesChanged?.Invoke(); });
            var exe = await Task.Run(() => EmulatorUpdater.InstallAsync(e, info, dir, progress));
            Settings.EmulatorPaths[e.Id] = exe;
            Settings.InstalledVersions[e.Id] = info.Key;
            SaveSettings();
            Updates[e.Id] = new(UpdateState.UpToDate, info, $"Up to date ({info.Version})");
            UpdatesChanged?.Invoke();
            await DetectEmulatorsAsync();
            return $"{e.Name} {info.Version} installed in {dir}. Your BIOS, saves and settings were left untouched.";
        }
        catch (Exception ex)
        {
            Updates[e.Id] = new(UpdateState.Failed, info, $"Update failed: {ex.Message}");
            UpdatesChanged?.Invoke();
            Log.Warn($"{e.Name}: {ex.Message}");
            throw;
        }
    }

    // ─────────── Per-game auto power ───────────

    private DispatcherTimer? _watch;
    private GameEntry? _activeGame;
    private int _idleTicks;

    public void StartWatcher()
    {
        _watch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _watch.Tick += async (_, _) => await WatchTickAsync();
        _watch.Start();
    }

    private async Task WatchTickAsync()
    {
        if (!Settings.AutoPowerPerGame) { AutoStatus = "Auto power per game is off"; return; }
        var (exe, title) = ForegroundWindow();
        var emu = exe is null ? null : Emulators.FirstOrDefault(e =>
            e.ExeNames.Contains(exe, StringComparer.OrdinalIgnoreCase) ||
            (e.ExePath is not null && string.Equals(System.IO.Path.GetFileName(e.ExePath), exe, StringComparison.OrdinalIgnoreCase)));
        var game = emu is null ? null : MatchGame(emu, title);

        if (game is not null)
        {
            _idleTicks = 0;
            if (_activeGame == game) return;
            _activeGame = game;
            var tier = TierForGame(game);
            AutoStatus = $"Playing {game.Title} → {tier.Label()} ({PowerService.PlanFor(Device, tier).Spl} W)";
            await Task.Run(() => ApplyPower(tier, $"Auto: {game.Title}"));
        }
        else if (_activeGame is not null && ++_idleTicks >= 5)
        {
            Log.Info($"Auto: {_activeGame.Title} closed – back to {CurrentTier.Label()}");
            _activeGame = null;
            AutoStatus = "Watching for games…";
            var tier = CurrentTier;
            await Task.Run(() => ApplyPower(tier, "Auto: restore"));
        }
        else if (_activeGame is null) AutoStatus = "Watching for games…";
    }

    private GameEntry? MatchGame(EmulatorAdapter emu, string title)
    {
        var norm = Normalize(title);
        if (norm.Length == 0) return null;
        var games = Db.GamesFor(emu.DbKey).ToList();
        return games.FirstOrDefault(g => g.Ids.Any(id => norm.Contains(Normalize(id))))
               ?? games.Where(g => Normalize(g.Title).Length >= 4 && norm.Contains(Normalize(g.Title)))
                       .MaxBy(g => g.Title.Length);
    }

    private static string Normalize(string s) => NonAlnum().Replace(s, "").ToLowerInvariant();

    [GeneratedRegex("[^A-Za-z0-9]")] private static partial Regex NonAlnum();

    private static (string? Exe, string Title) ForegroundWindow()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return (null, "");
        var sb = new StringBuilder(512);
        GetWindowText(hwnd, sb, sb.Capacity);
        GetWindowThreadProcessId(hwnd, out var pid);
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return (p.ProcessName + ".exe", sb.ToString());
        }
        catch { return (null, sb.ToString()); }
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
}
