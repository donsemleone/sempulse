using System.IO;
using Sempulse.Services;
using Sempulse.Views;

namespace Sempulse.Tests;

public class SettingsTests
{
    [Fact]
    public void Normalize_clamps_every_range()
    {
        var settings = new PanelSettings { Thickness = 5, WidgetWidth = 2000, Opacity = -3, PetMode = "dance" }.Normalize();
        Assert.Equal(40, settings.Thickness);
        Assert.Equal(900, settings.WidgetWidth);
        Assert.Equal(0, settings.Opacity);
        Assert.Equal("auto", settings.PetMode);
    }

    [Fact]
    public void Store_round_trips_and_survives_a_damaged_file()
    {
        string directory = Path.Combine(Path.GetTempPath(), "sempulse-tests-" + Guid.NewGuid());
        try
        {
            var store = new SettingsStore(directory);
            Assert.Equal(new PanelSettings(), store.Load());

            var saved = new PanelSettings { Mode = PanelMode.Widget, Position = BarEdge.Top, Thickness = 80, Pet = false, PetMode = "sleep" };
            store.Save(saved);
            Assert.Equal(saved, store.Load());
            Assert.Contains("\"mode\": \"widget\"", File.ReadAllText(Path.Combine(directory, "settings.json")));

            File.WriteAllText(Path.Combine(directory, "settings.json"), "{ not json");
            Assert.Equal(new PanelSettings(), store.Load());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}

public class FormattingTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(-5, "0 B")]
    [InlineData(512, "512.0 B")]
    [InlineData(123456, "120.6 KB")]
    [InlineData(10 * 1024 * 1024, "10.0 MB")]
    [InlineData(5L * 1024 * 1024 * 1024 * 1024, "5120.0 GB")]
    public void FormatBytes_matches_the_old_panel(double bytes, string expected) =>
        Assert.Equal(expected, PanelView.FormatBytes(bytes));
}

public class WeatherTests
{
    [Theory]
    [InlineData(0, true, WeatherIcon.Sun, "Açık")]
    [InlineData(0, false, WeatherIcon.Moon, "Açık")]
    [InlineData(1, true, WeatherIcon.CloudSun, "Az bulutlu")]
    [InlineData(2, false, WeatherIcon.CloudMoon, "Parçalı bulutlu")]
    [InlineData(3, true, WeatherIcon.Cloud, "Kapalı")]
    [InlineData(45, true, WeatherIcon.CloudFog, "Sisli")]
    [InlineData(53, true, WeatherIcon.CloudDrizzle, "Çisenti")]
    [InlineData(63, true, WeatherIcon.CloudRain, "Yağmurlu")]
    [InlineData(81, true, WeatherIcon.CloudRain, "Sağanak")]
    [InlineData(73, true, WeatherIcon.CloudSnow, "Karlı")]
    [InlineData(86, true, WeatherIcon.CloudSnow, "Karlı")]
    [InlineData(95, true, WeatherIcon.CloudLightning, "Gök gürültülü")]
    [InlineData(83, true, WeatherIcon.Cloud, "Bulutlu")]
    public void Describe_groups_wmo_codes(int code, bool isDay, WeatherIcon icon, string text) =>
        Assert.Equal((icon, text), WeatherService.Describe(code, isDay));

    [Fact]
    public void Parse_reads_current_and_daily_values()
    {
        const string json = """
            {"current":{"temperature_2m":17.4,"weather_code":2,"is_day":1},
             "daily":{"temperature_2m_max":[19.1],"temperature_2m_min":[12.3],"weather_code":[3]}}
            """;
        Assert.Equal(new WeatherInfo(17.4, 2, true, 19.1, 12.3, 3), WeatherService.Parse(json));
    }

    [Fact]
    public void Parse_rejects_a_response_without_temperature() =>
        Assert.Null(WeatherService.Parse("""{"current":{"weather_code":2}}"""));
}

public class MediaTests
{
    static MediaSessionInfo Session(string app, string title, bool playing, string artist = "") => new(app, title, artist, playing);

    [Fact]
    public void Spotify_and_the_playing_other_session_are_split()
    {
        var state = MediaService.BuildState([
            Session("Spotify.exe", "Song", false, "Artist"),
            Session("chrome", "Paused video", false),
            Session("msedge", "Playing video", true),
        ]);
        Assert.Equal(new MediaInfo("Song", "Artist", false, true), state.Spotify);
        Assert.Equal("Playing video", state.Other.Title);
        Assert.Equal("Playing video", state.Active.Title);
    }

