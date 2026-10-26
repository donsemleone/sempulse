using System.IO;
using Sempulse.Layout;
using Sempulse.Services;

namespace Sempulse.Tests;

public class PanelLayoutTests
{
    static readonly DisplayInfo Primary = new(new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1040), 1);
    static readonly DisplayInfo Scaled = new(new PixelRect(0, 0, 2880, 1620), new PixelRect(0, 0, 2880, 1560), 1.5);
    static readonly DisplayInfo Secondary = new(new PixelRect(1920, 0, 2560, 1440), new PixelRect(1920, 0, 2560, 1400), 1);

    [Theory]
    [InlineData(BarEdge.Left, 0, 0, 44, 1080)]
    [InlineData(BarEdge.Right, 1876, 0, 44, 1080)]
    [InlineData(BarEdge.Top, 0, 0, 1920, 44)]
    [InlineData(BarEdge.Bottom, 0, 1036, 1920, 44)]
    public void Bar_hugs_the_chosen_edge_of_the_primary_display(BarEdge edge, int x, int y, int width, int height)
    {
        var layout = PanelLayout.Compute(new PanelSettings { Position = edge }, false, Primary, Secondary, null);
        Assert.Equal(new LayoutResult(false, PanelMode.Bar, new PixelRect(x, y, width, height)), layout);
    }

    [Fact]
    public void Bar_thickness_is_scaled_with_the_display()
    {
        var layout = PanelLayout.Compute(new PanelSettings { Position = BarEdge.Right, Thickness = 60 }, false, Scaled, null, null);
        Assert.Equal(new PixelRect(2790, 0, 90, 1620), layout.Bounds);
    }

    [Fact]
    public void Widget_starts_centred_and_keeps_a_dragged_position_on_screen()
    {
        var settings = new PanelSettings { Mode = PanelMode.Widget, WidgetWidth = 600 };
        Assert.Equal(new PixelRect(660, 442, 600, 156), PanelLayout.Compute(settings, false, Primary, null, null).Bounds);
        Assert.Equal(new PixelRect(100, 50, 600, 156), PanelLayout.Compute(settings, false, Primary, null, (100, 50)).Bounds);
        // Dragged partly off screen: pulled back inside the work area.
        Assert.Equal(new PixelRect(1320, 884, 600, 156), PanelLayout.Compute(settings, false, Primary, null, (1800, 1000)).Bounds);
    }

    [Fact]
    public void Fullscreen_moves_to_the_second_display_lower_left()
    {
        var layout = PanelLayout.Compute(new PanelSettings(), true, Primary, Secondary, null);
        Assert.Equal(new LayoutResult(false, PanelMode.Widget, new PixelRect(1990, 1174, 600, 156)), layout);
    }

    [Fact]
    public void Fullscreen_without_a_second_display_hides_the_panel() =>
        Assert.True(PanelLayout.Compute(new PanelSettings(), true, Primary, null, null).Hidden);

    [Theory]
    [InlineData(BarEdge.Right, 1546, 35)]
    [InlineData(BarEdge.Left, 54, 35)]
    [InlineData(BarEdge.Top, 35, 54)]
    [InlineData(BarEdge.Bottom, 35, 526)]
    public void Settings_open_beside_the_bar(BarEdge edge, int x, int y)
    {
        var bar = PanelLayout.Compute(new PanelSettings { Position = edge }, false, Primary, null, null).Bounds;
        var rect = PanelLayout.Settings(bar, Primary.Work, 1, PanelMode.Bar, edge, 500);
        Assert.Equal(new PixelRect(x, y, 320, 500), rect);
    }
}

public class DesktopIconLayoutTests
{
    static readonly IconPoint Spacing = new(75, 100);

    [Fact]
    public void Mod_is_never_negative()
    {
        Assert.Equal(1, DesktopIconLayout.Mod(7, 3));
        Assert.Equal(2, DesktopIconLayout.Mod(-1, 3));
    }

