using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace GameOp.Services;

public enum UpdateState { NotChecked, Unsupported, UpToDate, Available, Updating, Failed }

public sealed record UpdateInfo(string Version, string Key, DateTime Published, string AssetName, string Url, string? Sha256, long Size, string ReleasePage);

/// <summary>
/// Keeps emulators current from their official GitHub releases.
///
/// An update NEVER deletes anything. It only adds or replaces program files that are in the new release,
/// and it skips anything that belongs to you even if the release ships a file with the same name:
/// BIOS / firmware / keys, saves, memory cards, save states, configs, per-game settings, caches and games.
/// Every file it replaces is copied to backups\updates first, so an update can be rolled back.
/// </summary>
public static class EmulatorUpdater
{
    private sealed record Source(string Repo, Regex Asset, bool Prerelease = false);

    private static readonly Dictionary<string, Source> Sources = new()
    {
        ["pcsx2"] = new("PCSX2/pcsx2", new(@"^pcsx2-v[\d.]+-windows-x64-Qt\.7z$", RegexOptions.IgnoreCase)),
        ["rpcs3"] = new("RPCS3/rpcs3-binaries-win", new(@"_win64_msvc\.7z$", RegexOptions.IgnoreCase)),
        ["duckstation"] = new("stenzek/duckstation", new(@"^duckstation-windows-x64-release\.zip$", RegexOptions.IgnoreCase)),
        ["ppsspp"] = new("hrydgard/ppsspp", new(@"-Windows-x64\.zip$", RegexOptions.IgnoreCase)),
        ["cemu"] = new("cemu-project/Cemu", new(@"-windows-x64\.zip$", RegexOptions.IgnoreCase)),
        ["xenia"] = new("xenia-canary/xenia-canary-releases", new(@"^xenia_canary_windows.*\.zip$", RegexOptions.IgnoreCase)),
    };

    /// <summary>Emulators that ship their own updater (or don't publish on GitHub).</summary>
    public static string? BuiltInUpdaterNote(string id) => id switch
    {
        "dolphin" => "Dolphin updates itself: turn on Config → General → Auto Update.",
        "ryujinx" => "Ryujinx checks for updates itself when it starts.",
        "eden" or "citron" or "sudachi" => "This emulator updates through its own Help → Check for Updates menu or its website.",
        _ => null
    };

    public static bool IsSupported(string id) => Sources.ContainsKey(id);

    // ───────────── Protection rules ─────────────

