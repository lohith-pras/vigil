using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace Vigil;

/// <summary>
/// Keeps the PC (and optionally the display) awake via SetThreadExecutionState.
/// Must be used from the UI thread: the state belongs to the calling thread and lasts while it lives.
/// If the process dies, Windows drops the request on its own, so a crash can't leave the PC stuck awake.
///
/// "Wanted" = the user turned it on (Active) or a watched app is running.
/// "Holding" = wanted and not paused (on battery / locked, when those options are on).
/// Only Holding actually reaches Windows.
/// </summary>
sealed class KeepAwake
{
    // ---- manual session: null = off. One value, so "on", "how long" and "until when" can't disagree. ----
    // Chip sessions end on the uptime clock (EndsTick, Environment.TickCount64: counts through sleep, ignores
    // clock changes). "Until…" sessions end at a wall-clock time, which is what they mean.
    sealed record Session(TimeSpan? Duration, DateTime? UntilTime, long EndsTick);
    Session? _session;

    public bool Active => _session is not null;
    public TimeSpan? Duration => _session?.Duration;    // chip that started it; null = indefinite
    public DateTime? UntilTime => _session?.UntilTime;  // set when started with "Until…" (local time)

    // ---- options (saved in settings.json) ----
    bool _displayOn, _onlyPlugged, _pauseLocked;
    public bool DisplayOn { get => _displayOn; set { _displayOn = value; Update(); } }
    public bool OnlyWhenPluggedIn { get => _onlyPlugged; set { _onlyPlugged = value; Update(); } }
    public bool PauseWhenLocked { get => _pauseLocked; set { _pauseLocked = value; Update(); } }
    public bool OnAtStartup { get; set; }
    public List<string> WatchApps { get; } = new(); // process names, e.g. "obs64"

    // ---- live conditions ----
    public string? RunningApp { get; private set; }
    public bool OnBattery { get; private set; }
    bool _locked;
    public bool Locked { get => _locked; set { _locked = value; Update(); } }

    public bool Wanted => Active || RunningApp is not null;
    public string? PausedReason =>
        !Wanted ? null
        : _onlyPlugged && OnBattery ? "on battery"
        : _pauseLocked && _locked ? "PC is locked"
        : null;
    public bool Holding => Wanted && PausedReason is null;

    public TimeSpan? Remaining =>
        _session is not { Duration: not null } s ? null
        : s.UntilTime is { } until ? until.ToUniversalTime() - DateTime.UtcNow
        : TimeSpan.FromMilliseconds(s.EndsTick - Environment.TickCount64);

    public event Action? Changed;
    uint _applied; // last flags sent to Windows (0 = never)

    public void Start(TimeSpan? duration)
    {
        _session = new(duration, null, duration is { } d ? Environment.TickCount64 + (long)d.TotalMilliseconds : 0);
        Update();
    }

    /// <summary>Keep awake until a clock time today (or tomorrow if that time has passed).</summary>
    public void StartUntil(TimeSpan timeOfDay)
    {
        var until = DateTime.Today + timeOfDay;
        if (until <= DateTime.Now) until = until.AddDays(1);
        _session = new(until - DateTime.Now, until, 0);
        Update();
    }

    public void Stop()
    {
        _session = null;
        Update();
    }

    /// <summary>Called every second. Ends timed sessions, tracks power and watched apps.</summary>
    public void Tick(bool pluggedIn, bool checkApps)
    {
        if (Remaining <= TimeSpan.Zero) _session = null;
        OnBattery = !pluggedIn;
        if (checkApps) RunningApp = FindRunningApp();
        Update();
    }

    string? FindRunningApp()
    {
        foreach (var name in WatchApps)
        {
            var procs = Process.GetProcessesByName(name);
            foreach (var p in procs) p.Dispose();
            if (procs.Length > 0) return name;
        }
        return null;
    }

    string _lastState = "";

