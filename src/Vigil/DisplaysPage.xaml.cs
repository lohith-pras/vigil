using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;

namespace Vigil;

/// <summary>Displays tab. Reads screens when shown, releases monitor handles when hidden (tab switch or panel close).</summary>
public partial class DisplaysPage : UserControl
{
    List<DisplayScreen>? _screens;
    int _gen; // bumps on every load/close, so a slow load that finishes after the tab closed is thrown away

    internal bool Loading { get; private set; }
    internal IReadOnlyList<DisplayScreen>? Screens => _screens;

    public DisplaysPage()
    {
        InitializeComponent();
        IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) Reload(); else Close(); };
    }

    void Close()
    {
        _gen++;
        Loading = false;
        var old = _screens;
        _screens = null;
        if (old is not null) Task.Run(() => Displays.Release(old));
    }

    async void Reload()
    {
        Close();
        int gen = _gen;
        Loading = true;
        Status.Text = "Reading displays…";
        Status.Visibility = Visibility.Visible;
        ScreenList.Children.Clear();
        try
        {
            var (screens, mode) = await Task.Run(() => (Displays.Load(), Displays.CurrentMode()));
            if (gen != _gen) { Displays.Release(screens); return; }
            Show(screens, mode);
        }
        catch (Exception e)
        {
            if (gen != _gen) return;
            Loading = false;
            Status.Text = $"Couldn't read displays: {e.Message}";
        }
    }

    /// <summary>Build the screen blocks. Internal so the self-test can render made-up screens too.</summary>
    internal void Show(List<DisplayScreen> screens, uint mode)
    {
        _screens = screens;
        Loading = false;
        Status.Text = "No displays found";
        Status.Visibility = screens.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ScreenList.Children.Clear();
        foreach (var s in screens) ScreenList.Children.Add(Block(s));
        foreach (Button chip in ModeChips.Children)
            chip.Style = Find(mode == uint.Parse((string)chip.Tag) ? "ChipOn" : "Chip");
    }

    Style Find(string key) => (Style)FindResource(key);

    FrameworkElement Block(DisplayScreen s)
    {
        var block = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };

        var header = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        header.Children.Add(new TextBlock { Text = s.Title, FontWeight = FontWeights.SemiBold });
        if (s.Width > 0)
            header.Children.Add(new TextBlock
            {
                Text = $"{s.Width} × {s.Height}" + (s.Hz > 1 ? $" · {s.Hz} Hz" : ""),
                Style = Find("Muted"),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
            });
        block.Children.Add(header);

        if (s.Controls.Count == 0)
            block.Children.Add(new TextBlock { Text = "This monitor doesn't support control", Style = Find("Muted") });

        foreach (var c in s.Controls)
        {
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });

            var label = new TextBlock { Text = Pct(c.Value, c.Max), Style = Find("Value") };
            var slider = new Slider { Style = Find("ThinSlider"), Minimum = 0, Maximum = c.Max, Value = c.Value, Margin = new Thickness(0, 0, 8, 0) };
            AutomationProperties.SetName(slider, $"{s.Title} {c.Name}");
            // Handler after the initial Value, so building the UI never writes to a monitor.
            slider.ValueChanged += (_, e) =>
            {
                int v = (int)Math.Round(e.NewValue);
                label.Text = Pct(v, c.Max);
                c.Set(v);
            };
            row.Children.Add(new TextBlock { Text = c.Name, VerticalAlignment = VerticalAlignment.Center });
            Grid.SetColumn(slider, 1);
            Grid.SetColumn(label, 2);
            row.Children.Add(slider);
            row.Children.Add(label);
            block.Children.Add(row);
        }

        if (s.Rates.Length > 1)
        {
            var chips = new UniformGrid { Columns = Math.Min(4, s.Rates.Length), Margin = new Thickness(-3, 6, -3, 0) };
            foreach (int hz in s.Rates)
            {
                var chip = new Button { Style = Find(hz == s.Hz ? "ChipOn" : "Chip"), Content = $"{hz} Hz", Tag = (s, hz) };
                AutomationProperties.SetName(chip, $"{s.Title} {hz} hertz");
                chip.Click += Rate_Click;
                chips.Children.Add(chip);
            }
            block.Children.Add(chips);
        }
        return block;
    }

    static string Pct(int v, int max) => $"{Math.Round(v * 100.0 / Math.Max(1, max))}%";

    async void Rate_Click(object sender, RoutedEventArgs e)
    {
        var (s, hz) = ((DisplayScreen, int))((Button)sender).Tag;
        if (hz == s.Hz) return;
        bool ok = await Task.Run(() => Displays.SetRate(s.Device, hz));
        ActionError.Text = ok ? "" : $"Windows refused {hz} Hz on {s.Title}.";
        if (IsVisible) Reload();
    }

    async void Mode_Click(object sender, RoutedEventArgs e)
    {
        uint mode = uint.Parse((string)((Button)sender).Tag);
        bool ok = await Task.Run(() => Displays.SetMode(mode));
        ActionError.Text = ok ? "" : "Windows couldn't switch display mode. Is a second screen connected?";
        if (IsVisible) Reload();
    }

    async void Off_Click(object sender, RoutedEventArgs e)
    {
        var hwnd = new WindowInteropHelper(Window.GetWindow(this)).Handle;
        await Task.Delay(500); // let the click's own mouse-up pass, or it wakes the screens straight back up
        Displays.TurnOff(hwnd);
    }
}
