using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace GameOp.Services;

/// <summary>An on/off enhancement written into an emulator's config (widescreen patches, sharpening, HD texture loading…).</summary>
public sealed class Enhancement
{
    public string Id { get; set; } = "";
    /// <summary>Settings vocabulary key (emulators.json id), e.g. "pcsx2".</summary>
    public string Emulator { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Category { get; set; } = "Graphics";
    public JsonObject On { get; set; } = [];
    public JsonObject? Off { get; set; }
    /// <summary>Used instead of <see cref="On"/> in per-game profiles when the emulator needs a different form there.</summary>
    public JsonObject? PerGame { get; set; }
    /// <summary>Written into per-game profiles when the user has switched the enhancement off.</summary>
    public JsonObject? PerGameOff { get; set; }
    /// <summary>Turned on automatically the first time GameOp sees the emulator.</summary>
    public bool Default { get; set; }
    /// <summary>Costs noticeable GPU time; skipped on the Battery Saver level.</summary>
    public bool Heavy { get; set; }
}

/// <summary>An official downloadable add-on kept up to date (Cemu graphic packs, Xenia patches, RPCS3 patches…).</summary>
public sealed class Addon
{
    public string Id { get; set; } = "";
    /// <summary>Adapter id, e.g. "cemu".</summary>
    public string Emulator { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>"github-release" | "github-repo" | "url"</summary>
    public string Kind { get; set; } = "github-release";
    public string? Repo { get; set; }
    public string? Asset { get; set; }
    public string? Branch { get; set; }
    public string? Url { get; set; }
    /// <summary>Folder inside the archive to copy (after unwrapping a single top folder).</summary>
    public string? SubDir { get; set; }
    /// <summary>Destination relative to the emulator's config root. For a single file, the file path.</summary>
    public string InstallTo { get; set; } = "";
    public bool IsArchive { get; set; } = true;
    /// <summary>Enhancement to switch on after installing (e.g. the setting that makes the emulator load patches).</summary>
    public string? Enables { get; set; }
    public bool Default { get; set; } = true;
    /// <summary>Xenia patch packs: keep the user's "is_enabled = true" choices when files are replaced.</summary>
    public bool PreserveEnabledPatches { get; set; }
}

public sealed class TexturePack
{
    public string Id { get; set; } = "";
    public string Emulator { get; set; } = "";
    public string Title { get; set; } = "";
    public List<string> GameIds { get; set; } = [];
    public string Name { get; set; } = "";
    public string? Author { get; set; }
    public string? Version { get; set; }
    public string Url { get; set; } = "";
    public long SizeBytes { get; set; }
    public string? Format { get; set; }
    public string? RootHint { get; set; }
    public string? SourcePage { get; set; }
    public string? License { get; set; }
    public string? Notes { get; set; }

    [JsonIgnore] public string SizeText => SizeBytes <= 0 ? "?" : SizeBytes >= 1L << 30 ? $"{SizeBytes / (double)(1L << 30):0.0} GB" : $"{SizeBytes / 1048576.0:0} MB";
}

public sealed class ExtrasCatalog
{
    public List<Enhancement> Enhancements { get; set; } = [];
    public List<Addon> Addons { get; set; } = [];
    public List<TexturePack> Packs { get; set; } = [];

    private sealed class PackFile { public List<TexturePack> Packs { get; set; } = []; }

    public static string UserPackFile => Path.Combine(AppPaths.UserDir, "texturepacks.json");

    public static ExtrasCatalog Load()
    {
        var cat = Json.LoadBundled<ExtrasCatalog>("extras.json") ?? new ExtrasCatalog();
        var bundledPacks = Json.LoadBundled<PackFile>("texturepacks.json")?.Packs ?? [];
        var userPacks = Json.Load<PackFile>(UserPackFile)?.Packs ?? [];
        cat.Packs = bundledPacks.Where(p => userPacks.All(u => u.Id != p.Id)).Concat(userPacks)
            .Where(IsSafe).OrderBy(p => p.Emulator).ThenBy(p => p.Title).ToList();
        return cat;
    }

    private static readonly HashSet<string> Formats = new(StringComparer.OrdinalIgnoreCase) { "zip", "7z", "rar", "tar" };