    void Update()
    {
        uint flags = Native.ES_CONTINUOUS;
        if (Holding) flags |= Native.ES_SYSTEM_REQUIRED | (_displayOn ? Native.ES_DISPLAY_REQUIRED : 0);
        if (flags != _applied)
        {
            Native.SetThreadExecutionState(flags); // ES_CONTINUOUS alone = back to normal power rules
            _applied = flags;
        }

        // Only notify when something visible changed (Tick runs every second).
        string state = $"{Active}|{Duration}|{UntilTime}|{RunningApp}|{PausedReason}|{_displayOn}|{_onlyPlugged}|{_pauseLocked}|{OnAtStartup}|{string.Join(",", WatchApps)}";
        if (state == _lastState) return;
        _lastState = state;
        Changed?.Invoke();
    }

    /// <summary>For option changes that don't go through a property setter (watch list, start-up flag).</summary>
    public void Touch() => Update();
}

/// <summary>The only file Vigil writes: %APPDATA%\Vigil\settings.json (a few bytes).</summary>
sealed class Settings
{
    public bool KeepDisplayOn { get; set; }
    public bool OnlyWhenPluggedIn { get; set; }
    public bool PauseWhenLocked { get; set; }
    public bool KeepAwakeOnStartup { get; set; }
    public List<string> WatchApps { get; set; } = new();
    public bool ShowCpuInTray { get; set; } // false = app icon (default since 0.2)
    public bool AutoUpdateCheck { get; set; } = true; // background check + notification (since 0.3)
    public string? AnnouncedVersion { get; set; }     // newest release already announced, so it pops up once

    // Alerts (v0.3): all on except "battery charged".
    public bool AlertBatteryLow { get; set; } = true;
    public bool AlertCharged { get; set; }
    public int ChargeLimit { get; set; } = 100; // 80, 90 or 100
    public bool AlertDiskFull { get; set; } = true;
    public bool AlertBusy { get; set; } = true;   // CPU or RAM above 90 % for 2 min

    public void LoadInto(KeepAwake a)
    {
        a.DisplayOn = KeepDisplayOn;
        a.OnlyWhenPluggedIn = OnlyWhenPluggedIn;
        a.PauseWhenLocked = PauseWhenLocked;
        a.OnAtStartup = KeepAwakeOnStartup;
        a.WatchApps.Clear();
        a.WatchApps.AddRange(WatchApps ?? new()); // "WatchApps": null in a hand-edited file
    }

    /// <summary>Copy options from the live object; true if anything changed.</summary>
    public bool CopyFrom(KeepAwake a)
    {
        string before = JsonSerializer.Serialize(this);
        KeepDisplayOn = a.DisplayOn;
        OnlyWhenPluggedIn = a.OnlyWhenPluggedIn;
        PauseWhenLocked = a.PauseWhenLocked;
        KeepAwakeOnStartup = a.OnAtStartup;
        WatchApps = new List<string>(a.WatchApps);
        return JsonSerializer.Serialize(this) != before;
    }

    static readonly string FilePath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Vigil", "settings.json");

    public static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new(); }
        catch { return new(); } // missing or corrupt: defaults
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            // Write beside, then swap: a power cut mid-write can't leave an empty settings file.
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch { } // a settings write failing must never take the app down
    }
}

/// <summary>Start with Windows = a value under HKCU\...\Run. No admin needed; the registry is the source of truth.</summary>
static class Startup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "Vigil";

    static string Command => $"\"{Environment.ProcessPath}\"";

    public static bool Enabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(Name) is string;
        }
        set
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RunKey);
                if (value) key.SetValue(Name, Command);
                else key.DeleteValue(Name, throwOnMissingValue: false);
            }
            catch (Exception) { } // registry locked down (policy, antivirus): the menu tick shows it didn't take
        }
    }

    /// <summary>If the exe was moved (e.g. after an update), point the Run entry at the new path.</summary>
    public static void FixPath()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(Name) is string cmd && cmd != Command) key.SetValue(Name, Command);
        }
        catch (Exception) { } // runs at every start: a refused write must not stop Vigil starting
    }
}

static class Theme
{
    const string Personalize = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>Taskbar: light or dark (decides the tray number's colour).</summary>
    public static bool TaskbarLight => Read("SystemUsesLightTheme");

    static bool Read(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(Personalize);
        return key?.GetValue(name) is int v && v == 1;
    }
}
