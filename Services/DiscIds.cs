using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace GameOp.Services;

/// <summary>Reads game IDs straight from disc images so texture packs can be matched to the user's library.</summary>
public static partial class DiscIds
{
    /// <summary>GameCube / Wii: 6-char ID from .iso/.gcm (offset 0), .wbfs (0x200) or .rvz/.wia (disc header copy at 0x58).</summary>
    public static string? Dolphin(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var head = new byte[0x60];
            if (fs.Read(head, 0, head.Length) < head.Length) return null;
            var magic = Encoding.ASCII.GetString(head, 0, 4);
            int offset = magic switch
            {
                "RVZ\u0001" or "WIA\u0001" => 0x58,
                "WBFS" => 0x200,
                _ => 0
            };
            var id = new byte[6];
            fs.Seek(offset, SeekOrigin.Begin);
            if (fs.Read(id, 0, 6) < 6) return null;
            var s = Encoding.ASCII.GetString(id);
            return DolphinId().IsMatch(s) ? s : null;
        }
        catch { return null; }
    }

    /// <summary>PSP: reads UMD_DATA.BIN ("ULUS-10041|…") from the root of an ISO9660 UMD image.</summary>
    public static string? Psp(string isoPath)
    {
        var data = Iso9660.ReadRootFile(isoPath, "UMD_DATA.BIN");
        if (data is null) return null;
        var text = Encoding.ASCII.GetString(data, 0, Math.Min(data.Length, 64));
        var m = PspId().Match(text);
        return m.Success ? m.Groups[1].Value + m.Groups[2].Value : null;
    }

    [GeneratedRegex("^[A-Z0-9]{6}$")] private static partial Regex DolphinId();
    [GeneratedRegex(@"^([A-Z]{4})-(\d{5})")] private static partial Regex PspId();
}

/// <summary>Tiny ISO9660 reader (2048-byte sectors) for fetching a file from the root directory.</summary>
public static class Iso9660
{
    public static byte[]? ReadRootFile(string isoPath, string name)
    {
        try
        {
            using var fs = File.OpenRead(isoPath);
            var pvd = new byte[2048];
            fs.Seek(16 * 2048, SeekOrigin.Begin);
            fs.ReadExactly(pvd);
            if (Encoding.ASCII.GetString(pvd, 1, 5) != "CD001") return null;
            var rootLba = BitConverter.ToUInt32(pvd, 156 + 2);
            var rootSize = BitConverter.ToUInt32(pvd, 156 + 10);
            var dir = new byte[Math.Min(rootSize, 1 << 20)];
            fs.Seek(rootLba * 2048L, SeekOrigin.Begin);
            fs.ReadExactly(dir);
            for (var off = 0; off < dir.Length;)
            {
                var len = dir[off];
                if (len == 0) { off = (off / 2048 + 1) * 2048; continue; }
                var nameLen = dir[off + 32];
                var recName = Encoding.ASCII.GetString(dir, off + 33, nameLen).Split(';')[0];
                if (recName.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    var lba = BitConverter.ToUInt32(dir, off + 2);
                    var size = (int)Math.Min(BitConverter.ToUInt32(dir, off + 10), 1 << 20);
                    var buf = new byte[size];
                    fs.Seek(lba * 2048L, SeekOrigin.Begin);
                    fs.ReadExactly(buf);
                    return buf;
                }
                off += len;
            }
        }
        catch { /* not a readable ISO */ }
        return null;
    }
}
