using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Sempulse.Controls;

// One line of text. When it does not fit, it slides to its end and back, pausing at both ends;
// with "reduce animations" turned on in Windows it is cut with an ellipsis instead.
internal sealed class MarqueeText : Border
{
    readonly TextBlock text;
    readonly TranslateTransform shift = new();
    readonly HorizontalAlignment restingAlignment;
    double runningDistance = -1;

    public MarqueeText(double size, Brush brush, FontWeight weight, HorizontalAlignment alignment)
    {
        restingAlignment = alignment;
        ClipToBounds = true;
        text = Ui.Text("", size, brush, weight);
        text.RenderTransform = shift;
        text.TextTrimming = TextTrimming.CharacterEllipsis;
        text.HorizontalAlignment = alignment;
        Child = text;
        SizeChanged += (_, _) => Restart();
    }

    public string Value
    {
        get => text.Text;
        set
        {
            if (text.Text == value) return;
            text.Text = value;
            ToolTip = value;
            runningDistance = -1;
            Restart();
        }
    }

    void Restart()
    {
        if (ActualWidth <= 0) return;
        text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double overflow = Math.Ceiling(text.DesiredSize.Width - ActualWidth);
        bool scroll = overflow > 1 && SystemParameters.ClientAreaAnimation;
        if (scroll && overflow == runningDistance) return;
        runningDistance = scroll ? overflow : -1;
        shift.BeginAnimation(TranslateTransform.XProperty, null);
        shift.X = 0;
        if (!scroll)
        {
            text.Width = double.NaN;
            text.HorizontalAlignment = restingAlignment;
            text.TextTrimming = TextTrimming.CharacterEllipsis;
            return;
        }
        // Unclipped text, left aligned, sliding by exactly the hidden width.
        text.TextTrimming = TextTrimming.None;
        text.HorizontalAlignment = HorizontalAlignment.Left;
        text.Width = text.DesiredSize.Width;
        double seconds = Math.Max(7, Math.Min(22, 5 + overflow / 30));
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        var animation = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromSeconds(seconds),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
        };
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromPercent(0)));
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromPercent(0.15)));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(-overflow, KeyTime.FromPercent(0.85), ease));
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(-overflow, KeyTime.FromPercent(1)));
        // A transparent window is redrawn in full for every frame; 24 fps keeps the slide smooth at a fraction of the cost.
        Timeline.SetDesiredFrameRate(animation, 24);
        animation.Freeze();
        shift.BeginAnimation(TranslateTransform.XProperty, animation);
    }
}
