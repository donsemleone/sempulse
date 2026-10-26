using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Sempulse.Controls;
using Sempulse.Services;
using MediaState = Sempulse.Services.MediaState;

namespace Sempulse.Views;

public interface IPanelActions
{
    void ToggleSettings();
    void MediaControl(string target, string action);
    void MoveDesktopIcons();
    void UndoDesktopIcons();
    void SetPetMode(string mode);
}

// The panel's content in one of three shapes: horizontal bar, vertical bar or floating widget.
// Built in code because the three layouts differ in structure, not just in style.
internal sealed class PanelView : Border
{
    static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    static readonly (string Mode, string Icon, string Label)[] PetButtons =
    [
        ("auto", "Shuffle", "Kedi kendi seçsin"),
        ("walk", "Footprints", "Kedi yürüsün"),
        ("sit", "Cat", "Kedi otursun"),
        ("sleep", "Moon", "Kedi uyusun"),
        ("jump", "Rabbit", "Kedi zıplasın"),
        ("puff", "Zap", "Kedi tüylerini kabartsın"),
        ("play", "MousePointer2", "Kedi fare imleciyle oynasın"),
    ];

    readonly PanelSettings settings;
    readonly IPanelActions actions;
    readonly bool vertical, widget, thin;
    readonly MeterStyle meterStyle;

    Meter cpu = null!, memory = null!, gpu = null!, vram = null!;
    TextBlock netDown = null!, netUp = null!;
    readonly List<MediaGroupView> mediaGroups = [];
    Button moveButton = null!, undoButton = null!;
    readonly Dictionary<string, Button> petButtons = [];
    FrameworkElement weatherBox = null!;
    LucideIcon weatherIcon = null!;
    TextBlock weatherTemp = null!, clock = null!;
    readonly List<TextBlock> dateParts = [];

    public PanelView(PanelSettings settings, PanelMode effectiveMode, IPanelActions actions)
    {
        this.settings = settings;
        this.actions = actions;
        widget = effectiveMode == PanelMode.Widget;
        vertical = !widget && settings.Vertical;
        thin = !widget && settings.Thickness < 60;
        meterStyle = widget ? MeterStyle.Widget : vertical ? MeterStyle.Vertical : MeterStyle.Bar;

        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        UseLayoutRounding = true;
        bool clear = settings.Opacity == 0;
        // Never fully transparent: a 1/255 fill keeps the whole panel clickable, like the old window.
        Background = Ui.Frozen(Color.FromArgb((byte)Math.Max(1, Math.Round(settings.Opacity * 2.55)), 20, 20, 25));
        BorderBrush = Ui.BorderBrush;

        if (widget)
        {
            CornerRadius = new CornerRadius(12);
            BorderThickness = clear ? new Thickness(0) : new Thickness(1);
            Padding = new Thickness(16, 12, 16, 12);
            Child = BuildWidget();
        }
        else if (vertical)
        {
            BorderThickness = clear ? new Thickness(0)
                : new Thickness(settings.Position == BarEdge.Left ? 0 : 1, 0, settings.Position == BarEdge.Right ? 0 : 1, 0);
            Padding = new Thickness(0, 15, 0, 15);
            Child = BuildVertical();
        }
        else
        {
            BorderThickness = clear ? new Thickness(0)
                : new Thickness(0, settings.Position == BarEdge.Top ? 0 : 1, 0, settings.Position == BarEdge.Bottom ? 0 : 1);
            Padding = new Thickness(15, 0, 15, 0);
            Child = BuildHorizontal();
        }
    }

    // ---- Layouts ----------------------------------------------------------------

    UIElement BuildHorizontal()
    {
        var grid = new Grid { VerticalAlignment = VerticalAlignment.Center };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < 3; i++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var settingsButton = SettingsButton();
        settingsButton.Margin = new Thickness(0, 0, 24, 0);
        var top = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        top.Children.Add(settingsButton);
        top.Children.Add(BuildStats(15));
        Place(grid, top, 0);

        Place(grid, BuildMedia(), 1);
        var desktop = BuildDesktopActions();
        desktop.Margin = new Thickness(18, 0, 18, 0);
        Place(grid, desktop, 2);
        Place(grid, BuildClock(), 3);
        return grid;
    }