    /// <summary>Pack fields become download targets and folder names (and GameOp runs elevated), so they must be tame.</summary>
    public static bool IsSafe(TexturePack p) =>
        Uri.TryCreate(p.Url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps &&
        p.GameIds.Count > 0 && p.GameIds.All(SafeId.IsValid) &&
        (p.Format is null || Formats.Contains(p.Format)) &&
        (string.IsNullOrEmpty(p.RootHint) || (!Path.IsPathRooted(p.RootHint) && !p.RootHint.Split('/', '\\').Contains("..")));

    /// <summary>Imports a texture-pack catalog (same shape as Data\texturepacks.json) from a file or URL.</summary>
    public static async Task<int> ImportPacksAsync(string pathOrUrl)
    {
        string text;
        if (Uri.TryCreate(pathOrUrl, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps)
        {
            using var http = Downloads.Http(github: false);
            text = await http.GetStringAsync(u);
        }
        else text = await File.ReadAllTextAsync(pathOrUrl);

        var incoming = JsonSerializer.Deserialize<PackFile>(text, Json.Options)?.Packs ?? [];
        if (incoming.Count == 0 || incoming.Any(p => string.IsNullOrEmpty(p.Id) || !IsSafe(p)))
            throw new InvalidDataException("Every pack needs an id, an https url and plain game ids (letters, digits, '-' or '_').");
        var existing = Json.Load<PackFile>(UserPackFile)?.Packs ?? [];
        existing.RemoveAll(e => incoming.Any(i => i.Id == e.Id));
        existing.AddRange(incoming);
        Json.Save(UserPackFile, new PackFile { Packs = existing });
        return incoming.Count;
    }
}

/// <summary>Reads / toggles "is_enabled" in Xenia Canary .patch.toml files ([[patch]] tables with a name).</summary>
public static class XeniaPatches
{
    private static readonly Regex NameLine = new(@"^\s*name\s*=\s*""(.*)""\s*$");
    private static readonly Regex EnabledLine = new(@"^(\s*is_enabled\s*=\s*)(true|false)(.*)$");

    public static IEnumerable<(string Name, bool Enabled, int Line)> Patches(IReadOnlyList<string> lines)
    {
        string? name = null;
        for (var i = 0; i < lines.Count; i++)
        {
            var t = lines[i].Trim();
            if (t == "[[patch]]") { name = null; continue; }
            if (t.StartsWith("[[patch.")) { name = null; continue; } // entries belong to the patch above
            if (name is null && NameLine.Match(lines[i]) is { Success: true } n) { name = n.Groups[1].Value; continue; }
            if (name is not null && EnabledLine.Match(lines[i]) is { Success: true } e)
            {
                yield return (name, e.Groups[2].Value == "true", i);
                name = null;
            }
        }
    }

    public static HashSet<string> EnabledNames(IReadOnlyList<string> lines) =>
        Patches(lines).Where(p => p.Enabled).Select(p => p.Name).ToHashSet();

    public static string[] SetEnabled(IReadOnlyList<string> lines, IReadOnlySet<string> names, bool enabled)
    {
        var copy = lines.ToArray();
        foreach (var p in Patches(lines).Where(p => names.Contains(p.Name)).ToList())
            copy[p.Line] = EnabledLine.Replace(copy[p.Line], m => $"{m.Groups[1].Value}{(enabled ? "true" : "false")}{m.Groups[3].Value}");
        return copy;
    }
}

public static class ExtrasService
{
    // ───────────── Add-ons ─────────────

    public sealed record AddonRelease(string Key, string Version, string Url, long Size, string? Sha256);

    public static async Task<AddonRelease> CheckAddonAsync(Addon a)
    {
        using var http = Downloads.Http();
        switch (a.Kind)
        {
            case "github-release":
            {
                var rel = JsonNode.Parse(await http.GetStringAsync($"https://api.github.com/repos/{a.Repo}/releases/latest"))!;
                var rx = new Regex(a.Asset ?? ".*", RegexOptions.IgnoreCase);
                var asset = (rel["assets"] as JsonArray)!.OfType<JsonObject>().FirstOrDefault(x => rx.IsMatch(x["name"]!.GetValue<string>()))
                            ?? throw new InvalidDataException($"No matching file in the latest {a.Repo} release.");
                var digest = asset["digest"]?.GetValue<string>();
                var tag = rel["tag_name"]!.GetValue<string>();
                return new($"{tag}|{asset["updated_at"]}", tag, asset["browser_download_url"]!.GetValue<string>(),
                    asset["size"]?.GetValue<long>() ?? 0, digest?.StartsWith("sha256:") == true ? digest[7..] : null);
            }
            case "github-repo":
            {
                var branch = a.Branch;
                if (branch is null)
                {
                    var repo = JsonNode.Parse(await http.GetStringAsync($"https://api.github.com/repos/{a.Repo}"))!;
                    branch = repo["default_branch"]!.GetValue<string>();
                }
                var commit = JsonNode.Parse(await http.GetStringAsync($"https://api.github.com/repos/{a.Repo}/commits/{branch}"))!;
                var sha = commit["sha"]!.GetValue<string>();
                var date = commit["commit"]?["committer"]?["date"]?.GetValue<DateTime>() ?? DateTime.UtcNow;
                return new(sha, $"{date:yyyy-MM-dd} ({sha[..7]})", $"https://codeload.github.com/{a.Repo}/zip/{sha}", 0, null);
            }
            case "rpcs3-patches":
            {
                // Official RPCS3 patch API: {"return_code":0,"version":"1.2","sha256":"…","patch":"<patch.yml text>"}
                using var plain = Downloads.Http(github: false);
                var json = JsonNode.Parse(await plain.GetStringAsync(a.Url))!;
                var sha = json["sha256"]!.GetValue<string>();
                return new(sha, $"patch.yml v{json["version"]} ({sha[..8]})", a.Url!, 0, null);
            }
            default: // plain URL: version = Last-Modified / ETag
            {
                using var plain = Downloads.Http(github: false);
                using var req = new HttpRequestMessage(HttpMethod.Head, a.Url);
                using var resp = await plain.SendAsync(req);
                var key = resp.Headers.ETag?.Tag ?? resp.Content.Headers.LastModified?.ToString("O") ?? DateTime.UtcNow.ToString("yyyy-MM-dd");
                var ver = resp.Content.Headers.LastModified?.ToString("yyyy-MM-dd") ?? "latest";
                return new(key, ver, a.Url!, resp.Content.Headers.ContentLength ?? 0, null);
            }
        }
    }

    public static async Task<string> InstallAddonAsync(Addon a, AddonRelease rel, EmulatorAdapter e, IProgress<string>? progress = null)
    {
        if (e.ConfigRoot is null) throw new InvalidOperationException($"{e.Name} isn't set up yet – run it once first.");
        if (a.Kind == "rpcs3-patches")
        {
            using var plain = Downloads.Http(github: false);
            var json = JsonNode.Parse(await plain.GetStringAsync(a.Url))!;
            var text = json["patch"]!.GetValue<string>();
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
            if (!hash.Equals(json["sha256"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The RPCS3 patch file failed its checksum.");
            var target = Path.Combine(e.ConfigRoot, a.InstallTo);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (File.Exists(target)) Backup.BeforeWrite(target);
            await File.WriteAllTextAsync(target, text);
            return $"{a.Name} {rel.Version} installed to {target}.";
        }

        using var tmp = new Downloads.TempDir();
        var file = Path.Combine(tmp.Path, a.IsArchive ? "addon.zip" : Path.GetFileName(a.InstallTo));
        progress?.Report($"Downloading {a.Name}…");
        await Downloads.DownloadAsync(rel.Url, file, rel.Size, rel.Sha256, progress);

        var dest = Path.Combine(e.ConfigRoot, a.InstallTo);
        if (!a.IsArchive)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            if (File.Exists(dest)) Backup.BeforeWrite(dest);
            File.Copy(file, dest, overwrite: true);
            return $"{a.Name} {rel.Version} installed.";
        }

        progress?.Report("Extracting…");
        var x = Path.Combine(tmp.Path, "x");
        Downloads.Extract(file, x);
        var root = Downloads.UnwrapSingleFolder(x);
        if (!string.IsNullOrEmpty(a.SubDir) && Directory.Exists(Path.Combine(root, a.SubDir)))
            root = Path.Combine(root, a.SubDir);

        // Remember every patch's on/off state, and keep a copy of the old files (they may contain the user's own patches).
        Dictionary<string, List<(string Name, bool Enabled)>>? states = null;
        if (a.PreserveEnabledPatches && Directory.Exists(dest))
        {
            var keep = Path.Combine(AppPaths.BackupDir, "addons", a.Id, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            states = new(StringComparer.OrdinalIgnoreCase);
            foreach (var f in Directory.EnumerateFiles(dest, "*.patch.toml"))
            {
                var lines = File.ReadAllLines(f);
                states[Path.GetFileName(f)] = XeniaPatches.Patches(lines).Select(p => (p.Name, p.Enabled)).ToList();
                Directory.CreateDirectory(keep);
                File.Copy(f, Path.Combine(keep, Path.GetFileName(f)), overwrite: true);
            }
        }

        var n = Downloads.CopyTree(root, dest);

        var kept = 0;
        if (states is not null)
            foreach (var (patchFile, list) in states)
            {
                var path = Path.Combine(dest, patchFile);
                if (!File.Exists(path) || list.Count == 0) continue;
                var lines = File.ReadAllLines(path);
                lines = XeniaPatches.SetEnabled(lines, list.Where(p => p.Enabled).Select(p => p.Name).ToHashSet(), true);
                lines = XeniaPatches.SetEnabled(lines, list.Where(p => !p.Enabled).Select(p => p.Name).ToHashSet(), false);
                File.WriteAllLines(path, lines);
                kept += list.Count(p => p.Enabled);
            }
        return $"{a.Name} {rel.Version}: {n} files installed to {dest}." + (kept > 0 ? $" Kept {kept} patches you had turned on." : "");
    }

    // ───────────── Texture packs ─────────────

    /// <summary>Picks the pack's content folder: RootHint, else a folder named after the game id, else the unwrapped root.</summary>
    public static string FindPackRoot(string extracted, TexturePack? pack, string gameId)
    {
        if (!string.IsNullOrEmpty(pack?.RootHint))
        {
            var hinted = Path.Combine(extracted, pack.RootHint);
            if (Directory.Exists(hinted)) return hinted;
            var nested = Path.Combine(Downloads.UnwrapSingleFolder(extracted), pack.RootHint);
            if (Directory.Exists(nested)) return nested;
        }
        var byId = Directory.EnumerateDirectories(extracted, "*", SearchOption.AllDirectories)
            .Where(d => Path.GetFileName(d).Equals(gameId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(d => d.Length).FirstOrDefault();
        var root = byId ?? Downloads.UnwrapSingleFolder(extracted);
        // PCSX2 / DuckStation packs often ship "<SERIAL>\replacements\…" (maybe next to a readme or dumps\);
        // their target folder already ends in \replacements, so use the inner folder whenever it exists.
        var replacements = Path.Combine(root, "replacements");
        return Directory.Exists(replacements) ? replacements : root;
    }

    public static async Task<string> InstallPackAsync(TexturePack pack, EmulatorAdapter e, string gameId, IProgress<string>? progress = null)
    {
        var target = e.TextureDir(gameId) ?? throw new InvalidOperationException($"{e.Name} doesn't load texture packs.");
        using var tmp = new Downloads.TempDir();
        var file = Path.Combine(tmp.Path, "pack." + (pack.Format ?? "zip"));
        progress?.Report($"Downloading {pack.Name} ({pack.SizeText})…");
        await Downloads.DownloadAsync(pack.Url, file, pack.SizeBytes, null, progress);
        progress?.Report("Extracting…");
        var x = Path.Combine(tmp.Path, "x");
        Downloads.Extract(file, x);
        var n = Downloads.CopyTree(FindPackRoot(x, pack, gameId), target);
        return $"{pack.Name}: {n} textures installed to {target}.";
    }

    /// <summary>Installs a pack the user downloaded themselves (archive or folder) — for packs on Google Drive, Mega, etc.</summary>
    public static Task<string> InstallPackFromFileAsync(string path, EmulatorAdapter e, string gameId) => Task.Run(() =>
    {
        var target = e.TextureDir(gameId) ?? throw new InvalidOperationException($"{e.Name} doesn't load texture packs.");
        using var tmp = new Downloads.TempDir();
        string source;
        if (Directory.Exists(path)) source = path;
        else
        {
            Downloads.Extract(path, tmp.Path);
            source = tmp.Path;
        }
        var n = Downloads.CopyTree(FindPackRoot(source, null, gameId), target);
        return $"{n} textures installed to {target}.";
    });
}
