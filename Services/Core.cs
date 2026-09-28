using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;

namespace GameOp.Services;

public static class AppPaths
{
    public static string ExeDir => AppContext.BaseDirectory;
    public static string BundledData => Path.Combine(ExeDir, "Data");
    /// <summary>Portable builds (portable.txt next to the exe) keep everything in .\UserData.</summary>
    public static bool IsPortable { get; } = File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.txt"));
    public static string UserDir { get; } = IsPortable
        ? Path.Combine(AppContext.BaseDirectory, "UserData")
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

    private static HashSet<string> LoadCreated() =>
        new(Json.Load<List<string>>(CreatedListFile) ?? [], StringComparer.OrdinalIgnoreCase);

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
                File.Copy(file, file + OriginalSuffix);

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
            foreach (var orig in Directory.EnumerateFiles(root, "*" + OriginalSuffix, SearchOption.AllDirectories))
            {
                File.Copy(orig, orig[..^OriginalSuffix.Length], overwrite: true);
                File.Delete(orig);
                count++;
            }
            var created = LoadCreated();
            var fullRoot = Path.GetFullPath(root);
            foreach (var file in created.Where(f => f.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                if (File.Exists(file)) { File.Delete(file); count++; }
                created.Remove(file);
            }
            Json.Save(CreatedListFile, created.ToList());
            return count;
        }
    }
}
