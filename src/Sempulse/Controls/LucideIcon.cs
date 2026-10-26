using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Sempulse.Controls;

// A Lucide icon: 24x24 path data drawn with a 2 unit round stroke, scaled to Size like the SVG original.
public sealed class LucideIcon : Viewbox
{
    static readonly Dictionary<string, Geometry> Cache = [];

    readonly Path path = new()
    {
        StrokeThickness = 2,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        StrokeLineJoin = PenLineJoin.Round,
    };

    public LucideIcon(string name, double size, Brush brush, bool filled = false)
    {
        Width = Height = size;
        Stretch = Stretch.Uniform;
        var canvas = new Canvas { Width = 24, Height = 24 };
        canvas.Children.Add(path);
        Child = canvas;
        Icon = name;
        path.Stroke = brush;
        if (filled) path.Fill = brush;
        SnapsToDevicePixels = true;
    }

    public string Icon
    {
        set
        {
            if (!Cache.TryGetValue(value, out var geometry))
            {
                geometry = Geometry.Parse(LucideData.Paths[value]);
                geometry.Freeze();
                Cache[value] = geometry;
            }
            path.Data = geometry;
        }
    }

    public Brush Brush
    {
        set
        {
            path.Stroke = value;
            if (path.Fill is not null) path.Fill = value;
        }
    }

    public bool Filled { set => path.Fill = value ? path.Stroke : null; }
}