    [Fact]
    public void AlignUp_lands_on_the_grid()
    {
        Assert.Equal(10, DesktopIconLayout.AlignUp(3, 75, 10));
        Assert.Equal(85, DesktopIconLayout.AlignUp(11, 75, 10));
        Assert.Equal(10, DesktopIconLayout.AlignUp(10, 75, 10));
    }

    [Fact]
    public void Destinations_fill_columns_and_skip_occupied_slots()
    {
        var occupied = new List<IconPoint> { new(2000, 0) };
        var slots = DesktopIconLayout.PlanDestinations(3, new IconPoint(2000, 0), new IconPoint(2300, 150), Spacing, new IconPoint(0, 0), occupied);
        // The grid is aligned with the sample icon (x = 0 mod 75), so the first column is 2025; (2025, 0) touches the occupied icon.
        Assert.Equal([new IconPoint(2025, 100), new IconPoint(2100, 0), new IconPoint(2100, 100)], slots);
        Assert.Single(occupied); // The caller's list is not changed.
    }

    [Fact]
    public void Destinations_stop_when_the_area_is_full()
    {
        var slots = DesktopIconLayout.PlanDestinations(10, new IconPoint(0, 0), new IconPoint(75, 100), Spacing, new IconPoint(0, 0), []);
        Assert.Equal(4, slots.Count);
    }

    [Fact]
    public void Restore_prefers_the_saved_spot_then_the_nearest_free_one()
    {
        var work = new IconRect(0, 0, 1920, 1040);
        Assert.Equal(new IconPoint(150, 200), DesktopIconLayout.FindRestoreSlot(new IconPoint(150, 200), work, Spacing, []));
        var taken = new List<IconPoint> { new(150, 200) };
        Assert.Equal(new IconPoint(75, 200), DesktopIconLayout.FindRestoreSlot(new IconPoint(150, 200), work, Spacing, taken));
        Assert.Null(DesktopIconLayout.FindRestoreSlot(new IconPoint(0, 0), new IconRect(0, 0, 75, 100), Spacing, [new(0, 0)]));
    }

    [Fact]
    public void Snapshot_round_trips_paths_with_tabs_and_unicode()
    {
        string file = Path.Combine(Path.GetTempPath(), "sempulse-icons-" + Guid.NewGuid() + ".txt");
        try
        {
            var snapshot = new DesktopIconLayout.Snapshot();
            snapshot.Add(@"C:\Users\Ünal\Desktop\Rapor	2026.docx", new IconPoint(10, -20));
            snapshot.Add(@"C:\Users\Ünal\Desktop\Klasör", new IconPoint(85, 120));
            snapshot.Add(@"c:\users\ünal\desktop\klasör", new IconPoint(1, 1)); // Same path, different case: ignored.
            DesktopIconLayout.WriteSnapshot(file, snapshot);

            var read = DesktopIconLayout.ReadSnapshot(file);
            Assert.Equal(snapshot.Order, read.Order);
            Assert.Equal(new IconPoint(10, -20), read.Positions[@"C:\Users\Ünal\Desktop\Rapor	2026.docx"]);

            DesktopIconLayout.WriteSnapshot(file, new DesktopIconLayout.Snapshot());
            Assert.False(File.Exists(file));
            Assert.Equal(0, DesktopIconLayout.ReadSnapshot(file).Count);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void A_damaged_snapshot_is_reported()
    {
        string file = Path.Combine(Path.GetTempPath(), "sempulse-icons-" + Guid.NewGuid() + ".txt");
        try
        {
            File.WriteAllText(file, "SempulseDesktopIconsV1\nnot-base64!\t1\t2\n");
            Assert.Equal("SNAPSHOT_INVALID", Assert.Throws<DesktopIconException>(() => DesktopIconLayout.ReadSnapshot(file)).Code);
        }
        finally { File.Delete(file); }
    }
}
