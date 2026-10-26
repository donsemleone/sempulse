using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace Sempulse.Controls;

public enum MeterStyle { Bar, Widget, Vertical }

// Icon, a thin fill bar and a percentage. Vertical bars fill from the bottom.
internal sealed class Meter : StackPanel
{
    readonly string label;
    readonly MeterStyle style;
    readonly Border fill;
    readonly double trackLength;
    readonly TextBlock percent;
    double shownLength = -1;

    public Meter(string icon, string label, MeterStyle style, bool showPercent)
    {
        this.label = label;
        this.style = style;
        bool vertical = style == MeterStyle.Vertical;
        Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal;
        HorizontalAlignment = vertical ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Center;
        Background = System.Windows.Media.Brushes.Transparent; // Tooltip over the whole row.

        trackLength = style switch { MeterStyle.Vertical => 24, MeterStyle.Widget => 24, _ => 53 };
        fill = new Border
        {
            Background = Ui.Accent,
            CornerRadius = new CornerRadius(3),
            HorizontalAlignment = vertical ? HorizontalAlignment.Stretch : HorizontalAlignment.Left,
            VerticalAlignment = vertical ? VerticalAlignment.Bottom : VerticalAlignment.Stretch,
            Width = vertical ? double.NaN : 0,
            Height = vertical ? 0 : double.NaN,
        };
        var track = new Border
        {
            Background = Ui.Track,
            CornerRadius = new CornerRadius(3),
            ClipToBounds = true,
            Width = vertical ? 4 : trackLength,
            Height = vertical ? trackLength : 5,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = fill,
        };
        percent = Ui.Text("—", style == MeterStyle.Widget ? 10 : 11);
        percent.HorizontalAlignment = HorizontalAlignment.Center;
        percent.VerticalAlignment = VerticalAlignment.Center;

        var iconElement = new LucideIcon(icon, 14, Ui.Secondary) { HorizontalAlignment = HorizontalAlignment.Center };
        double gap = vertical || style == MeterStyle.Widget ? 4 : 8;
        track.Margin = vertical ? new Thickness(0, gap, 0, 0) : new Thickness(gap, 0, 0, 0);
        percent.Margin = vertical ? new Thickness(0, gap, 0, 0) : new Thickness(gap, 0, 0, 0);
        Children.Add(iconElement);
        Children.Add(track);
        if (showPercent) Children.Add(percent);
        Update(null);
    }

    public void Update(double? value, string? detail = null)
    {
        bool valid = value is double number && double.IsFinite(number);
        double clamped = valid ? Math.Clamp(value!.Value, 0, 100) : 0;
        int rounded = (int)Math.Round(clamped);
        percent.Text = valid ? $"{rounded}%" : "—";
        ToolTip = $"{label}: " + (valid ? $"{rounded}%" + (detail is null ? "" : $" ({detail})") : "Ölçüm yok");

        double length = Math.Round(trackLength * clamped / 100);
        if (length == shownLength) return; // Same pixel: no redraw of the (transparent, fully repainted) window.
        shownLength = length;
        var animation = new DoubleAnimation(length, TimeSpan.FromMilliseconds(300)) { EasingFunction = new QuadraticEase() };
        Timeline.SetDesiredFrameRate(animation, 30);
        fill.BeginAnimation(style == MeterStyle.Vertical ? HeightProperty : WidthProperty, animation);
    }
}
