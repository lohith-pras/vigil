using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace Vigil;

/// <summary>
/// A one-line text readout (drop-in for a numeric TextBlock) where only the characters that changed
/// rise 30 % of a line into place and fade in, 500 ms on <see cref="Motion.Soft"/>. Static when motion is off.
/// </summary>
sealed class RollingText : FrameworkElement
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(RollingText),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender, OnTextChanged));

    public static readonly DependencyProperty FontFamilyProperty = TextElement.FontFamilyProperty.AddOwner(typeof(RollingText),
        new FrameworkPropertyMetadata(SystemFonts.MessageFontFamily,
            FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender, OnLookChanged));

    public static readonly DependencyProperty FontSizeProperty = TextElement.FontSizeProperty.AddOwner(typeof(RollingText),
        new FrameworkPropertyMetadata(SystemFonts.MessageFontSize,
            FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender, OnLookChanged));

    public static readonly DependencyProperty FontWeightProperty = TextElement.FontWeightProperty.AddOwner(typeof(RollingText),
        new FrameworkPropertyMetadata(FontWeights.Normal,
            FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender, OnLookChanged));

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(typeof(RollingText),
        new FrameworkPropertyMetadata(Brushes.Black,
            FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender, OnLookChanged));

    // 0 → 1 while the changed characters rise; 1 (or no _rolling) = plain static draw.
    static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        "Progress", typeof(double), typeof(RollingText),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public FontFamily FontFamily { get => (FontFamily)GetValue(FontFamilyProperty); set => SetValue(FontFamilyProperty, value); }
    public double FontSize { get => (double)GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }
    public FontWeight FontWeight { get => (FontWeight)GetValue(FontWeightProperty); set => SetValue(FontWeightProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    List<FormattedText>? _glyphs; // one per char, rebuilt only when text/font/brush/DPI change
    double _width, _lineHeight;
    bool[]? _rolling;             // which indices of the current text are rising

    public RollingText() => ClipToBounds = true;

    static void OnLookChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((RollingText)d)._glyphs = null;

    static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((RollingText)d).Roll((string?)e.OldValue ?? "", (string?)e.NewValue ?? "");

    void Roll(string old, string now)
    {
        _glyphs = null;
        _rolling = null;
        if (!Motion.Enabled || !IsVisible || old.Length == 0 || now.Length == 0)
        {
            BeginAnimation(ProgressProperty, null);
            return;
        }
        var roll = new bool[now.Length];
        for (int i = 0; i < now.Length; i++) roll[i] = old.Length != now.Length || old[i] != now[i];
        _rolling = roll;
        Motion.FromTo(this, ProgressProperty, 0, 1, 500);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        _glyphs = null;
        InvalidateMeasure();
        InvalidateVisual();
        base.OnDpiChanged(oldDpi, newDpi);
    }

    List<FormattedText> Glyphs()
    {
        if (_glyphs is not null) return _glyphs;
        string text = Text ?? "";
        var typeface = new Typeface(FontFamily, FontStyles.Normal, FontWeight, FontStretches.Normal);
        double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var glyphs = new List<FormattedText>(text.Length);
        double width = 0;
        // ponytail: one FormattedText per UTF-16 char; fine for numeric readouts, would split surrogate pairs.
        foreach (char c in text)
        {
            var ft = new FormattedText(c.ToString(), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, FontSize, Foreground, ppd);
            glyphs.Add(ft);
            width += ft.WidthIncludingTrailingWhitespace;
        }
        _width = width;
        _lineHeight = FontFamily.LineSpacing * FontSize;
        return _glyphs = glyphs;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        Glyphs();
        return new Size(_width, _lineHeight);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var glyphs = Glyphs();
        double p = (double)GetValue(ProgressProperty);
        bool[]? roll = p < 1 ? _rolling : null;
        double x = 0, rise = (1 - p) * 0.3 * _lineHeight;
        for (int i = 0; i < glyphs.Count; i++)
        {
            if (roll is not null && i < roll.Length && roll[i])
            {
                dc.PushOpacity(p);
                dc.DrawText(glyphs[i], new Point(x, rise));
                dc.Pop();
            }
            else dc.DrawText(glyphs[i], new Point(x, 0));
            x += glyphs[i].WidthIncludingTrailingWhitespace;
        }
    }
}
