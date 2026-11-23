using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Vigil;

/// <summary>Shared motion rules: two curves, and one place that decides whether anything may move.</summary>
static class Motion
{
    /// <summary>Self-test sets this so screenshots are deterministic: nothing animates, not even fades.</summary>
    public static bool Off { get; set; }

    /// <summary>Things may move/slide. False when Off or Windows "Show animations" is off (reduced motion).</summary>
    public static bool Enabled => !Off && SystemParameters.ClientAreaAnimation;

    /// <summary>Plain opacity fades may run. Reduced motion keeps fades; only Off stops them.</summary>
    public static bool Fades => !Off;

    /// <summary>Things travelling on screen (dot field, rows re-ranking): cubic-bezier(.45,.05,.25,1).</summary>
    public static readonly KeySpline Soft = Frozen(new KeySpline(0.45, 0.05, 0.25, 1));

    /// <summary>Things appearing and feedback (open, tabs, readings, bars, chips): strong ease-out, cubic-bezier(.23,1,.32,1).
    /// Starts fast so the response lands while the eye is still on it.</summary>
    public static readonly KeySpline Out = Frozen(new KeySpline(0.23, 1, 0.32, 1));

    static KeySpline Frozen(KeySpline s) { s.Freeze(); return s; }

    /// <summary>A new one-keyframe animation to <paramref name="to"/> over <paramref name="ms"/> on <paramref name="curve"/> (default <see cref="Out"/>).</summary>
    public static DoubleAnimationUsingKeyFrames To(double to, double ms, double delayMs = 0, KeySpline? curve = null)
    {
        var a = new DoubleAnimationUsingKeyFrames();
        a.KeyFrames.Add(new SplineDoubleKeyFrame(to, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(Math.Max(0, ms))), curve ?? Out));
        if (delayMs > 0) a.BeginTime = TimeSpan.FromMilliseconds(delayMs);
        return a;
    }

    /// <summary>A new animation from <paramref name="from"/> to <paramref name="to"/> on <see cref="Out"/> (for one-shot effects).</summary>
    public static DoubleAnimationUsingKeyFrames FromToAnim(double from, double to, double ms)
    {
        var a = To(to, ms);
        a.KeyFrames.Insert(0, new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        return a;
    }

    /// <summary>Animate to <paramref name="to"/>, or set it directly when not allowed to animate
    /// (fade: true = opacity-only, which only needs <see cref="Fades"/>).</summary>
    public static void Animate(IAnimatable target, DependencyProperty property, double to, double ms, double delayMs = 0, bool fade = false, KeySpline? curve = null)
    {
        if (fade ? Fades : Enabled) { target.BeginAnimation(property, To(to, ms, delayMs, curve)); return; }
        target.BeginAnimation(property, null);
        ((DependencyObject)target).SetValue(property, to);
    }

    /// <summary>Jump to <paramref name="from"/>, then animate to <paramref name="to"/>; just sets <paramref name="to"/> when not allowed to animate.</summary>
    public static void FromTo(IAnimatable target, DependencyProperty property, double from, double to, double ms, double delayMs = 0, bool fade = false, KeySpline? curve = null)
    {
        target.BeginAnimation(property, null);
        if (fade ? Fades : Enabled) ((DependencyObject)target).SetValue(property, from);
        Animate(target, property, to, ms, delayMs, fade, curve);
    }

    /// <summary>The element's TranslateTransform, adding one when it has no transform; null when it has another kind.</summary>
    public static TranslateTransform? Shift(UIElement e)
    {
        if (e.RenderTransform is TranslateTransform t) return t;
        if (e.RenderTransform is not null && e.RenderTransform != Transform.Identity) return null;
        e.RenderTransform = t = new TranslateTransform();
        return t;
    }

    /// <summary>Something that just appeared: fades in and drops 4 px into place, 180 ms.</summary>
    public static void Reveal(UIElement e)
    {
        FromTo(e, UIElement.OpacityProperty, 0, 1, 180, fade: true);
        if (Shift(e) is { } t) FromTo(t, TranslateTransform.YProperty, -4, 0, 180);
    }

    /// <summary>Open or close a disclosure. Opening reveals the body (<see cref="Reveal"/>); closing is instant.
    /// The toggle's "›" turns 90° to point down, 180 ms. First call splits the string content "›  Label"
    /// into a turnable chevron and the label.</summary>
    public static void Disclose(ContentControl toggle, UIElement body, bool show)
    {
        body.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (toggle.Content is string text)
        {
            string label = text.TrimStart('›', '⌄', ' ');
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock { Text = "›", RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new RotateTransform() });
            row.Children.Add(new TextBlock { Text = "  " + label });
            toggle.Content = row;
            AutomationProperties.SetName(toggle, label);
        }
        var turn = (RotateTransform)((StackPanel)toggle.Content).Children[0].RenderTransform;
        Animate(turn, RotateTransform.AngleProperty, show ? 90 : 0, 180);
        if (show) Reveal(body);
    }
}
