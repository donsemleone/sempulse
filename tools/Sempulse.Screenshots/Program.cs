using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Sempulse.Services;
using Sempulse.Views;

namespace Sempulse.Screenshots;

// Draws each panel layout with fixed demo values onto a dusk gradient and saves PNGs for the README.
static class Program
{
    const double Scale = 2; // Retina-sharp images.

    sealed class NoActions : IPanelActions
    {
        public void ToggleSettings() { }
        public void MediaControl(string target, string action) { }
        public void MoveDesktopIcons() { }
        public void UndoDesktopIcons() { }
        public void SetPetMode(string mode) { }
    }

    static readonly StatsSnapshot Stats = new(23, 9_400_000_000, 16_000_000_000, 41, 3_100_000_000, 8_000_000_000, 1_258_291, 48_128);
    static readonly MediaState Media = new(
        new MediaInfo("Midnight City", "M83", true, true),
        new MediaInfo("Lo-fi beats to study to", "Chillhop", false, true),
        new MediaInfo("Midnight City", "M83", true, true));
    static readonly WeatherInfo Weather = new(18.4, 2, true, 21, 13, 3);
    static readonly DateTime Now = new(2026, 10, 26, 21, 45, 0);

    [STAThread]
    static void Main(string[] args)
    {
        string output = args.Length > 0 ? args[0] : Path.Combine("docs", "media");
        Directory.CreateDirectory(output);
        var app = new App();
        app.InitializeComponent();

        Save(Panel(new PanelSettings { Position = BarEdge.Top, Thickness = 44, Opacity = 70 }, PanelMode.Bar, 1600, 44),
            Path.Combine(output, "bar-horizontal.png"), padding: 0);
        Save(Panel(new PanelSettings { Position = BarEdge.Right, Thickness = 44, Opacity = 70 }, PanelMode.Bar, 44, 1040),
            Path.Combine(output, "bar-vertical.png"), padding: 0);
        Save(Panel(new PanelSettings { Mode = PanelMode.Widget, WidgetWidth = 600, Opacity = 70, PetMode = "sleep" }, PanelMode.Widget, 600, 156),
            Path.Combine(output, "widget.png"), padding: 24);

        var settingsWindow = new SettingsWindow(() => new PanelSettings(), _ => { }, () => { });
        var settings = (FrameworkElement)settingsWindow.Content;
        settingsWindow.Content = null;
        settings.Width = 320;
        Save(settings, Path.Combine(output, "settings.png"), padding: 24);
        SaveCatPoses(Path.Combine(output, "cat-poses.png"));
        Console.WriteLine($"Images written to {Path.GetFullPath(output)}");
    }

    // Walk, sit, yawn, sleep, puff and jump side by side on the same dusk background.
    static void SaveCatPoses(string file)
    {
        (string Pose, int Z)[] poses = [("walk-a", 0), ("sit", 0), ("yawn", 0), ("sleep", 3), ("puff", 0), ("jump", 0)];
        const int scale = 6, gap = 36, margin = 40;
        var frames = poses.Select(pose => Sempulse.Pet.PetForm.RenderPose(pose.Pose, scale, pose.Z)).ToList();
        int width = frames.Sum(frame => frame.Width) + gap * (frames.Count - 1) + margin * 2;
        int height = frames.Max(frame => frame.Height) + margin * 2;
        using var sheet = new System.Drawing.Bitmap(width, height);
        using (var graphics = System.Drawing.Graphics.FromImage(sheet))
        {
            using var background = new System.Drawing.Drawing2D.LinearGradientBrush(new System.Drawing.Rectangle(0, 0, width, height),
                System.Drawing.Color.FromArgb(0x3B, 0x2A, 0x5C), System.Drawing.Color.FromArgb(0xC8, 0x5A, 0x4A), 35f);
            graphics.FillRectangle(background, 0, 0, width, height);
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            int x = margin;
            foreach (var frame in frames)
            {
                graphics.DrawImageUnscaled(frame, x, height - margin - frame.Height);
                x += frame.Width + gap;
                frame.Dispose();
            }
        }
        sheet.Save(file, System.Drawing.Imaging.ImageFormat.Png);
        Console.WriteLine($"{Path.GetFileName(file)} {width}x{height}");
    }

    static FrameworkElement Panel(PanelSettings settings, PanelMode mode, double width, double height)
    {
        var view = new PanelView(settings.Normalize(), mode, new NoActions()) { Width = width, Height = height };
        view.UpdateStats(Stats);
        view.UpdateMedia(Media);
        view.UpdateWeather(Weather);
        view.UpdateClock(Now);
        view.UpdateDesktopIcons(false, true, null);
        view.UpdatePetMode(settings.PetMode);
        return view;
    }

    static void Save(FrameworkElement element, string file, double padding)
    {
        element.Measure(new Size(element.Width, double.PositiveInfinity));
        double width = element.Width, height = double.IsNaN(element.Height) ? element.DesiredSize.Height : element.Height;
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();

        var canvas = new DrawingVisual();
        using (var context = canvas.RenderOpen())
        {
            var area = new Rect(0, 0, width + padding * 2, height + padding * 2);
            context.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x3B, 0x2A, 0x5C), Color.FromRgb(0xC8, 0x5A, 0x4A), 35), null, area);
            context.DrawRectangle(new VisualBrush(element) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
                null, new Rect(padding, padding, width, height));
        }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling((width + padding * 2) * Scale), (int)Math.Ceiling((height + padding * 2) * Scale),
            96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        bitmap.Render(canvas);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(file);
        encoder.Save(stream);
        Console.WriteLine($"{Path.GetFileName(file)} {bitmap.PixelWidth}x{bitmap.PixelHeight}");
    }
}
