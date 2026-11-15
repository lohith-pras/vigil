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
        IsVisibleChanged += (_, _) => SetAmbient();
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

        // ponytail: no acrylic. An opaque panel looks the same on every desktop; acrylic under an 80 % layer
        // turned grey over bright wallpapers or with transparency effects off.
    }

    /// <summary>Show next to the tray icon. cursor = click position in physical pixels.</summary>
    public void ShowAt(System.Drawing.Point cursor)
    {
        // The "Until…" row belongs to one opening: a time suggested hours ago would now mean tomorrow.
        UntilRow.Visibility = Visibility.Collapsed;
        UntilError.Text = "";
        Refresh();
        Show();
        Activate();
        Position(cursor);
        PlayOpen();
    }

    // ---------- motion ----------

    static readonly KeySpline InOut = Freeze(new KeySpline(0.42, 0, 0.58, 1)); // CSS ease-in-out: blinks and loops
    static readonly IEasingFunction InOutEase = Freeze(new SineEase { EasingMode = EasingMode.EaseInOut });

    static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

    /// <summary>Open: 5 px rise + fade, 400 ms; then the page's sections rise in. Close stays instant (Hide).</summary>
    void PlayOpen()
    {
        var shift = (TranslateTransform)Shell.RenderTransform;
        Motion.FromTo(Shell, OpacityProperty, 0, 1, 400, fade: true);
        Motion.FromTo(shift, TranslateTransform.YProperty, 5, 0, 400);
        RevealSections(ActivePage());
    }

    FrameworkElement ActivePage() => _tabIndex switch
    {
        0 => AwakePage,
        1 => DisplaysPage,
        2 => SystemPage,
        3 => ProcPage,
        4 => NetPage,
        _ => DiskPage,
    };

    /// <summary>The element's TranslateTransform, adding one when it has no transform; null when it has another kind.</summary>
    static TranslateTransform? Shift(UIElement e)
    {
        if (e.RenderTransform is TranslateTransform t) return t;
        if (e.RenderTransform is not null && e.RenderTransform != Transform.Identity) return null;
        e.RenderTransform = t = new TranslateTransform();
        return t;
    }

    /// <summary>Each visible direct child of the page rises 4 px and fades in, 700 ms, 70 ms apart.</summary>
    static void RevealSections(FrameworkElement page)
    {
        var root = page is UserControl { Content: Panel inner } ? inner : page as Panel;
        if (root is null) return;
        int i = 0;
        foreach (UIElement child in root.Children)
        {
            if (child.Visibility != Visibility.Visible) continue;
            double delay = 70 * i++;
            Motion.FromTo(child, OpacityProperty, 0, 1, 700, delay, fade: true);
            if (Shift(child) is { } t) Motion.FromTo(t, TranslateTransform.YProperty, 4, 0, 700, delay);
        }
    }

    /// <summary>A newly chosen page fades in and slides 5 px from the side it came from (500 ms), sections staggering.</summary>
    static void ShowPage(FrameworkElement page, double fromX)
    {
        Motion.FromTo(page, OpacityProperty, 0, 1, 500, fade: true);
        if (Shift(page) is { } t) Motion.FromTo(t, TranslateTransform.XProperty, fromX, 0, 500);
        RevealSections(page);
    }

    int _tabIndex = 2;

    /// <summary>The cobalt dot under the active tab; travels 550 ms, stretching to 2.4x wide halfway.</summary>
    void MoveTabDot(bool animate)
    {
        double w = TabGrid.ActualWidth;
        if (w <= 0) return;
        double x = w / 6 * _tabIndex + w / 12 - TabDot.Width / 2;
        if (animate && Motion.Enabled && IsVisible)
        {
            TabDotShift.BeginAnimation(TranslateTransform.XProperty, Motion.To(x, 550));
            var stretch = new DoubleAnimationUsingKeyFrames();
            stretch.KeyFrames.Add(new SplineDoubleKeyFrame(2.4, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(275)), Motion.Soft));
            stretch.KeyFrames.Add(new SplineDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(550)), Motion.Soft));
            TabDotScale.BeginAnimation(ScaleTransform.ScaleXProperty, stretch);
        }
        else
        {
            TabDotShift.BeginAnimation(TranslateTransform.XProperty, null);
            TabDotShift.X = x;
            TabDotScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        }
    }

    // Ambient loops: what is running now (0 / false / null = stopped). Only SetAmbient changes these.
    double _blinkMs;
    bool _pulsing, _colonPulsing;
    AnimationClock? _downClock, _upClock;

    /// <summary>The one place that decides which repeating animations run: only while the panel shows and motion is
    /// on, and each only while its element is on screen. Starts or stops a loop only when its state changes.</summary>
    void SetAmbient()
    {
        bool on = IsVisible && Motion.Enabled;

        double blink = !on ? 0 : _awake.Holding ? 6000 : 10000;
        if (blink != _blinkMs)
        {
            _blinkMs = blink;
            EyeScale.BeginAnimation(ScaleTransform.ScaleYProperty, blink > 0 ? Blink(blink) : null);
        }

        bool pulse = on && HeaderDot.Visibility == Visibility.Visible;
        if (pulse != _pulsing)
        {
            _pulsing = pulse;
            HeaderPulseScale.BeginAnimation(ScaleTransform.ScaleXProperty, pulse ? Breathe(1, 2.6, 2000) : null);
            HeaderPulseScale.BeginAnimation(ScaleTransform.ScaleYProperty, pulse ? Breathe(1, 2.6, 2000) : null);
            HeaderPulse.BeginAnimation(OpacityProperty, pulse ? Breathe(0.28, 0, 2000) : null);
        }

        bool colon = on && _tabIndex == 0 && AwakeClock.Visibility == Visibility.Visible;
        if (colon != _colonPulsing)
        {
            _colonPulsing = colon;
            AwakeColon.BeginAnimation(OpacityProperty, colon ? Breathe(1, 0.45, 1000) : null);
        }

        bool net = on && _tabIndex == 4;
        _downClock = Lane(DownLane, DownFlow, _downClock, net ? _stats.Down : 0, 4e6, +1);
        _upClock = Lane(UpLane, UpFlow, _upClock, net ? _stats.Up : 0, 4e5, -1);
    }

    /// <summary>ScaleY 1 → .15 → 1 at the end of each period (CSS keyframes 93 % / 96.5 % / 100 %).</summary>
    static DoubleAnimationUsingKeyFrames Blink(double periodMs)
    {
        var a = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(periodMs), RepeatBehavior = RepeatBehavior.Forever };
        a.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0.93)));
        a.KeyFrames.Add(new SplineDoubleKeyFrame(0.15, KeyTime.FromPercent(0.965), InOut));
        a.KeyFrames.Add(new SplineDoubleKeyFrame(1, KeyTime.FromPercent(1), InOut));
        return a;
    }

    /// <summary>from → to → from forever, half a cycle each way, ease-in-out.</summary>
    static DoubleAnimation Breathe(double rest, double peak, double halfMs) =>
        new(rest, peak, TimeSpan.FromMilliseconds(halfMs)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = InOutEase };

    const double LanePitch = 26.0 / 3; // a dot travels 26 px (-4 → 22) per pass; three dots per pass

    /// <summary>Traffic dots next to a rate: one linear loop that slides the dot column one pitch; its speed follows the
    /// rate (pass 3.4 s idle → 1.8 s at max) through the clock's SpeedRatio, so it never restarts while running.</summary>
    static AnimationClock? Lane(FrameworkElement lane, TranslateTransform flow, AnimationClock? clock, double rate, double max, int dir)
    {
        if (rate <= 0)
        {
            if (clock is not null)
            {
                clock.Controller?.Stop();
                flow.ApplyAnimationClock(TranslateTransform.YProperty, null);
            }
            lane.Visibility = Visibility.Collapsed;
            return null;
        }
        lane.Visibility = Visibility.Visible;
        double speed = 3.4 / (3.4 - 1.6 * Math.Min(1, rate / max));
        if (clock is null)
        {
            var loop = new DoubleAnimation(0, dir * LanePitch, TimeSpan.FromSeconds(3.4 / 3)) { RepeatBehavior = RepeatBehavior.Forever };
            clock = loop.CreateClock();
            flow.ApplyAnimationClock(TranslateTransform.YProperty, clock);
        }
        if (clock.Controller is { } c && Math.Abs(c.SpeedRatio - speed) > 0.05 * speed) c.SpeedRatio = speed;
        return clock;
    }

    // Bars ease to new values (1.4 s); first value after showing is set directly so nothing sweeps from stale data.
    readonly Dictionary<RangeBase, double> _barTo = new();

    void SetBar(RangeBase bar, double to, bool linear = false)
    {
        if (bar.IsVisible && _barTo.TryGetValue(bar, out double was))
        {
            if (was == to || (!linear && Math.Abs(was - to) < 0.5)) return;
            _barTo[bar] = to;
            // AwakeBar drains every second: a 1 s linear step to the next value keeps it moving continuously.
            if (linear && Motion.Enabled) bar.BeginAnimation(RangeBase.ValueProperty, new DoubleAnimation(to, TimeSpan.FromSeconds(1)));
            else Motion.Animate(bar, RangeBase.ValueProperty, to, 1400);
            return;
        }
        _barTo[bar] = to;
        bar.BeginAnimation(RangeBase.ValueProperty, null);
        bar.Value = to;
    }

    // RamBar's own brush, so its colour can cross-fade to Warn (800 ms) instead of swapping frozen brushes.
    SolidColorBrush? _ramBrush;
    bool _ramHigh;

    void SetRamColor(bool high)
    {
        var c = ((SolidColorBrush)Resources[high ? "Warn" : "Text"]).Color;
        if (_ramBrush is null)
        {
            _ramBrush = new SolidColorBrush(c);
            RamBar.Foreground = _ramBrush;
        }
        else if (high == _ramHigh) return;
        else if (Motion.Fades && RamBar.IsVisible)
        {
            var a = new ColorAnimationUsingKeyFrames();
            a.KeyFrames.Add(new SplineColorKeyFrame(c, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(800)), Motion.Soft));
            _ramBrush.BeginAnimation(SolidColorBrush.ColorProperty, a);
        }
        else
        {
            _ramBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            _ramBrush.Color = c;
        }
        _ramHigh = high;
    }

    /// <summary>A chip that just became selected: its white state is revealed as a circle growing from the click
    /// point (keyboard: the centre), 750 ms. The style swap already happened; this only clips it in.</summary>
    static void RevealChip(Button chip)
    {
        if (!Motion.Enabled || !chip.IsVisible) return;
        double w = chip.ActualWidth, h = chip.ActualHeight;
        var p = chip.IsMouseOver ? Mouse.GetPosition(chip) : new Point(w / 2, h / 2);
        double dx = Math.Max(p.X, w - p.X), dy = Math.Max(p.Y, h - p.Y);
        double r = Math.Sqrt(dx * dx + dy * dy);
        var clip = new EllipseGeometry(p, 0, 0);
        chip.Clip = clip;
        var grow = Motion.To(r, 750);
        grow.Completed += (_, _) => { if (chip.Clip == clip) chip.Clip = null; };
        clip.BeginAnimation(EllipseGeometry.RadiusXProperty, grow);
        clip.BeginAnimation(EllipseGeometry.RadiusYProperty, Motion.To(r, 750));
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
        SetBar(RamBar, s.RamFraction * 100);
        SetRamColor(high);
        RamDetail.Text = $"{Fmt.Gb(s.RamUsed)} / {Fmt.Gb(s.RamTotal)} GB";
        RamLevel.Text = s.RamFraction >= 0.9 ? "very high" : high ? "high" : "";
        RamLevel.Foreground = (Brush)Resources["Warn"];

        DownText.Text = Fmt.Rate(s.Down);
        UpText.Text = Fmt.Rate(s.Up);
        SysDown.Text = Fmt.Rate(s.Down);
        SysBat.Text = s.Battery.Present ? $"{s.Battery.Percent * 100:0}%" : "—";

        // Header: what's holding the PC awake, else the two numbers people glance for.
        HeaderDot.Visibility = HeaderPulse.Visibility = _awake.Holding ? Visibility.Visible : Visibility.Collapsed;
        if (_awake.Holding) HeaderStatus.Text = "awake" + (_awake.Remaining is { } left ? $" · {Short(left)}" : "");
        else
        {
            // Memory at 80 % or more turns amber here too, same rule as the System tab.
            HeaderStatus.Inlines.Clear();
            HeaderStatus.Inlines.Add(new Run($"cpu {s.Cpu * 100:0}% · ram "));
            var ram = new Run($"{s.RamFraction * 100:0}%");
            if (s.RamFraction >= 0.8) ram.Foreground = (Brush)Resources["Warn"];
            HeaderStatus.Inlines.Add(ram);
        }

        RefreshBattery(s.Battery);
        if (!ReferenceEquals(_shownDisks, s.Disks)) RebuildDisks(s.Disks);
        if (!ReferenceEquals(_shownDevices, s.Devices)) RebuildDevices(s.Devices);
        RefreshAwake();
        if (ProcTab.IsChecked == true && IsVisible) RefreshProcesses();
        SetAmbient();
    }

    // ---------- processes ----------

    internal readonly ProcessSampler _procs = new();
    const long Never = long.MinValue / 2;
    long _procsAt = Never; // Environment.TickCount64 (ms): unaffected by clock changes
    long _procsDrawnAt = Never; // last RefreshProcesses call; a gap means the list wasn't on screen
    bool _byRam;
    (string Name, long At)? _armed; // End clicked once on this app; a second click within 3 s ends it
    // Rows by app name, reused while the app stays in the top 8: a row keeps its place in a glide and its End state,
    // and a click never lands on a button that was just replaced.
    readonly Dictionary<string, ProcRow> _rows = new();
    ProcRow? _leaving; // an ended app sliding out; the list holds still until it's gone

    sealed class ProcRow(string name, Grid root, TranslateTransform shift, Run count, TextBlock cpu, TextBlock ram,
                         Button? end, SolidColorBrush fill, SolidColorBrush ring, SolidColorBrush ink, Border drain)
    {
        public readonly string Name = name;
        public readonly Grid Root = root;
        public readonly TranslateTransform Shift = shift;
        public readonly Run Count = count;
        public readonly TextBlock Cpu = cpu, Ram = ram;
        public readonly Button? End = end; // null for apps that can't be ended
        public readonly SolidColorBrush Fill = fill, Ring = ring, Ink = ink; // End's own brushes, so armed can cross-fade
        public readonly Border Drain = drain; // 2 px line along End's bottom edge: the 3 s it stays armed
        public bool Armed;
    }

    static readonly Color RestRing = Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF); // the Mini style's outline

    void RefreshProcesses(bool force = false)
    {
        long now = Environment.TickCount64;
        // Tab just chosen (Tab_Checked resets the sampler and _procsAt) or not drawn for a while (panel was hidden):
        // start from a settled list, nothing glides from stale positions.
        bool settle = (!force && _procsAt == Never) || now - _procsDrawnAt > 2500;
        _procsDrawnAt = now;
        if (_leaving is not null && !settle) return; // the rest glide up once the ended app is gone

        // Sample every 2 s, only while this tab is showing (see ProcessSampler).
        bool changed = force || settle;
        if (now - _procsAt >= 2000)
        {
            _procsAt = now;
            _procs.Sample();
            changed = true;
        }
        if (_armed is { } a && now - a.At > 3000) { _armed = null; changed = true; }
        if (!changed) return;

        SortCpu.Style = (Style)FindResource(_byRam ? "Seg" : "SegOn");
        SortRam.Style = (Style)FindResource(_byRam ? "SegOn" : "Seg");

        if (settle)
        {
            _leaving = null;
            _rows.Clear();
            ProcList.Children.Clear(); // drops the old rows with any animation still running on them
        }

        var top = _procs.Top(_byRam, 8).ToList();
        double pitch = ProcList.Children.Count > 0 && ProcList.Children[0] is FrameworkElement { ActualHeight: > 0 } first
            ? first.ActualHeight : 41; // 40 px row + 1 px hairline
        // Where each surviving row is now (rows below one that leaves glide up into its slot).
        var was = new Dictionary<ProcRow, int>();
        foreach (var app in top)
            if (_rows.TryGetValue(app.Name, out var r)) was[r] = ProcList.Children.IndexOf(r.Root);
        foreach (var gone in _rows.Keys.Except(top.Select(x => x.Name)).ToList()) _rows.Remove(gone);
        var kept = new HashSet<UIElement>(_rows.Values.Select(x => (UIElement)x.Root));
        for (int i = ProcList.Children.Count - 1; i >= 0; i--)
            if (!kept.Contains(ProcList.Children[i])) ProcList.Children.RemoveAt(i);

        for (int i = 0; i < top.Count; i++)
        {
            var app = top[i];
            bool fresh = !_rows.TryGetValue(app.Name, out var row);
            if (row is null) _rows[app.Name] = row = NewRow(app.Name);
            UpdateRow(row, app);

            // Children stay in ranked order (keyboard order, SelfTest); move only what's out of place.
            if (ProcList.Children.IndexOf(row.Root) != i)
            {
                ProcList.Children.Remove(row.Root);
                ProcList.Children.Insert(i, row.Root);
            }

            if (settle) continue;
            if (fresh) Motion.FromTo(row.Root, OpacityProperty, 0, 1, 500, fade: true);
            else if (was.TryGetValue(row, out int slot) && slot >= 0 && slot != i)
            {
                // FLIP: start where it was on screen (including a glide in flight), then ease into the new slot.
                Motion.FromTo(row.Shift, TranslateTransform.YProperty, row.Shift.Y + (slot - i) * pitch, 0, 800);
            }
        }
    }

    ProcRow NewRow(string app)
    {
        var root = new Grid();
        var shift = new TranslateTransform();
        root.RenderTransform = shift;
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });

        // Hairline above every row.
        var hairline = new Border { Background = (Brush)Resources["CardBorder"] };
        Grid.SetColumnSpan(hairline, 4);
        root.Children.Add(hairline);

        var name = new TextBlock
        {
            FontSize = 14,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = app,
        };
        var count = new Run { FontFamily = (FontFamily)Resources["Mono"], FontSize = 11, Foreground = (Brush)Resources["Faint"] };
        name.Inlines.Add(new Run(app));
        name.Inlines.Add(count);
        var cpu = new TextBlock { Style = (Style)FindResource("Value") };
        var ram = new TextBlock { Style = (Style)FindResource("Value") };
        Grid.SetRow(name, 1);
        Grid.SetRow(cpu, 1);
        Grid.SetRow(ram, 1);
        Grid.SetColumn(cpu, 1);
        Grid.SetColumn(ram, 2);
        root.Children.Add(name);
        root.Children.Add(cpu);
        root.Children.Add(ram);

        var danger = ((SolidColorBrush)Resources["Danger"]).Color;
        var fill = new SolidColorBrush(Color.FromArgb(0, danger.R, danger.G, danger.B)); // clear red: fades in as pure alpha
        var ring = new SolidColorBrush(RestRing);
        var ink = new SolidColorBrush(((SolidColorBrush)Resources["SubText"]).Color);
        // Inside the pill's bottom edge (24 px pill centred in 40: inner bottom at 31), 8 px in from each side
        // so the 12 px rounded ends clip nothing.
        var drain = new Border
        {
            Height = 2,
            Background = Brushes.White,
            Opacity = 0.8,
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 29, 8, 0),
            RenderTransformOrigin = new Point(0, 0.5),
            RenderTransform = new ScaleTransform(),
            Visibility = Visibility.Collapsed,
        };
        Button? end = null;
        if (ProcessSampler.CanEnd(app))
        {
            end = new Button
            {
                Style = (Style)FindResource("Mini"),
                Content = "End",
                HorizontalAlignment = HorizontalAlignment.Right,
                Tag = app,
                ToolTip = $"End {app}",
                Background = fill,
                BorderBrush = ring,
                Foreground = ink,
            };
            AutomationProperties.SetName(end, $"End {app}");
            end.Click += EndTask_Click;
            end.SizeChanged += (_, e) => drain.Width = Math.Max(0, e.NewSize.Width - 16); // "End?" is wider than "End"
            Grid.SetRow(end, 1);
            Grid.SetColumn(end, 3);
            root.Children.Add(end); // a direct child of the row: SelfTest finds End buttons in ProcList's Grids
            Grid.SetRow(drain, 1);
            Grid.SetColumn(drain, 3);
            root.Children.Add(drain);
        }
        return new ProcRow(app, root, shift, count, cpu, ram, end, fill, ring, ink, drain);
    }

    void UpdateRow(ProcRow row, AppUsage app)
    {
        row.Count.Text = app.Count > 1 ? $" ×{app.Count}" : "";
        row.Cpu.Text = _procs.Measured ? $"{app.Cpu * 100:0.0}%" : "…";
        row.Cpu.Foreground = (Brush)Resources[_byRam ? "SubText" : "Text"];
        row.Ram.Text = app.Ram >= 1L << 30 ? $"{app.Ram / (double)(1L << 30):0.0} GB" : $"{app.Ram >> 20} MB";
        row.Ram.Foreground = (Brush)Resources[_byRam ? "Text" : "SubText"];

        bool armed = _armed?.Name == app.Name;
        if (row.End is not { } end || armed == row.Armed) return; // unchanged: a running drain keeps running
        row.Armed = armed;
        end.Content = armed ? "End?" : "End";
        end.ToolTip = armed ? $"Click again to end {app.Name}" : $"End {app.Name}";

        // Cross-fade to red "End?" and back (450 ms); instant under Motion.Off (red fill, white "End?").
        var danger = ((SolidColorBrush)Resources["Danger"]).Color;
        Tint(row.Fill, armed ? danger : Color.FromArgb(0, danger.R, danger.G, danger.B));
        Tint(row.Ring, armed ? danger : RestRing);
        Tint(row.Ink, armed ? Colors.White : ((SolidColorBrush)Resources["SubText"]).Color);

        // The drain: full width → 0, linear, over what's left of the 3 s (all of it when just armed).
        var scale = (ScaleTransform)row.Drain.RenderTransform;
        if (armed && Motion.Enabled && _armed is { } a)
        {
            double left = Math.Clamp(3000 - (Environment.TickCount64 - a.At), 0, 3000);
            row.Drain.Visibility = Visibility.Visible;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(left / 3000, 0, TimeSpan.FromMilliseconds(left)));
        }
        else
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            row.Drain.Visibility = Visibility.Collapsed;
        }
    }

    static void Tint(SolidColorBrush brush, Color to)
    {
        if (!Motion.Fades)
        {
            brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            brush.Color = to;
            return;
        }
        var a = new ColorAnimationUsingKeyFrames();
        a.KeyFrames.Add(new SplineColorKeyFrame(to, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(450)), Motion.Soft));
        brush.BeginAnimation(SolidColorBrush.ColorProperty, a);
    }

    void Sort_Click(object sender, RoutedEventArgs e)
    {
        _byRam = sender == SortRam;
        RefreshProcesses(force: true);
    }

    void EndTask_Click(object sender, RoutedEventArgs e)
    {
        string name = (string)((Button)sender).Tag;
        if (_armed?.Name != name)
        {
            // Two-step: first click arms for 3 s, second click ends.
            _armed = (name, Environment.TickCount64);
            ProcStatus.Text = "";
            RefreshProcesses(force: true);
            return;
        }
        _armed = null;
        string? error = ProcessSampler.End(name);
        ProcStatus.Text = error ?? $"Ended {name}.";
        if (error is null && Motion.Enabled && _rows.TryGetValue(name, out var row) && row.Root.IsVisible)
        {
            Leave(row);
            return;
        }
        _procsAt = Never; // re-sample now
        RefreshProcesses(force: true);
    }

    /// <summary>The ended app's row fades out and slides 6 px left (500 ms); then the list re-samples and the rest glide up.</summary>
    void Leave(ProcRow row)
    {
        _leaving = row;
        row.Root.IsHitTestVisible = false;
        var fade = Motion.To(0, 500);
        fade.Completed += (_, _) =>
        {
            if (_leaving != row) return; // the list was rebuilt meanwhile
            _leaving = null;
            _rows.Remove(row.Name); // its element stays one more sync, so the rows below glide from their old slots
            _procsAt = Never; // re-sample now
            if (ProcTab.IsChecked == true && IsVisible) RefreshProcesses(force: true);
            else ProcList.Children.Remove(row.Root);
        };
        row.Root.BeginAnimation(OpacityProperty, fade);
        row.Shift.BeginAnimation(TranslateTransform.XProperty, Motion.To(-6, 500));
    }

    string? _pillColor;

    void RefreshBattery(BatteryInfo b)
    {
        SetBar(BatBar, b.Present ? b.Percent * 100 : 0);
        BatPct.Text = b.Present ? $"{b.Percent * 100:0}%" : "—";

        (string text, string color) = !b.Present ? ("No battery", "Faint")
            : b.Charging ? ("Charging", "Good")
            : b.PluggedIn ? ("Plugged in", "Good")
            : b.Percent < 0.2 ? ("On battery", "Danger")
            : ("On battery", "Warn");
        BatState.Text = text;
        if (color != _pillColor) // runs every second: only touch brushes when the state actually changes
        {
            _pillColor = color;
            var brush = (SolidColorBrush)Resources[color];
            BatDot.Fill = brush;
            BatPill.Background = new SolidColorBrush(Color.FromArgb(0x24, brush.Color.R, brush.Color.G, brush.Color.B));
            BatState.Foreground = brush;
        }

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
            : !a.Wanted ? "--:--"
            : a.Remaining is { } left ? Clock(left)
            : "On";
        var big = (Brush)Resources[a.Holding ? "Text" : a.Wanted ? "SubText" : "Faint"];
        AwakeBig.Foreground = big;
        // While counting down (and motion is on) the same reading shows as rolling digits with a breathing colon.
        bool rolling = Motion.Enabled && a.PausedReason is null && a.Wanted && a.Remaining is not null;
        AwakeBig.Visibility = rolling ? Visibility.Collapsed : Visibility.Visible;
        AwakeClock.Visibility = rolling ? Visibility.Visible : Visibility.Collapsed;
        if (rolling)
        {
            int colon = AwakeBig.Text.LastIndexOf(':');
            TextElement.SetForeground(AwakeClock, big);
            AwakeLead.Text = AwakeBig.Text[..colon];
            AwakeSecs.Text = AwakeBig.Text[(colon + 1)..];
        }
        AwakeStatus.Text =
            a.PausedReason is { } why ? why
            : !a.Wanted ? "Off · normal power settings"
            : !a.Active ? $"while {a.RunningApp} is open"
            : a.UntilTime is { } t ? $"left · until {t:HH:mm}"
            : a.Remaining is not null ? "left"
            : "until you turn it off";

        // Remaining time as a cobalt bar over hatch; only for timed sessions.
        if (a.Active && a.Remaining is { } rem && a.Duration is { } total && total > TimeSpan.Zero)
        {
            SetBar(AwakeBar, Math.Clamp(rem / total * 100, 0, 100), linear: true); // before showing: first value lands directly
            AwakeBar.Visibility = Visibility.Visible;
        }
        else AwakeBar.Visibility = Visibility.Collapsed;

        foreach (Button chip in Chips.Children)
        {
            bool on = (string)chip.Tag == "until"
                ? a.Active && a.UntilTime is not null
                : a.Active && a.UntilTime is null && ChipDuration(chip) == a.Duration;
            chip.Style = (Style)FindResource(on ? "DurOn" : "Dur");
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
        int prev = _tabIndex;
        _tabIndex = int.Parse((string)tab.Tag);
        MoveTabDot(animate: true);
        AwakePage.Visibility = tab == AwakeTab ? Visibility.Visible : Visibility.Collapsed;
        DisplaysPage.Visibility = tab == DisplaysTab ? Visibility.Visible : Visibility.Collapsed;
        SystemPage.Visibility = tab == SystemTab ? Visibility.Visible : Visibility.Collapsed;
        NetPage.Visibility = tab == NetTab ? Visibility.Visible : Visibility.Collapsed;
        ProcPage.Visibility = tab == ProcTab ? Visibility.Visible : Visibility.Collapsed;
        if (tab == ProcTab) { _procs.Reset(); _procsAt = Never; RefreshProcesses(); }
        DiskPage.Visibility = tab == DiskTab ? Visibility.Visible : Visibility.Collapsed;
        if (IsVisible && prev != _tabIndex) ShowPage(ActivePage(), _tabIndex > prev ? 5 : -5);
        SetAmbient();
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
        else
        {
            _awake.Start(duration); // raises Changed → Refresh, which swaps this chip to ChipOn
            RevealChip(button);
        }
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
        OptionsChevron.Text = show ? "\uE70D" : "\uE76C"; // ChevronDown : ChevronRight
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
        ["Bg"] = "#FF000000",         // panel: opaque black
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
