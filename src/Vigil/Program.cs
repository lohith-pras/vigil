using System;
using System.Drawing;
using System.Linq;
using System.Drawing.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace Vigil;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // CI only: exercise every screen headlessly, save screenshots, exit non-zero on any error.
        if (args.Length >= 2 && args[0] == "--selftest") return SelfTest.Run(args[1]);

        // One copy only: a second launch just exits. After an update ("--updated") the new exe
        // waits for the old one to quit and release the mutex instead.
        using var mutex = new Mutex(false, @"Local\Vigil.SingleInstance");
        bool updated = args.Contains("--updated");
        bool first;
        try { first = mutex.WaitOne(updated ? TimeSpan.FromSeconds(20) : TimeSpan.Zero); }
        catch (AbandonedMutexException) { first = true; } // previous copy exited without releasing
        if (!first) return 0;
        Updater.CleanUp();

        // Draw with the CPU, not the GPU: the panel is simple, and GPU rendering loads the graphics
        // drivers into the process (~100+ MB on a laptop with AMD + NVIDIA) and can wake the dGPU.
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

        // No main window: WPF runs until Quit calls Shutdown().
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        using var tray = new Tray(app.Shutdown);
        // On a crash, still remove the tray icon and drop keep-awake before the process dies.
        app.DispatcherUnhandledException += (_, _) => tray.Dispose();
        if (args.Length >= 2 && args[0] == "--memtest") MemTest.Schedule(tray, app, args[1]);
        app.Run();
        return 0;
    }
}

sealed class Tray : IDisposable
{
    readonly Forms.NotifyIcon _icon;
    readonly Forms.ContextMenuStrip _menu;
    readonly DispatcherTimer _timer;
    readonly Stats _stats = new();
    readonly KeepAwake _awake = new();
    readonly Settings _settings = Settings.Load();
    readonly Alerts _alerts;
    readonly Action _quit;
    readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;

    PanelWindow? _panel;
    DateTime _panelHiddenAt;
    IntPtr _hicon;
    string _iconKey = "";
    int _tick;
    bool _disposed;

