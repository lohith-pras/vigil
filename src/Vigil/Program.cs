using System;
using System.Drawing;
using System.Drawing.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace Vigil;

static class Program
{
    [STAThread]
    static void Main()
    {
        // One copy only: a second launch just exits.
        using var mutex = new Mutex(true, @"Local\Vigil.SingleInstance", out bool first);
        if (!first) return;

        // No main window: WPF runs until Quit calls Shutdown().
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        using var tray = new Tray(app.Shutdown);
        // On a crash, still remove the tray icon and drop keep-awake before the process dies.
        app.DispatcherUnhandledException += (_, _) => tray.Dispose();
        app.Run();
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
    readonly Action _quit;

    PanelWindow? _panel;
    DateTime _panelHiddenAt;
    IntPtr _hicon;
    string _iconKey = "";
    int _tick;
    bool _disposed;

    public Tray(Action quit)
    {
        _quit = quit;
        _awake.DisplayOn = _settings.KeepDisplayOn;
        Startup.FixPath();

        _menu = new Forms.ContextMenuStrip();
        var startItem = new Forms.ToolStripMenuItem("Start with Windows", null, (_, _) => Startup.Enabled = !Startup.Enabled);
        var awakeItem = new Forms.ToolStripMenuItem("Keep awake", null, (_, _) =>
        {
            if (_awake.Active) _awake.Stop(); else _awake.Start(null);
        });
        _menu.Items.Add(startItem);
        _menu.Items.Add(awakeItem);
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add("Quit", null, (_, _) => _quit());
        _menu.Opening += (_, _) =>
        {
            startItem.Checked = Startup.Enabled;
            awakeItem.Checked = _awake.Active;
        };

        _icon = new Forms.NotifyIcon { ContextMenuStrip = _menu, Text = "Vigil" };
        _icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) TogglePanel(); };

        _awake.Changed += () =>
        {
            if (_settings.KeepDisplayOn != _awake.DisplayOn)
            {
                _settings.KeepDisplayOn = _awake.DisplayOn;
                _settings.Save();
            }
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
    }

    void OnTick()
    {
        _stats.Sample();
        _awake.Tick();
        if (_tick++ % 2 == 0)
        {
            RenderIcon();
            UpdateTooltip();
        }
        if (_panel?.IsVisible == true) _panel.Refresh();
    }

    void TogglePanel()
    {
        // Clicking the tray icon first deactivates (and hides) an open panel; don't reopen it right away.
        if ((DateTime.UtcNow - _panelHiddenAt).TotalMilliseconds < 300) return;
        if (_panel?.IsVisible == true) { _panel.Hide(); return; }

        if (_panel is null)
        {
            _panel = new PanelWindow(_stats, _awake, _quit);
            _panel.IsVisibleChanged += (_, e) => { if (!(bool)e.NewValue) _panelHiddenAt = DateTime.UtcNow; };
        }
        _stats.RefreshDisks();
        _panel.ShowAt(Forms.Cursor.Position);
    }

    void RenderIcon()
    {
        int cpu = (int)Math.Round(_stats.Cpu * 100);
        bool light = Theme.TaskbarLight;
        string key = $"{cpu}|{_awake.Active}|{light}";
        if (key == _iconKey) return;
        _iconKey = key;

        int size = Forms.SystemInformation.SmallIconSize.Width; // 16 at 100 %, 24 at 150 %
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit; // ClearType fringes on a transparent icon
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            // ponytail: 100 % shows as 99 so the number always fits in two digits.
            string text = Math.Min(cpu, 99).ToString();
            float px = size * (text.Length == 1 ? 0.9f : 0.78f);
            using var font = new Font("Segoe UI", px, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(light ? Color.Black : Color.White);
            using var fmt = (StringFormat)StringFormat.GenericTypographic.Clone();
            fmt.Alignment = StringAlignment.Center;
            fmt.LineAlignment = StringAlignment.Center;
            g.DrawString(text, font, brush, new RectangleF(0, 0, size, size + 1), fmt);

            if (_awake.Active)
            {
                // Keep-awake indicator: a blue dot in the top-right corner.
                float d = size * 0.34f;
                using var dot = new SolidBrush(Color.FromArgb(0x3B, 0x82, 0xF6));
                g.FillEllipse(dot, size - d, 0, d, d);
            }
        }

        IntPtr hicon = bmp.GetHicon();
        var old = _icon.Icon;
        IntPtr oldHandle = _hicon;
        _icon.Icon = Icon.FromHandle(hicon);
        _hicon = hicon;
        // Icon.FromHandle doesn't own the HICON: destroy it ourselves or GDI handles leak every redraw.
        old?.Dispose();
        if (oldHandle != IntPtr.Zero) Native.DestroyIcon(oldHandle);
    }

    void UpdateTooltip()
    {
        var s = _stats;
        string text = $"CPU {s.Cpu * 100:0}% · RAM {Fmt.Gb(s.RamUsed)}/{s.RamTotal / 1073741824.0:0} GB · ↓ {Fmt.Rate(s.Down)} ↑ {Fmt.Rate(s.Up)}";
        if (s.Battery.Present) text += $" · 🔋 {s.Battery.Percent * 100:0}%";
        if (_awake.Active) text += " · awake";
        if (text.Length > 127) text = text[..127]; // NotifyIcon limit
        if (_icon.Text != text) _icon.Text = text;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        if (_awake.Active) _awake.Stop();
        _panel?.Close();
        // Hide + dispose removes the icon at once instead of leaving a "ghost" until hovered.
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        if (_hicon != IntPtr.Zero) Native.DestroyIcon(_hicon);
    }
}