    [Fact]
    public void Empty_sessions_use_the_placeholders()
    {
        var state = MediaService.BuildState([]);
        Assert.Equal(MediaState.Empty, state);
    }

    [Fact]
    public void Untitled_sessions_get_a_fallback_title()
    {
        var state = MediaService.BuildState([Session("SpotifyAB.SpotifyMusic", "", true)]);
        Assert.Equal("Spotify", state.Spotify.Title);
        Assert.False(state.Other.Exists);
    }
}

public class GpuTests
{
    [Fact]
    public void Busiest_engine_sums_processes_per_engine()
    {
        var instances = new Dictionary<string, double>
        {
            ["pid_1_luid_0x0_0x1_phys_0_eng_0_engtype_3D"] = 30,
            ["pid_2_luid_0x0_0x1_phys_0_eng_0_engtype_3D"] = 25,
            ["pid_1_luid_0x0_0x1_phys_0_eng_1_engtype_Copy"] = 40,
            ["engine_without_adapter_id"] = 99,
        };
        Assert.Equal(55, GpuCounters.BusiestEngine(instances));
        Assert.Null(GpuCounters.BusiestEngine(new Dictionary<string, double>()));
        Assert.Equal(100, GpuCounters.BusiestEngine(new Dictionary<string, double> { ["a_luid_x"] = 80, ["b_luid_x"] = 70 }));
    }

    [Fact]
    public void Vram_skips_adapters_without_dedicated_memory()
    {
        var totals = new Dictionary<string, ulong>
        {
            [GpuCounters.LuidKey(0, 0x1234)] = 8UL << 30,
            [GpuCounters.LuidKey(0, 0x9999)] = 0,
        };
        var usage = new Dictionary<string, double>
        {
            ["luid_0x00000000_0x00001234_phys_0"] = 3e9,
            ["luid_0x00000000_0x00009999_phys_0"] = 5e9,
        };
        Assert.Equal((3_000_000_000UL, 8UL << 30), GpuCounters.BusiestVram(usage, totals));
        Assert.Null(GpuCounters.BusiestVram(usage, new Dictionary<string, ulong>()));
    }
}

public class FullscreenTests
{
    static FullscreenMonitor.RECT Rect(int left, int top, int right, int bottom) => new() { Left = left, Top = top, Right = right, Bottom = bottom };

    [Fact]
    public void Exclusive_and_borderless_fullscreen_count()
    {
        var monitor = Rect(0, 0, 1920, 1080);
        var work = Rect(0, 0, 1920, 1040);
        Assert.True(FullscreenMonitor.IsFullscreenWindow(Rect(0, 0, 1920, 1080), monitor, work, borderless: true, maximized: false));
        Assert.True(FullscreenMonitor.IsFullscreenWindow(Rect(-1, -1, 1921, 1081), monitor, work, borderless: false, maximized: false));
        // A borderless window filling the work area (taskbar visible) still counts.
        Assert.True(FullscreenMonitor.IsFullscreenWindow(Rect(0, 0, 1920, 1040), monitor, work, borderless: true, maximized: true));
    }

    [Fact]
    public void Maximized_framed_windows_do_not_count()
    {
        var monitor = Rect(0, 0, 1920, 1080);
        var work = Rect(0, 0, 1920, 1040);
        Assert.False(FullscreenMonitor.IsFullscreenWindow(Rect(0, 0, 1920, 1040), monitor, work, borderless: false, maximized: true));
        Assert.False(FullscreenMonitor.IsFullscreenWindow(Rect(0, 0, 1920, 1080), monitor, work, borderless: false, maximized: true));
        Assert.False(FullscreenMonitor.IsFullscreenWindow(Rect(100, 100, 900, 700), monitor, work, borderless: true, maximized: false));
    }

    [Fact]
    public void Debounce_needs_two_matching_samples()
    {
        var debounce = new FullscreenDebounce();
        Assert.Null(debounce.Push(true));
        Assert.True(debounce.Push(true));
        Assert.Null(debounce.Push(true));
        Assert.Null(debounce.Push(false));
        Assert.Null(debounce.Push(true)); // A single blip back does not count.
        Assert.Null(debounce.Push(false));
        Assert.False(debounce.Push(false));
    }
}
