using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Vigil;

/// <summary>
/// The last 60 samples as a field of dots: one column per second, 6 dots tall (100 % = full column).
/// Unused dots stay faintly visible; the newest column is cobalt ("now").
/// Each new sample slides the field one column left while the new column lights from the bottom up.
/// Software rendering: everything but the newest column's lit dots is one cached, frozen drawing
/// (one geometry per brush), so an animation frame is 1 DrawDrawing + at most 6 dots.
/// </summary>
sealed class DotMatrix : FrameworkElement
{
    const int Rows = 6;
    const double Gap = 2;
    const double SlideMs = 950, DotMs = 600, StaggerMs = 55;
    const double RevealMs = DotMs + (Rows - 1) * StaggerMs;

    static readonly Brush Off = Frozen(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF));
    static readonly Brush Now = Frozen(Color.FromRgb(0x4C, 0x7D, 0xFF));
    static readonly Brush[] On = Enumerable.Range(0, Rows)
        .Select(r => Frozen(Color.FromArgb((byte)(0x5A + r * 0x1A), 0xFA, 0xFA, 0xFA))) // brighter towards the top
        .ToArray();

    /// <summary>1 = field still one column to the right (old position), 0 = settled.</summary>
    static readonly DependencyProperty SlideProperty = DependencyProperty.Register(
        nameof(Slide), typeof(double), typeof(DotMatrix),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Linear 0 → 1 over the whole staggered reveal; each dot derives its own eased progress.</summary>
    static readonly DependencyProperty RevealProperty = DependencyProperty.Register(
        nameof(Reveal), typeof(double), typeof(DotMatrix),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    double Slide => (double)GetValue(SlideProperty);
    double Reveal => (double)GetValue(RevealProperty);

    double[] _values = Array.Empty<double>();
    Drawing? _static;               // every dot except the newest column's lit ones
    Rect[] _nowDots = Array.Empty<Rect>(); // newest column's lit dots, bottom first
    Size _builtFor;                 // size the cached drawing was built for
    double _step;                   // one column + gap
    bool _settleNext = true;        // first Update after becoming visible shows the final state

    public DotMatrix()
    {
        ClipToBounds = true;
        IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) _settleNext = true; };
    }

    public void Update(IEnumerable<double> values)
    {
        var next = values.ToArray();
        if (next.AsSpan().SequenceEqual(_values)) return;

        bool animate = Motion.Enabled && IsVisible && !_settleNext && AdvancedByOne(_values, next);
        _settleNext = false;
        _values = next;
        Rebuild(ActualWidth, ActualHeight);

        if (animate)
        {
            Motion.FromTo(this, SlideProperty, 1, 0, SlideMs);
            BeginAnimation(RevealProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(RevealMs)));
        }
        else
        {
            BeginAnimation(SlideProperty, null);
            BeginAnimation(RevealProperty, null);
            SetValue(SlideProperty, 0.0);
            SetValue(RevealProperty, 1.0);
        }
        InvalidateVisual();
    }

    /// <summary>b is a with one sample appended (and, once full, the oldest dropped).</summary>
    static bool AdvancedByOne(double[] a, double[] b) =>
        b.Length > 0 && (b.Length == a.Length
            ? a.AsSpan(1).SequenceEqual(b.AsSpan(0, b.Length - 1))
            : b.Length == a.Length + 1 && a.AsSpan().SequenceEqual(b.AsSpan(0, a.Length)));

    void Rebuild(double w, double h)
    {
        _static = null;
        _nowDots = Array.Empty<Rect>();
        _builtFor = new Size(w, h);
        int cols = Stats.HistoryLength;
        if (w <= 0 || h <= 0) return;

        double colW = (w - Gap * (cols - 1)) / cols;
        double dotH = (h - Gap * (Rows - 1)) / Rows;
        _step = colW + Gap;
        int offset = cols - _values.Length; // fill in from the right during the first minute

        var off = new GeometryGroup();
        var on = Enumerable.Range(0, Rows).Select(_ => new GeometryGroup()).ToArray();
        var now = new List<Rect>(Rows);

        for (int c = 0; c < cols; c++)
        {
            int i = c - offset;
            double v = i >= 0 ? Math.Clamp(_values[i], 0, 1) : 0;
            int lit = v <= 0.005 ? 0 : Math.Max(1, (int)Math.Ceiling(v * Rows));
            bool newest = i == _values.Length - 1;
            double x = c * _step;
            for (int r = 0; r < Rows; r++)
            {
                var rect = new Rect(x, h - (r + 1) * dotH - r * Gap, colW, dotH);
                if (r >= lit) off.Children.Add(new RectangleGeometry(rect, 1, 1));
                else if (newest) now.Add(rect);
                else on[r].Children.Add(new RectangleGeometry(rect, 1, 1));
            }
        }

        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(Off, null, off));
        for (int r = 0; r < Rows; r++)
            if (on[r].Children.Count > 0) group.Children.Add(new GeometryDrawing(On[r], null, on[r]));
        group.Freeze();
        _static = group;
        _nowDots = now.ToArray();
    }

    protected override void OnRender(DrawingContext dc)
    {
        // WPF renders during arrange, before it reports the new size: build here when the size is new
        // (first layout, DPI change), otherwise the first frame would be empty.
        if (_builtFor != RenderSize) Rebuild(RenderSize.Width, RenderSize.Height);
        if (_static == null) return;

        double dx = Slide * _step;
        bool shifted = dx != 0;
        if (shifted) dc.PushTransform(new TranslateTransform(dx, 0));

        dc.DrawDrawing(_static);

        double elapsed = Reveal * RevealMs;
        for (int r = 0; r < _nowDots.Length; r++)
        {
            var rect = _nowDots[r];
            double t = Math.Clamp((elapsed - r * StaggerMs) / DotMs, 0, 1);
            if (t >= 1) { dc.DrawRoundedRectangle(Now, null, rect, 1, 1); continue; }
            double p = Motion.Soft.GetSplineProgress(t);
            if (p <= 0) continue;
            double s = 0.7 + 0.3 * p;
            dc.PushOpacity(p);
            dc.PushTransform(new ScaleTransform(s, s, rect.X + rect.Width / 2, rect.Y + rect.Height / 2));
            dc.DrawRoundedRectangle(Now, null, rect, 1, 1);
            dc.Pop();
            dc.Pop();
        }

        if (shifted) dc.Pop();
    }

    static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
