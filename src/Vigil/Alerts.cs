using System.Collections.Generic;

namespace Vigil;

/// <summary>
/// Tray notifications. Check() runs on the 1 s tick with readings Stats already has (no extra sampling).
/// Each alert fires once, then re-arms only after its condition has cleared.
/// </summary>
sealed class Alerts
{
    public const int BusySeconds = 120; // CPU / RAM must stay above 90 % this long

    readonly Settings _s;
    readonly HashSet<string> _fired = new();
    int _cpuBusy, _ramBusy;

    public Alerts(Settings settings) => _s = settings;

    /// <summary>Alerts that start now, as (title, text). Usually empty.</summary>
    public List<(string Title, string Text)> Check(BatteryInfo b, IReadOnlyList<DiskInfo> disks, double cpu, double ram)
    {
        var due = new List<(string, string)>();

        void Rule(string key, bool enabled, bool condition, string title, string text)
        {
            if (!enabled || !condition) _fired.Remove(key);
            else if (_fired.Add(key)) due.Add((title, text));
        }

        foreach (var d in disks)
            Rule("disk:" + d.Name, _s.AlertDiskFull, d.UsedFraction > 0.9,
                 $"Drive {d.Name} almost full", $"{d.UsedFraction * 100:0}% used, {Fmt.Size(d.Free)} free.");

        _cpuBusy = cpu > 0.9 ? _cpuBusy + 1 : 0;
        _ramBusy = ram > 0.9 ? _ramBusy + 1 : 0;
        Rule("cpu", _s.AlertBusy, _cpuBusy >= BusySeconds, "CPU busy", $"CPU has been above 90% for {BusySeconds / 60} minutes.");
        Rule("ram", _s.AlertBusy, _ramBusy >= BusySeconds, "Memory almost full", $"RAM has been above 90% for {BusySeconds / 60} minutes.");

        int pct = (int)System.Math.Round(b.Percent * 100);
        bool onBattery = b.Present && !b.PluggedIn;
        Rule("charged", _s.AlertCharged, b.Present && b.PluggedIn && pct >= _s.ChargeLimit,
             "Battery charged", $"Battery is at {pct}%. You can unplug.");
        // ponytail: listed last so at 10 % the more urgent text is the one the tray shows.
        Rule("low20", _s.AlertBatteryLow, onBattery && pct <= 20, "Battery low", $"{pct}% left. Plug in soon.");
        Rule("low10", _s.AlertBatteryLow, onBattery && pct <= 10, "Battery very low", $"{pct}% left. Plug in now.");
        return due;
    }
}
