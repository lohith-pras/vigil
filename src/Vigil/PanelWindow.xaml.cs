using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows.Automation;
using System.Windows.Controls.Primitives;
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

    internal PanelWindow(Stats stats, KeepAwake awake, Action quit)
    {
        _stats = stats;
        _awake = awake;
        _quit = quit;
        InitializeComponent();
        // After InitializeComponent: XAML loads <Window.Resources> as deferred content, which throws
        // ("Set property ResourceDictionary.DeferrableContent") if the dictionary already has entries.
        ApplyTheme();

        NetGraph.Label = max => Fmt.Rate(max);
        SystemTab.IsChecked = true; // after InitializeComponent: Tab_Checked needs the pages to exist

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
        ApplyDwmTheme();
    }

    /// <summary>Show next to the tray icon. cursor = click position in physical pixels.</summary>
    public void ShowAt(System.Drawing.Point cursor)
    {
        ApplyTheme();
        Refresh();
        Show();
        Activate();
        Position(cursor);
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

        CpuBar.Value = s.Cpu * 100;
        CpuText.Text = $"{s.Cpu * 100:0}%";
        CpuGraph.Update(s.CpuHistory);

        RamBar.Value = s.RamFraction * 100;
        RamText.Text = $"{Fmt.Gb(s.RamUsed)} / {Fmt.Gb(s.RamTotal)} GB · {s.RamFraction * 100:0}%";
        RamGraph.Update(s.RamHistory);

        DownText.Text = Fmt.Rate(s.Down);
        UpText.Text = Fmt.Rate(s.Up);
        NetGraph.Update(s.DownHistory);

        RefreshBattery(s.Battery);
        if (!ReferenceEquals(_shownDisks, s.Disks)) RebuildDisks(s.Disks);
        if (!ReferenceEquals(_shownDevices, s.Devices)) RebuildDevices(s.Devices);
        RefreshAwake();
    }

    void RefreshBattery(BatteryInfo b)
    {
        BatBar.Value = b.Present ? b.Percent * 100 : 0;
        BatPct.Text = b.Present ? $"{b.Percent * 100:0}%" : "—";

        (string text, string color) = !b.Present ? ("No battery", "SubText")
            : b.Charging ? ("Charging", "Good")
            : b.PluggedIn ? ("Plugged in", "Good")
            : b.Percent < 0.2 ? ("On battery", "Danger")
            : ("On battery", "Warn");
        BatState.Text = text;
        var brush = (SolidColorBrush)Resources[color];
        BatDot.Fill = brush;
        BatPill.Background = new SolidColorBrush(Color.FromArgb(0x33, brush.Color.R, brush.Color.G, brush.Color.B));

        BatTime.Text = b.Present && !b.PluggedIn && b.SecondsLeft > 0
            ? $"{Fmt.Duration(TimeSpan.FromSeconds(b.SecondsLeft))} left"
            : "";
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
            header.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(d.Label) ? d.Name : $"{d.Name}  {d.Label}",
                FontWeight = FontWeights.SemiBold,
            });
            header.Children.Add(new TextBlock
            {
                Text = $"{d.UsedFraction * 100:0}% used",
                Style = (Style)FindResource("Value"),
                Foreground = full ? danger : (Brush)Resources["Text"],
            });

            var bar = new ProgressBar { Style = (Style)FindResource("Bar"), Value = d.UsedFraction * 100, Margin = new Thickness(0, 6, 0, 4) };
            if (full) bar.Foreground = danger;

            var detail = new TextBlock
            {
                Text = $"{Fmt.Size(d.Free)} free of {Fmt.Size(d.Total)}",
                Style = (Style)FindResource("Muted"),
            };

            var block = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
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
            var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var name = new TextBlock { Text = d.Name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, ToolTip = d.Name };
            var bar = new ProgressBar { Style = (Style)FindResource("Bar"), Value = d.Percent };
            if (d.Percent <= 20) bar.Foreground = (Brush)Resources["Danger"];
            var pct = new TextBlock { Text = $"{d.Percent}%", Style = (Style)FindResource("Value"), MinWidth = 36 };
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

        AwakeStatus.Text =
            a.PausedReason is { } why ? $"Keep awake paused: {why}"
            : !a.Wanted ? "PC follows its normal power settings"
            : !a.Active ? $"Keeping PC awake while {a.RunningApp} is open"
            : a.UntilTime is { } t ? $"Keeping PC awake until {t:HH:mm} · {Fmt.Duration(a.Remaining ?? TimeSpan.Zero)} left"
            : a.Remaining is { } r ? $"Keeping PC awake · {Fmt.Duration(r)} left"
            : "Keeping PC awake until you turn it off";

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
        if (SectionTitle is null) return; // fires during InitializeComponent
        var tab = (RadioButton)sender;
        SectionTitle.Text = ((string)tab.Tag).ToUpperInvariant();
        AwakePage.Visibility = tab == AwakeTab ? Visibility.Visible : Visibility.Collapsed;
        SystemPage.Visibility = tab == SystemTab ? Visibility.Visible : Visibility.Collapsed;
        NetPage.Visibility = tab == NetTab ? Visibility.Visible : Visibility.Collapsed;
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
        menu.IsOpen = true;
    }

    void Quit_Click(object sender, RoutedEventArgs e) => _quit();

    // ---------- theme ----------

    bool _light;

    void ApplyTheme()
    {
        _light = ForceLight ?? Theme.AppsLight;
        var palette = _light
            ? new Dictionary<string, string>
            {
                ["Bg"] = "#F3F3F3", ["Card"] = "#FFFFFF", ["CardBorder"] = "#E3E3E3", ["Text"] = "#1B1B1B",
                ["SubText"] = "#6E6E6E", ["Track"] = "#E3E3E3", ["Hover"] = "#ECECEC", ["TabSel"] = "#DCE8FD",
                ["ChipBg"] = "#EDEDED",
            }
            : new Dictionary<string, string>
            {
                ["Bg"] = "#1E1E1E", ["Card"] = "#2A2A2A", ["CardBorder"] = "#3A3A3A", ["Text"] = "#F2F2F2",
                ["SubText"] = "#9B9B9B", ["Track"] = "#3D3D3D", ["Hover"] = "#343434", ["TabSel"] = "#24385A",
                ["ChipBg"] = "#383838",
            };
        palette["Accent"] = "#3B82F6";
        palette["Teal"] = "#2DD4BF";
        palette["Good"] = "#22C55E";
        palette["Warn"] = "#EAB308";
        palette["Danger"] = "#EF4444";

        foreach (var (key, hex) in palette)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            Resources[key] = brush;
        }
        _shownDisks = null; // rebuild rows with the new colours
        _shownDevices = null;
        if (_hwnd != IntPtr.Zero) ApplyDwmTheme();
    }

    void ApplyDwmTheme()
    {
        int dark = _light ? 0 : 1;
        Native.DwmSetWindowAttribute(_hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
    }
}