    UIElement BuildVertical()
    {
        var grid = new Grid();
        foreach (var height in new[] { GridLength.Auto, Star, GridLength.Auto, Star, GridLength.Auto, Star, GridLength.Auto })
            grid.RowDefinitions.Add(new RowDefinition { Height = height });

        var settingsButton = SettingsButton();
        settingsButton.HorizontalAlignment = HorizontalAlignment.Center;
        settingsButton.Margin = new Thickness(0, 0, 0, 24);
        var top = new StackPanel { Margin = new Thickness(0, 0, 0, 22) };
        top.Children.Add(settingsButton);
        top.Children.Add(BuildStats(20));
        Place(grid, top, row: 0);

        Place(grid, BuildMedia(), row: 2);
        var desktop = BuildDesktopActions();
        desktop.Margin = new Thickness(0, 10, 0, 10);
        Place(grid, desktop, row: 4);
        var clockSection = BuildClock();
        clockSection.Margin = new Thickness(0, 0, 0, 8);
        Place(grid, clockSection, row: 6);
        return grid;
    }

    UIElement BuildWidget()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = Star });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
        grid.RowDefinitions.Add(new RowDefinition { Height = Star });

        var settingsButton = SettingsButton();
        settingsButton.Margin = new Thickness(0, 0, 12, 0);
        var top = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        top.Children.Add(settingsButton);
        top.Children.Add(BuildStats(10));
        Place(grid, top, 0, 0);

        var clockSection = BuildClock();
        clockSection.MinWidth = 66;
        clockSection.HorizontalAlignment = HorizontalAlignment.Right;
        Place(grid, clockSection, 2, 0);

        Place(grid, TopRule(BuildMedia()), 0, 2);
        Place(grid, TopRule(BuildDesktopActions()), 2, 2);
        return grid;
    }

    static readonly GridLength Star = new(1, GridUnitType.Star);

    static void Place(Grid grid, UIElement element, int column = 0, int row = 0)
    {
        Grid.SetColumn(element, column);
        Grid.SetRow(element, row);
        grid.Children.Add(element);
    }

    static Border TopRule(UIElement child) => new()
    {
        BorderBrush = Ui.BorderBrush,
        BorderThickness = new Thickness(0, 1, 0, 0),
        Padding = new Thickness(0, 10, 0, 0),
        Child = child,
    };

    Button SettingsButton()
    {
        var button = new Button
        {
            Style = (Style)Application.Current.FindResource("SettingsToggle"),
            ToolTip = "Ayarlar",
            VerticalAlignment = VerticalAlignment.Center,
            Content = new LucideIcon("Settings", 15, Ui.Secondary),
        };
        button.SetBinding(ForegroundProxy.BrushProperty, new System.Windows.Data.Binding(nameof(Button.Foreground)) { Source = button });
        System.Windows.Automation.AutomationProperties.SetName(button, "Ayarlar");
        button.Click += (_, _) => actions.ToggleSettings();
        return button;
    }

    // ---- Stats ----------------------------------------------------------------------

    UIElement BuildStats(double gap)
    {
        bool showPercent = !thin;
        cpu = new Meter("Cpu", "CPU", meterStyle, showPercent);
        memory = new Meter("MemoryStick", "Bellek", meterStyle, showPercent);
        gpu = new Meter("Gauge", "GPU",meterStyle, showPercent);
        vram = new Meter("Microchip", "VRAM", meterStyle, showPercent);

        double netSize = widget || vertical ? 9 : 11;
        netDown = Ui.Text("↓ 0 B", netSize);
        netUp = Ui.Text("↑ 0 B", netSize);
        var netInfo = vertical
            ? Ui.Stack(Orientation.Vertical, 2, netDown, netUp)
            : widget ? Ui.Stack(Orientation.Vertical, 1, netDown, netUp) : Ui.Stack(Orientation.Horizontal, 12, netDown, netUp);
        netInfo.VerticalAlignment = VerticalAlignment.Center;
        netInfo.HorizontalAlignment = HorizontalAlignment.Center;
        if (vertical) { netDown.HorizontalAlignment = HorizontalAlignment.Center; netUp.HorizontalAlignment = HorizontalAlignment.Center; }
        var wifi = new LucideIcon("Wifi", 14, Ui.Secondary) { HorizontalAlignment = HorizontalAlignment.Center };
        var net = Ui.Stack(vertical ? Orientation.Vertical : Orientation.Horizontal, vertical ? 4 : widget ? 4 : 8, wifi, netInfo);
        net.Background = Brushes.Transparent;
        net.ToolTip = "İnternet aktarım hızı";
        net.HorizontalAlignment = vertical ? HorizontalAlignment.Center : HorizontalAlignment.Left;

        if (vertical) return Ui.Stack(Orientation.Vertical, gap, cpu, memory, gpu, vram, net);
        // Meters share equal cells so the bars line up; the network figures take what they need.
        var meters = new UniformGrid { Rows = 1, VerticalAlignment = VerticalAlignment.Center };
        foreach (var meter in new[] { cpu, memory, gpu, vram })
        {
            meter.Margin = new Thickness(0, 0, gap, 0);
            meters.Children.Add(meter);
        }
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(meters);
        row.Children.Add(net);
        return row;
    }

    public void UpdateStats(StatsSnapshot stats)
    {
        cpu.Update(stats.CpuLoad);
        memory.Update(stats.MemTotal > 0 ? stats.MemUsed * 100.0 / stats.MemTotal : null);
        gpu.Update(stats.GpuLoad);
        double? vramPercent = stats.VramTotal is > 0 && stats.VramUsed is ulong used ? used * 100.0 / stats.VramTotal.Value : null;
        vram.Update(vramPercent, stats.VramTotal is > 0 ? $"{FormatBytes(stats.VramUsed ?? 0)} / {FormatBytes(stats.VramTotal.Value)}" : null);
        string suffix = vertical ? "" : "/s";
        netDown.Text = $"↓ {FormatBytes(stats.NetRx)}{suffix}";
        netUp.Text = $"↑ {FormatBytes(stats.NetTx)}{suffix}";
    }

    public static string FormatBytes(double bytes)
    {
        if (!double.IsFinite(bytes) || bytes <= 0) return "0 B";
        int unit = Math.Min(3, (int)Math.Floor(Math.Log(bytes) / Math.Log(1024)));
        return (bytes / Math.Pow(1024, unit)).ToString("0.0", CultureInfo.InvariantCulture) + " " + new[] { "B", "KB", "MB", "GB" }[unit];
    }

    // ---- Media ------------------------------------------------------------------------

    UIElement BuildMedia()
    {
        mediaGroups.Clear();
        var groups = settings.MediaMode == MediaLayout.Separate
            ? new[] { new MediaGroupView("Spotify", "spotify", meterStyle, actions), new MediaGroupView("Diğer medya", "other", meterStyle, actions) }
            : new[] { new MediaGroupView("Aktif medya", "global", meterStyle, actions) };
        mediaGroups.AddRange(groups);

        Border Divider() => new()
        {
            Background = Ui.Divider,
            Width = vertical ? 22 : 1,
            Height = vertical ? 1 : widget ? 48 : 22,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        if (widget)
        {
            var grid = new Grid { VerticalAlignment = VerticalAlignment.Center };
            for (int i = 0; i < groups.Length; i++)
            {
                if (i > 0)
                {
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var divider = Divider();
                    divider.Margin = new Thickness(12, 0, 12, 0);
                    Place(grid, divider, grid.ColumnDefinitions.Count - 1);
                }
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = Star });
                Place(grid, groups[i], grid.ColumnDefinitions.Count - 1);
            }
            return grid;
        }
        var children = new List<UIElement>();
        for (int i = 0; i < groups.Length; i++)
        {
            if (i > 0) children.Add(Divider());
            children.Add(groups[i]);
        }
        var stack = Ui.Stack(vertical ? Orientation.Vertical : Orientation.Horizontal, 12, [.. children]);
        stack.VerticalAlignment = VerticalAlignment.Center;
        return stack;
    }

    public void UpdateMedia(MediaState state)
    {
        if (settings.MediaMode == MediaLayout.Separate && mediaGroups.Count == 2)
        {
            mediaGroups[0].Update(state.Spotify);
            mediaGroups[1].Update(state.Other);
        }
        else if (mediaGroups.Count == 1) mediaGroups[0].Update(state.Active);
    }

    // ---- Desktop icons and the cat --------------------------------------------------

    FrameworkElement BuildDesktopActions()
    {
        double padding = vertical ? 4 : 5;
        moveButton = Ui.IconButton("FolderInput", 16, "Masaüstü simgelerini ikinci ekrana taşı", actions.MoveDesktopIcons, padding);
        undoButton = Ui.IconButton("Undo2", 16, "Masaüstü simgelerini önceki konumlarına geri al", actions.UndoDesktopIcons, padding);
        undoButton.IsEnabled = false;
        var icons = Ui.Stack(vertical && thin ? Orientation.Vertical : Orientation.Horizontal, 3, moveButton, undoButton);
        icons.HorizontalAlignment = HorizontalAlignment.Center;
        icons.VerticalAlignment = VerticalAlignment.Center;

        petButtons.Clear();
        if (!settings.Pet) return icons;
        foreach (var (mode, icon, label) in PetButtons)
            petButtons[mode] = Ui.IconButton(icon, 15, label, () => actions.SetPetMode(mode), padding);

        Panel pets;
        if (widget)
        {
            pets = new UniformGrid { Columns = 4, Margin = new Thickness(18, 0, 0, 0) };
            foreach (var button in petButtons.Values) { button.Margin = new Thickness(0, 0, 3, 3); pets.Children.Add(button); }
        }
        else if (vertical)
        {
            // Wraps on wide vertical bars instead of spilling past the edge.
            pets = new WrapPanel
            {
                Orientation = thin ? Orientation.Vertical : Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 22, 0, 0),
                MaxWidth = settings.Thickness - 6,
            };
            foreach (var button in petButtons.Values) { button.Margin = new Thickness(1.5); pets.Children.Add(button); }
        }
        else
        {
            pets = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(22, 0, 0, 0) };
            foreach (var button in petButtons.Values) { button.Margin = new Thickness(3, 0, 0, 0); pets.Children.Add(button); }
            petButtons.Values.First().Margin = new Thickness(0);
        }
        pets.VerticalAlignment = VerticalAlignment.Center;
        var all = new StackPanel
        {
            Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        all.Children.Add(icons);
        all.Children.Add(pets);
        return all;
    }

    public void UpdateDesktopIcons(bool busy, bool canUndo, int? lastCount)
    {
        moveButton.IsEnabled = !busy;
        undoButton.IsEnabled = !busy && canUndo;
        moveButton.ToolTip = lastCount is int count
            ? $"{count} simge taşındı; yeni simgeler için tekrar basın"
            : "Masaüstü simgelerini ikinci ekrana taşı";
    }

    public void UpdatePetMode(string mode)
    {
        foreach (var (name, button) in petButtons)
            button.Foreground = name == mode ? Ui.PetActive : Ui.Primary;
    }

    // ---- Clock and weather ----------------------------------------------------------

    FrameworkElement BuildClock()
    {
        weatherIcon = new LucideIcon("Cloud", 13, Ui.Secondary) { HorizontalAlignment = HorizontalAlignment.Center };
        weatherTemp = Ui.Text("", vertical ? 10 : 11, Ui.Secondary);
        weatherTemp.HorizontalAlignment = HorizontalAlignment.Center;
        var weather = vertical ? Ui.Stack(Orientation.Vertical, 2, weatherIcon, weatherTemp) : Ui.Stack(Orientation.Horizontal, 4, weatherIcon, weatherTemp);
        weather.HorizontalAlignment = HorizontalAlignment.Center;
        weather.VerticalAlignment = VerticalAlignment.Center;
        weather.Background = Brushes.Transparent;
        weather.Visibility = Visibility.Collapsed;
        weatherBox = weather;

        clock = Ui.Text("", widget ? 18 : vertical ? 10 : 16, Ui.Primary, widget ? FontWeights.ExtraLight : FontWeights.Normal);
        clock.HorizontalAlignment = HorizontalAlignment.Center;
        clock.VerticalAlignment = VerticalAlignment.Center;

        dateParts.Clear();
        int dateCount = vertical ? 3 : 1;
        for (int i = 0; i < dateCount; i++)
        {
            var part = Ui.Text("", vertical || widget ? 9 : 10);
            part.HorizontalAlignment = HorizontalAlignment.Center;
            dateParts.Add(part);
        }
        var date = Ui.Stack(Orientation.Vertical, 4, [.. dateParts]);
        date.HorizontalAlignment = HorizontalAlignment.Center;
        date.VerticalAlignment = VerticalAlignment.Center;

        if (!vertical && !widget)
        {
            date.Margin = new Thickness(0, 4, 0, 0);
            var row = Ui.Stack(Orientation.Horizontal, 10, weather, clock, date);
            row.VerticalAlignment = VerticalAlignment.Center;
            return row;
        }
        if (vertical) weather.Margin = new Thickness(0, 0, 0, 4);
        else weather.Margin = new Thickness(0, 0, 0, 2);
        var column = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        column.Children.Add(weather);
        column.Children.Add(clock);
        column.Children.Add(date);
        return column;
    }

    public void UpdateClock(DateTime now)
    {
        clock.Text = now.ToString("HH:mm", Turkish);
        if (vertical)
        {
            dateParts[0].Text = now.ToString("ddd", Turkish).ToUpper(Turkish);
            dateParts[1].Text = now.ToString("MMM", Turkish).ToUpper(Turkish);
            dateParts[2].Text = now.Day.ToString(Turkish);
        }
        else dateParts[0].Text = now.ToString("d MMM ddd", Turkish).ToUpper(Turkish);
    }

    public void UpdateWeather(WeatherInfo? weather)
    {
        if (weather is null)
        {
            weatherBox.Visibility = Visibility.Collapsed;
            return;
        }
        var now = WeatherService.Describe(weather.Code, weather.IsDay);
        weatherIcon.Icon = now.Icon.ToString();
        int temp = (int)Math.Round(weather.Temp, MidpointRounding.AwayFromZero);
        weatherTemp.Text = $"{temp}°";
        string range = "";
        if (weather.Min is double min && weather.Max is double max)
        {
            var today = WeatherService.Describe(weather.DailyCode ?? weather.Code, true);
            range = $", bugün {Math.Round(min, MidpointRounding.AwayFromZero)}° – {Math.Round(max, MidpointRounding.AwayFromZero)}°, {today.Text.ToLower(Turkish)}";
        }
        weatherBox.ToolTip = $"İstanbul: {now.Text}, şu an {temp}°{range}";
        weatherBox.Visibility = Visibility.Visible;
    }
}

