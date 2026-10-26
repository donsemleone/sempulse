using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Sempulse.Controls;

// Small factories so the code-built panel reads like the markup it replaces.
internal static class Ui
{
    public static readonly FontFamily Font = new("Segoe UI");
    public static readonly Brush Primary = Frozen(Colors.White);
    public static readonly Brush Secondary = Frozen(Color.FromRgb(0xA0, 0xA0, 0xA5));
    public static readonly Brush Accent = Frozen(Color.FromRgb(0x1D, 0xB9, 0x54));
    public static readonly Brush PetActive = Frozen(Color.FromRgb(0xFF, 0xD2, 0x3F));
    public static readonly Brush Track = Frozen(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));
    public static readonly Brush Divider = Frozen(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
    public static readonly Brush BorderBrush = Frozen(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));

    public static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public static TextBlock Text(string text, double size, Brush? brush = null, FontWeight? weight = null) => new()
    {
        Text = text,
        FontFamily = Font,
        FontSize = size,
        FontWeight = weight ?? FontWeights.Normal,
        Foreground = brush ?? Primary,
        TextWrapping = TextWrapping.NoWrap,
    };

    public static Button IconButton(string icon, double size, string tooltip, Action onClick, double padding = 5)
    {
        var button = new Button
        {
            Style = (Style)Application.Current.FindResource("IconButton"),
            Padding = new Thickness(padding),
            ToolTip = tooltip,
        };
        button.Content = new LucideIcon(icon, size, Primary);
        // The icon follows the button's foreground (white, or yellow for the active cat mode).
        button.SetBinding(ForegroundProxy.BrushProperty, new System.Windows.Data.Binding(nameof(Button.Foreground)) { Source = button });
        button.Click += (_, _) => onClick();
        return button;
    }

    public static StackPanel Stack(Orientation orientation, double gap, params UIElement[] children)
    {
        var panel = new StackPanel { Orientation = orientation };
        for (int i = 0; i < children.Length; i++)
        {
            if (i > 0 && children[i] is FrameworkElement element)
            {
                var margin = element.Margin;
                element.Margin = orientation == Orientation.Horizontal
                    ? new Thickness(margin.Left + gap, margin.Top, margin.Right, margin.Bottom)
                    : new Thickness(margin.Left, margin.Top + gap, margin.Right, margin.Bottom);
            }
            panel.Children.Add(children[i]);
        }
        return panel;
    }
}

// Lets a button's Foreground recolor the LucideIcon inside it.
internal static class ForegroundProxy
{
    public static readonly DependencyProperty BrushProperty = DependencyProperty.RegisterAttached(
        "Brush", typeof(Brush), typeof(ForegroundProxy),
        new PropertyMetadata(null, (target, args) =>
        {
            if (target is ContentControl { Content: LucideIcon icon } && args.NewValue is Brush brush) icon.Brush = brush;
        }));
}