    public Tray(Action quit)
    {
        _quit = quit;
        _alerts = new Alerts(_settings);
        _settings.LoadInto(_awake);
        if (_awake.OnAtStartup) _awake.Start(null);
        Startup.FixPath();
        SystemEvents.SessionSwitch += OnSessionSwitch;

        _menu = new Forms.ContextMenuStrip();
        var startItem = new Forms.ToolStripMenuItem("Start with Windows", null, (_, _) => Startup.Enabled = !Startup.Enabled);
        var awakeItem = new Forms.ToolStripMenuItem("Keep awake", null, (_, _) =>
        {
            if (_awake.Active) _awake.Stop(); else _awake.Start(null);
        });
        var cpuItem = new Forms.ToolStripMenuItem("Show CPU % in tray icon", null, (_, _) => ToggleCpuInTray());
        var updateItem = new Forms.ToolStripMenuItem("Check for updates…", null, (_, _) => CheckForUpdates());
        _menu.Items.Add(startItem);
        _menu.Items.Add(awakeItem);
        _menu.Items.Add(cpuItem);
        _menu.Items.Add(updateItem);
        var autoItem = new Forms.ToolStripMenuItem("Check for updates automatically", null, (_, _) => ToggleAutoUpdate());
        _menu.Items.Add(autoItem);
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add("Quit", null, (_, _) => _quit());
        _menu.Opening += (_, _) =>
        {
            startItem.Checked = Startup.Enabled;
            cpuItem.Checked = _settings.ShowCpuInTray;
            autoItem.Checked = _settings.AutoUpdateCheck;
            awakeItem.Checked = _awake.Active;
        };

        _icon = new Forms.NotifyIcon { ContextMenuStrip = _menu, Text = "Vigil" };
        _icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) TogglePanel(); };

        _awake.Changed += () =>
        {
            if (_settings.CopyFrom(_awake)) _settings.Save();
            RenderIcon();
            _panel?.Refresh();
        };

        _stats.Sample(); // primes the CPU baseline (first reading is discarded)
        RenderIcon();
        _icon.Visible = true;

        // ponytail: one 1 s timer for everything. Sampling is a few cheap syscalls; the icon/tooltip
        // only redraw every 2 s and only when the text actually changes.
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => OnTick();
        _timer.Start();

        // Background update check: 2 min after start, then every 6 h, only while the option is on.
        _icon.BalloonTipClicked += (_, _) => { if (_balloonIsUpdate) CheckForUpdates(); };
        _icon.BalloonTipClosed += (_, _) => _balloonIsUpdate = false;
        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
        _updateTimer.Tick += async (_, _) =>
        {
            _updateTimer.Interval = TimeSpan.FromHours(6);
            await BackgroundUpdateCheck();
        };
        _updateTimer.Start();
    }

    readonly DispatcherTimer _updateTimer;
    bool _balloonIsUpdate;

    /// <summary>Quiet check: a notification only when a newer release exists and hasn't been announced yet.</summary>
    async System.Threading.Tasks.Task BackgroundUpdateCheck()
    {
        if (!_settings.AutoUpdateCheck || _updating) return;
        try
        {
            var release = await Updater.CheckAsync();
            if (release is null || release.Version <= Updater.Current) return;
            string v = release.Version.ToString();
            if (_settings.AnnouncedVersion == v) return;
            _settings.AnnouncedVersion = v;
            _settings.Save();
            _balloonIsUpdate = true;
            _icon.ShowBalloonTip(15_000, $"Vigil {v} is available",
                $"You have {Updater.Current}. Click to update; Vigil restarts by itself.", Forms.ToolTipIcon.Info);
        }
        catch (Exception) { } // offline or rate-limited: try again next round, never bother the user
    }

    public bool AutoUpdateCheck => _settings.AutoUpdateCheck;

    public void ToggleAutoUpdate()
    {
        _settings.AutoUpdateCheck = !_settings.AutoUpdateCheck;
        _settings.Save();
    }

    void OnTick()
    {
        _stats.Sample();
        // ponytail: battery state from the 1 s sample; app list re-checked every 5 s.
        _awake.Tick(pluggedIn: _stats.Battery.PluggedIn || !_stats.Battery.Present, checkApps: _tick % 5 == 0);
        foreach (var (title, text) in _alerts.Check(_stats.Battery, _stats.Disks, _stats.Cpu, _stats.RamFraction))
        {
            _balloonIsUpdate = false; // clicking an alert must not start an update
            _icon.ShowBalloonTip(10_000, title, text, Forms.ToolTipIcon.Warning); // Windows 10/11 shows it as a toast
        }
        if (_tick++ % 2 == 0)
        {
            RenderIcon();
            UpdateTooltip();
        }
        if (_panel?.IsVisible == true) _panel.Refresh();
    }

    void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        // SystemEvents may raise this on its own thread; KeepAwake must be touched on the UI thread.
        if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.SessionUnlock)
            _ui.BeginInvoke(() => _awake.Locked = e.Reason == SessionSwitchReason.SessionLock);
    }

    DispatcherTimer? _trim;

    void OnPanelHidden()
    {
        _panelHiddenAt = DateTime.UtcNow;
        // A few seconds after the panel closes, give its memory back to Windows (unless it reopened).
        _trim ??= new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Background, (_, _) =>
        {
            _trim!.Stop();
            if (_panel?.IsVisible == true) return;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            Native.SetProcessWorkingSetSize(Native.GetCurrentProcess(), new IntPtr(-1), new IntPtr(-1));
        }, _ui);
        _trim.Stop();
        _trim.Start();
    }

    internal void ShowPanelForTest()
    {
        if (_panel is null)
        {
            _panel = new PanelWindow(_stats, _awake, _quit, this);
            _panel.IsVisibleChanged += (_, e) => { if (!(bool)e.NewValue) OnPanelHidden(); };
        }
        _panel.ShowAt(new System.Drawing.Point(600, 500));
    }

    internal void HidePanelForTest() => _panel?.Hide();

    void TogglePanel()
    {
        // Clicking the tray icon first deactivates (and hides) an open panel; don't reopen it right away.
        if ((DateTime.UtcNow - _panelHiddenAt).TotalMilliseconds < 300) return;
        if (_panel?.IsVisible == true) { _panel.Hide(); return; }

        if (_panel is null)
        {
            _panel = new PanelWindow(_stats, _awake, _quit, this);
            _panel.IsVisibleChanged += (_, e) => { if (!(bool)e.NewValue) OnPanelHidden(); };
        }
        _stats.RefreshDisks();
        _stats.RefreshDevices();
        _stats.RefreshHealth();
        _panel.ShowAt(Forms.Cursor.Position);
    }

    void RenderIcon()
    {
        int cpu = (int)Math.Round(_stats.Cpu * 100);
        bool light = Theme.TaskbarLight;
        int? shown = _settings.ShowCpuInTray ? cpu : null;
        string key = $"{shown}|{_awake.Holding}|{light}";
        if (key == _iconKey) return;
        _iconKey = key;

        int size = Forms.SystemInformation.SmallIconSize.Width; // 16 at 100 %, 24 at 150 %
        using var bmp = DrawIcon(shown, _awake.Holding, light, size);
        IntPtr hicon = bmp.GetHicon();
        var old = _icon.Icon;
        IntPtr oldHandle = _hicon;
        _icon.Icon = Icon.FromHandle(hicon);
        _hicon = hicon;
        // Icon.FromHandle doesn't own the HICON: destroy it ourselves or GDI handles leak every redraw.
        old?.Dispose();
        if (oldHandle != IntPtr.Zero) Native.DestroyIcon(oldHandle);
    }

    public void ToggleCpuInTray()
    {
        _settings.ShowCpuInTray = !_settings.ShowCpuInTray;
        _settings.Save();
        RenderIcon();
    }

    public bool ShowCpuInTray => _settings.ShowCpuInTray;

    internal Settings Settings => _settings;

    bool _updating;

    /// <summary>Only network call in the app, and only on click.</summary>
    public async void CheckForUpdates()
    {
        if (_updating) return;
        _updating = true;
        _panel?.Hide();
        var current = Updater.Current;
        Release? release = null;
        try
        {
            _icon.Text = "Vigil: checking for updates…";
            release = await Updater.CheckAsync();
            if (release is null || release.Version <= current)
            {
                Ask($"Vigil {current} is up to date.", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Information);
                return;
            }
            var answer = Ask($"Vigil {release.Version} is available (you have {current}).\n\n" +
                             $"Download it ({release.Size / 1048576.0:0.#} MB) and restart now?",
                             Forms.MessageBoxButtons.YesNo, Forms.MessageBoxIcon.Information);
            if (answer != Forms.DialogResult.Yes) return;

            _icon.Text = $"Vigil: downloading {release.Version}…";
            await Updater.InstallAsync(release);
            _quit(); // the new exe is starting and waits for this one to exit
        }
        catch (Exception e)
        {
            var answer = Ask($"Couldn't update: {e.Message}\n\nOpen the download page instead?",
                             Forms.MessageBoxButtons.YesNo, Forms.MessageBoxIcon.Warning);
            if (answer == Forms.DialogResult.Yes) Updater.OpenReleasesPage();
        }
        finally
        {
            _updating = false;
            UpdateTooltip();
        }
    }

    static Forms.DialogResult Ask(string text, Forms.MessageBoxButtons buttons, Forms.MessageBoxIcon icon) =>
        // DefaultDesktopOnly keeps the box in front: a tray app has no window to own it.
        Forms.MessageBox.Show(text, "Vigil", buttons, icon, Forms.MessageBoxDefaultButton.Button1,
                              Forms.MessageBoxOptions.DefaultDesktopOnly);

    /// <summary>
    /// Tray icon: the app icon (cpu = null) or CPU % as a number. While keep-awake holds, a dot:
    /// green with a taskbar-coloured ring on the app icon, blue top-right on the number.
    /// </summary>
    public static Bitmap DrawIcon(int? cpu, bool awake, bool lightTaskbar, int size)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit; // ClearType fringes on a transparent icon
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        if (cpu is null)
        {
            using (var stream = typeof(Tray).Assembly.GetManifestResourceStream("Vigil.ico")!)
            using (var appIcon = new Icon(stream, size, size)) // picks the closest size from the .ico
            using (var art = appIcon.ToBitmap())
                g.DrawImage(art, 0, 0, size, size);

            if (awake)
            {
                float d = size * 0.5f, ring = Math.Max(1.5f, size / 12f);
                using var bg = new SolidBrush(lightTaskbar ? Color.FromArgb(0xF3, 0xF3, 0xF3) : Color.FromArgb(0x1C, 0x1C, 0x1C));
                using var dot = new SolidBrush(Color.FromArgb(0x22, 0xC5, 0x5E));
                g.FillEllipse(bg, size - d - ring / 2, size - d - ring / 2, d + ring, d + ring);
                g.FillEllipse(dot, size - d + ring / 2, size - d + ring / 2, d - ring, d - ring);
            }
            return bmp;
        }

        // ponytail: 100 % shows as 99 so the number always fits in two digits.
        string text = Math.Min(cpu.Value, 99).ToString();
        float px = size * (text.Length == 1 ? 0.9f : 0.78f);
        using var font = new Font("Segoe UI", px, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(lightTaskbar ? Color.Black : Color.White);
        using var fmt = (StringFormat)StringFormat.GenericTypographic.Clone();
        fmt.Alignment = StringAlignment.Center;
        fmt.LineAlignment = StringAlignment.Center;
        g.DrawString(text, font, brush, new RectangleF(0, 0, size, size + 1), fmt);

        if (awake)
        {
            float d = size * 0.34f;
            using var dot = new SolidBrush(Color.FromArgb(0x3B, 0x82, 0xF6));
            g.FillEllipse(dot, size - d, 0, d, d);
        }
        return bmp;
    }

    void UpdateTooltip()
    {
        var s = _stats;
        string text = $"CPU {s.Cpu * 100:0}% · RAM {Fmt.Gb(s.RamUsed)}/{s.RamTotal / 1073741824.0:0} GB · ↓ {Fmt.Rate(s.Down)} ↑ {Fmt.Rate(s.Up)}";
        if (s.Battery.Present) text += $" · 🔋 {s.Battery.Percent * 100:0}%";
        if (_awake.Holding) text += " · awake";
        if (text.Length > 127) text = text[..127]; // NotifyIcon limit
        if (_icon.Text != text) _icon.Text = text;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _updateTimer.Stop();
        SystemEvents.SessionSwitch -= OnSessionSwitch; // static event: unsubscribe or it leaks
        // Back to normal power rules. Direct call, not _awake.Stop(): that would fire Changed and
        // could save a "stopped" state over the user's options during shutdown.
        Native.SetThreadExecutionState(Native.ES_CONTINUOUS);
        _panel?.Close();
        // Hide + dispose removes the icon at once instead of leaving a "ghost" until hovered.
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        if (_hicon != IntPtr.Zero) Native.DestroyIcon(_hicon);
    }
}
