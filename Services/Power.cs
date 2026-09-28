using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using GameOp.Models;
using Microsoft.Win32.SafeHandles;

namespace GameOp.Services;

public static class DeviceDetector
{
    public static (string Model, string Cpu) ReadHardware()
    {
        string model = "", board = "", cpu = "";
        try
        {
            using var cs = new ManagementObjectSearcher("SELECT Model FROM Win32_ComputerSystem");
            foreach (var o in cs.Get()) model = o["Model"]?.ToString() ?? "";
            using var bb = new ManagementObjectSearcher("SELECT Product FROM Win32_BaseBoard");
            foreach (var o in bb.Get()) board = o["Product"]?.ToString() ?? "";
            using var p = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor");
            foreach (var o in p.Get()) cpu = o["Name"]?.ToString()?.Trim() ?? "";
        }
        catch (Exception ex) { Log.Warn($"Hardware query failed: {ex.Message}"); }
        return ($"{model} {board}".Trim(), cpu);
    }

    /// <summary>Matches by ASUS model code (RC71L, RC72LA, RC73YA, RC73XA) then narrows by APU name.</summary>
    public static DeviceProfile? Match(IReadOnlyList<DeviceProfile> devices, string model, string cpu)
    {
        bool CpuOk(DeviceProfile d) =>
            cpu.Contains(d.CpuMatch, StringComparison.OrdinalIgnoreCase) &&
            (d.CpuExclude is null || !cpu.Contains(d.CpuExclude, StringComparison.OrdinalIgnoreCase));

        var byModel = devices.Where(d => d.ModelCodes.Any(c => model.Contains(c, StringComparison.OrdinalIgnoreCase))).ToList();
        return byModel.FirstOrDefault(CpuOk) ?? byModel.FirstOrDefault() ?? devices.FirstOrDefault(CpuOk);
    }
}

/// <summary>
/// ASUS ATKACPI driver interface (the same one Armoury Crate and G-Helper use) for performance mode and
/// APU power limits. Only used when the device is a detected ROG Ally.
/// </summary>
public sealed class AsusAcpi : IDisposable
{
    private const uint Devs = 0x53564544;                 // "DEVS" – set device
    private const uint IoctlAcpi = 0x0022240C;
    public const uint PerformanceMode = 0x00120075;       // 0 = Performance/Balanced, 1 = Turbo, 2 = Silent
    public const uint PptSpl = 0x001200A0;                // sustained (STAPM / PL1)
    public const uint PptSppt = 0x001200A3;               // slow boost
    public const uint PptFppt = 0x001200C1;               // fast boost

    private readonly SafeFileHandle _handle;

    private AsusAcpi(SafeFileHandle h) => _handle = h;

    public static AsusAcpi? TryOpen()
    {
        var h = CreateFile(@"\\.\ATKACPI", 0xC0000000, 3, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
        if (h.IsInvalid) { h.Dispose(); return null; }
        return new AsusAcpi(h);
    }

    public int DeviceSet(uint deviceId, int value)
    {
        var args = new byte[8];
        BitConverter.GetBytes(deviceId).CopyTo(args, 0);
        BitConverter.GetBytes((uint)value).CopyTo(args, 4);
        var buf = new byte[8 + args.Length];
        BitConverter.GetBytes(Devs).CopyTo(buf, 0);
        BitConverter.GetBytes((uint)args.Length).CopyTo(buf, 4);
        args.CopyTo(buf, 8);
        var outBuf = new byte[16];
        if (!DeviceIoControl(_handle, IoctlAcpi, buf, (uint)buf.Length, outBuf, (uint)outBuf.Length, out _, IntPtr.Zero))
            return -1;
        return BitConverter.ToInt32(outBuf, 0);
    }

    public void Dispose() => _handle.Dispose();

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[] inBuf, uint inSize, byte[] outBuf, uint outSize, out uint returned, IntPtr overlapped);
}

public static class PowerService
{
    public static readonly Guid OverlayEfficiency = new("961cc777-2547-4f9d-8174-7d86181b8a7a");
    public static readonly Guid OverlayBalanced = Guid.Empty;
    public static readonly Guid OverlayPerformance = new("ded574b5-45a0-4f42-8737-46345c09c238");

    public sealed record TdpPlan(int Spl, int Sppt, int Fppt, int AsusMode);

    public static TdpPlan PlanFor(DeviceProfile d, Tier tier, int? customWatts = null)
    {
        var spl = Math.Clamp(customWatts ?? d.Tdp.For(tier), d.Limits.MinSpl, d.Limits.MaxSpl);
        var sppt = Math.Min(spl + 5, d.Limits.MaxSppt);
        var fppt = Math.Min(spl + 10, d.Limits.MaxFppt);
        var mode = tier switch { Tier.Battery => 2, Tier.Balanced => 0, _ => 1 };
        return new TdpPlan(spl, Math.Max(sppt, spl), Math.Max(fppt, sppt), mode);
    }

