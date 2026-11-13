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
    // ---- manual session ----
    public bool Active { get; private set; }
    public TimeSpan? Duration { get; private set; }   // chip that started it; null = indefinite
    public DateTime? UntilTime { get; private set; }  // set when started with "Until…" (local time)
    DateTime _untilUtc;

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

    public TimeSpan? Remaining => Active && Duration is not null ? _untilUtc - DateTime.UtcNow : null;

    public event Action? Changed;
    uint _applied; // last flags sent to Windows (0 = never)

    public void Start(TimeSpan? duration)
    {
        Active = true;
        Duration = duration;
        UntilTime = null;
        if (duration is { } d) _untilUtc = DateTime.UtcNow + d;
        Update();
    }

    /// <summary>Keep awake until a clock time today (or tomorrow if that time has passed).</summary>
    public void StartUntil(TimeSpan timeOfDay)
    {
        var until = DateTime.Today + timeOfDay;
        if (until <= DateTime.Now) until = until.AddDays(1);
        Active = true;
        UntilTime = until;
        Duration = until - DateTime.Now;
        _untilUtc = until.ToUniversalTime();
        Update();
    }

    public void Stop()
    {
        Active = false;
        Duration = null;
        UntilTime = null;
        Update();
    }

    /// <summary>Called every second. Ends timed sessions, tracks power and watched apps.</summary>
    public void Tick(bool pluggedIn, bool checkApps)
    {
        if (Remaining is { } r && r <= TimeSpan.Zero)
        {
            Active = false;
            Duration = null;
            UntilTime = null;
        }
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
        a.WatchApps.AddRange(WatchApps);
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
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
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
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue(Name, Command);
            else key.DeleteValue(Name, throwOnMissingValue: false);
        }
    }

    /// <summary>If the exe was moved (e.g. after an update), point the Run entry at the new path.</summary>
    public static void FixPath()
    {
        if (Enabled) Enabled = true;
    }
}

static class Theme
{
    const string Personalize = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>App windows: light or dark. Defaults to dark.</summary>
    public static bool AppsLight => Read("AppsUseLightTheme");

    /// <summary>Taskbar: light or dark (decides the tray number's colour).</summary>
    public static bool TaskbarLight => Read("SystemUsesLightTheme");

    static bool Read(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(Personalize);
        return key?.GetValue(name) is int v && v == 1;
    }
}