    private static readonly HashSet<string> ProtectedDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "bios", "firmware", "keys", "nand", "sdmc", "user", "userdata", "portable", "memstick",
        "dev_flash", "dev_flash2", "dev_flash3", "dev_hdd0", "dev_hdd1", "dev_usb000", "dev_bdvd",
        "config", "custom_configs", "inis", "gamesettings", "gameprofiles_user", "memcards", "sstates",
        "savestates", "saves", "savedata", "cheats", "cheats_ws", "patches", "textures", "covers", "snaps",
        "cache", "shaders", "shadercache", "logs", "games", "roms", "screenshots", "videos", "content", "cache_host",
    };

    private static readonly HashSet<string> ProtectedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".bin", ".rom", ".pup", ".keys", ".nvm", ".mec", ".mcd", ".ps2", ".sav", ".srm", ".p2s", ".sstate", ".bak", ".gameop-original",
    };

    // User configs that emulators keep beside the exe.
    private static readonly HashSet<string> RootConfigExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ini", ".yml", ".yaml", ".toml", ".xml", ".json", ".txt", ".cfg",
    };

    /// <summary>True if a file at this path (relative to the install dir) must never be overwritten.</summary>
    public static bool IsProtected(string relativePath)
    {
        var parts = relativePath.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return true;
        // User-data folders live at the top of the install (bios\, memcards\, dev_hdd0\ …). Only the top level is
        // checked so program folders such as resources\shaders still get updated.
        if (parts.Length > 1 && ProtectedDirs.Contains(parts[0])) return true;
        var name = parts[^1];
        if (ProtectedExtensions.Contains(Path.GetExtension(name))) return true;
        if (name.StartsWith("scph", StringComparison.OrdinalIgnoreCase)) return true;          // PS1/PS2 BIOS dumps
        if (parts.Length == 1 && RootConfigExtensions.Contains(Path.GetExtension(name))) return true;
        return false;
    }

    // ───────────── Checking ─────────────

    public static async Task<UpdateInfo?> CheckAsync(string emulatorId, bool includePrerelease)
    {
        if (!Sources.TryGetValue(emulatorId, out var src)) return null;
        using var http = Http();
        var url = includePrerelease || src.Prerelease
            ? $"https://api.github.com/repos/{src.Repo}/releases?per_page=10"
            : $"https://api.github.com/repos/{src.Repo}/releases/latest";
        var node = JsonNode.Parse(await http.GetStringAsync(url));
        var releases = node is JsonArray arr ? arr.OfType<JsonObject>() : [node!.AsObject()];

        foreach (var rel in releases)
        {
            if (rel["draft"]?.GetValue<bool>() == true) continue;
            var asset = (rel["assets"] as JsonArray)?.OfType<JsonObject>()
                .FirstOrDefault(a => src.Asset.IsMatch(a["name"]!.GetValue<string>()));
            if (asset is null) continue;

            var tag = rel["tag_name"]!.GetValue<string>();
            var name = asset["name"]!.GetValue<string>();
            var updated = asset["updated_at"]!.GetValue<DateTime>();
            var version = FriendlyVersion(tag, name, updated);
            var digest = asset["digest"]?.GetValue<string>();
            return new UpdateInfo(version, $"{tag}|{name}|{updated:O}", updated, name,
                asset["browser_download_url"]!.GetValue<string>(),
                digest?.StartsWith("sha256:") == true ? digest[7..] : null,
                asset["size"]?.GetValue<long>() ?? 0,
                rel["html_url"]?.GetValue<string>() ?? $"https://github.com/{src.Repo}/releases");
        }
        return null;
    }

    private static string FriendlyVersion(string tag, string asset, DateTime date)
    {
        var m = Regex.Match(asset, @"v?(\d+\.\d+(?:\.\d+)*(?:-\d+)?)");
        if (m.Success) return m.Groups[1].Value;
        return tag is "latest" or "preview" || tag.Length > 20 || Regex.IsMatch(tag, "^[0-9a-f]{7,}$")
            ? $"{date:yyyy-MM-dd} ({tag[..Math.Min(tag.Length, 7)]})" : tag;
    }

    /// <summary>Compares with what GameOp last installed; falls back to the exe's date the first time.</summary>
    public static UpdateState Evaluate(EmulatorAdapter e, UpdateInfo info, Models.AppSettings s)
    {
        if (s.InstalledVersions.TryGetValue(e.Id, out var key))
            return key == info.Key ? UpdateState.UpToDate : UpdateState.Available;
        if (e.ExePath is null || !File.Exists(e.ExePath)) return UpdateState.Available;
        var exeDate = File.GetLastWriteTimeUtc(e.ExePath);
        if (exeDate >= info.Published.AddDays(-1))
        {
            s.InstalledVersions[e.Id] = info.Key; // assume current; track from here
            return UpdateState.UpToDate;
        }
        return UpdateState.Available;
    }

    public static bool IsRunning(EmulatorAdapter e) =>
        e.ExeNames.Select(Path.GetFileNameWithoutExtension)
            .Concat(e.ExePath is null ? [] : [Path.GetFileNameWithoutExtension(e.ExePath)])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Any(n => Process.GetProcessesByName(n!).Length > 0);

    // ───────────── Installing ─────────────

    /// <summary>
    /// Downloads, verifies and installs a release into <paramref name="installDir"/>. Returns the path of the new exe.
    /// </summary>
    public static async Task<string> InstallAsync(EmulatorAdapter e, UpdateInfo info, string installDir, IProgress<string>? progress = null)
    {
        if (IsRunning(e)) throw new InvalidOperationException($"{e.Name} is running. Close it and try again.");

        var work = Path.Combine(Path.GetTempPath(), "GameOp-update", $"{e.Id}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(work);
        try
        {
            // Download
            var archive = Path.Combine(work, info.AssetName);
            progress?.Report($"Downloading {info.AssetName} ({info.Size / 1048576.0:0.0} MB)…");
            using (var http = Http())
            await using (var src = await http.GetStreamAsync(info.Url))
            await using (var dst = File.Create(archive))
                await src.CopyToAsync(dst);

            // Verify
            if (info.Sha256 is not null)
            {
                progress?.Report("Verifying checksum…");
                await using var fs = File.OpenRead(archive);
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(fs));
                if (!hash.Equals(info.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Checksum mismatch – the download was corrupted or tampered with. Nothing was changed.");
            }

            // Extract
            progress?.Report("Extracting…");
            var extracted = Path.Combine(work, "x");
            Directory.CreateDirectory(extracted);
            using (var a = ArchiveFactory.OpenArchive(archive))
                a.WriteToDirectory(extracted, new ExtractionOptions { ExtractFullPath = true, Overwrite = true });

            // Package root = the folder containing the emulator's exe.
            var exe = e.ExeNames
                .Select(n => Directory.EnumerateFiles(extracted, n, SearchOption.AllDirectories).FirstOrDefault())
                .FirstOrDefault(f => f is not null)
                ?? throw new InvalidDataException($"The release doesn't contain {string.Join(" / ", e.ExeNames)}.");
            var packageRoot = Path.GetDirectoryName(exe)!;

            // Copy: add/replace program files only. Never delete, never touch protected files.
            progress?.Report("Installing…");
            var backup = Path.Combine(AppPaths.BackupDir, "updates", e.Id, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            int copied = 0, kept = 0;
            Directory.CreateDirectory(installDir);
            foreach (var file in Directory.EnumerateFiles(packageRoot, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(packageRoot, file);
                var target = Path.Combine(installDir, rel);
                if (File.Exists(target))
                {
                    if (IsProtected(rel)) { kept++; continue; }
                    var b = Path.Combine(backup, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(b)!);
                    File.Copy(target, b, overwrite: true);
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
                copied++;
            }
            if (Directory.Exists(backup)) File.WriteAllText(Path.Combine(backup, "gameop-update.txt"), $"{installDir}\n{info.Version}\n");

            Log.Ok($"{e.Name} updated to {info.Version}: {copied} files installed, {kept} of your files left untouched");
            return Path.Combine(installDir, Path.GetFileName(exe));
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { /* temp cleanup is best-effort */ }
        }
    }

    /// <summary>Puts back the program files replaced by the most recent update.</summary>
    public static string Rollback(EmulatorAdapter e)
    {
        var dir = Path.Combine(AppPaths.BackupDir, "updates", e.Id);
        var last = Directory.Exists(dir)
            ? Directory.GetDirectories(dir).Where(d => !d.EndsWith("-rolled-back")).OrderDescending().FirstOrDefault()
            : null;
        if (last is null || !File.Exists(Path.Combine(last, "gameop-update.txt"))) return "No update to roll back.";
        if (IsRunning(e)) return $"{e.Name} is running. Close it first.";
        var installDir = File.ReadAllLines(Path.Combine(last, "gameop-update.txt"))[0];
        var n = 0;
        foreach (var f in Directory.EnumerateFiles(last, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(last, f);
            if (rel == "gameop-update.txt") continue;
            var target = Path.Combine(installDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(f, target, overwrite: true);
            n++;
        }
        Directory.Move(last, last + "-rolled-back");
        return $"Restored {n} files from before the update.";
    }

    private static HttpClient Http()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("GameOp/1.0 (+https://github.com/nahalewski/GameOp)");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }
}
