using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Vigil;

/// <summary>One screen as Windows sees it, plus whatever can be controlled on it.</summary>
sealed class DisplayScreen
{
    public string Title = "";
    public string Device = "";      // \\.\DISPLAYn ("" for the separate built-in card)
    public bool Primary;
    public int Width, Height, Hz;
    public int[] Rates = Array.Empty<int>();
    public readonly List<DisplayControl> Controls = new();

    internal Displays.PHYSICAL_MONITOR[] Physical = Array.Empty<Displays.PHYSICAL_MONITOR>();
    internal bool Closed;            // handles released; guarded by Displays.Gate

    public override string ToString() =>
        $"{Title} {(Device.Length > 0 ? $"({Device}) {Width}×{Height} @ {Hz} Hz, rates [{string.Join(",", Rates)}]" : "")}"
        + (Controls.Count == 0 ? " no control" : " " + string.Join(", ", Controls.Select(c => $"{c.Name} {c.Value}/{c.Max}")));
}

/// <summary>A brightness / contrast / volume slider. Set() is throttled: only the latest value is applied.</summary>
sealed class DisplayControl
{
    public readonly string Name;
    public int Value;
    public readonly int Max;
    readonly DisplayScreen _screen;
    readonly IntPtr _handle;
    readonly byte _code;             // VCP code; 0 = laptop panel via WMI

    int _pending = -1, _busy;

    public DisplayControl(string name, int value, int max, DisplayScreen screen, IntPtr handle, byte code)
    {
        Name = name; Value = value; Max = max; _screen = screen; _handle = handle; _code = code;
    }

    /// <summary>From the UI thread on every slider tick. Never queues: a running worker picks up the newest value.</summary>
    public void Set(int value)
    {
        Value = value;
        Interlocked.Exchange(ref _pending, value);
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        Task.Run(() =>
        {
            while (true)
            {
                int v = Interlocked.Exchange(ref _pending, -1);
                if (v < 0)
                {
                    Volatile.Write(ref _busy, 0);
                    // A value may have arrived after the exchange above but before _busy was cleared.
                    if (Volatile.Read(ref _pending) < 0 || Interlocked.Exchange(ref _busy, 1) == 1) return;
                    continue;
                }
                lock (Displays.Gate)
                {
                    if (_screen.Closed) return;
                    try
                    {
                        if (_code == 0) Displays.WmiSetBrightness(v);
                        else
                        {
                            Displays.SetVCPFeature(_handle, _code, (uint)v);
                            Thread.Sleep(Displays.DdcGapMs);
                        }
                    }
                    catch (Exception) { } // monitor unplugged / WMI gone: nothing useful to show mid-drag
                }
            }
        });
    }
}

static class Displays
{
    /// <summary>All DDC/CI and WMI traffic goes through this one lock (monitors dislike overlapping commands).</summary>
    public static readonly object Gate = new();

    // ponytail: DDC/CI wants ~50 ms between commands; some monitors need more. Tune here.
    public const int DdcGapMs = 50;
    const int DdcTries = 2; // DDC reads fail intermittently on real hardware; one retry catches most

    static readonly (byte Code, string Name)[] Vcp = { (0x10, "Brightness"), (0x12, "Contrast"), (0x62, "Volume") };

    /// <summary>Blocking (DDC is slow): call off the UI thread. Caller must Release() the result.</summary>
    public static List<DisplayScreen> Load()
    {
        lock (Gate)
        {
            var screens = new List<DisplayScreen>();
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr hdc, ref RECT r, IntPtr d) =>
            {
                screens.Add(Read(h));
                return true;
            }, IntPtr.Zero);
            screens.Sort((a, b) => a.Primary != b.Primary ? (a.Primary ? -1 : 1) : string.CompareOrdinal(a.Device, b.Device));

