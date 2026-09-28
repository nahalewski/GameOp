using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace GameOp.Services;

/// <summary>
/// Reads a PS2 DVD .iso (ISO9660, 2048-byte sectors) and returns the serial and the ELF CRC that PCSX2
/// uses to name per-game settings files: gamesettings\SLUS-20312_ABCD1234.ini.
/// </summary>
public static partial class Ps2Iso
{
    private const int Sector = 2048;

    public static (string Serial, uint Crc)? Read(string isoPath)
    {
        try
        {
            using var fs = new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
            var pvd = ReadSectors(fs, 16, 1);
            if (Encoding.ASCII.GetString(pvd, 1, 5) != "CD001") return null;

            var root = DirRecord.Parse(pvd, 156);
            var cnf = FindFile(fs, root, "SYSTEM.CNF");
            if (cnf is null) return null;
            var cnfText = Encoding.ASCII.GetString(ReadExtent(fs, cnf.Value));

            var m = BootRegex().Match(cnfText);
            if (!m.Success) return null;
            var bootPath = m.Groups[1].Value.Trim().Replace('/', '\\').TrimStart('\\');

            // Walk sub-directories if the boot ELF isn't in the root.
            var dir = root;
            var parts = bootPath.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < parts.Length - 1; i++)
            {
                var sub = FindFile(fs, dir, parts[i]);
                if (sub is null) return null;
                dir = sub.Value;
            }
            var elfName = parts[^1];
            var elf = FindFile(fs, dir, elfName.Split(';')[0]);
            if (elf is null) return null;

            var data = ReadExtent(fs, elf.Value);
            uint crc = 0;
            for (var i = 0; i + 4 <= data.Length; i += 4) crc ^= BitConverter.ToUInt32(data, i);

            return (ToSerial(elfName), crc);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>"SLUS_203.12;1" → "SLUS-20312"</summary>
    public static string ToSerial(string elfName)
    {
        var name = elfName.Split(';')[0].Replace(".", "");
        var us = name.IndexOf('_');
        return us > 0 ? $"{name[..us]}-{name[(us + 1)..]}" : name;
    }

    private readonly record struct DirRecord(uint Lba, uint Size, bool IsDir, string Name)
    {
        public static DirRecord Parse(byte[] buf, int off)
        {
            var lba = BitConverter.ToUInt32(buf, off + 2);
            var size = BitConverter.ToUInt32(buf, off + 10);
            var flags = buf[off + 25];
            var nameLen = buf[off + 32];
            var name = Encoding.ASCII.GetString(buf, off + 33, nameLen);
            return new DirRecord(lba, size, (flags & 2) != 0, name);
        }
    }

    private static DirRecord? FindFile(FileStream fs, DirRecord dir, string name)
    {
        var data = ReadExtent(fs, dir);
        var off = 0;
        while (off < data.Length)
        {
            var len = data[off];
            if (len == 0)
            {
                // Records never span sectors; skip to the next one.
                off = (off / Sector + 1) * Sector;
                continue;
            }
            if (off + 33 > data.Length) break;
            var rec = DirRecord.Parse(data, off);
            var recName = rec.Name.Split(';')[0];
            if (string.Equals(recName, name, StringComparison.OrdinalIgnoreCase)) return rec;
            off += len;
        }
        return null;
    }

    private static byte[] ReadExtent(FileStream fs, DirRecord rec)
    {
        var size = (int)Math.Min(rec.Size, 64 * 1024 * 1024);
        var buf = new byte[size];
        fs.Seek((long)rec.Lba * Sector, SeekOrigin.Begin);
        fs.ReadExactly(buf, 0, size);
        return buf;
    }

    private static byte[] ReadSectors(FileStream fs, long lba, int count)
    {
        var buf = new byte[count * Sector];
        fs.Seek(lba * Sector, SeekOrigin.Begin);
        fs.ReadExactly(buf, 0, buf.Length);
        return buf;
    }

    [GeneratedRegex(@"BOOT2\s*=\s*cdrom0:\\?([^\r\n]+)", RegexOptions.IgnoreCase)]
    private static partial Regex BootRegex();
}
