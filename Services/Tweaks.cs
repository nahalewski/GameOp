using System.IO;
using GameOp.Models;
using Microsoft.Win32;

namespace GameOp.Services;

/// <summary>A registry-backed Windows tweak with a recorded original value so it can be reverted.</summary>
/// <param name="Off">Value written when the user turns the tweak off and GameOp has no earlier value to restore.</param>
public sealed record RegTweak(string Id, RegistryHive Hive, string Key, string Value, int On, int Off, bool NeedsReboot);

public static class TweakService
{
    public static readonly RegTweak[] GameMode =
    [
        new("gamemode.auto", RegistryHive.CurrentUser, @"Software\Microsoft\GameBar", "AutoGameModeEnabled", 1, 0, false),
        new("gamemode.allow", RegistryHive.CurrentUser, @"Software\Microsoft\GameBar", "AllowAutoGameMode", 1, 0, false),
    ];

    public static readonly RegTweak[] DisableGameDvr =
    [
        new("dvr.capture", RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0, 1, false),
        new("dvr.enabled", RegistryHive.CurrentUser, @"System\GameConfigStore", "GameDVR_Enabled", 0, 1, false),
    ];

    public static readonly RegTweak[] Hags =
    [
        new("hags", RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", 2, 1, true),
    ];

    public static readonly RegTweak[] DisableMemoryIntegrity =
    [
        new("hvci", RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity", "Enabled", 0, 1, true),
    ];

    private sealed class Snapshot { public Dictionary<string, int?> Values { get; set; } = []; }

    private static Snapshot LoadSnapshot() => Json.Load<Snapshot>(AppPaths.TweakBackupFile) ?? new Snapshot();

    public static bool IsApplied(RegTweak[] tweaks) => tweaks.All(t => Read(t) == t.On);

    /// <summary>Applies or reverts a group. Returns true if a reboot is needed for the change to take effect.</summary>
    public static bool Set(RegTweak[] tweaks, bool enable)
    {
        var snap = LoadSnapshot();
        var reboot = false;
        foreach (var t in tweaks)
        {
            var current = Read(t);
            if (enable)
            {
                if (!snap.Values.ContainsKey(t.Id)) snap.Values[t.Id] = current;
                if (current != t.On) { Write(t, t.On); reboot |= t.NeedsReboot; }
            }
            else if (snap.Values.TryGetValue(t.Id, out var original) && original != t.On)
            {
                // Put back what the user had before GameOp.
                if (original is null) Delete(t); else Write(t, original.Value);
                snap.Values.Remove(t.Id);
                reboot |= t.NeedsReboot && current != original;
            }
            else
            {
                // No earlier value to restore (or it was already "on" before GameOp): write the explicit off value.
                snap.Values.Remove(t.Id);
                if (current != t.Off) { Write(t, t.Off); reboot |= t.NeedsReboot; }
            }
        }
        Json.Save(AppPaths.TweakBackupFile, snap);
        return reboot;
    }

    private static RegistryKey Base(RegistryHive hive) => RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);

    private static int? Read(RegTweak t)
    {
        using var k = Base(t.Hive).OpenSubKey(t.Key);
        return k?.GetValue(t.Value) is int i ? i : null;
    }

    private static void Write(RegTweak t, int value)
    {
        using var k = Base(t.Hive).CreateSubKey(t.Key, writable: true);
        k.SetValue(t.Value, value, RegistryValueKind.DWord);
    }

    private static void Delete(RegTweak t)
    {
        using var k = Base(t.Hive).OpenSubKey(t.Key, writable: true);
        k?.DeleteValue(t.Value, throwOnMissingValue: false);
    }

    /// <summary>Per-tier Windows power behaviour: power mode overlay, CPU boost and panel refresh rate.</summary>
    public static IEnumerable<string> ApplyTierSystem(Tier tier, AppSettings s)
    {
        if (s.TweakPowerModePerTier)
        {
            var overlay = tier switch
            {
                Tier.Battery => PowerService.OverlayEfficiency,
                Tier.Balanced => PowerService.OverlayBalanced,
                _ => PowerService.OverlayPerformance
            };
            yield return PowerService.SetPowerOverlay(overlay) ? $"Windows power mode → {tier.Label()}" : "Couldn't set Windows power mode";
        }
        if (s.TweakBoostPerTier)
        {
            var boost = tier == Tier.Battery ? 0 : 2;
            PowerService.SetCpuBoost(boost);
            yield return boost == 0 ? "CPU boost off (saves battery)" : "CPU boost aggressive";
        }
        if (s.TweakRefreshPerTier)
        {
            var hz = DisplayService.SetRefresh(tier == Tier.Battery ? 60 : 120);
            yield return hz > 0 ? $"Display {hz} Hz" : "Refresh rate unchanged";
        }
    }
}
