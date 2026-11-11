using System;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace Vigil;

/// <summary>
/// Keeps the PC (and optionally the display) awake via SetThreadExecutionState.
/// Must be used from the UI thread: the state belongs to the calling thread and lasts while it lives.
/// If the process dies, Windows drops the request on its own, so a crash can't leave the PC stuck awake.
/// </summary>
sealed class KeepAwake
{
    public bool Active { get; private set; }
    public TimeSpan? Duration { get; private set; } // null = indefinitely
    DateTime _until;

    bool _displayOn;
    public bool DisplayOn
    {
        get => _displayOn;
        set { _displayOn = value; if (Active) Apply(); Changed?.Invoke(); }
    }

    public event Action? Changed;

    public TimeSpan? Remaining => Active && Duration is not null ? _until - DateTime.UtcNow : null;

    public void Start(TimeSpan? duration)
    {
        Active = true;
        Duration = duration;
        if (duration is { } d) _until = DateTime.UtcNow + d;
        Apply();
        Changed?.Invoke();
    }

    public void Stop()
    {
        Active = false;
        Duration = null;
        Native.SetThreadExecutionState(Native.ES_CONTINUOUS); // back to normal power rules
        Changed?.Invoke();
    }

    /// <summary>Called every second; ends a timed session.</summary>
    public void Tick()
    {
        if (Remaining is { } r && r <= TimeSpan.Zero) Stop();
    }

    void Apply() =>
        Native.SetThreadExecutionState(Native.ES_CONTINUOUS | Native.ES_SYSTEM_REQUIRED
                                       | (_displayOn ? Native.ES_DISPLAY_REQUIRED : 0));
}

/// <summary>The only file Vigil writes: %APPDATA%\Vigil\settings.json (a few bytes).</summary>
sealed class Settings
{
    public bool KeepDisplayOn { get; set; }

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
