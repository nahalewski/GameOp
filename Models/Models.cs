using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace GameOp.Models;

public enum Tier { Battery, Balanced, Performance, Max }

public static class TierInfo
{
    public static readonly Tier[] All = [Tier.Battery, Tier.Balanced, Tier.Performance, Tier.Max];

    public static string Key(this Tier t) => t.ToString().ToLowerInvariant();

    public static string Label(this Tier t) => t switch
    {
        Tier.Battery => "Battery Saver",
        Tier.Balanced => "Balanced",
        Tier.Performance => "Performance",
        Tier.Max => "Max Out",
        _ => t.ToString()
    };

    public static string Blurb(this Tier t) => t switch
    {
        Tier.Battery => "Low TDP, 60 Hz, CPU boost off, native-ish resolution. For light games and long sessions.",
        Tier.Balanced => "Mid TDP with 2x upscaling where it's cheap. A good default for most games.",
        Tier.Performance => "High TDP, 120 Hz, 1080p-class upscaling. Best when plugged in or for demanding games.",
        Tier.Max => "Every limit at the top: max TDP, highest resolution scale and accuracy. Plug in.",
        _ => ""
    };

    public static Tier Parse(string? s) => s?.ToLowerInvariant() switch
    {
        "battery" => Tier.Battery,
        "balanced" => Tier.Balanced,
        "performance" => Tier.Performance,
        "max" => Tier.Max,
        _ => Tier.Balanced
    };
}

public sealed class TdpSet
{
    public int Battery { get; set; } = 10;
    public int Balanced { get; set; } = 15;
    public int Performance { get; set; } = 25;
    public int Max { get; set; } = 30;

    public int For(Tier t) => t switch
    {
        Tier.Battery => Battery,
        Tier.Balanced => Balanced,
        Tier.Performance => Performance,
        _ => Max
    };
}

public sealed class TdpLimits
{
    public int MinSpl { get; set; } = 7;
    public int MaxSpl { get; set; } = 30;
    public int MaxSppt { get; set; } = 35;
    public int MaxFppt { get; set; } = 35;
}

public sealed class DeviceProfile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> ModelCodes { get; set; } = [];
    public string CpuMatch { get; set; } = "";
    public string? CpuExclude { get; set; }
    public string Apu { get; set; } = "";
    public string Cpu { get; set; } = "";
    public string Gpu { get; set; } = "";
    public string Ram { get; set; } = "";
    public string Display { get; set; } = "";
    public int BatteryWh { get; set; }
    public string GpuClass { get; set; } = "high";
    public TdpSet Tdp { get; set; } = new();
    public TdpLimits Limits { get; set; } = new();

    [JsonIgnore] public bool IsLowGpu => GpuClass.Equals("low", StringComparison.OrdinalIgnoreCase);
    [JsonIgnore] public bool IsAlly { get; set; } = true;
}

public sealed class TierBaseline
{
    public JsonObject? Settings { get; set; }
    public JsonObject? LowGpu { get; set; }
}

public sealed class EmulatorDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string System { get; set; } = "";
    public Dictionary<string, TierBaseline> Tiers { get; set; } = [];
}

public sealed class GameEntry
{
    public string Emulator { get; set; } = "";
    public string Title { get; set; } = "";
    public List<string> Ids { get; set; } = [];
    public string Demand { get; set; } = "medium";
    public string RecommendedTier { get; set; } = "balanced";
    public string? Compatibility { get; set; }
    public JsonObject? Settings { get; set; }
    public Dictionary<string, JsonObject>? TierSettings { get; set; }
    public string? Notes { get; set; }
    public string? Source { get; set; }

    [JsonIgnore] public string IdList => string.Join(", ", Ids);
    [JsonIgnore] public Tier Recommended => TierInfo.Parse(RecommendedTier);
    [JsonIgnore] public bool HasOverrides => Settings is { Count: > 0 } || TierSettings is { Count: > 0 };
}

public sealed class AppSettings
{
    public Dictionary<string, string> EmulatorPaths { get; set; } = [];
    public List<string> ExtraSearchRoots { get; set; } = [];
    public string? RyzenAdjPath { get; set; }
    public string? DatabaseUrl { get; set; }
    public bool AutoPowerPerGame { get; set; } = true;
    public bool GamepadNavigation { get; set; } = true;
    public string LastTier { get; set; } = "balanced";
    public string? ForcedDeviceId { get; set; }
    // Emulator updates
    public bool AutoUpdateEmulators { get; set; } = true;
    public bool IncludePrereleases { get; set; } = false;
    public string EmulatorInstallRoot { get; set; } = @"C:\Emulators";
    /// <summary>Release key GameOp last installed (or confirmed) per emulator.</summary>
    public Dictionary<string, string> InstalledVersions { get; set; } = [];
    /// <summary>Release key the user rolled back from; not offered again until a newer release appears.</summary>
    public Dictionary<string, string> SkippedReleases { get; set; } = [];
    public DateTime LastUpdateCheck { get; set; }

    // Extras
    public HashSet<string> EnhancementsOn { get; set; } = [];
    public HashSet<string> EnhancementsOff { get; set; } = [];
    /// <summary>Emulators whose default enhancements have already been switched on once.</summary>
    public HashSet<string> EnhancementDefaultsDone { get; set; } = [];
    public bool AutoUpdateAddons { get; set; } = true;
    public HashSet<string> AddonsDisabled { get; set; } = [];
    public Dictionary<string, string> AddonVersions { get; set; } = [];
    public bool AutoDownloadPacks { get; set; } = true;
    /// <summary>Game database and texture-pack catalog are refreshed from here automatically (empty = off).</summary>
    public string RemoteCatalogBase { get; set; } = "https://raw.githubusercontent.com/nahalewski/GameOp/main/Data/";
    public double MaxAutoPackGb { get; set; } = 4;
    /// <summary>packId → "gameId|version" of installed texture packs.</summary>
    public Dictionary<string, string> InstalledPacks { get; set; } = [];
    /// <summary>"emulator:id" of games the user has applied a profile to (counts as "owned" for auto-downloads).</summary>
    public HashSet<string> ProfiledGames { get; set; } = [];

    /// <summary>Per-game tier chosen by the user, keyed "emulator:firstId".</summary>
    public Dictionary<string, string> GameTiers { get; set; } = [];

    // System tweak choices
    public bool TweakGameMode { get; set; } = true;
    public bool TweakDisableGameDvr { get; set; } = true;
    public bool TweakHags { get; set; } = true;
    public bool TweakRefreshPerTier { get; set; } = true;
    public bool TweakBoostPerTier { get; set; } = true;
    public bool TweakPowerModePerTier { get; set; } = true;
    public bool TweakDisableMemoryIntegrity { get; set; } = false;
}
