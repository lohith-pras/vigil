using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows.Automation;
using System.Windows.Controls.Primitives;
using System.Linq;
using System.Windows.Media.Animation;
using System.Windows.Documents;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace Vigil;

public partial class PanelWindow : Window
{
    readonly Stats _stats;
    readonly KeepAwake _awake;
    readonly Action _quit;
    IntPtr _hwnd;
    IReadOnlyList<DiskInfo>? _shownDisks;
    IReadOnlyList<DeviceBattery>? _shownDevices;
    bool _closing;

    /// <summary>Self-test only: force light (true) or dark (false) instead of following Windows.</summary>
    internal bool? ForceLight { get; set; }

    readonly Tray? _tray;

    internal PanelWindow(Stats stats, KeepAwake awake, Action quit, Tray? tray = null)
    {
        _tray = tray;
        _stats = stats;
        _awake = awake;
        _quit = quit;
        InitializeComponent();
        // After InitializeComponent: XAML loads <Window.Resources> as deferred content, which throws
        // ("Set property ResourceDictionary.DeferrableContent") if the dictionary already has entries.
        ApplyTheme();

        if (tray is not null) AlertsBlock.Bind(tray.Settings, tray.Settings.Save);
        SystemTab.IsChecked = true; // after InitializeComponent: Tab_Checked needs the pages to exist

        TabGrid.SizeChanged += (_, _) => MoveTabDot(animate: false);
        SourceInitialized += (_, _) => SetupWindow();
        Closing += (_, _) => _closing = true;
        // Click anywhere outside closes the panel. Not while closing: Hide() on a closing window throws.
        Deactivated += (_, _) => { if (!_closing && IsVisible) Hide(); };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Hide(); };
    }

    void SetupWindow()
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        // Tool window: keeps the panel out of Alt+Tab.
        long ex = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE).ToInt64();
        Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_TOOLWINDOW));
        // Windows 11 rounded corners (ignored on Windows 10).
        int round = Native.DWMWCP_ROUND;
        Native.DwmSetWindowAttribute(_hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        int dark = 1;
        Native.DwmSetWindowAttribute(_hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

        // Windows 11 22H2+: real acrylic behind the panel. The window paints transparent and DWM fills it
        // with blurred desktop; the panel's 80 % zinc layer sits on top. On Windows 10 (or if this fails)
        // the transparent areas render black, which under the same layer is simply a solid near-black.
        int acrylic = Native.DWMSBT_TRANSIENTWINDOW;
        if (Native.DwmSetWindowAttribute(_hwnd, Native.DWMWA_SYSTEMBACKDROP_TYPE, ref acrylic, sizeof(int)) == 0)
        {
            var margins = new Native.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            Native.DwmExtendFrameIntoClientArea(_hwnd, ref margins);
            if (HwndSource.FromHwnd(_hwnd)?.CompositionTarget is { } target) target.BackgroundColor = Colors.Transparent;
        }
    }

    /// <summary>Show next to the tray icon. cursor = click position in physical pixels.</summary>
    public void ShowAt(System.Drawing.Point cursor)
    {
        ApplyTheme();
        Refresh();
        Show();
        Activate();
        Position(cursor);
        PlayOpen();
    }

    // ---------- motion ----------

    static readonly KeySpline EaseOut = new(0.23, 1, 0.32, 1);   // entering
    static readonly KeySpline EaseInOut = new(0.77, 0, 0.175, 1); // moving on screen

    // "Show animations in Windows" off = reduced motion: fades only, nothing slides.
    static bool Motion => SystemParameters.ClientAreaAnimation;

    static DoubleAnimationUsingKeyFrames Spline(double to, double ms, KeySpline curve)
    {
        var a = new DoubleAnimationUsingKeyFrames();
        a.KeyFrames.Add(new SplineDoubleKeyFrame(to, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms)), curve));
        return a;
    }

    /// <summary>Open: 6 px rise + fade, 150 ms. Close stays instant (Hide).</summary>
    void PlayOpen()
    {
        var shift = (TranslateTransform)Shell.RenderTransform;
        Shell.Opacity = 0;
        Shell.BeginAnimation(OpacityProperty, Spline(1, 150, EaseOut));
        if (Motion)
        {
            shift.Y = 6;
            shift.BeginAnimation(TranslateTransform.YProperty, Spline(0, 150, EaseOut));
        }
    }

    int _tabIndex = 2;

    /// <summary>The cobalt dot under the active tab; slides 180 ms between tabs.</summary>
    void MoveTabDot(bool animate)
    {
        double w = TabGrid.ActualWidth;
        if (w <= 0) return;
        double x = w / 6 * _tabIndex + w / 12 - TabDot.Width / 2;
        var t = (TranslateTransform)TabDot.RenderTransform;
        if (animate && Motion && IsVisible) t.BeginAnimation(TranslateTransform.XProperty, Spline(x, 180, EaseInOut));
        else { t.BeginAnimation(TranslateTransform.XProperty, null); t.X = x; }
    }

    void Position(System.Drawing.Point cursor)
    {
        // 1) Move onto the clicked monitor first, so WPF rescales the window to that monitor's DPI.
        const uint flags = Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE;
        Native.SetWindowPos(_hwnd, IntPtr.Zero, cursor.X, cursor.Y, 0, 0, flags);

        // 2) Now the pixel size is right for that monitor: place it against the taskbar.
        Native.GetWindowRect(_hwnd, out var r);
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        int gap = (int)(12 * w / Width);

        var screen = Forms.Screen.FromPoint(cursor);
        var wa = screen.WorkingArea; // screen minus taskbar
        var b = screen.Bounds;
        int x, y;

        if (wa.Bottom < b.Bottom)      { x = cursor.X - w / 2; y = wa.Bottom - h - gap; } // taskbar bottom
        else if (wa.Top > b.Top)       { x = cursor.X - w / 2; y = wa.Top + gap; }        // top
        else if (wa.Left > b.Left)     { x = wa.Left + gap;    y = cursor.Y - h / 2; }    // left
        else if (wa.Right < b.Right)   { x = wa.Right - w - gap; y = cursor.Y - h / 2; }  // right
        else // auto-hide taskbar: open away from whichever half was clicked
        {
            x = cursor.X - w / 2;
            y = cursor.Y > b.Top + b.Height / 2 ? cursor.Y - h - gap : cursor.Y + gap;
        }

        x = Math.Max(wa.Left + gap, Math.Min(x, wa.Right - w - gap));
        y = Math.Max(wa.Top + gap, Math.Min(y, wa.Bottom - h - gap));
        Native.SetWindowPos(_hwnd, IntPtr.Zero, x, y, 0, 0, flags);
    }

    // ---------- refresh ----------

    public void Refresh()
    {
        var s = _stats;

        CpuText.Text = $"{s.Cpu * 100:0}%";
        CpuGraph.Update(s.CpuHistory);
        CpuPeak.Text = s.CpuHistory.Count > 0 ? $"peak {s.CpuHistory.Max() * 100:0}%" : "";

        bool high = s.RamFraction >= 0.8;
        RamText.Text = $"{s.RamFraction * 100:0}%";
        RamText.Foreground = (Brush)Resources[high ? "Warn" : "Text"];
        RamBar.Value = s.RamFraction * 100;
        RamBar.Foreground = (Brush)Resources[high ? "Warn" : "Text"];
        RamDetail.Text = $"{Fmt.Gb(s.RamUsed)} / {Fmt.Gb(s.RamTotal)} GB";
        RamLevel.Text = s.RamFraction >= 0.9 ? "very high" : high ? "high" : "";
        RamLevel.Foreground = (Brush)Resources["Warn"];

        DownText.Text = Fmt.Rate(s.Down);
        UpText.Text = Fmt.Rate(s.Up);
        SysDown.Text = Fmt.Rate(s.Down);
        SysBat.Text = s.Battery.Present ? $"{s.Battery.Percent * 100:0}%" : "—";

        // Header: what's holding the PC awake, else the two numbers people glance for.
        HeaderDot.Visibility = _awake.Holding ? Visibility.Visible : Visibility.Collapsed;
        HeaderStatus.Text = _awake.Holding
            ? "awake" + (_awake.Remaining is { } left ? $" · {Short(left)}" : "")
            : $"cpu {s.Cpu * 100:0}% · ram {s.RamFraction * 100:0}%";

        RefreshBattery(s.Battery);
        if (!ReferenceEquals(_shownDisks, s.Disks)) RebuildDisks(s.Disks);
        if (!ReferenceEquals(_shownDevices, s.Devices)) RebuildDevices(s.Devices);
        RefreshAwake();
        if (ProcTab.IsChecked == true && IsVisible) RefreshProcesses();
    }

    // ---------- processes ----------

    internal readonly ProcessSampler _procs = new();
    DateTime _procsAt = DateTime.MinValue;
    bool _byRam;
    string? _armed;         // app whose End button was clicked once
    DateTime _armedAt;

    void RefreshProcesses(bool force = false)
    {
        // Sample every 2 s, only while this tab is showing (see ProcessSampler).
        bool changed = force;
        if ((DateTime.UtcNow - _procsAt).TotalSeconds >= 2)
        {
            _procsAt = DateTime.UtcNow;
            _procs.Sample();
            changed = true;
        }
        if (_armed is not null && (DateTime.UtcNow - _armedAt).TotalSeconds > 3) { _armed = null; changed = true; }
        // Rebuild rows only when something changed, so a click never lands on a button that was just replaced.
        if (!changed) return;

        SortCpu.Style = (Style)FindResource(_byRam ? "Seg" : "SegOn");
        SortRam.Style = (Style)FindResource(_byRam ? "SegOn" : "Seg");

        ProcList.Children.Clear();
        foreach (var app in _procs.Top(_byRam, 8))
        {
            var row = new Grid { Height = 40 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });

            var name = new TextBlock
            {
                FontSize = 14,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = app.Name,
            };
            name.Inlines.Add(new Run(app.Name));
            if (app.Count > 1)
                name.Inlines.Add(new Run($" ×{app.Count}") { FontFamily = (FontFamily)Resources["Mono"], FontSize = 11, Foreground = (Brush)Resources["Faint"] });
            var cpu = new TextBlock
            {
                Text = _procs.Measured ? $"{app.Cpu * 100:0.0}%" : "…",
                Style = (Style)FindResource("Value"),
                Foreground = (Brush)Resources[_byRam ? "SubText" : "Text"],
            };
            var ram = new TextBlock
            {
                Text = app.Ram >= 1L << 30 ? $"{app.Ram / (double)(1L << 30):0.0} GB" : $"{app.Ram >> 20} MB",
                Style = (Style)FindResource("Value"),
                Foreground = (Brush)Resources[_byRam ? "Text" : "SubText"],
            };
            Grid.SetColumn(cpu, 1);
            Grid.SetColumn(ram, 2);
            row.Children.Add(name);
            row.Children.Add(cpu);
            row.Children.Add(ram);

            if (ProcessSampler.CanEnd(app.Name))
            {
                bool armed = _armed == app.Name;
                var end = new Button
                {
                    Style = (Style)FindResource(armed ? "MiniDanger" : "Mini"),
                    Content = armed ? "End?" : "End",
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Tag = app.Name,
                    ToolTip = armed ? $"Click again to end {app.Name}" : $"End {app.Name}",
                };
                AutomationProperties.SetName(end, $"End {app.Name}");
                end.Click += EndTask_Click;
                Grid.SetColumn(end, 3);
                row.Children.Add(end);
            }
            // Hairline above every row.
            ProcList.Children.Add(new Border { BorderBrush = (Brush)Resources["CardBorder"], BorderThickness = new Thickness(0, 1, 0, 0), Child = row });
        }
    }

    void Sort_Click(object sender, RoutedEventArgs e)
    {
        _byRam = sender == SortRam;
        RefreshProcesses(force: true);
    }

    void EndTask_Click(object sender, RoutedEventArgs e)
    {
        string name = (string)((Button)sender).Tag;
        if (_armed != name)
        {
            // Two-step: first click arms for 3 s, second click ends.
            _armed = name;
            _armedAt = DateTime.UtcNow;
            ProcStatus.Text = "";
        }
        else
        {
            _armed = null;
            ProcStatus.Text = ProcessSampler.End(name) ?? $"Ended {name}.";
            _procsAt = DateTime.MinValue; // re-sample now
        }
        RefreshProcesses(force: true);
    }

    void RefreshBattery(BatteryInfo b)
    {
        BatBar.Value = b.Present ? b.Percent * 100 : 0;
        BatPct.Text = b.Present ? $"{b.Percent * 100:0}%" : "—";

        (string text, string color) = !b.Present ? ("No battery", "Faint")
            : b.Charging ? ("Charging", "Good")
            : b.PluggedIn ? ("Plugged in", "Good")
            : b.Percent < 0.2 ? ("On battery", "Danger")
            : ("On battery", "Warn");
        BatState.Text = text;
        var brush = (SolidColorBrush)Resources[color];
        BatDot.Fill = brush;
        BatPill.Background = new SolidColorBrush(Color.FromArgb(0x24, brush.Color.R, brush.Color.G, brush.Color.B));
        BatState.Foreground = brush;

        BatTime.Text = b.Present && !b.PluggedIn && b.SecondsLeft > 0
            ? $"{Fmt.Duration(TimeSpan.FromSeconds(b.SecondsLeft))} left"
            : "";
        var health = b.Present ? _stats.Health : null;
        BatHealth.Text = health?.Text ?? "";
        BatHealth.Visibility = health is null ? Visibility.Collapsed : Visibility.Visible;
    }

    void RebuildDisks(IReadOnlyList<DiskInfo> disks)
    {
        _shownDisks = disks;
        DiskList.Children.Clear();
        foreach (var d in disks)
        {
            bool full = d.UsedFraction > 0.85;
            var danger = (Brush)Resources["Danger"];

            var header = new Grid();
            var title = new TextBlock { FontSize = 15, FontFamily = (FontFamily)Resources["SansMedium"], FontWeight = FontWeights.Medium };
            title.Inlines.Add(new Run(d.Name));
            if (!string.IsNullOrWhiteSpace(d.Label))
                title.Inlines.Add(new Run("  " + d.Label) { FontFamily = (FontFamily)Resources["Sans"], FontWeight = FontWeights.Normal, Foreground = (Brush)Resources["SubText"] });
            header.Children.Add(title);
            header.Children.Add(new TextBlock
            {
                Text = $"{d.UsedFraction * 100:0}% used",
                Style = (Style)FindResource("Value"),
                Foreground = full ? danger : (Brush)Resources["Text"],
            });

            var bar = new ProgressBar { Style = (Style)FindResource("Bar"), Value = d.UsedFraction * 100, Margin = new Thickness(0, 10, 0, 8) };
            if (full) bar.Foreground = danger;

            var detail = new TextBlock
            {
                Text = $"{Fmt.Size(d.Free)} free of {Fmt.Size(d.Total)}",
                Style = (Style)FindResource("Axis"),
            };

            var block = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };
            block.Children.Add(header);
            block.Children.Add(bar);
            block.Children.Add(detail);
            DiskList.Children.Add(block);
        }
        if (disks.Count == 0)
            DiskList.Children.Add(new TextBlock { Text = "No fixed drives found", Style = (Style)FindResource("Muted") });
    }

    string _shownApps = "\0";

    void RebuildDevices(IReadOnlyList<DeviceBattery> devices)
    {
        _shownDevices = devices;
        DeviceList.Children.Clear();
        foreach (var d in devices)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var name = new TextBlock { Text = d.Name, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, ToolTip = d.Name };
            var bar = new ProgressBar { Style = (Style)FindResource("Bar"), Value = d.Percent, Height = 4, Margin = new Thickness(10, 0, 10, 0) };
            bool low = d.Percent <= 20;
            if (low) bar.Foreground = (Brush)Resources["Danger"];
            var pct = new TextBlock { Text = $"{d.Percent}%", Style = (Style)FindResource("Value"), MinWidth = 40, Foreground = (Brush)Resources[low ? "Danger" : "Text"] };
            Grid.SetColumn(bar, 1);
            Grid.SetColumn(pct, 2);
            row.Children.Add(name);
            row.Children.Add(bar);
            row.Children.Add(pct);
            DeviceList.Children.Add(row);
        }
        if (devices.Count == 0)
            DeviceList.Children.Add(new TextBlock
            {
                Text = "No Bluetooth devices report battery",
                Style = (Style)FindResource("Muted"),
            });
    }

    void RefreshAwake()
    {
        var a = _awake;
        AwakeSwitch.IsChecked = a.Active;
        DisplaySwitch.IsChecked = a.DisplayOn;
        PluggedSwitch.IsChecked = a.OnlyWhenPluggedIn;
        LockedSwitch.IsChecked = a.PauseWhenLocked;
        StartupSwitch.IsChecked = a.OnAtStartup;

        // Big reading: the countdown when timed, otherwise the state in one word.
        AwakeBig.Text =
            a.PausedReason is not null ? "Paused"
            : !a.Wanted ? "Off"
            : a.Remaining is { } left ? Clock(left)
            : "On";
        AwakeBig.Foreground = (Brush)Resources[a.Holding ? "Text" : "SubText"];
        AwakeStatus.Text =
            a.PausedReason is { } why ? why
            : !a.Wanted ? "PC follows its normal power settings"
            : !a.Active ? $"while {a.RunningApp} is open"
            : a.UntilTime is { } t ? $"left · until {t:HH:mm}"
            : a.Remaining is not null ? "left"
            : "until you turn it off";

        // Remaining time as a cobalt bar over hatch; only for timed sessions.
        if (a.Active && a.Remaining is { } rem && a.Duration is { } total && total > TimeSpan.Zero)
        {
            AwakeBar.Visibility = Visibility.Visible;
            AwakeBar.Value = Math.Clamp(rem / total * 100, 0, 100);
        }
        else AwakeBar.Visibility = Visibility.Collapsed;

        foreach (Button chip in Chips.Children)
        {
            bool on = (string)chip.Tag == "until"
                ? a.Active && a.UntilTime is not null
                : a.Active && a.UntilTime is null && ChipDuration(chip) == a.Duration;
            chip.Style = (Style)FindResource(on ? "ChipOn" : "Chip");
        }

        string apps = string.Join("|", a.WatchApps);
        if (apps != _shownApps) RebuildAppList(apps);
    }

    void RebuildAppList(string key)
    {
        _shownApps = key;
        AppList.Children.Clear();
        foreach (var name in _awake.WatchApps)
        {
            var chip = new Button
            {
                Style = (Style)FindResource("Chip"),
                Content = $"{name}  ✕",
                Tag = name,
                Padding = new Thickness(12, 0, 12, 0),
                ToolTip = $"Remove {name}",
            };
            AutomationProperties.SetName(chip, $"Remove {name}");
            chip.Click += (_, _) => { _awake.WatchApps.Remove(name); _awake.Touch(); };
            AppList.Children.Add(chip);
        }
        var add = new Button { Style = (Style)FindResource("Chip"), Content = "+ Add app", MinWidth = 90 };
        add.Click += AddApp_Click;
        AppList.Children.Add(add);
    }

    void AddApp_Click(object sender, RoutedEventArgs e)
    {
        // Apps with a window right now, by process name (what Windows matches on).
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.MainWindowHandle != IntPtr.Zero && p.Id != Environment.ProcessId) names.Add(p.ProcessName);
            }
            catch (InvalidOperationException) { } // exited meanwhile
            finally { p.Dispose(); }
        }
        names.ExceptWith(_awake.WatchApps);

        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Bottom };
        foreach (var name in names)
        {
            var item = new MenuItem { Header = name };
            item.Click += (_, _) => { _awake.WatchApps.Add(name); _awake.Touch(); };
            menu.Items.Add(item);
        }
        if (names.Count == 0) menu.Items.Add(new MenuItem { Header = "No other apps open", IsEnabled = false });
        menu.IsOpen = true;
    }

    static TimeSpan? ChipDuration(Button chip)
    {
        int minutes = int.Parse((string)chip.Tag);
        return minutes == 0 ? null : TimeSpan.FromMinutes(minutes);
    }

    // ---------- events ----------

    void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (DiskPage is null) return; // fires during InitializeComponent, before the pages exist
        var tab = (RadioButton)sender;
        _tabIndex = int.Parse((string)tab.Tag);
        MoveTabDot(animate: true);
        AwakePage.Visibility = tab == AwakeTab ? Visibility.Visible : Visibility.Collapsed;
        DisplaysPage.Visibility = tab == DisplaysTab ? Visibility.Visible : Visibility.Collapsed;
        SystemPage.Visibility = tab == SystemTab ? Visibility.Visible : Visibility.Collapsed;
        NetPage.Visibility = tab == NetTab ? Visibility.Visible : Visibility.Collapsed;
        ProcPage.Visibility = tab == ProcTab ? Visibility.Visible : Visibility.Collapsed;
        if (tab == ProcTab) { _procs.Reset(); _procsAt = DateTime.MinValue; RefreshProcesses(); }
        DiskPage.Visibility = tab == DiskTab ? Visibility.Visible : Visibility.Collapsed;
    }

    void Chip_Click(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        if ((string)button.Tag == "until")
        {
            if (_awake.Active && _awake.UntilTime is not null) { _awake.Stop(); return; }
            bool show = UntilRow.Visibility != Visibility.Visible;
            UntilRow.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (show)
            {
                // Suggest the next half hour at least an hour away.
                var t = DateTime.Now.AddHours(1);
                t = t.Date.AddHours(t.Hour).AddMinutes(t.Minute < 30 ? 30 : 60);
                UntilBox.Text = t.ToString("HH:mm");
                UntilError.Text = "";
                UntilBox.Focus();
                UntilBox.SelectAll();
            }
            return;
        }
        var duration = ChipDuration(button);
        if (_awake.Active && _awake.Duration == duration) _awake.Stop(); // same chip again = stop
        else _awake.Start(duration);
    }

    void AwakeSwitch_Click(object sender, RoutedEventArgs e)
    {
        if (AwakeSwitch.IsChecked == true) _awake.Start(null);
        else _awake.Stop();
    }

    void UntilStart_Click(object sender, RoutedEventArgs e)
    {
        if (TimeSpan.TryParse(UntilBox.Text.Trim(), out var time) && time >= TimeSpan.Zero && time < TimeSpan.FromDays(1))
        {
            UntilError.Text = "";
            UntilRow.Visibility = Visibility.Collapsed;
            _awake.StartUntil(time);
        }
        else UntilError.Text = "Use HH:mm, e.g. 18:30";
    }

    void Option_Click(object sender, RoutedEventArgs e)
    {
        var box = (CheckBox)sender;
        bool on = box.IsChecked == true;
        switch ((string)box.Tag)
        {
            case "display": _awake.DisplayOn = on; break;
            case "plugged": _awake.OnlyWhenPluggedIn = on; break;
            case "locked": _awake.PauseWhenLocked = on; break;
            case "startup": _awake.OnAtStartup = on; _awake.Touch(); break;
        }
    }

    void OptionsToggle_Click(object sender, RoutedEventArgs e)
    {
        bool show = OptionsPanel.Visibility != Visibility.Visible;
        OptionsPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        OptionsToggle.Content = (show ? "⌄  " : "›  ") + "Options";
    }

    void Settings_Click(object sender, RoutedEventArgs e)
    {
        var startup = new MenuItem { Header = "Start with Windows", IsCheckable = true, IsChecked = Startup.Enabled };
        startup.Click += (_, _) => Startup.Enabled = startup.IsChecked;
        var menu = new ContextMenu
        {
            PlacementTarget = SettingsButton,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Top,
        };
        menu.Items.Add(startup);
        if (_tray is { } tray)
        {
            var cpu = new MenuItem { Header = "Show CPU % in tray icon", IsCheckable = true, IsChecked = tray.ShowCpuInTray };
            cpu.Click += (_, _) => tray.ToggleCpuInTray();
            menu.Items.Add(cpu);
            var update = new MenuItem { Header = "Check for updates…" };
            update.Click += (_, _) => tray.CheckForUpdates();
            menu.Items.Add(update);
            var auto = new MenuItem { Header = "Check for updates automatically", IsCheckable = true, IsChecked = tray.AutoUpdateCheck };
            auto.Click += (_, _) => tray.ToggleAutoUpdate();
            menu.Items.Add(auto);
            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = $"Vigil {Updater.Current}", IsEnabled = false });
        }
        menu.IsOpen = true;
    }

    void Quit_Click(object sender, RoutedEventArgs e) => _quit();

    // ---------- theme ----------

    // ponytail: one dark palette (the design is dark-native); ForceLight is kept so old callers compile.
    static readonly Dictionary<string, string> Palette = new()
    {
        ["Bg"] = "#CC09090B",         // panel: zinc-950 at 80 % over acrylic
        ["Card"] = "#08FFFFFF",       // faint wells (speed test box)
        ["CardBorder"] = "#12FFFFFF", // hairlines
        ["Text"] = "#FAFAFA",
        ["SubText"] = "#A1A1AA",
        ["Faint"] = "#8B8B94",        // labels, help, axis
        ["Accent"] = "#4C7DFF",       // now / here / on, nothing else
        ["Track"] = "#1FFFFFFF",
        ["Hover"] = "#0AFFFFFF",
        ["TabSel"] = "#00000000",
        ["ChipBg"] = "#00000000",
        ["Teal"] = "#2DD4BF",
        ["Good"] = "#3DD68C",
        ["Warn"] = "#F5B54A",
        ["Danger"] = "#F2555A",
    };

    void ApplyTheme()
    {
        if (Resources.Contains("Bg")) return; // static palette: set once
        foreach (var (key, hex) in Palette)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            Resources[key] = brush;
        }
    }

    static string Clock(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";

    static string Short(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:00}m" : $"{Math.Max(1, (int)Math.Ceiling(t.TotalMinutes))}m";
}
