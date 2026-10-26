using Sempulse.Services;

namespace Sempulse.Layout;

// Physical screen pixels.
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
}

public sealed record DisplayInfo(PixelRect Bounds, PixelRect Work, double Scale);

public sealed record LayoutResult(bool Hidden, PanelMode EffectiveMode, PixelRect Bounds);

// Where the panel and its settings window go. Sizes in the settings are device independent (96 DPI) pixels,
// so each length is scaled by the target monitor; positions stay in physical pixels.
public static class PanelLayout
{
    public const int WidgetHeight = 156;
    public const int SettingsWidth = 320;

    static int Clamp(int value, int min, int max) => Math.Max(min, Math.Min(max, value));

    public static LayoutResult Compute(PanelSettings settings, bool fullscreen, DisplayInfo primary, DisplayInfo? secondary,
        (int X, int Y)? savedWidget)
    {
        if (fullscreen)
        {
            // A fullscreen app owns the primary display: float on the second display's lower left, or hide.
            if (secondary is null) return new LayoutResult(true, PanelMode.Widget, default);
            var area = secondary.Work;
            double scale = secondary.Scale;
            int width = (int)Math.Round(Math.Min(settings.WidgetWidth, Math.Max(320, area.Width / scale - 140)) * scale);
            int height = (int)Math.Round(WidgetHeight * scale);
            int inset = (int)Math.Round(70 * scale);
            return new LayoutResult(false, PanelMode.Widget, new PixelRect(
                Clamp(area.X + inset, area.X, area.Right - width),
                Clamp(area.Bottom - inset - height, area.Y, area.Bottom - height),
                width, height));
        }

        if (settings.Mode == PanelMode.Bar)
        {
            var bounds = primary.Bounds;
            int thickness = (int)Math.Round(settings.Thickness * primary.Scale);
            var rect = settings.Position switch
            {
                BarEdge.Left => new PixelRect(bounds.X, bounds.Y, thickness, bounds.Height),
                BarEdge.Right => new PixelRect(bounds.Right - thickness, bounds.Y, thickness, bounds.Height),
                BarEdge.Top => new PixelRect(bounds.X, bounds.Y, bounds.Width, thickness),
                _ => new PixelRect(bounds.X, bounds.Bottom - thickness, bounds.Width, thickness),
            };
            return new LayoutResult(false, PanelMode.Bar, rect);
        }

        {
            var area = primary.Work;
            double scale = primary.Scale;
            int width = (int)Math.Round(Math.Min(settings.WidgetWidth, Math.Max(320, area.Width / scale - 20)) * scale);
            int height = (int)Math.Round(WidgetHeight * scale);
            var rect = savedWidget is { } saved
                ? new PixelRect(Clamp(saved.X, area.X, area.Right - width), Clamp(saved.Y, area.Y, area.Bottom - height), width, height)
                : new PixelRect(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height);
            return new LayoutResult(false, PanelMode.Widget, rect);
        }
    }

    // The settings window opens beside the panel, on the side facing the screen centre.
    public static PixelRect Settings(PixelRect panel, PixelRect work, double scale, PanelMode mode, BarEdge position, int contentHeight)
    {
        int width = (int)Math.Round(SettingsWidth * scale);
        int height = Math.Min((int)Math.Round(contentHeight * scale), work.Height);
        int gap = (int)Math.Round(10 * scale), offset = (int)Math.Round(35 * scale);
        int x = panel.X, y = panel.Bottom + gap;
        if (mode == PanelMode.Bar)
        {
            (x, y) = position switch
            {
                BarEdge.Right => (panel.X - width - gap, panel.Y + offset),
                BarEdge.Left => (panel.Right + gap, panel.Y + offset),
                BarEdge.Top => (panel.X + offset, panel.Bottom + gap),
                _ => (panel.X + offset, panel.Y - height - gap),
            };
        }
        return new PixelRect(Clamp(x, work.X, work.Right - width), Clamp(y, work.Y, work.Bottom - height), width, height);
    }
}
