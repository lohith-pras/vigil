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
    readonly HashSet<string> _fired = new(); // rules that have fired and not yet cleared ("C:" for drives)
    readonly List<(string Title, string Text)> _due = new();
    int _cpuBusy, _ramBusy;

    public Alerts(Settings settings) => _s = settings;

    /// <summary>
    /// Alerts that start now, as (title, text). Usually empty. The list is reused: read it before the next call.
    /// Runs every second, so texts are only formatted for an alert that actually fires.
    /// </summary>
    public List<(string Title, string Text)> Check(BatteryInfo b, IReadOnlyList<DiskInfo> disks, double cpu, double ram)
    {
        _due.Clear();

        foreach (var d in disks)
            if (Starts(d.Name, _s.AlertDiskFull && d.UsedFraction > 0.9))
                _due.Add(($"Drive {d.Name} almost full", $"{d.UsedFraction * 100:0}% used, {Fmt.Size(d.Free)} free."));

        _cpuBusy = cpu > 0.9 ? _cpuBusy + 1 : 0;
        _ramBusy = ram > 0.9 ? _ramBusy + 1 : 0;
        if (Starts("cpu", _s.AlertBusy && _cpuBusy >= BusySeconds))
            _due.Add(("CPU busy", $"CPU has been above 90% for {BusySeconds / 60} minutes."));
        if (Starts("ram", _s.AlertBusy && _ramBusy >= BusySeconds))
            _due.Add(("Memory almost full", $"RAM has been above 90% for {BusySeconds / 60} minutes."));

        int pct = (int)System.Math.Round(b.Percent * 100);
        bool onBattery = b.Present && !b.PluggedIn;
        if (Starts("charged", _s.AlertCharged && b.Present && b.PluggedIn && pct >= _s.ChargeLimit))
            _due.Add(("Battery charged", $"Battery is at {pct}%. You can unplug."));
        // ponytail: listed last so at 10 % the more urgent text is the one the tray shows.
        if (Starts("low20", _s.AlertBatteryLow && onBattery && pct <= 20))
            _due.Add(("Battery low", $"{pct}% left. Plug in soon."));
        if (Starts("low10", _s.AlertBatteryLow && onBattery && pct <= 10))
            _due.Add(("Battery very low", $"{pct}% left. Plug in now."));
        return _due;
    }

    /// <summary>Per-rule state: armed -> fired (true once, on the edge) -> armed again when the condition clears.</summary>
    bool Starts(string key, bool on)
    {
        if (on) return _fired.Add(key);
        _fired.Remove(key);
        return false;
    }
}
