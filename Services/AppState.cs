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
    public ExtrasCatalog Extras { get; private set; } = new();
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
        Extras = ExtrasCatalog.Load();
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
        // Scan on a worker thread into a new map, then publish it on the UI thread (pages read InstalledIds).
        var scanned = await Task.Run(() =>
        {
            EmulatorRegistry.Detect(Emulators, Settings);
            var ids = new Dictionary<string, HashSet<string>>();
            foreach (var e in Emulators.Where(e => e.IsDetected))
            {
                try { ids[e.Id] = new HashSet<string>(e.ScanInstalledIds(), StringComparer.OrdinalIgnoreCase); }
                catch (Exception ex) { Log.Warn($"{e.Name} library scan: {ex.Message}"); }
            }
            return ids;
        });
        InstalledIds.Clear();
        foreach (var (k, v) in scanned) InstalledIds[k] = v;
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
        // Power mode, CPU boost and refresh rate are handheld settings – never change them on a desktop PC.
        if (!Device.IsAlly) return;
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
            var settings = BuildSettings(e.DbKey, tier, null);
            var r = e.ApplyGlobal(settings);
            Log.Ok($"{e.Name}: {tier.Label()} defaults → {string.Join(", ", r.Files.Select(System.IO.Path.GetFileName))}");
            foreach (var w in r.Warnings) Log.Warn(w);
        }
        catch (Exception ex) { Log.Warn($"{e.Name}: {ex.Message}"); }
    }

    public ApplyResult ApplyGame(EmulatorAdapter e, GameEntry g, Tier tier)
    {
        var settings = BuildSettings(e.DbKey, tier, g);
        var r = e.ApplyGame(g, settings);
        if (r.Files.Count > 0 && Settings.ProfiledGames.Add($"{g.Emulator}:{g.Ids[0]}")) SaveSettings();
        if (r.Files.Count > 0) Log.Ok($"{e.Name}: {g.Title} [{tier.Label()}] → {r.Files.Count} file(s)");
        foreach (var w in r.Warnings) Log.Warn(w);
        return r;
    }

    /// <summary>Writes per-game profiles for every database game of this emulator (each at its own tier unless forced).</summary>
    public (int Games, int Files) InstallAllProfiles(EmulatorAdapter e, Tier? forced)
    {
        int games = 0, files = 0;
        e.ScanInstalledIds();
        e.BulkMode = true;
        try
        {
            foreach (var g in Db.GamesFor(e.DbKey))
            {
                try
                {
                    var settings = BuildSettings(e.DbKey, forced ?? TierForGame(g), g);
                    var r = e.ApplyGame(g, settings);
                    if (r.Files.Count > 0) { games++; files += r.Files.Count; }
                }
                catch (Exception ex) { Log.Warn($"{e.Name} / {g.Title}: {ex.Message}"); }
            }
        }
        finally { e.BulkMode = false; }
        Log.Ok($"{e.Name}: installed profiles for {games} games ({files} files)");
        return (games, files);
    }

    // ─────────── Extras: enhancements ───────────

    public event Action? ExtrasChanged;

    public void ReloadExtras()
    {
        Extras = ExtrasCatalog.Load();
        ExtrasChanged?.Invoke();
    }

    public bool IsOn(Enhancement x) => Settings.EnhancementsOn.Contains(x.Id);

    /// <summary>Game database settings plus the enhancements the user switched on for this emulator.</summary>
    public System.Text.Json.Nodes.JsonObject BuildSettings(string dbKey, Tier tier, GameEntry? game)
    {
        var s = Db.BuildSettings(dbKey, tier, Device, game);
        foreach (var x in Extras.Enhancements.Where(x => x.Emulator == dbKey))
        {
            var on = IsOn(x) && !(x.Heavy && tier == Tier.Battery);
            if (on) Json.DeepMerge(s, game is not null && x.PerGame is not null ? x.PerGame : x.On);
            else if (Settings.EnhancementsOff.Contains(x.Id) || IsOn(x)) // only touch what the user (or a tier) turned off
            {
                var off = game is not null && x.PerGame is not null ? x.PerGameOff : x.Off;
                if (off is not null) Json.DeepMerge(s, off);
            }
        }
        // Game-specific fixes win over enhancements.
        if (game is not null)
        {
            Json.DeepMerge(s, game.Settings);
            if (game.TierSettings?.TryGetValue(tier.Key(), out var ts) == true) Json.DeepMerge(s, ts);
        }
        return s;
    }

    /// <summary>Turns an enhancement on/off in the global config and in every per-game profile GameOp wrote. Returns false if a write failed.</summary>
    public bool SetEnhancement(Enhancement x, bool on)
    {
        if (on) { Settings.EnhancementsOn.Add(x.Id); Settings.EnhancementsOff.Remove(x.Id); }
        else { Settings.EnhancementsOn.Remove(x.Id); Settings.EnhancementsOff.Add(x.Id); }
        SaveSettings();
        var ok = true;
        // Heavy enhancements stay off globally while Battery Saver is active.
        var payload = on && !(x.Heavy && CurrentTier == Tier.Battery) ? x.On : x.Off;
        foreach (var e in Emulators.Where(e => e.DbKey == x.Emulator && e.IsDetected))
        {
            try
            {
                if (payload is not null) e.ApplyGlobal(payload);
                ReapplyProfiles(e);
                Log.Ok($"{e.Name}: {x.Name} {(on ? "on" : "off")}");
            }
            catch (Exception ex) { ok = false; Log.Warn($"{e.Name}: {x.Name}: {ex.Message}"); }
        }
        ExtrasChanged?.Invoke();
        return ok;
    }

    /// <summary>Rewrites the per-game profiles the user already has for this emulator (keeps them in sync with enhancements).</summary>
    private void ReapplyProfiles(EmulatorAdapter e)
    {
        if (!e.SupportsPerGame) return;
        e.BulkMode = true;
        try
        {
            foreach (var key in Settings.ProfiledGames.Where(k => k.StartsWith(e.DbKey + ":")).ToList())
            {
                var id = key[(e.DbKey.Length + 1)..];
                if (Db.GamesFor(e.DbKey).FirstOrDefault(g => g.Ids.Contains(id)) is { } g)
                    e.ApplyGame(g, BuildSettings(e.DbKey, TierForGame(g), g));
            }
        }
        finally { e.BulkMode = false; }
    }

    /// <summary>Switches on each emulator's default enhancements the first time it's found (retried until the writes succeed).</summary>
    public void ApplyDefaultEnhancements()
    {
        foreach (var key in Emulators.Where(e => e.IsDetected).Select(e => e.DbKey).Distinct().ToList())
        {
            if (Settings.EnhancementDefaultsDone.Contains(key)) continue;
            var ok = true;
            foreach (var x in Extras.Enhancements.Where(x => x.Emulator == key && x.Default && !Settings.EnhancementsOff.Contains(x.Id)))
                ok &= SetEnhancement(x, true);
            if (ok) Settings.EnhancementDefaultsDone.Add(key);
        }
        SaveSettings();
    }

    // ─────────── Extras: add-ons and texture packs ───────────

    public Dictionary<string, (string Status, ExtrasService.AddonRelease? Latest)> AddonStatus { get; } = [];
    public Dictionary<string, string> PackStatus { get; } = [];

    public EmulatorAdapter? AdapterFor(string emulatorId) =>
        Emulators.FirstOrDefault(e => e.Id == emulatorId && e.IsDetected) ?? Emulators.FirstOrDefault(e => e.DbKey == emulatorId && e.IsDetected);

    public async Task CheckAddonsAsync(bool autoInstall)
    {
        foreach (var a in Extras.Addons)
        {
            if (AdapterFor(a.Emulator) is null) { AddonStatus[a.Id] = ($"Needs {a.Emulator}", null); continue; }
            try
            {
                var rel = await ExtrasService.CheckAddonAsync(a);
                var installed = Settings.AddonVersions.GetValueOrDefault(a.Id);
                AddonStatus[a.Id] = (installed is null ? $"Not installed (latest {rel.Version})"
                                   : installed == rel.Key ? $"Up to date ({rel.Version})" : $"Update available: {rel.Version}", rel);
                var wanted = a.Default && !Settings.AddonsDisabled.Contains(a.Id);
                if (autoInstall && wanted && installed != rel.Key) await InstallAddonAsync(a);
            }
            catch (Exception ex) { AddonStatus[a.Id] = ($"Couldn't check: {ex.Message}", null); }
        }
        ExtrasChanged?.Invoke();
    }

    /// <summary>Add-ons / packs being installed right now (manual clicks and the background job share this).</summary>
    private readonly HashSet<string> _busy = [];

    public async Task<string> InstallAddonAsync(Addon a)
    {
        var e = AdapterFor(a.Emulator) ?? throw new InvalidOperationException($"Install {a.Emulator} first.");
        if (!_busy.Add(a.Id)) return $"{a.Name} is already being installed.";
        try { return await InstallAddonCoreAsync(a, e); }
        finally { _busy.Remove(a.Id); }
    }

    private async Task<string> InstallAddonCoreAsync(Addon a, EmulatorAdapter e)
    {
        var rel = AddonStatus.GetValueOrDefault(a.Id).Latest ?? await ExtrasService.CheckAddonAsync(a);
        AddonStatus[a.Id] = ("Installing…", rel);
        ExtrasChanged?.Invoke();
        try
        {
            var msg = await Task.Run(() => ExtrasService.InstallAddonAsync(a, rel, e));
            Settings.AddonVersions[a.Id] = rel.Key;
            Settings.AddonsDisabled.Remove(a.Id);
            SaveSettings();
            AddonStatus[a.Id] = ($"Up to date ({rel.Version})", rel);
            if (a.Enables is not null && Extras.Enhancements.FirstOrDefault(x => x.Id == a.Enables) is { } enh && !IsOn(enh))
                SetEnhancement(enh, true);
            Log.Ok(msg);
            return msg;
        }
        catch (Exception ex)
        {
            AddonStatus[a.Id] = ($"Failed: {ex.Message}", rel);
            Log.Warn($"{a.Name}: {ex.Message}");
            throw;
        }
        finally { ExtrasChanged?.Invoke(); }
    }

    /// <summary>Game ids the user owns for this emulator: found in the library scan or given a profile.</summary>
    public HashSet<string> OwnedIds(EmulatorAdapter e)
    {
        var ids = new HashSet<string>(InstalledIds.GetValueOrDefault(e.Id) ?? [], StringComparer.OrdinalIgnoreCase);
        foreach (var k in Settings.ProfiledGames.Where(k => k.StartsWith(e.DbKey + ":")))
            ids.Add(k[(e.DbKey.Length + 1)..]);
        return ids;
    }

    public bool IsPackInstalled(TexturePack p) => Settings.InstalledPacks.ContainsKey(p.Id);

    public IEnumerable<TexturePack> PacksFor(GameEntry g) =>
        Extras.Packs.Where(p => p.Emulator == g.Emulator && p.GameIds.Any(id => g.Ids.Contains(id, StringComparer.OrdinalIgnoreCase)
            || (id.Length == 3 && g.Ids.Any(gid => gid.StartsWith(id, StringComparison.OrdinalIgnoreCase)))));

    /// <summary>Installs a pack for <paramref name="forGame"/> (the game the user picked) or the pack's first owned id.</summary>
    public async Task<string> InstallPackAsync(TexturePack p, GameEntry? forGame = null)
    {
        var e = AdapterFor(p.Emulator) ?? throw new InvalidOperationException($"Install {p.Emulator} first.");
        var owned = OwnedIds(e);
        var gameId = forGame?.Ids.FirstOrDefault(id => p.GameIds.Contains(id, StringComparer.OrdinalIgnoreCase))
                     ?? p.GameIds.FirstOrDefault(owned.Contains) ?? p.GameIds[0];
        if (gameId.Length == 3 && forGame is not null) gameId = forGame.Ids.FirstOrDefault(id => id.StartsWith(gameId)) ?? gameId;
        if (!_busy.Add(p.Id)) return $"{p.Name} is already downloading.";
        try { return await InstallPackCoreAsync(p, e, gameId); }
        finally { _busy.Remove(p.Id); }
    }

    private async Task<string> InstallPackCoreAsync(TexturePack p, EmulatorAdapter e, string gameId)
    {
        PackStatus[p.Id] = "Downloading…";
        ExtrasChanged?.Invoke();
        try
        {
            var progress = new Progress<string>(m => { PackStatus[p.Id] = m; ExtrasChanged?.Invoke(); });
            var msg = await Task.Run(() => ExtrasService.InstallPackAsync(p, e, gameId, progress));
            Settings.InstalledPacks[p.Id] = $"{gameId}|{p.Version}";
            SaveSettings();
            EnableTextureLoading(e);
            PackStatus[p.Id] = "Installed";
            Log.Ok(msg);
            return msg;
        }
        catch (Exception ex)
        {
            PackStatus[p.Id] = $"Failed: {ex.Message}";
            Log.Warn($"{p.Name}: {ex.Message}");
            throw;
        }
        finally { ExtrasChanged?.Invoke(); }
    }

    public async Task<string> InstallPackFromFileAsync(string path, EmulatorAdapter e, string gameId)
    {
        var msg = await ExtrasService.InstallPackFromFileAsync(path, e, gameId);
        EnableTextureLoading(e);
        Log.Ok(msg);
        return msg;
    }

    /// <summary>Turns on the emulator's "load HD textures" enhancement (convention: "&lt;emulator&gt;.hdtextures").</summary>
    private void EnableTextureLoading(EmulatorAdapter e)
    {
        if (Extras.Enhancements.FirstOrDefault(x => x.Id == $"{e.DbKey}.hdtextures") is { } x && !IsOn(x))
            System.Windows.Application.Current.Dispatcher.Invoke(() => SetEnhancement(x, true));
    }

    /// <summary>Background job: keep add-ons current and fetch texture packs for games the user owns.</summary>
    /// <summary>Pulls the latest game database and texture-pack catalog published in the GameOp repo.</summary>
    private async Task RefreshRemoteCatalogsAsync()
    {
        if (string.IsNullOrWhiteSpace(Settings.RemoteCatalogBase) ||
            !Uri.TryCreate(Settings.RemoteCatalogBase, UriKind.Absolute, out var b) || b.Scheme != Uri.UriSchemeHttps) return;
        try
        {
            var n = await Db.UpdateFromUrlAsync(new Uri(b, "gamedb.json").ToString());
            Log.Info($"Game database refreshed ({n} games, v{Db.Version})");
        }
        catch (Exception ex) { Log.Info($"Game database not refreshed: {ex.Message}"); }
        try
        {
            var n = await ExtrasCatalog.ImportPacksAsync(new Uri(b, "texturepacks.json").ToString());
            ReloadExtras();
            Log.Info($"Texture-pack catalog refreshed ({n} packs)");
        }
        catch (Exception ex) { Log.Info($"Texture-pack catalog not refreshed: {ex.Message}"); }
    }

    private bool _extrasRunning;

    public async Task RunExtrasAutoAsync()
    {
        if (_extrasRunning) return;
        _extrasRunning = true;
        try
        {
            await RefreshRemoteCatalogsAsync();
            ApplyDefaultEnhancements();
            await CheckAddonsAsync(Settings.AutoUpdateAddons);
            if (!Settings.AutoDownloadPacks) return;
            var maxBytes = (long)(Settings.MaxAutoPackGb * (1L << 30));
            // Unknown sizes (0) are never downloaded unattended.
            foreach (var p in Extras.Packs.Where(p => !IsPackInstalled(p) && p.SizeBytes > 0 && p.SizeBytes <= maxBytes).ToList())
            {
                if (AdapterFor(p.Emulator) is not { } e || !p.GameIds.Any(OwnedIds(e).Contains)) continue;
                try { await InstallPackAsync(p); }
                catch { /* logged; retried next cycle */ }
            }
        }
        finally { _extrasRunning = false; }
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
        _updateTimer.Tick += async (_, _) =>
        {
            await CheckUpdatesAsync(Settings.AutoUpdateEmulators);
            await RunExtrasAutoAsync();
        };
        _updateTimer.Start();
    }

    /// <summary>Checks every supported emulator; installs updates for ones that aren't running when <paramref name="autoInstall"/>.</summary>
    public async Task CheckUpdatesAsync(bool autoInstall, bool waitIfBusy = false)
    {
        // Background checks skip if an install is running; a manual check waits for it.
        if (!await _updateGate.WaitAsync(waitIfBusy ? Timeout.Infinite : 0)) return;
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
                try { await InstallCoreAsync(e, Updates[e.Id].Info!); }
                catch { /* already logged and shown on the card; carry on with the other emulators */ }
            }
        }
        finally { _updateGate.Release(); }
    }

    /// <summary>Rolls back the last update and skips that release until a newer one comes out.</summary>
    public async Task<string> RollbackAsync(EmulatorAdapter e)
    {
        await _updateGate.WaitAsync();
        try
        {
            var msg = await Task.Run(() => EmulatorUpdater.Rollback(e));
            if (msg.StartsWith("Restored"))
            {
                if (Updates.TryGetValue(e.Id, out var u) && u.Info is not null) Settings.SkippedReleases[e.Id] = u.Info.Key;
                else if (Settings.InstalledVersions.TryGetValue(e.Id, out var k)) Settings.SkippedReleases[e.Id] = k;
                Settings.InstalledVersions.Remove(e.Id);
                SaveSettings();
                Updates[e.Id] = new(UpdateState.UpToDate, null, "Rolled back – this release will be skipped");
                UpdatesChanged?.Invoke();
            }
            Log.Info($"{e.Name}: {msg}");
            return msg;
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
        if (!Settings.AutoPowerPerGame)
        {
            AutoStatus = "Auto power per game is off";
            if (_activeGame is not null)
            {
                // Switched off mid-game: go back to the user's level.
                _activeGame = null;
                var t = CurrentTier;
                await Task.Run(() => ApplyPower(t, "Auto power off: restore"));
            }
            return;
        }
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
