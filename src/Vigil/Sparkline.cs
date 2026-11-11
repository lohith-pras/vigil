using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace Vigil;

/// <summary>
/// Line graph of the last 60 samples: dashed ceiling line with a label, line, faint gradient fill.
/// Max = 0 scales to the largest value (network); otherwise values are 0..Max (CPU/RAM = 1).
/// </summary>
sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GuideProperty = DependencyProperty.Register(
        nameof(Guide), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public Brush Guide { get => (Brush)GetValue(GuideProperty); set => SetValue(GuideProperty, value); }

    public double Max { get; set; } = 1;
    public Func<double, string> Label { get; set; } = m => $"{m * 100:0}%";

    double[] _values = Array.Empty<double>();

    public void Update(IEnumerable<double> values)
    {
        _values = values.ToArray();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        double max = Max > 0 ? Max : Math.Max(_values.DefaultIfEmpty(0).Max(), 10 * 1024); // floor: 10 KB/s
        const double top = 14; // room for the ceiling label

        var guide = new Pen(Guide, 1) { DashStyle = DashStyles.Dash };
        dc.DrawLine(guide, new Point(0, top + 0.5), new Point(w, top + 0.5));
        dc.DrawLine(new Pen(Guide, 1), new Point(0, h - 0.5), new Point(w, h - 0.5));

        var label = new FormattedText(Label(max), CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 10, Guide, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(label, new Point(0, 0));

        if (_values.Length < 2) return;

        // Newest sample at the right edge; the graph fills in from the right over the first minute.
        double step = w / (Stats.HistoryLength - 1);
        double x0 = w - step * (_values.Length - 1);
        Point P(int i) => new(x0 + i * step, h - (h - top) * Math.Clamp(_values[i] / max, 0, 1));

        var line = new StreamGeometry();
        using (var g = line.Open())
        {
            g.BeginFigure(P(0), false, false);
            for (int i = 1; i < _values.Length; i++) g.LineTo(P(i), true, true);
        }
        line.Freeze();

        var area = new StreamGeometry();
        using (var g = area.Open())
        {
            g.BeginFigure(new Point(x0, h), true, true);
            for (int i = 0; i < _values.Length; i++) g.LineTo(P(i), true, true);
            g.LineTo(new Point(w, h), true, true);
        }
        area.Freeze();

        var c = Stroke is SolidColorBrush sb ? sb.Color : Colors.DodgerBlue;
        var fill = new LinearGradientBrush(Color.FromArgb(0x55, c.R, c.G, c.B), Color.FromArgb(0, c.R, c.G, c.B), 90);
        dc.DrawGeometry(fill, null, area);
        dc.DrawGeometry(null, new Pen(Stroke, 1.6) { LineJoin = PenLineJoin.Round }, line);
    }
}
