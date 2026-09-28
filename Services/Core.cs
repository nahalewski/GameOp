using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;

namespace GameOp.Services;

public static class AppPaths
{
    /// <summary>Folder of the running GameOp.exe (not the single-file extraction folder).</summary>
    public static string ExeDir { get; } = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
    /// <summary>Optional Data\ override folder; otherwise the copies embedded in the exe are used.</summary>
    public static string BundledData => Path.Combine(ExeDir, "Data");

    /// <summary>
    /// Portable mode – the exe's name contains "Portable" or a portable.txt sits next to it – keeps settings,
    /// backups and logs in GameOp-Data next to the exe instead of %LOCALAPPDATA%\GameOp.
    /// </summary>
    public static bool IsPortable { get; } =
        (Path.GetFileName(Environment.ProcessPath) ?? "").Contains("portable", StringComparison.OrdinalIgnoreCase) ||
        File.Exists(Path.Combine(ExeDir, "portable.txt"));

    public static string UserDir { get; } = IsPortable
        ? Path.Combine(ExeDir, "GameOp-Data")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameOp");
    public static string SettingsFile => Path.Combine(UserDir, "settings.json");
    public static string UserGameDb => Path.Combine(UserDir, "gamedb.json");
    public static string BackupDir => Path.Combine(UserDir, "backups");
    public static string TweakBackupFile => Path.Combine(UserDir, "tweaks-backup.json");

    public static string Documents => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    public static string RoamingAppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    public static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static T? Load<T>(string path) =>
        File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) : default;

    /// <summary>Loads a bundled data file: Data\&lt;name&gt; next to the exe if present, else the copy embedded in the exe.</summary>
    public static T? LoadBundled<T>(string name)
    {
        var file = Path.Combine(AppPaths.BundledData, name);
        if (File.Exists(file)) return Load<T>(file);
        using var s = typeof(Json).Assembly.GetManifestResourceStream("GameOp.Data." + name);
        return s is null ? default : JsonSerializer.Deserialize<T>(s, Options);
    }

    public static void Save<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(value, Options));
    }

    /// <summary>Recursively merges <paramref name="source"/> into <paramref name="target"/> (source wins).</summary>
    public static void DeepMerge(JsonObject target, JsonObject? source)
    {
        if (source is null) return;
        foreach (var (key, value) in source)
        {
            if (key.StartsWith('_')) continue; // comments
            if (value is JsonObject srcObj && target[key] is JsonObject dstObj)
                DeepMerge(dstObj, srcObj);
            else if (value is JsonArray srcArr && target[key] is JsonArray dstArr)
            {
                // Lists (e.g. PCSX2 patch names) are unioned rather than replaced.
                foreach (var item in srcArr)
                    if (!dstArr.Any(d => d?.ToJsonString() == item?.ToJsonString())) dstArr.Add(item?.DeepClone());
            }
            else
                target[key] = value?.DeepClone();
        }
    }

    /// <summary>Flattens a settings tree to "A / B / key = value" lines for display.</summary>
    public static IEnumerable<(string Path, string Value)> Flatten(JsonObject obj, string prefix = "")
    {
        foreach (var (key, value) in obj)
        {
            var path = prefix.Length == 0 ? key : $"{prefix} › {key}";
            if (value is JsonObject child)
                foreach (var item in Flatten(child, path)) yield return item;
            else
                yield return (path, value?.ToJsonString().Trim('"') ?? "");
        }
    }
}

/// <summary>Game ids end up in file names, so ids from downloaded or typed data must be plain tokens.</summary>
public static partial class SafeId
{
    [System.Text.RegularExpressions.GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_-]{1,31}$")]
    private static partial System.Text.RegularExpressions.Regex Pattern();

    public static bool IsValid(string? id) => id is not null && Pattern().IsMatch(id);
}

public static class Log
{
    public static ObservableCollection<string> Entries { get; } = [];

    public static void Info(string message) => Add("•", message);
    public static void Ok(string message) => Add("✓", message);
    public static void Warn(string message) => Add("!", message);

    private static void Add(string glyph, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss}  {glyph}  {message}";
        void Insert() { Entries.Insert(0, line); while (Entries.Count > 500) Entries.RemoveAt(Entries.Count - 1); }
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) Insert(); else dispatcher.BeginInvoke(Insert);
        try
        {
            Directory.CreateDirectory(AppPaths.UserDir);
            File.AppendAllText(Path.Combine(AppPaths.UserDir, "gameop.log"), $"{DateTime.Now:yyyy-MM-dd} {line}{Environment.NewLine}");
        }
        catch { /* logging must never throw */ }
    }
}

public static class Backup
{
    private const string OriginalSuffix = ".gameop-original";
    private static string CreatedListFile => Path.Combine(AppPaths.UserDir, "created-files.json");
    private static readonly object Gate = new();

    private static string OriginalsListFile => Path.Combine(AppPaths.UserDir, "original-files.json");

    private static HashSet<string> LoadList(string file) =>
        new(Json.Load<List<string>>(file) ?? [], StringComparer.OrdinalIgnoreCase);

    private static HashSet<string> LoadCreated() => LoadList(CreatedListFile);

    /// <summary>
    /// Call before writing any emulator config. The first time GameOp touches an existing file it keeps a
    /// pristine copy (file.gameop-original); files GameOp creates from scratch are recorded so a restore can
    /// delete them. Every write also gets a timestamped copy under %LOCALAPPDATA%\GameOp\backups.
    /// </summary>
    public static void BeforeWrite(string file)
    {
        lock (Gate)
        {
            var created = LoadCreated();
            if (!File.Exists(file))
            {
                if (created.Add(Path.GetFullPath(file))) Json.Save(CreatedListFile, created.ToList());
                return;
            }
            if (!created.Contains(Path.GetFullPath(file)) && !File.Exists(file + OriginalSuffix))
            {
                File.Copy(file, file + OriginalSuffix);
                var originals = LoadList(OriginalsListFile);
                if (originals.Add(Path.GetFullPath(file))) Json.Save(OriginalsListFile, originals.ToList());
            }

            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmm");
            var safeName = file.Replace(':', '_').Replace('\\', '_').Replace('/', '_');
            var dest = Path.Combine(AppPaths.BackupDir, stamp, safeName);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            if (!File.Exists(dest)) File.Copy(file, dest);
        }
    }

    /// <summary>Puts back every original under <paramref name="root"/> and deletes files GameOp created there.</summary>
    public static int RestoreOriginals(string root)
    {
        lock (Gate)
        {
            var count = 0;
            if (!Directory.Exists(root)) return 0;
            var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;

            // Only files GameOp itself backed up (never a scan of the folder, which may be e.g. the Desktop).
            var originals = LoadList(OriginalsListFile);
            foreach (var file in originals.Where(f => f.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                var orig = file + OriginalSuffix;
                if (File.Exists(orig))
                {
                    File.Copy(orig, file, overwrite: true);
                    File.Delete(orig);
                    count++;
                }
                originals.Remove(file);
            }
            Json.Save(OriginalsListFile, originals.ToList());

            var created = LoadCreated();
            var keep = Path.Combine(AppPaths.BackupDir, "removed-by-restore", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            foreach (var file in created.Where(f => f.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                if (File.Exists(file))
                {
                    // Keep a copy: the user may have edited this profile in the emulator since GameOp created it.
                    var dest = Path.Combine(keep, Path.GetRelativePath(fullRoot, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.Move(file, dest, overwrite: true);
                    count++;
                }
                created.Remove(file);
            }
            Json.Save(CreatedListFile, created.ToList());
            return count;
        }
    }
}