// Title, artist and prev/play/next for one media source.
internal sealed class MediaGroupView : Grid
{
    readonly string label;
    readonly MarqueeText title, artist;
    readonly LucideIcon playIcon;

    public MediaGroupView(string label, string target, MeterStyle style, IPanelActions actions)
    {
        this.label = label;
        bool vertical = style == MeterStyle.Vertical, widget = style == MeterStyle.Widget;
        var alignment = vertical ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        title = new MarqueeText(11, Ui.Primary, FontWeights.SemiBold, alignment);
        artist = new MarqueeText(9, Ui.Secondary, FontWeights.Normal, alignment);

        var previous = Ui.IconButton("SkipBack", 14, $"{label}: önceki", () => actions.MediaControl(target, "prev"));
        var next = Ui.IconButton("SkipForward", 14, $"{label}: sonraki", () => actions.MediaControl(target, "next"));
        playIcon = new LucideIcon("Play", 14, Brushes.Black, filled: true);
        var (fill, hover) = target switch
        {
            "spotify" => (Color.FromRgb(0x1D, 0xB9, 0x54), Color.FromRgb(0x1E, 0xD7, 0x60)),
            "other" => (Color.FromRgb(0xC0, 0x84, 0xFC), Color.FromRgb(0xD8, 0xB4, 0xFE)),
            _ => (Colors.White, Color.FromRgb(0xE0, 0xE0, 0xE0)),
        };
        var play = new Button
        {
            Style = (Style)Application.Current.FindResource("PlayButton"),
            Background = Ui.Frozen(fill),
            Tag = Ui.Frozen(hover),
            Content = playIcon,
            ToolTip = $"{label}: oynat/duraklat",
        };
        System.Windows.Automation.AutomationProperties.SetName(play, $"{label}: oynat/duraklat");
        play.Click += (_, _) => actions.MediaControl(target, "play-pause");
        var buttons = Ui.Stack(vertical ? Orientation.Vertical : Orientation.Horizontal, 4, previous, play, next);
        buttons.VerticalAlignment = VerticalAlignment.Center;
        buttons.HorizontalAlignment = HorizontalAlignment.Center;

        if (vertical)
        {
            var info = new StackPanel();
            info.Children.Add(title);
            info.Children.Add(artist);
            buttons.Margin = new Thickness(0, 6, 0, 0);
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            SetRow(buttons, 1);
            Children.Add(info);
            Children.Add(buttons);
        }
        else if (widget)
        {
            var labelText = Ui.Text(label.ToUpper(CultureInfo.GetCultureInfo("tr-TR")), 9, Ui.Secondary, FontWeights.Bold);
            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            info.Children.Add(labelText);
            info.Children.Add(title);
            info.Children.Add(artist);
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            SetColumn(buttons, 1);
            Children.Add(info);
            Children.Add(buttons);
        }
        else
        {
            // Title and artist side by side, together at most 180 px, then the buttons.
            title.MaxWidth = 120;
            artist.MaxWidth = 60;
            artist.Margin = new Thickness(6, 2, 0, 0);
            var info = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(title);
            info.Children.Add(artist);
            buttons.Margin = new Thickness(8, 0, 0, 0);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(info);
            row.Children.Add(buttons);
            Children.Add(row);
        }
        Update(null);
    }

    public void Update(MediaInfo? media)
    {
        title.Value = string.IsNullOrEmpty(media?.Title) ? label : media.Title;
        artist.Value = media?.Artist ?? "";
        artist.Visibility = string.IsNullOrEmpty(media?.Artist) ? Visibility.Collapsed : Visibility.Visible;
        playIcon.Icon = media?.IsPlaying == true ? "Pause" : "Play";
    }
}
