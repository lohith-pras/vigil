using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace Vigil;

/// <summary>Shared motion rules: one curve, and one place that decides whether anything may move.</summary>
static class Motion
{
    /// <summary>Self-test sets this so screenshots are deterministic: nothing animates, not even fades.</summary>
    public static bool Off { get; set; }

    /// <summary>Things may move/slide. False when Off or Windows "Show animations" is off (reduced motion).</summary>
    public static bool Enabled => !Off && SystemParameters.ClientAreaAnimation;

    /// <summary>Plain opacity fades may run. Reduced motion keeps fades; only Off stops them.</summary>
    public static bool Fades => !Off;

    /// <summary>The one curve for everything: cubic-bezier(.45,.05,.25,1).</summary>
    public static readonly KeySpline Soft = Frozen(new KeySpline(0.45, 0.05, 0.25, 1));

    static KeySpline Frozen(KeySpline s) { s.Freeze(); return s; }

    /// <summary>A new one-keyframe animation to <paramref name="to"/> over <paramref name="ms"/> on <see cref="Soft"/>.</summary>
    public static DoubleAnimationUsingKeyFrames To(double to, double ms, double delayMs = 0)
    {
        var a = new DoubleAnimationUsingKeyFrames();
        a.KeyFrames.Add(new SplineDoubleKeyFrame(to, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(Math.Max(0, ms))), Soft));
        if (delayMs > 0) a.BeginTime = TimeSpan.FromMilliseconds(delayMs);
        return a;
    }

    /// <summary>Animate to <paramref name="to"/>, or set it directly when not allowed to animate
    /// (fade: true = opacity-only, which only needs <see cref="Fades"/>).</summary>
    public static void Animate(IAnimatable target, DependencyProperty property, double to, double ms, double delayMs = 0, bool fade = false)
    {
        if (fade ? Fades : Enabled) { target.BeginAnimation(property, To(to, ms, delayMs)); return; }
        target.BeginAnimation(property, null);
        ((DependencyObject)target).SetValue(property, to);
    }

    /// <summary>Jump to <paramref name="from"/>, then animate to <paramref name="to"/>; just sets <paramref name="to"/> when not allowed to animate.</summary>
    public static void FromTo(IAnimatable target, DependencyProperty property, double from, double to, double ms, double delayMs = 0, bool fade = false)
    {
        target.BeginAnimation(property, null);
        if (fade ? Fades : Enabled) ((DependencyObject)target).SetValue(property, from);
        Animate(target, property, to, ms, delayMs, fade);
    }
}
