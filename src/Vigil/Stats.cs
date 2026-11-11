using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using Forms = System.Windows.Forms;

namespace Vigil;

record DiskInfo(string Name, string Label, long Free, long Total)
{
    public double UsedFraction => Total > 0 ? 1.0 - (double)Free / Total : 0;
}

record BatteryInfo(bool Present, double Percent, bool PluggedIn, bool Charging, int SecondsLeft);

/// <summary>All readouts. Sample() is called once per second on the UI thread.</summary>
sealed class Stats
{
    public const int HistoryLength = 60;

    public double Cpu;                       // 0..1
    public ulong RamUsed, RamTotal;
    public double Down, Up;                  // bytes per second
    public IReadOnlyList<DiskInfo> Disks = Array.Empty<DiskInfo>();
    public BatteryInfo Battery = new(false, 0, true, false, -1);

    public readonly Queue<double> CpuHistory = new();
    public readonly Queue<double> RamHistory = new();
    public readonly Queue<double> DownHistory = new();

    public double RamFraction => RamTotal > 0 ? (double)RamUsed / RamTotal : 0;

    long _idle, _kernel, _user;
    bool _cpuPrimed;

    NetworkInterface[] _nics = Array.Empty<NetworkInterface>();
    long _rx, _tx, _netStamp;

    int _tick;

    public void Sample()
    {
        SampleCpu();
        SampleRam();
        SampleNetwork();
        SampleBattery();
        if (_tick % 30 == 0) SampleDisks(); // disk space barely moves; spec allows 30 s
        _tick++;
    }

    public void RefreshDisks() => SampleDisks();

    void SampleCpu()
    {
        if (!Native.GetSystemTimes(out long idle, out long kernel, out long user)) return;
        long dIdle = idle - _idle;
        long dTotal = (kernel - _kernel) + (user - _user); // kernel time includes idle time
        _idle = idle; _kernel = kernel; _user = user;

        // First reading has no baseline and is always wrong: discard it.
        if (!_cpuPrimed) { _cpuPrimed = true; return; }
        if (dTotal <= 0) return;

        Cpu = Math.Clamp(1.0 - (double)dIdle / dTotal, 0, 1);
        Push(CpuHistory, Cpu);
    }

    void SampleRam()
    {
        var m = new Native.MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
        if (!Native.GlobalMemoryStatusEx(ref m)) return;
        RamTotal = m.ullTotalPhys;
        RamUsed = m.ullTotalPhys - m.ullAvailPhys;
        Push(RamHistory, RamFraction);
    }

    void SampleNetwork()
    {
        // Adapters change rarely; re-list every 30 s and restart the baseline so a new adapter's
        // lifetime byte count doesn't show up as one giant spike.
        if (_tick % 30 == 0)
        {
            _nics = ActiveAdapters();
            _netStamp = 0;
        }

        long rx = 0, tx = 0;
        foreach (var nic in _nics)
        {
            try
            {
                var s = nic.GetIPStatistics();
                rx += s.BytesReceived;
                tx += s.BytesSent;
            }
            catch (Exception e) when (e is NetworkInformationException or PlatformNotSupportedException)
            {
                _netStamp = 0; // adapter vanished or can't report; re-baseline
            }
        }

        long now = Stopwatch.GetTimestamp();
        double secs = (now - _netStamp) / (double)Stopwatch.Frequency;
        if (_netStamp != 0 && secs > 0 && rx >= _rx && tx >= _tx)
        {
            Down = (rx - _rx) / secs;
            Up = (tx - _tx) / secs;
        }
        else
        {
            Down = Up = 0;
        }
        _rx = rx; _tx = tx; _netStamp = now;
        Push(DownHistory, Down);
    }

    // ponytail: name-based filter for virtual adapters. Add a keyword here if some VPN/VM adapter slips through
    // (symptom: speeds roughly double what they should be).
    static readonly string[] VirtualHints =
    {
        "virtual", "hyper-v", "vmware", "virtualbox", "vpn", "tap-", "wintun", "wireguard",
        "tailscale", "zerotier", "npcap", "loopback", "pseudo", "bluetooth",
    };

    static NetworkInterface[] ActiveAdapters()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                            && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                            && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel
                            && !VirtualHints.Any(h => n.Description.Contains(h, StringComparison.OrdinalIgnoreCase)
                                                   || n.Name.Contains(h, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
        }
        catch (NetworkInformationException)
        {
            return Array.Empty<NetworkInterface>();
        }
    }

    void SampleBattery()
    {
        var p = Forms.SystemInformation.PowerStatus;
        // Unknown is 255 (every bit set), so it must be compared, not masked.
        bool present = p.BatteryChargeStatus != Forms.BatteryChargeStatus.Unknown
                       && (p.BatteryChargeStatus & Forms.BatteryChargeStatus.NoSystemBattery) == 0;
        Battery = new BatteryInfo(
            present,
            Math.Clamp(p.BatteryLifePercent, 0f, 1f),
            p.PowerLineStatus == Forms.PowerLineStatus.Online,
            (p.BatteryChargeStatus & Forms.BatteryChargeStatus.Charging) != 0,
            p.BatteryLifeRemaining); // -1 when Windows doesn't know (e.g. while charging)
    }

    void SampleDisks()
    {
        var list = new List<DiskInfo>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                list.Add(new DiskInfo(d.Name.TrimEnd('\\'), d.VolumeLabel, d.AvailableFreeSpace, d.TotalSize));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        Disks = list;
    }

    static void Push(Queue<double> q, double v)
    {
        q.Enqueue(v);
        while (q.Count > HistoryLength) q.Dequeue();
    }
}

static class Fmt
{
    public static string Rate(double bytesPerSec) =>
        bytesPerSec >= 1024 * 1024 ? $"{bytesPerSec / (1024 * 1024):0.0} MB/s" : $"{bytesPerSec / 1024:0} KB/s";

    public static string Gb(ulong bytes) => $"{bytes / 1073741824.0:0.0}";

    public static string Size(long bytes) =>
        bytes >= 1L << 40 ? $"{bytes / (double)(1L << 40):0.00} TB" : $"{bytes / (double)(1L << 30):0} GB";

    public static string Duration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:00}m" : $"{t.Minutes}m {t.Seconds:00}s";
}
