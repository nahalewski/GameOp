using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using GameOp.Models;

namespace GameOp.Services;

public sealed class GameDbFile
{
    public string? Version { get; set; }
    public string? Emulator { get; set; }   // allows single-emulator files
    public List<GameEntry> Games { get; set; } = [];
}

public sealed class GameDatabase
{
    public List<DeviceProfile> Devices { get; private set; } = [];
    public List<EmulatorDef> Emulators { get; private set; } = [];
    public List<GameEntry> Games { get; private set; } = [];
    public string Version { get; private set; } = "?";
    public string Source { get; private set; } = "bundled";

    private sealed class DevicesFile { public List<DeviceProfile> Devices { get; set; } = []; }
    private sealed class EmulatorsFile { public List<EmulatorDef> Emulators { get; set; } = []; }

    public static GameDatabase Load()
    {
        var db = new GameDatabase
        {
            Devices = Json.LoadBundled<DevicesFile>("devices.json")?.Devices ?? [],
            Emulators = Json.LoadBundled<EmulatorsFile>("emulators.json")?.Emulators ?? [],
        };
        db.LoadGames();
        return db;
    }

    private void LoadGames()
    {
        var bundled = Prepare(Json.LoadBundled<GameDbFile>("gamedb.json"));
        var user = File.Exists(AppPaths.UserGameDb) ? ReadGameFile(AppPaths.UserGameDb) : null;

        // Downloaded/imported entries override bundled ones for the same emulator + id.
        var games = new List<GameEntry>(bundled?.Games ?? []);
        if (user is not null)
        {
            var userKeys = user.Games.SelectMany(g => g.Ids.Select(id => $"{g.Emulator}:{id}")).ToHashSet(StringComparer.OrdinalIgnoreCase);
            games.RemoveAll(g => g.Ids.Any(id => userKeys.Contains($"{g.Emulator}:{id}")));
            games.AddRange(user.Games);
            Source = "bundled + downloaded";
        }
        Games = games.OrderBy(g => g.Emulator).ThenBy(g => g.Title).ToList();
        Version = user?.Version ?? bundled?.Version ?? "?";
    }

    private static GameDbFile? Prepare(GameDbFile? file)
    {
        if (file?.Emulator is { } emu)
            foreach (var g in file.Games.Where(g => string.IsNullOrEmpty(g.Emulator))) g.Emulator = emu;
        if (file is not null) Sanitize(file.Games);
        return file;
    }

    private static GameDbFile? ReadGameFile(string path)
    {
        try
        {
            return Prepare(Json.Load<GameDbFile>(path));
        }
        catch (Exception ex)
        {
            Log.Warn($"Couldn't read {Path.GetFileName(path)}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Game ids become file names, so anything that isn't a plain token is dropped (and games left without ids).</summary>
    private static void Sanitize(List<GameEntry> games)
    {
        foreach (var g in games)
        {
            var bad = g.Ids.Where(id => !SafeId.IsValid(id)).ToList();
            if (bad.Count > 0) Log.Warn($"{g.Title}: ignored invalid id(s) {string.Join(", ", bad)}");
            g.Ids = g.Ids.Where(SafeId.IsValid).ToList();
        }
        games.RemoveAll(g => g.Ids.Count == 0);
    }

    public EmulatorDef? Emulator(string id) => Emulators.FirstOrDefault(e => e.Id == id);

    public IEnumerable<GameEntry> GamesFor(string dbKey) => Games.Where(g => g.Emulator == dbKey);

    /// <summary>Tier baseline → low-GPU adjustments → game overrides → game per-tier overrides.</summary>
    public JsonObject BuildSettings(string dbKey, Tier tier, DeviceProfile device, GameEntry? game)
    {
        var result = new JsonObject();
        var emu = Emulator(dbKey);
        if (emu is not null && emu.Tiers.TryGetValue(tier.Key(), out var baseline))
        {
            Json.DeepMerge(result, baseline.Settings);
            if (device.IsLowGpu) Json.DeepMerge(result, baseline.LowGpu);
        }
        if (game is not null)
        {
            Json.DeepMerge(result, game.Settings);
            if (game.TierSettings?.TryGetValue(tier.Key(), out var ts) == true) Json.DeepMerge(result, ts);
        }
        return result;
    }

    /// <summary>Downloads a game database (same JSON shape as Data\gamedb.json) and merges it over the bundled one.</summary>
    public async Task<int> UpdateFromUrlAsync(string url)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("GameOp/1.0");
        var text = await http.GetStringAsync(url);
        return ImportText(text);
    }

    public int ImportFile(string path) => ImportText(File.ReadAllText(path));

    private int ImportText(string text)
    {
        var incoming = JsonSerializer.Deserialize<GameDbFile>(text, Json.Options)
                       ?? throw new InvalidDataException("Empty database file.");
        if (incoming.Emulator is { } emu)
            foreach (var g in incoming.Games.Where(g => string.IsNullOrEmpty(g.Emulator))) g.Emulator = emu;
        Sanitize(incoming.Games);
        if (incoming.Games.Count == 0 || incoming.Games.Any(g => string.IsNullOrWhiteSpace(g.Emulator) || g.Ids.Count == 0))
            throw new InvalidDataException("Every game needs an 'emulator' and at least one id.");

        // Merge with anything previously downloaded.
        var existing = File.Exists(AppPaths.UserGameDb) ? ReadGameFile(AppPaths.UserGameDb) : null;
        var merged = existing?.Games ?? [];
        var keys = incoming.Games.SelectMany(g => g.Ids.Select(id => $"{g.Emulator}:{id}")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        merged.RemoveAll(g => g.Ids.Any(id => keys.Contains($"{g.Emulator}:{id}")));
        merged.AddRange(incoming.Games);
        Json.Save(AppPaths.UserGameDb, new GameDbFile { Version = incoming.Version ?? DateTime.Now.ToString("yyyy.MM.dd"), Games = merged });
        LoadGames();
        return incoming.Games.Count;
    }

    public void ResetToBundled()
    {
        if (File.Exists(AppPaths.UserGameDb)) File.Delete(AppPaths.UserGameDb);
        Source = "bundled";
        LoadGames();
    }

    /// <summary>Live compatibility status from the official RPCS3 compatibility API.</summary>
    public static async Task<string?> Rpcs3CompatAsync(string serial)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("GameOp/1.0");
            var json = JsonNode.Parse(await http.GetStringAsync($"https://rpcs3.net/compatibility?api=v1&g={Uri.EscapeDataString(serial)}"));
            if (json?["results"] is JsonObject results && results[serial] is JsonObject r)
                return $"{r["status"]} (updated {r["date"]})";
        }
        catch { /* offline is fine */ }
        return null;
    }
}