            int? wmi = WmiBrightness();
            if (wmi is { } level)
            {
                // The laptop panel has no DDC. If exactly one screen didn't answer DDC, that's it.
                var silent = screens.Where(s => s.Controls.Count == 0).ToList();
                DisplayScreen target;
                if (silent.Count == 1) { target = silent[0]; target.Title = "Built-in display" + (target.Primary ? " · main" : ""); }
                else { target = new DisplayScreen { Title = "Built-in display" }; screens.Add(target); }
                target.Controls.Add(new DisplayControl("Brightness", level, 100, target, IntPtr.Zero, 0));
            }
            return screens;
        }
    }

    static DisplayScreen Read(IntPtr hMon)
    {
        var s = new DisplayScreen();
        var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        if (GetMonitorInfoW(hMon, ref mi))
        {
            s.Device = mi.szDevice;
            s.Primary = (mi.dwFlags & MONITORINFOF_PRIMARY) != 0;
        }
        string digits = new(s.Device.Where(char.IsDigit).ToArray());
        s.Title = $"Display {(digits.Length > 0 ? digits : "?")}" + (s.Primary ? " · main" : "");

        var dm = NewDevMode();
        if (EnumDisplaySettingsW(s.Device, ENUM_CURRENT_SETTINGS, ref dm))
        {
            s.Width = dm.dmPelsWidth; s.Height = dm.dmPelsHeight; s.Hz = dm.dmDisplayFrequency;
            var rates = new SortedSet<int>();
            var m = NewDevMode();
            for (int i = 0; EnumDisplaySettingsW(s.Device, i, ref m); i++)
                if (m.dmPelsWidth == s.Width && m.dmPelsHeight == s.Height && m.dmBitsPerPel == dm.dmBitsPerPel && m.dmDisplayFrequency > 1)
                    rates.Add(m.dmDisplayFrequency);
            s.Rates = rates.ToArray();
        }

        if (GetNumberOfPhysicalMonitorsFromHMONITOR(hMon, out uint n) && n > 0)
        {
            var phys = new PHYSICAL_MONITOR[n];
            if (GetPhysicalMonitorsFromHMONITOR(hMon, n, phys))
            {
                s.Physical = phys;
                IntPtr h = phys[0].hPhysicalMonitor; // ponytail: cloned outputs share one HMONITOR; we control the first
                foreach (var (code, name) in Vcp)
                    for (int t = 0; t < DdcTries; t++)
                        if (GetVCPFeatureAndVCPFeatureReply(h, code, IntPtr.Zero, out uint cur, out uint max) && max > 0)
                        {
                            s.Controls.Add(new DisplayControl(name, (int)Math.Min(cur, max), (int)max, s, h, code));
                            break;
                        }
            }
        }
        return s;
    }

    /// <summary>Free physical monitor handles. Safe to call twice, from any thread.</summary>
    public static void Release(IEnumerable<DisplayScreen>? screens)
    {
        if (screens is null) return;
        lock (Gate)
            foreach (var s in screens)
            {
                if (s.Closed) continue;
                s.Closed = true;
                if (s.Physical.Length > 0) DestroyPhysicalMonitors((uint)s.Physical.Length, s.Physical);
            }
    }

    // ---------- laptop panel (WMI) ----------

    static int? WmiBrightness()
    {
        try
        {
            using var q = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness WHERE Active=TRUE");
            foreach (ManagementObject o in q.Get())
                using (o) return Convert.ToInt32(o["CurrentBrightness"]);
        }
        catch (Exception) { } // desktops / VMs: class not supported
        return null;
    }

    internal static void WmiSetBrightness(int level)
    {
        using var q = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM WmiMonitorBrightnessMethods WHERE Active=TRUE");
        foreach (ManagementObject o in q.Get())
            using (o)
            {
                using var p = o.GetMethodParameters("WmiSetBrightness");
                p["Timeout"] = 1u;
                p["Brightness"] = (byte)Math.Clamp(level, 0, 100);
                o.InvokeMethod("WmiSetBrightness", p, null);
                return;
            }
    }

    // ---------- refresh rate / mode / power ----------

    /// <summary>Change refresh rate at the current resolution. Returns false if Windows refused.</summary>
    public static bool SetRate(string device, int hz)
    {
        var dm = NewDevMode();
        if (!EnumDisplaySettingsW(device, ENUM_CURRENT_SETTINGS, ref dm)) return false;
        dm.dmDisplayFrequency = hz;
        dm.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY;
        return ChangeDisplaySettingsExW(device, ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY, IntPtr.Zero) == 0;
    }

    public const uint ModeInternal = 0x1, ModeClone = 0x2, ModeExtend = 0x4, ModeExternal = 0x8; // = SDC_TOPOLOGY_* = topology ids

    /// <summary>Same as Win+P. Returns false if Windows refused (e.g. only one screen).</summary>
    public static bool SetMode(uint topology) => SetDisplayConfig(0, IntPtr.Zero, 0, IntPtr.Zero, SDC_APPLY | topology) == 0;

    /// <summary>Current Win+P mode (one of Mode*), or 0 if Windows doesn't say.</summary>
    public static uint CurrentMode()
    {
        if (GetDisplayConfigBufferSizes(QDC_DATABASE_CURRENT, out uint np, out uint nm) != 0) return 0;
        IntPtr paths = Marshal.AllocHGlobal((int)Math.Max(1, np) * 72), modes = Marshal.AllocHGlobal((int)Math.Max(1, nm) * 64);
        try
        {
            return QueryDisplayConfig(QDC_DATABASE_CURRENT, ref np, paths, ref nm, modes, out uint topology) == 0 ? topology : 0;
        }
        finally { Marshal.FreeHGlobal(paths); Marshal.FreeHGlobal(modes); }
    }

    /// <summary>Screens off until mouse/keyboard. Sent to our own window; DefWindowProc does the work.</summary>
    public static void TurnOff(IntPtr hwnd) => SendMessageW(hwnd, WM_SYSCOMMAND, (IntPtr)SC_MONITORPOWER, (IntPtr)2);

    // ---------- interop ----------

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor, rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 1)]
    internal struct PHYSICAL_MONITOR
    {
        public IntPtr hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szPhysicalMonitorDescription;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields;
        public int dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    static DEVMODE NewDevMode() => new() { dmSize = (short)Marshal.SizeOf<DEVMODE>() };

    const uint MONITORINFOF_PRIMARY = 1;
    const int ENUM_CURRENT_SETTINGS = -1;
    const int DM_PELSWIDTH = 0x80000, DM_PELSHEIGHT = 0x100000, DM_DISPLAYFREQUENCY = 0x400000;
    const uint CDS_UPDATEREGISTRY = 1;
    const uint SDC_APPLY = 0x80;
    const uint QDC_DATABASE_CURRENT = 0x4;
    const uint WM_SYSCOMMAND = 0x0112;
    const int SC_MONITORPOWER = 0xF170;

    delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

    [DllImport("user32.dll")]
    static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFOEX info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool EnumDisplaySettingsW(string device, int mode, ref DEVMODE dm);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int ChangeDisplaySettingsExW(string device, ref DEVMODE dm, IntPtr hwnd, uint flags, IntPtr param);

    [DllImport("user32.dll")]
    static extern int SetDisplayConfig(uint numPaths, IntPtr paths, uint numModes, IntPtr modes, uint flags);

    [DllImport("user32.dll")]
    static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);

    [DllImport("user32.dll")]
    static extern int QueryDisplayConfig(uint flags, ref uint numPaths, IntPtr paths, ref uint numModes, IntPtr modes, out uint topology);

    [DllImport("user32.dll")]
    static extern IntPtr SendMessageW(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);

    [DllImport("dxva2.dll", SetLastError = true)]
    static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint count);

    [DllImport("dxva2.dll", SetLastError = true)]
    static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint count, [Out] PHYSICAL_MONITOR[] monitors);

    [DllImport("dxva2.dll", SetLastError = true)]
    static extern bool DestroyPhysicalMonitors(uint count, PHYSICAL_MONITOR[] monitors);

    [DllImport("dxva2.dll", SetLastError = true)]
    static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr monitor, byte code, IntPtr type, out uint current, out uint max);

    [DllImport("dxva2.dll", SetLastError = true)]
    internal static extern bool SetVCPFeature(IntPtr monitor, byte code, uint value);
}
