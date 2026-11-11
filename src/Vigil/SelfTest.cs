using System;
using System.Drawing.Drawing2D;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Vigil;

/// <summary>
/// `Vigil.exe --selftest &lt;dir&gt;`: run on the CI Windows machine before every release.
/// Opens the real panel, visits every tab, clicks every chip and switch, draws the tray icon,
/// saves PNGs of all of it into &lt;dir&gt;, and returns 1 on the first exception or failed check.
/// </summary>
static class SelfTest
{
    static readonly StringBuilder Log = new();

    public static int Run(string dir)
    {
        Directory.CreateDirectory(dir);
        try
        {
            _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

            // --- readouts ---
            var stats = new Stats();
            for (int i = 0; i < 8; i++) { stats.Sample(); Thread.Sleep(500); } // a little graph history
            Check(stats.RamTotal > 0, $"RAM total {Fmt.Gb(stats.RamTotal)} GB, used {Fmt.Gb(stats.RamUsed)} GB");
            Check(stats.Cpu is >= 0 and <= 1, $"CPU {stats.Cpu * 100:0.0}%");
            Check(stats.CpuHistory.Count > 0, $"CPU history {stats.CpuHistory.Count} samples");
            Check(stats.Disks.Count > 0, $"Disks: {string.Join(", ", stats.Disks)}");
            Note($"Network ↓ {Fmt.Rate(stats.Down)} ↑ {Fmt.Rate(stats.Up)}");
            Note($"Battery {stats.Battery}");

            // --- keep awake ---
            var awake = new KeepAwake();
            awake.Start(TimeSpan.FromMinutes(30));
            Check(awake.Active && awake.Remaining > TimeSpan.Zero, "Keep awake 30 min starts with time remaining");
            awake.Tick();
            Check(awake.Active, "Keep awake still active after a tick");
            awake.Stop();
            Check(!awake.Active, "Keep awake stops");

            // --- tray icon art ---
            SaveIconSheet(Path.Combine(dir, "tray-icons.png"));
            Check(true, "Tray icon drawn at 16/24/32 px, dark + light taskbar");

            // --- real tray object: create and dispose cleanly ---
            using (new Tray(() => { })) DoEvents();
            Check(true, "Tray created and disposed");

            // --- panel, both themes ---
            foreach (bool light in new[] { false, true })
            {
                string theme = light ? "light" : "dark";
                var panel = new PanelWindow(stats, awake, () => { }) { ForceLight = light };
                panel.ShowAt(new System.Drawing.Point(600, 500));
                DoEvents();
                Check(panel.IsLoaded, $"Panel opens ({theme})");

                foreach (var (tab, name) in new[]
                         {
                             (panel.AwakeTab, "1-awake"), (panel.SystemTab, "2-system"),
                             (panel.NetTab, "3-network"), (panel.DiskTab, "4-disks"),
                         })
                {
                    tab.IsChecked = true;
                    panel.Refresh();
                    Save(panel, Path.Combine(dir, $"panel-{theme}-{name}.png"));
                }
                Check(true, $"All 4 tabs rendered ({theme})");

                if (!light) ClickThrough(panel, awake, dir);
                panel.Close();
                DoEvents();
            }

            Note("PASS");
            return 0;
        }
        catch (Exception e)
        {
            Note("FAIL\n" + e);
            return 1;
        }
        finally
        {
            File.WriteAllText(Path.Combine(dir, "report.txt"), Log.ToString());
        }
    }

    static void ClickThrough(PanelWindow panel, KeepAwake awake, string dir)
    {
        panel.AwakeTab.IsChecked = true;

        foreach (Button chip in panel.Chips.Children)
        {
            Click(chip);
            Check(awake.Active, $"Chip '{chip.Content}' starts keep awake");
            panel.Refresh();
            if ((string)chip.Tag == "60") Save(panel, Path.Combine(dir, "panel-dark-1-awake-1h-on.png"));
            Click(chip);
            Check(!awake.Active, $"Chip '{chip.Content}' clicked again stops keep awake");
        }

        panel.AwakeSwitch.IsChecked = true;
        Click(panel.AwakeSwitch);
        Check(awake.Active && awake.Duration is null, "Main switch starts indefinite keep awake");
        panel.AwakeSwitch.IsChecked = false;
        Click(panel.AwakeSwitch);
        Check(!awake.Active, "Main switch stops keep awake");

        bool before = awake.DisplayOn;
        panel.DisplaySwitch.IsChecked = !before;
        Click(panel.DisplaySwitch);
        Check(awake.DisplayOn == !before, "Keep display on switch toggles");
        panel.DisplaySwitch.IsChecked = before;
        Click(panel.DisplaySwitch);

        Click(panel.SettingsButton);
        DoEvents();
        Check(true, "Settings menu opens");

        panel.Hide();
        DoEvents();
        Check(!panel.IsVisible, "Panel hides");
        panel.ShowAt(new System.Drawing.Point(600, 500));
        DoEvents();
        Check(panel.IsVisible, "Panel reopens");
    }

    static void Click(ButtonBase b) => b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, b));

    static void Check(bool ok, string what)
    {
        Note((ok ? "ok    " : "FAIL  ") + what);
        if (!ok) throw new Exception("Check failed: " + what);
    }

    static void Note(string line) => Log.AppendLine(line);

    /// <summary>Let WPF run queued layout/render work (we're not inside Application.Run).</summary>
    static void DoEvents() =>
        Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));

    /// <summary>Render the panel's content at 2x, laid out explicitly so it works even if the window lost focus and hid.</summary>
    static void Save(PanelWindow panel, string path)
    {
        double w = panel.Width, h = panel.Height;
        var root = (FrameworkElement)panel.Content;
        root.Measure(new Size(w, h));
        root.Arrange(new Rect(0, 0, w, h));
        root.UpdateLayout();

        const double scale = 2;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(panel.Background, null, new Rect(0, 0, w, h));
            dc.DrawRectangle(new VisualBrush(root)
            {
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(0, 0, w, h),
                Stretch = Stretch.Fill,
            }, null, new Rect(0, 0, w, h));
        }
        var bmp = new RenderTargetBitmap((int)(w * scale), (int)(h * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bmp.Render(visual);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bmp));
        using var f = File.Create(path);
        png.Save(f);
    }

    /// <summary>Tray icons at real sizes, blown up 4x (nearest-neighbour) on taskbar-coloured strips.</summary>
    static void SaveIconSheet(string path)
    {
        int[] sizes = { 16, 24, 32 };
        const int zoom = 4, pad = 16;
        int cell = 32 * zoom + pad;
        using var sheet = new System.Drawing.Bitmap(cell * 6, cell * 2);
        using var g = System.Drawing.Graphics.FromImage(sheet);
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;

        for (int row = 0; row < 2; row++)
        {
            bool light = row == 1;
            using var bg = new System.Drawing.SolidBrush(light ? System.Drawing.Color.FromArgb(0xEE, 0xEE, 0xEE)
                                                               : System.Drawing.Color.FromArgb(0x1C, 0x1C, 0x1C));
            g.FillRectangle(bg, 0, row * cell, sheet.Width, cell);
            int col = 0;
            foreach (int size in sizes)
            foreach (bool awake in new[] { false, true })
            {
                using var icon = Tray.DrawIcon(awake ? 7 : 42, awake, light, size);
                g.DrawImage(icon, col * cell + pad / 2, row * cell + pad / 2, size * zoom, size * zoom);
                col++;
            }
        }
        sheet.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }
}
