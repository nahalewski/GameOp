using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace GameOp.Services;

/// <summary>Formats a JSON settings value into the text an emulator expects.</summary>
public delegate string ValueFormatter(JsonNode? value);

public static class Formatters
{
    public static string Lower(JsonNode? v) => Raw(v, "true", "false");
    public static string Title(JsonNode? v) => Raw(v, "True", "False");
    public static string Toml(JsonNode? v) =>
        v is JsonValue jv && jv.GetValueKind() == JsonValueKind.String ? $"\"{jv.GetValue<string>()}\"" : Lower(v);

    private static string Raw(JsonNode? v, string t, string f)
    {
        if (v is null) return "";
        return v.GetValueKind() switch
        {
            JsonValueKind.True => t,
            JsonValueKind.False => f,
            JsonValueKind.String => v.GetValue<string>(),
            _ => v.ToJsonString()
        };
    }
}

/// <summary>
/// Line-preserving INI editor. Keeps comments, ordering and unknown keys; only touches keys it sets.
/// Also used for TOML files that stick to [section] + key = value (Xenia).
/// </summary>
public sealed class IniFile
{
    private readonly List<string> _lines;
    private readonly string _path;
    public string Separator { get; set; } = " = ";

    public IniFile(string path)
    {
        _path = path;
        _lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : [];
        var sample = _lines.FirstOrDefault(l => l.Contains('=') && !l.TrimStart().StartsWith('[') && !IsComment(l));
        if (sample is not null) Separator = sample.Contains(" = ") ? " = " : "=";
    }

    private static bool IsComment(string l) { var t = l.TrimStart(); return t.StartsWith(';') || t.StartsWith('#'); }

    private static string? SectionName(string line)
    {
        var t = line.Trim();
        return t.StartsWith('[') && t.EndsWith(']') ? t[1..^1].Trim() : null;
    }

    private static string? KeyName(string line)
    {
        if (IsComment(line)) return null;
        var i = line.IndexOf('=');
        return i <= 0 ? null : line[..i].Trim();
    }

    private (int start, int end) FindSection(string section)
    {
        for (var i = 0; i < _lines.Count; i++)
        {
            if (!string.Equals(SectionName(_lines[i]), section, StringComparison.OrdinalIgnoreCase)) continue;
            var end = i + 1;
            while (end < _lines.Count && SectionName(_lines[end]) is null) end++;
            return (i, end);
        }
        return (-1, -1);
    }

    public string? Get(string section, string key) => GetAll(section, key).FirstOrDefault();

    public IEnumerable<string> GetAll(string section, string key)
    {
        var (start, end) = FindSection(section);
        if (start < 0) yield break;
        for (var i = start + 1; i < end; i++)
            if (string.Equals(KeyName(_lines[i]), key, StringComparison.OrdinalIgnoreCase))
                yield return _lines[i][(_lines[i].IndexOf('=') + 1)..].Trim();
    }

    public void Set(string section, string key, string value)
    {
        var (start, end) = FindSection(section);
        if (start < 0)
        {
            if (_lines.Count > 0 && _lines[^1].Trim().Length > 0) _lines.Add("");
            _lines.Add($"[{section}]");
            _lines.Add($"{key}{Separator}{value}");
            return;
        }
        for (var i = start + 1; i < end; i++)
        {
            if (!string.Equals(KeyName(_lines[i]), key, StringComparison.OrdinalIgnoreCase)) continue;
            _lines[i] = $"{_lines[i][.._lines[i].IndexOf('=')].TrimEnd()}{Separator}{value}";
            return;
        }
        // Insert after the last non-blank line of the section.
        var insertAt = end;
        while (insertAt - 1 > start && _lines[insertAt - 1].Trim().Length == 0) insertAt--;
        _lines.Insert(insertAt, $"{key}{Separator}{value}");
    }

    /// <summary>Applies {section: {key: value}} settings.</summary>
    public void Apply(JsonObject settings, ValueFormatter format)
    {
        foreach (var (section, keys) in settings)
        {
            if (keys is not JsonObject obj) continue;
            foreach (var (key, value) in obj) Set(section, key, format(value));
        }
    }

