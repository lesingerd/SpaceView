using System.Windows;
using System.Windows.Media.Animation;

namespace SpaceView.Models;

/// <summary>
/// Plays the fading activity highlight on an overlay element of a tree row. The element binds
/// <c>RowPulse.Stamp</c> to <see cref="TreeRowViewModel.ActivityStamp"/>; whenever the bound value changes (a new pulse, or
/// a recycled or newly realized container taking over another row) the fade is restarted from how old the stamp is, or
/// cleared if it is over. The animation does not hold its value afterwards (FillBehavior.Stop), so the overlay returns to
/// its own opacity of 0 and nothing can stay lit.
/// </summary>
public static class RowPulse
{
    private const double DurationMs = 1300;
    private const double PeakOpacity = 1.0;

    public static readonly DependencyProperty StampProperty = DependencyProperty.RegisterAttached(
        "Stamp", typeof(long), typeof(RowPulse), new PropertyMetadata(0L, OnStampChanged));

    public static long GetStamp(DependencyObject element) => (long)element.GetValue(StampProperty);
    public static void SetStamp(DependencyObject element, long value) => element.SetValue(StampProperty, value);

    private static void OnStampChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element)
            return;

        long stamp = (long)e.NewValue;
        double elapsed = Environment.TickCount64 - stamp;
        if (stamp == 0 || elapsed < 0 || elapsed >= DurationMs || !SystemParameters.ClientAreaAnimation)
        {
            element.BeginAnimation(UIElement.OpacityProperty, null); // stop a fade left from the row this container showed before
            return;
        }

        // A row that is realized part-way through its fade joins it where it would be
        double remaining = 1.0 - elapsed / DurationMs;
        var fade = new DoubleAnimation(PeakOpacity * remaining, 0.0, TimeSpan.FromMilliseconds(DurationMs - elapsed))
        {
            FillBehavior = FillBehavior.Stop
        };
        Timeline.SetDesiredFrameRate(fade, 30);
        element.BeginAnimation(UIElement.OpacityProperty, fade);
    }
}
