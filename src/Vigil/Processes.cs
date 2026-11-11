using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;

namespace Vigil;

record AppUsage(string Name, int Count, double Cpu, long Ram); // Cpu = share of the whole machine, 0..1

/// <summary>
/// Top apps by CPU or RAM, grouped by process name (like Task Manager's app groups).
/// Only sampled while the Processes tab is open: walking every process costs more than the rest of Vigil.
/// </summary>
sealed class ProcessSampler
{
    readonly Dictionary<int, TimeSpan> _lastCpu = new();
    long _lastStamp;

    public IReadOnlyList<AppUsage> Apps { get; private set; } = Array.Empty<AppUsage>();
    public bool Measured { get; private set; } // false until two samples exist (CPU needs a delta)

    public void Sample()
    {
        long now = Stopwatch.GetTimestamp();
        double elapsed = _lastStamp == 0 ? 0 : (now - _lastStamp) / (double)Stopwatch.Frequency;
        double capacity = elapsed * Environment.ProcessorCount;

        var cpu = new Dictionary<int, TimeSpan>();
        var groups = new Dictionary<string, (int count, double cpu, long ram)>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                if (p.Id == 0) continue; // System Idle Process
                string name;
                long ram = 0;
                double share = 0;
                try
                {
                    name = p.ProcessName;
                    ram = p.WorkingSet64;
                    var t = p.TotalProcessorTime; // throws for protected/system processes: they count as 0 CPU
                    cpu[p.Id] = t;
                    if (capacity > 0 && _lastCpu.TryGetValue(p.Id, out var before))
                        share = Math.Max(0, (t - before).TotalSeconds / capacity);
                }
                catch (Exception e) when (e is Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    try { name = p.ProcessName; } catch { continue; } // already exited
                }

                groups.TryGetValue(name, out var g);
                groups[name] = (g.count + 1, g.cpu + share, g.ram + ram);
            }
        }

        _lastCpu.Clear();
        foreach (var (pid, t) in cpu) _lastCpu[pid] = t;
        Measured = _lastStamp != 0;
        _lastStamp = now;
        Apps = groups.Select(kv => new AppUsage(kv.Key, kv.Value.count, Math.Min(kv.Value.cpu, 1), kv.Value.ram)).ToList();
    }

    public void Reset()
    {
        _lastCpu.Clear();
        _lastStamp = 0;
        Measured = false;
    }

    public IEnumerable<AppUsage> Top(bool byRam, int count) =>
        (byRam ? Apps.OrderByDescending(a => a.Ram) : Apps.OrderByDescending(a => a.Cpu).ThenByDescending(a => a.Ram))
        .Take(count);

    // Ending these either takes Windows down, logs you out, or is Vigil itself.
    static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Registry", "Memory Compression", "smss", "csrss", "wininit", "winlogon",
        "services", "lsass", "svchost", "dwm", "fontdrvhost", "Secure System",
    };

    public static bool CanEnd(string name) =>
        !Protected.Contains(name) && !name.Equals(Process.GetCurrentProcess().ProcessName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Ends every process with this name. Returns an error message, or null if all ended.</summary>
    public static string? End(string name)
    {
        if (!CanEnd(name)) return $"{name} can't be ended from here.";
        int failed = 0, total = 0;
        foreach (var p in Process.GetProcessesByName(name))
        {
            using (p)
            {
                total++;
                try { p.Kill(entireProcessTree: false); }
                catch (Exception e) when (e is Win32Exception or InvalidOperationException or NotSupportedException) { failed++; }
            }
        }
        return failed == 0 ? null
            : failed == total ? $"Windows wouldn't let Vigil end {name} (it may need admin rights)."
            : $"Ended {total - failed} of {total} {name} processes; the rest need admin rights.";
    }
}
