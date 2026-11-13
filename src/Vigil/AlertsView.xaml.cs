using System;
using System.Windows;
using System.Windows.Controls;

namespace Vigil;

public partial class AlertsView : UserControl
{
    Settings _settings = new();
    Action _save = () => { };

    public AlertsView()
    {
        InitializeComponent();
        Show();
    }

    /// <summary>Show and edit these settings; save runs after every change.</summary>
    internal void Bind(Settings settings, Action save)
    {
        _settings = settings;
        _save = save;
        Show();
    }

    void Show()
    {
        LowSwitch.IsChecked = _settings.AlertBatteryLow;
        ChargedSwitch.IsChecked = _settings.AlertCharged;
        DiskSwitch.IsChecked = _settings.AlertDiskFull;
        BusySwitch.IsChecked = _settings.AlertBusy;
        foreach (Button b in Limits.Children)
            b.SetResourceReference(StyleProperty, (string)b.Tag == _settings.ChargeLimit.ToString() ? "ChipOn" : "Chip");
        Limits.IsEnabled = _settings.AlertCharged;
        Limits.Opacity = _settings.AlertCharged ? 1 : 0.5;
    }

    void Switch_Click(object sender, RoutedEventArgs e)
    {
        _settings.AlertBatteryLow = LowSwitch.IsChecked == true;
        _settings.AlertCharged = ChargedSwitch.IsChecked == true;
        _settings.AlertDiskFull = DiskSwitch.IsChecked == true;
        _settings.AlertBusy = BusySwitch.IsChecked == true;
        _save();
        Show();
    }

    void Limit_Click(object sender, RoutedEventArgs e)
    {
        _settings.ChargeLimit = int.Parse((string)((Button)sender).Tag);
        _save();
        Show();
    }

    void Toggle_Click(object sender, RoutedEventArgs e)
    {
        bool show = Body.Visibility != Visibility.Visible;
        Body.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        Toggle.Content = (show ? "⌄  " : "›  ") + "Alerts";
    }
}
