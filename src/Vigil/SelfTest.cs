using System;
using System.Collections.Generic;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
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
            var devices = BluetoothBatteries.Read();
            Check(true, $"Bluetooth battery query ran: {devices.Count} device(s) {string.Join(", ", devices)}");

            // --- battery health (CI has no battery: the query must just not crash) ---
            stats.RefreshHealth();
            Check(true, $"Battery health query ran: {stats.Health?.Text ?? "no battery / not reported"}");
            Check(BatteryHealth.FromInfo(0, 50_000, 45_000, 312)?.Text == "Health 90% · 312 cycles"
                  && BatteryHealth.FromInfo(0, 50_000, 45_000, 0)?.Text == "Health 90%"
                  && BatteryHealth.FromInfo(0, 50_000, 45_000, 0xFFFFFFFF)?.Cycles == 0
                  && BatteryHealth.FromInfo(0x40000000, 100, 100, 5) is null
                  && BatteryHealth.FromInfo(0, 0, 45_000, 5) is null
                  && BatteryHealth.FromInfo(0, 50_000, 0xFFFFFFFF, 5) is null, "Battery health: values, relative/unknown hidden");

            AlertChecks();
            Check(System.Text.Json.JsonSerializer.Deserialize<Settings>("{}")!.AutoUpdateCheck, "Automatic update check is on by default (old settings.json too)");

            // --- processes ---
            var sampler = new ProcessSampler();
            sampler.Sample();
            Thread.Sleep(1000);
            sampler.Sample();
            var top = sampler.Top(false, 5).ToList();
            Check(sampler.Measured && top.Count == 5, $"Process sampler: {sampler.Apps.Count} apps, top CPU {string.Join(", ", top.Select(a => $"{a.Name} {a.Cpu * 100:0.0}%"))}");
            Check(!ProcessSampler.CanEnd("csrss") && !ProcessSampler.CanEnd(System.Diagnostics.Process.GetCurrentProcess().ProcessName),
                  "System processes and Vigil itself can't be ended");
            var victim = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ping", "-n 60 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false })!;
            Thread.Sleep(300);
            string? endError = ProcessSampler.End("PING");
            Check(endError is null && victim.WaitForExit(5000), "End task ends a process");

            // --- keep awake ---
            var awake = new KeepAwake();
            awake.Start(TimeSpan.FromMinutes(30));
            Check(awake.Active && awake.Remaining > TimeSpan.Zero, "Keep awake 30 min starts with time remaining");
            awake.Tick(pluggedIn: true, checkApps: true);
            Check(awake.Active && awake.Holding, "Keep awake still holding after a tick");
            awake.Stop();
            Check(!awake.Active && !awake.Holding, "Keep awake stops");

            awake.StartUntil(DateTime.Now.AddHours(2).TimeOfDay);
            Check(awake.UntilTime is not null && awake.Remaining > TimeSpan.FromHours(1.9), "Until… starts with ~2 h left");
            awake.StartUntil(DateTime.Now.AddMinutes(-5).TimeOfDay);
            Check(awake.Remaining > TimeSpan.FromHours(23), "Until… with a past time means tomorrow");
            awake.Stop();

            awake.OnlyWhenPluggedIn = true;
            awake.Start(null);
            awake.Tick(pluggedIn: false, checkApps: false);
            Check(awake.Wanted && !awake.Holding && awake.PausedReason == "on battery", "Only when plugged in: pauses on battery");
            awake.Tick(pluggedIn: true, checkApps: false);
            Check(awake.Holding, "Only when plugged in: resumes when plugged in");
            awake.OnlyWhenPluggedIn = false;

            awake.PauseWhenLocked = true;
            awake.Locked = true;
            Check(!awake.Holding && awake.PausedReason == "PC is locked", "Pause when locked: pauses");
            awake.Locked = false;
            Check(awake.Holding, "Pause when locked: resumes on unlock");
            awake.PauseWhenLocked = false;
            awake.Stop();

            string self = System.Diagnostics.Process.GetCurrentProcess().ProcessName;
            awake.WatchApps.Add(self);
            awake.Tick(pluggedIn: true, checkApps: true);
            Check(!awake.Active && awake.Holding && awake.RunningApp == self, $"Watched app '{self}' running holds awake");
            awake.WatchApps.Clear();
            awake.Tick(pluggedIn: true, checkApps: true);
            Check(!awake.Holding, "Watch list empty: released");

            // --- updater: parsing + a real (non-fatal) check against GitHub ---
            using (var doc = System.Text.Json.JsonDocument.Parse("""
                {"tag_name":"v9.8.7","html_url":"https://example/r","assets":[
                  {"name":"Vigil.exe","browser_download_url":"https://example/a","size":123,"digest":"sha256:ABCD"},
                  {"name":"Vigil-standalone.exe","browser_download_url":"https://example/b","size":456}]}
                """))
            {
                var small = Updater.Parse(doc.RootElement, "Vigil.exe");
                var big = Updater.Parse(doc.RootElement, "Vigil-standalone.exe");
                Check(small is { Size: 123, Sha256: "ABCD" } && small.Version == new Version(9, 8, 7)
                      && big is { AssetUrl: "https://example/b", Sha256: null }, "Update release parsing");
            }
            try
            {
                var latest = Updater.CheckAsync().GetAwaiter().GetResult();
                Note($"ok    Update check: running {Updater.Current}, latest {latest?.Version} ({latest?.AssetName}, sha256 {(latest?.Sha256 is null ? "missing" : "present")})");
            }
            catch (Exception e) { Note($"warn  Update check failed (network/rate limit?): {e.Message}"); }

            // --- speed test: one tiny real run (non-fatal, like the update check) ---
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var tiny = new SpeedTest.Plan(3, new[] { 100_000L, 1_000_000L }, new[] { 100_000L, 500_000L }, 5);
                var r = SpeedTest.RunAsync(tiny, null, cts.Token).GetAwaiter().GetResult();
                Note($"ok    Speed test (tiny): ↓ {r.DownMbps:0.0} Mbps ↑ {r.UpMbps:0.0} Mbps, ping {r.PingMs:0} ms");
                if (r.DownMbps <= 0 || r.UpMbps <= 0 || r.PingMs <= 0) Note("warn  Speed test returned a zero value");
            }
            catch (Exception e) { Note($"warn  Speed test failed (network?): {e.GetBaseException().Message}"); }
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1));
                SpeedTest.RunAsync(SpeedTest.Full, null, cts.Token).GetAwaiter().GetResult();
                Check(false, "Speed test cancels");
            }
            catch (OperationCanceledException) { Check(true, "Speed test cancels"); }
            Check(SpeedTest.Full.MaxBytes is > 30_000_000 and < 100_000_000, $"Speed test data cap {SpeedTest.Full.MaxBytes / 1_000_000} MB");

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
                foreach (var (key, weight) in new[] { ("Sans", FontWeights.Normal), ("SansMedium", FontWeights.Medium), ("SansSemi", FontWeights.SemiBold), ("Mono", FontWeights.Normal), ("MonoMedium", FontWeights.Medium) })
                {
                    var family = (FontFamily)panel.Resources[key];
                    bool ok = new Typeface(family, FontStyles.Normal, weight, FontStretches.Normal).TryGetGlyphTypeface(out var glyphs);
                    string got = ok ? string.Join("/", glyphs.FamilyNames.Values) + " " + glyphs.Weight : "none";
                    Check(ok && got.StartsWith("Geist"), $"Embedded font {key} resolves: {got}");
                }

                foreach (var (tab, name) in new[]
                         {
                             (panel.AwakeTab, "1-awake"), (panel.SystemTab, "2-system"),
                             (panel.ProcTab, "3-processes"), (panel.NetTab, "4-network"), (panel.DiskTab, "5-disks"),
                         })
                {
                    tab.IsChecked = true;
                    panel.Refresh();
                    Save(panel, Path.Combine(dir, $"panel-{theme}-{name}.png"));
                }
                Check(true, $"All tabs rendered ({theme})");

                if (!light)
                {
                    TestDisplays(panel, dir);

                    // No Bluetooth on the CI machine: render fake rows once to check the layout.
                    var real = stats.Devices;
                    stats.Devices = new[] { new DeviceBattery("WH-1000XM4", 76), new DeviceBattery("MX Master 3S", 15) };
                    panel.NetTab.IsChecked = true;
                    panel.Refresh();
                    Check(panel.DeviceList.Children.Count == 2, "Bluetooth device rows render");
                    Save(panel, Path.Combine(dir, "panel-dark-4-network-devices.png"));

                    // Speed test result, battery health and the alerts block, on a tall render of the whole tab.
                    var realHealth = stats.Health;
                    stats.Health = new BatteryHealth(50_000, 43_100, 312);
                    var realBattery = stats.Battery;
                    stats.Battery = new BatteryInfo(true, 0.76, false, false, 9000);
                    panel.SpeedTestBlock.ShowResult(new SpeedTest.Result(14, 245.3, 38.7));
                    var alertSettings = new Settings();
                    panel.AlertsBlock.Bind(alertSettings, () => { });
                    Click(panel.AlertsBlock.Toggle);
                    panel.AlertsBlock.ChargedSwitch.IsChecked = true;
                    Click(panel.AlertsBlock.ChargedSwitch);
                    Click((Button)panel.AlertsBlock.Limits.Children[0]);
                    panel.AlertsBlock.BusySwitch.IsChecked = false;
                    Click(panel.AlertsBlock.BusySwitch);
                    Check(alertSettings is { AlertCharged: true, ChargeLimit: 80, AlertBusy: false, AlertBatteryLow: true, AlertDiskFull: true },
                          "Alerts switches and charge limit set settings");
                    panel.Refresh();
                    Check(panel.BatHealth.Visibility == Visibility.Visible && panel.BatHealth.Text == "Health 86% · 312 cycles",
                          $"Battery health shows ('{panel.BatHealth.Text}')");
                    double h = panel.Height;
                    panel.Height = 1180;
                    Save(panel, Path.Combine(dir, "panel-dark-4-network-full.png"));
                    panel.Height = h;
                    stats.Health = realHealth;
                    stats.Battery = realBattery;

                    panel.ProcTab.IsChecked = true;
                    panel.Refresh();
                    Thread.Sleep(2100);
                    panel.Refresh();
                    Check(panel.ProcList.Children.Count > 0, "Processes tab lists apps");
                    var firstEnd = panel.ProcList.Children.OfType<Grid>().SelectMany(g => g.Children.OfType<Button>()).FirstOrDefault();
                    if (firstEnd is not null)
                    {
                        Click(firstEnd); // arm only; never actually end a CI process from the UI
                        Check(panel.ProcList.Children.OfType<Grid>().SelectMany(g => g.Children.OfType<Button>()).Any(b => (string)b.Content == "End?"),
                              "End button arms on first click");
                    }
                    Click(panel.SortRam);
                    Save(panel, Path.Combine(dir, "panel-dark-3-processes-ram.png"));
                    stats.Devices = real;
                    panel.Refresh();
                    ClickThrough(panel, awake, dir);
                }
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

    /// <summary>Read-only: never changes mode, refresh rate, brightness or screen power on the CI machine.</summary>
    static void TestDisplays(PanelWindow panel, string dir)
    {
        var page = panel.DisplaysPage;
        if (!panel.IsVisible) panel.ShowAt(new System.Drawing.Point(600, 500)); // the page only loads while shown
        panel.DisplaysTab.IsChecked = true;
        WaitFor(() => page.IsVisible, 5);
        WaitFor(() => !page.Loading, 20);
        var screens = page.Screens;
        Check(!page.Loading && screens is not null, $"Displays tab loads off the UI thread: {screens?.Count} screen(s), mode {Displays.CurrentMode()}");
        foreach (var s in screens!) Note($"      {s}");
        Check(screens.Count > 0, "At least one display found");
        Save(panel, Path.Combine(dir, "panel-dark-2-displays.png"));
        if (screens.Any(s => s.Controls.Count == 0))
            Check(Texts(page).Contains("This monitor doesn't support control"), "Unsupported monitor message renders");

        panel.SystemTab.IsChecked = true;
        WaitFor(() => screens.All(s => s.Closed), 5);
        Check(screens.All(s => s.Closed), "Monitor handles released when the tab closes");

        // Layout with made-up hardware: DDC monitor with all three sliders, a silent monitor, a separate laptop card.
        panel.DisplaysTab.IsChecked = true;
        WaitFor(() => page.IsVisible, 5);
        WaitFor(() => !page.Loading, 20);
        Displays.Release(page.Screens);
        var ddc = new DisplayScreen { Title = "Display 1 · main", Device = "fake", Primary = true, Width = 2560, Height = 1440, Hz = 144, Rates = new[] { 60, 120, 144 } };
        ddc.Controls.Add(new DisplayControl("Brightness", 70, 100, ddc, IntPtr.Zero, 0x10));
        ddc.Controls.Add(new DisplayControl("Contrast", 50, 100, ddc, IntPtr.Zero, 0x12));
        ddc.Controls.Add(new DisplayControl("Volume", 30, 100, ddc, IntPtr.Zero, 0x62));
        var silent = new DisplayScreen { Title = "Display 2", Device = "fake", Width = 1920, Height = 1080, Hz = 60, Rates = new[] { 60 } };
        var laptop = new DisplayScreen { Title = "Built-in display" };
        laptop.Controls.Add(new DisplayControl("Brightness", 40, 100, laptop, IntPtr.Zero, 0));
        foreach (var f in new[] { ddc, silent, laptop }) f.Closed = true; // fake: any stray Set() is a no-op
        page.Show(new List<DisplayScreen> { ddc, silent, laptop }, Displays.ModeExtend);
        panel.Refresh();
        var texts = Texts(page).ToList();
        Check(texts.Contains("This monitor doesn't support control") && texts.Contains("144 Hz") && texts.Contains("Volume")
              && Descendants(page).OfType<Slider>().Count() == 4,
              "Display blocks render sliders, refresh-rate chips and the unsupported state");
        Save(panel, Path.Combine(dir, "panel-dark-2-displays-layout.png"));
        panel.SystemTab.IsChecked = true;
    }

    static void WaitFor(Func<bool> done, int seconds)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!done() && DateTime.UtcNow < until) { Thread.Sleep(50); DoEvents(); }
    }

    static IEnumerable<DependencyObject> Descendants(DependencyObject o)
    {
        foreach (var c in LogicalTreeHelper.GetChildren(o).OfType<DependencyObject>())
        {
            yield return c;
            foreach (var d in Descendants(c)) yield return d;
        }
    }

    static IEnumerable<string> Texts(DependencyObject o) =>
        Descendants(o).OfType<TextBlock>().Select(t => t.Text).Concat(Descendants(o).OfType<ContentControl>().Select(c => c.Content as string ?? ""));


    /// <summary>Alert rules as pure checks: fake readings in, fire once, re-arm only after the condition clears.</summary>
    static void AlertChecks()
    {
        var defaults = System.Text.Json.JsonSerializer.Deserialize<Settings>("{}")!;
        Check(defaults is { AlertBatteryLow: true, AlertCharged: false, ChargeLimit: 100, AlertDiskFull: true, AlertBusy: true },
              "Alert defaults (old settings.json): all on except battery charged");

        var cfg = new Settings();
        var alerts = new Alerts(cfg);
        var ok = new[] { new DiskInfo("C:", "", 50L << 30, 100L << 30) };
        var full = new[] { new DiskInfo("C:", "", 5L << 30, 100L << 30) };
        var none = new BatteryInfo(false, 0, true, false, -1);
        static BatteryInfo Bat(double pct, bool plugged) => new(true, pct, plugged, plugged, -1);
        string Fire(BatteryInfo b, DiskInfo[] d, double cpu = 0.1, double ram = 0.1) =>
            string.Join(" + ", alerts.Check(b, d, cpu, ram).Select(a => a.Title));

        Check(Fire(Bat(0.5, false), ok) == "", "Alerts: quiet at normal readings");
        Check(Fire(Bat(0.2, false), ok) == "Battery low", "Battery low fires at 20%");
        Check(Fire(Bat(0.19, false), ok) == "", "Battery low fires only once");
        Check(Fire(Bat(0.1, false), ok) == "Battery very low", "Battery very low fires at 10%");
        Check(Fire(Bat(0.09, true), ok) == "", "Plugged in: no low alert; charged alert off by default");
        Check(Fire(Bat(0.15, false), ok) == "Battery low", "Battery low re-arms after plugging in");
        Check(Fire(Bat(0.08, false), ok) == "Battery very low", "Battery very low re-arms too");

        cfg.AlertCharged = true;
        cfg.ChargeLimit = 80;
        Check(Fire(Bat(0.79, true), ok) == "", "Charged: not below the limit");
        Check(Fire(Bat(0.8, true), ok) == "Battery charged", "Charged fires at the 80% limit while plugged in");
        Check(Fire(Bat(0.85, true), ok) == "", "Charged fires only once");
        Check(Fire(Bat(0.85, false), ok) == "" && Fire(Bat(0.85, true), ok) == "Battery charged", "Charged re-arms after unplugging");
        cfg.AlertCharged = false;

        Check(Fire(none, full) == "Drive C: almost full", "Drive above 90% fires");
        Check(Fire(none, full) == "", "Drive alert fires only once");
        Check(Fire(none, ok) == "" && Fire(none, full) == "Drive C: almost full", "Drive alert re-arms after space is freed");
        cfg.AlertDiskFull = false;
        Check(Fire(none, ok) == "" && Fire(none, full) == "", "Drive alert off: silent");

        string busy = "";
        for (int i = 0; i < Alerts.BusySeconds - 1; i++) busy += Fire(none, ok, cpu: 0.95);
        Check(busy == "", "CPU busy: silent before 2 minutes");
        Check(Fire(none, ok, cpu: 0.95) == "CPU busy", "CPU busy fires after 2 minutes above 90%");
        Check(Fire(none, ok, cpu: 0.95) == "", "CPU busy fires only once");
        Fire(none, ok, cpu: 0.5);
        for (int i = 0; i < Alerts.BusySeconds - 1; i++) busy += Fire(none, ok, cpu: 0.95, ram: 0.95);
        Check(busy == "" && Fire(none, ok, cpu: 0.95, ram: 0.95) == "CPU busy + Memory almost full",
              "CPU re-arms after dropping; RAM fires after 2 minutes too");
        cfg.AlertBusy = false;
        Fire(none, ok);
        for (int i = 0; i < Alerts.BusySeconds + 5; i++) busy += Fire(none, ok, cpu: 0.95, ram: 0.95);
        Check(busy == "", "Busy alert off: silent");
    }

    static void ClickThrough(PanelWindow panel, KeepAwake awake, string dir)
    {
        panel.AwakeTab.IsChecked = true;

        foreach (Button chip in panel.Chips.Children)
        {
            if ((string)chip.Tag == "until") continue; // tested below
            Click(chip);
            Check(awake.Active, $"Chip '{chip.Content}' starts keep awake");
            panel.Refresh();
            if ((string)chip.Tag == "60") Save(panel, Path.Combine(dir, "panel-dark-1-awake-1h-on.png"));
            Click(chip);
            Check(!awake.Active, $"Chip '{chip.Content}' clicked again stops keep awake");
        }

        var until = (Button)panel.Chips.Children[^1];
        Click(until);
        Check(panel.UntilRow.Visibility == Visibility.Visible && panel.UntilBox.Text.Length == 5, $"Until… shows time box ('{panel.UntilBox.Text}')");
        panel.UntilBox.Text = "nonsense";
        Click(panel.UntilStart);
        Check(!awake.Active && panel.UntilError.Text.Length > 0, "Until… rejects a bad time");
        panel.UntilBox.Text = DateTime.Now.AddHours(3).ToString("HH:mm");
        Click(panel.UntilStart);
        Check(awake.Active && awake.UntilTime is not null, "Until… starts");
        panel.Refresh();
        Save(panel, Path.Combine(dir, "panel-dark-1-awake-until.png"));
        Click(until);
        Check(!awake.Active, "Until chip clicked again stops");

        Click(panel.OptionsToggle);
        foreach (var sw in new[] { panel.PluggedSwitch, panel.LockedSwitch, panel.StartupSwitch })
        {
            sw.IsChecked = true;
            Click(sw);
        }
        Check(awake.OnlyWhenPluggedIn && awake.PauseWhenLocked && awake.OnAtStartup, "Option switches set options");
        awake.WatchApps.Add("obs64");
        awake.Touch();
        panel.Refresh();
        Check(panel.AppList.Children.Count == 2, "App list shows watched app + Add button");
        Save(panel, Path.Combine(dir, "panel-dark-1-awake-options.png"));
        Click((Button)panel.AppList.Children[^1]); // "+ Add app" menu
        DoEvents();
        Click((Button)panel.AppList.Children[0]);  // remove obs64
        panel.Refresh();
        Check(awake.WatchApps.Count == 0 && panel.AppList.Children.Count == 1, "Removing watched app");
        foreach (var sw in new[] { panel.PluggedSwitch, panel.LockedSwitch, panel.StartupSwitch })
        {
            sw.IsChecked = false;
            Click(sw);
        }
        Click(panel.OptionsToggle);

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
            // The panel is transparent (acrylic shows through on Windows 11): paint a dark desktop behind it.
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x1E)), null, new Rect(0, 0, w, h));
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
        using var sheet = new System.Drawing.Bitmap(cell * 6, cell * 4);
        using var g = System.Drawing.Graphics.FromImage(sheet);
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;

        // Rows: app icon dark/light taskbar, then CPU-number mode dark/light.
        for (int row = 0; row < 4; row++)
        {
            bool light = row % 2 == 1, number = row >= 2;
            using var bg = new System.Drawing.SolidBrush(light ? System.Drawing.Color.FromArgb(0xEE, 0xEE, 0xEE)
                                                               : System.Drawing.Color.FromArgb(0x1C, 0x1C, 0x1C));
            g.FillRectangle(bg, 0, row * cell, sheet.Width, cell);
            int col = 0;
            foreach (int size in sizes)
            foreach (bool awake in new[] { false, true })
            {
                using var icon = Tray.DrawIcon(number ? (awake ? 7 : 42) : null, awake, light, size);
                g.DrawImage(icon, col * cell + pad / 2, row * cell + pad / 2, size * zoom, size * zoom);
                col++;
            }
        }
        sheet.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }
}