    public void Save()
    {
        Backup.BeforeWrite(_path);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllLines(_path, _lines, new UTF8Encoding(false));
    }
}

/// <summary>
/// Minimal YAML editor for RPCS3's config files (block mappings, 2-space indent, "Key: value").
/// Sets nested keys in place and appends missing ones under the right parent.
/// </summary>
public sealed class YamlFile
{
    private readonly List<string> _lines;
    private readonly string _path;

    public YamlFile(string path)
    {
        _path = path;
        _lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : [];
    }

    private static int Indent(string l) => l.Length - l.TrimStart(' ').Length;

    private static string? KeyOf(string line)
    {
        var t = line.TrimStart(' ');
        if (t.StartsWith('#') || t.StartsWith('-')) return null;
        var i = t.IndexOf(": ", StringComparison.Ordinal);
        if (i < 0 && t.EndsWith(':')) i = t.Length - 1;
        return i <= 0 ? null : t[..i];
    }

    public void Set(IReadOnlyList<string> path, string value)
    {
        int start = 0, end = _lines.Count, depth = 0;
        for (var p = 0; p < path.Count; p++)
        {
            var indent = depth * 2;
            var found = -1;
            for (var i = start; i < end; i++)
            {
                if (_lines[i].Trim().Length == 0 || Indent(_lines[i]) != indent) continue;
                if (KeyOf(_lines[i]) == path[p]) { found = i; break; }
            }
            var isLeaf = p == path.Count - 1;
            if (found < 0)
            {
                // Append the rest of the path at the end of the current block.
                var insertAt = end;
                while (insertAt > start && _lines[insertAt - 1].Trim().Length == 0) insertAt--;
                var newLines = new List<string>();
                for (var q = p; q < path.Count; q++)
                {
                    var pad = new string(' ', (depth + q - p) * 2);
                    newLines.Add(q == path.Count - 1 ? $"{pad}{path[q]}: {value}" : $"{pad}{path[q]}:");
                }
                _lines.InsertRange(insertAt, newLines);
                return;
            }
            if (isLeaf)
            {
                _lines[found] = $"{new string(' ', indent)}{path[p]}: {value}";
                return;
            }
            // Narrow to the child block.
            start = found + 1;
            var blockEnd = start;
            while (blockEnd < end && (_lines[blockEnd].Trim().Length == 0 || Indent(_lines[blockEnd]) > indent)) blockEnd++;
            end = blockEnd;
            depth++;
        }
    }

    public void Apply(JsonObject settings, List<string>? prefix = null)
    {
        prefix ??= [];
        foreach (var (key, value) in settings)
        {
            var path = new List<string>(prefix) { key };
            if (value is JsonObject child) Apply(child, path);
            else Set(path, FormatValue(value));
        }
    }

    private static string FormatValue(JsonNode? v)
    {
        var s = Formatters.Lower(v);
        return s.Length == 0 ? "\"\"" : s;
    }

    public void Save()
    {
        Backup.BeforeWrite(_path);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllLines(_path, _lines, new UTF8Encoding(false));
    }
}

/// <summary>Merges settings into a JSON config (Ryujinx Config.json).</summary>
public static class JsonConfigFile
{
    public static void Apply(string path, JsonObject settings)
    {
        var root = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? [] : [];
        Json.DeepMerge(root, settings);
        Backup.BeforeWrite(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}

/// <summary>Sets element values in an XML config (Cemu settings.xml, root &lt;content&gt;).</summary>
public static class XmlConfigFile
{
    public static void Apply(string path, JsonObject settings, string rootName = "content")
    {
        var doc = File.Exists(path) ? XDocument.Load(path) : new XDocument(new XElement(rootName));
        Apply(doc.Root!, settings);
        Backup.BeforeWrite(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        doc.Save(path);
    }

    private static void Apply(XElement parent, JsonObject settings)
    {
        foreach (var (key, value) in settings)
        {
            var el = parent.Element(key);
            if (el is null) { el = new XElement(key); parent.Add(el); }
            if (value is JsonObject child) Apply(el, child);
            else el.Value = Formatters.Lower(value);
        }
    }
}
