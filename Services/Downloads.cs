using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using SharpCompress.Archives;
using SharpCompress.Common;

namespace GameOp.Services;

/// <summary>Shared download / verify / extract helpers for emulator updates, add-ons and texture packs.</summary>
public static class Downloads
{
    public static HttpClient Http(bool github = true)
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("GameOp/1.0 (+https://github.com/nahalewski/GameOp)");
        if (github) http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    /// <summary>A scratch folder that deletes itself when disposed.</summary>
    public sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "GameOp-dl", Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, true); } catch { /* best effort */ } }
    }

    public static async Task DownloadAsync(string url, string dest, long expectedSize = 0, string? sha256 = null,
        IProgress<string>? progress = null, long maxBytes = 20L << 30)
    {
        using var http = Http(github: false);
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        var type = resp.Content.Headers.ContentType?.MediaType ?? "";
        if (type.StartsWith("text/") || type.Contains("html"))
            throw new InvalidDataException("The link returned a web page instead of a file. The host may require a browser download.");
        var contentLength = resp.Content.Headers.ContentLength;
        var total = contentLength ?? expectedSize;
        if (total > maxBytes) throw new InvalidDataException($"The download is {total / 1048576} MB, over the {maxBytes / 1048576} MB limit.");

        await using (var src = await resp.Content.ReadAsStreamAsync())
        await using (var dst = File.Create(dest))
        {
            var buf = new byte[1 << 20];
            long done = 0, lastReport = 0;
            int n;
            while ((n = await src.ReadAsync(buf)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n));
                done += n;
                if (done > maxBytes) throw new InvalidDataException($"The download passed the {maxBytes / 1048576} MB limit and was stopped.");
                if (done - lastReport > 8 << 20)
                {
                    lastReport = done;
                    progress?.Report(total > 0 ? $"Downloading… {done * 100 / total}% of {total / 1048576.0:0} MB" : $"Downloading… {done / 1048576.0:0} MB");
                }
            }
            if (contentLength is { } len && done != len)
                throw new IOException($"The download was cut off ({done / 1048576} of {len / 1048576} MB). Try again.");
        }

        if (sha256 is not null)
        {
            progress?.Report("Verifying checksum…");
            await using var fs = File.OpenRead(dest);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(fs));
            if (!hash.Equals(sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Checksum mismatch: the download was corrupted or tampered with. Nothing was changed.");
        }
    }

    /// <summary>Extracts zip / 7z / rar / tar archives. SharpCompress ≥ 0.48 blocks path traversal.</summary>
    public static void Extract(string archive, string destDir)
    {
        Directory.CreateDirectory(destDir);
        using var a = ArchiveFactory.OpenArchive(archive);
        a.WriteToDirectory(destDir, new ExtractionOptions { ExtractFullPath = true, Overwrite = true });
    }

    /// <summary>Skips wrapper folders: while a folder holds exactly one sub-folder and no files, go inside it.</summary>
    public static string UnwrapSingleFolder(string dir)
    {
        while (Directory.GetFiles(dir).Length == 0 && Directory.GetDirectories(dir) is [var only]) dir = only;
        return dir;
    }

    /// <summary>Copies a folder tree over a target. Never deletes anything already in the target.</summary>
    public static int CopyTree(string from, string to, Func<string, bool>? skip = null)
    {
        var n = 0;
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var rel = System.IO.Path.GetRelativePath(from, file);
            if (skip?.Invoke(rel) == true) continue;
            var target = System.IO.Path.Combine(to, rel);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
            n++;
        }
        return n;
    }
}
