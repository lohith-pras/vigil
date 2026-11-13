using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace Vigil;

/// <summary>
/// The last 60 samples as a field of dots: one column per second, 6 dots tall (100 % = full column).
/// Unused dots stay faintly visible; the newest column is cobalt ("now").
/// </summary>
sealed class DotMatrix : FrameworkElement
{
    const int Rows = 6;
    const double Gap = 2;

    static readonly Brush Off = Frozen(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF));
    static readonly Brush Now = Frozen(Color.FromRgb(0x4C, 0x7D, 0xFF));
    static readonly Brush[] On = Enumerable.Range(0, Rows)
        .Select(r => Frozen(Color.FromArgb((byte)(0x5A + r * 0x1A), 0xFA, 0xFA, 0xFA))) // brighter towards the top
        .ToArray();

    double[] _values = Array.Empty<double>();

    public void Update(IEnumerable<double> values)
    {
        _values = values.ToArray();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        int cols = Stats.HistoryLength;
        if (w <= 0 || h <= 0) return;

        double colW = (w - Gap * (cols - 1)) / cols;
        double dotH = (h - Gap * (Rows - 1)) / Rows;
        int offset = cols - _values.Length; // fill in from the right during the first minute

        for (int c = 0; c < cols; c++)
        {
            int i = c - offset;
            double v = i >= 0 ? Math.Clamp(_values[i], 0, 1) : 0;
            int lit = v <= 0.005 ? 0 : Math.Max(1, (int)Math.Ceiling(v * Rows));
            bool newest = i == _values.Length - 1;
            double x = c * (colW + Gap);
            for (int r = 0; r < Rows; r++)
            {
                double y = h - (r + 1) * dotH - r * Gap;
                var brush = r < lit ? (newest ? Now : On[r]) : Off;
                dc.DrawRoundedRectangle(brush, null, new Rect(x, y, colW, dotH), 1, 1);
            }
        }
    }

    static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
