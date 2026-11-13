using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Vigil;

public partial class SpeedTestView : UserControl
{
    CancellationTokenSource? _cts;

    public SpeedTestView()
    {
        InitializeComponent();
        Status.Text = $"Uses up to about {SpeedTest.Full.MaxBytes / 1_000_000} MB of data";
    }

    void Run_Click(object sender, RoutedEventArgs e)
    {
        if (_cts is not null) { _cts.Cancel(); return; } // button reads "Cancel" while running
        _ = RunAsync();
    }

    async Task RunAsync()
    {
        _cts = new CancellationTokenSource();
        RunButton.Content = "Cancel";
        RunButton.SetResourceReference(StyleProperty, "ChipOn");
        Results.Visibility = Visibility.Visible;
        DownValue.Text = UpValue.Text = PingValue.Text = "—";
        Status.Text = "Measuring ping…";

        // Progress<T> posts back to this (UI) thread; the test itself runs on the thread pool.
        var progress = new Progress<(string Phase, double Value)>(p =>
        {
            if (p.Phase == "Ping") PingValue.Text = $"{p.Value:0}";
            else if (p.Phase == "Download") { DownValue.Text = Mbps(p.Value); Status.Text = "Testing download…"; }
            else { UpValue.Text = Mbps(p.Value); Status.Text = "Testing upload…"; }
        });
        try
        {
            var token = _cts.Token;
            var r = await Task.Run(() => SpeedTest.RunAsync(SpeedTest.Full, progress, token));
            ShowResult(r);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            Status.Text = "Cancelled";
        }
        catch (Exception ex)
        {
            Status.Text = $"Couldn't finish: {ex.GetBaseException().Message}";
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            RunButton.Content = "Run speed test";
            RunButton.SetResourceReference(StyleProperty, "Chip");
        }
    }

    internal void ShowResult(SpeedTest.Result r)
    {
        Results.Visibility = Visibility.Visible;
        DownValue.Text = Mbps(r.DownMbps);
        UpValue.Text = Mbps(r.UpMbps);
        PingValue.Text = $"{r.PingMs:0}";
        Status.Text = $"Done · {DateTime.Now:HH:mm}";
    }

    static string Mbps(double v) => v >= 100 ? $"{v:0}" : $"{v:0.0}";
}