    /// <summary>Sets APU power limits. Prefers the ASUS ACPI interface; falls back to RyzenAdj if configured.</summary>
    public static string ApplyTdp(DeviceProfile device, TdpPlan plan, string? ryzenAdjPath)
    {
        if (!device.IsAlly) return "Not a ROG Ally – TDP unchanged (preview mode).";

        using (var acpi = AsusAcpi.TryOpen())
        {
            if (acpi is not null)
            {
                acpi.DeviceSet(AsusAcpi.PerformanceMode, plan.AsusMode);
                var a = acpi.DeviceSet(AsusAcpi.PptSpl, plan.Spl);
                var b = acpi.DeviceSet(AsusAcpi.PptSppt, plan.Sppt);
                var c = acpi.DeviceSet(AsusAcpi.PptFppt, plan.Fppt);
                if (a >= 0 && b >= 0 && c >= 0)
                    return $"TDP {plan.Spl} W (boost {plan.Sppt}/{plan.Fppt} W) via ASUS ACPI";
            }
        }

        if (!string.IsNullOrEmpty(ryzenAdjPath) && File.Exists(ryzenAdjPath))
        {
            var args = $"--stapm-limit={plan.Spl * 1000} --slow-limit={plan.Sppt * 1000} --fast-limit={plan.Fppt * 1000}";
            var (code, output) = Run(ryzenAdjPath, args);
            return code == 0 ? $"TDP {plan.Spl} W via RyzenAdj" : $"RyzenAdj failed ({code}): {output.Trim()}";
        }
        return "Couldn't reach the ASUS ACPI driver. Install Armoury Crate SE drivers or set a RyzenAdj path in Settings.";
    }

    public static bool SetPowerOverlay(Guid overlay) => PowerSetActiveOverlayScheme(overlay) == 0;

    public static Guid? GetPowerOverlay() => PowerGetEffectiveOverlayScheme(out var g) == 0 ? g : null;

    /// <summary>Processor performance boost mode on AC and DC: 0 = off, 2 = aggressive.</summary>
    public static void SetCpuBoost(int mode)
    {
        Run("powercfg", $"/setacvalueindex scheme_current sub_processor PERFBOOSTMODE {mode}");
        Run("powercfg", $"/setdcvalueindex scheme_current sub_processor PERFBOOSTMODE {mode}");
        Run("powercfg", "/setactive scheme_current");
    }

    public static (bool OnAc, int Percent) Battery()
    {
        if (!GetSystemPowerStatus(out var s)) return (true, -1);
        return (s.ACLineStatus == 1, s.BatteryLifePercent == 255 ? -1 : s.BatteryLifePercent);
    }

    public static (int Code, string Output) Run(string exe, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args)
            {
                CreateNoWindow = true, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true
            })!;
            var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(15000);
            return (p.ExitCode, output);
        }
        catch (Exception ex) { return (-1, ex.Message); }
    }

    [DllImport("powrprof.dll")] private static extern uint PowerSetActiveOverlayScheme(Guid overlay);
    [DllImport("powrprof.dll")] private static extern uint PowerGetEffectiveOverlayScheme(out Guid overlay);

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
}

/// <summary>Built-in display refresh rate switching (Ally panel: 60 / 120 Hz).</summary>
public static class DisplayService
{
    private const int EnumCurrentSettings = -1;
    private const int DmDisplayFrequency = 0x400000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplaySettings(string? device, int mode, ref DevMode dm);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int ChangeDisplaySettingsEx(string? device, ref DevMode dm, IntPtr hwnd, int flags, IntPtr param);

    public static int CurrentRefresh()
    {
        var dm = New();
        return EnumDisplaySettings(null, EnumCurrentSettings, ref dm) ? dm.dmDisplayFrequency : 0;
    }

    public static IReadOnlyList<int> SupportedRefreshRates()
    {
        var current = New();
        if (!EnumDisplaySettings(null, EnumCurrentSettings, ref current)) return [];
        var rates = new SortedSet<int>();
        var dm = New();
        for (var i = 0; EnumDisplaySettings(null, i, ref dm); i++)
            if (dm.dmPelsWidth == current.dmPelsWidth && dm.dmPelsHeight == current.dmPelsHeight) rates.Add(dm.dmDisplayFrequency);
        return rates.ToList();
    }

    /// <summary>Sets the closest supported refresh rate at the current resolution.</summary>
    public static int SetRefresh(int hz)
    {
        var rates = SupportedRefreshRates();
        if (rates.Count == 0) return 0;
        var target = rates.MinBy(r => Math.Abs(r - hz));
        var dm = New();
        EnumDisplaySettings(null, EnumCurrentSettings, ref dm);
        if (dm.dmDisplayFrequency == target) return target;
        dm.dmDisplayFrequency = target;
        dm.dmFields = DmDisplayFrequency;
        return ChangeDisplaySettingsEx(null, ref dm, IntPtr.Zero, 0x1 /* CDS_UPDATEREGISTRY */, IntPtr.Zero) == 0 ? target : 0;
    }

    private static DevMode New() => new() { dmSize = (short)Marshal.SizeOf<DevMode>() };
}
