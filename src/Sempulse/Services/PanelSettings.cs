using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sempulse.Services;

public enum PanelMode { Bar, Widget }
public enum BarEdge { Top, Bottom, Left, Right }
public enum MediaLayout { Separate, Unified }

public sealed record PanelSettings
{
    public static readonly string[] PetModes = ["auto", "walk", "sit", "sleep", "jump", "puff", "play"];

    public PanelMode Mode { get; init; } = PanelMode.Bar;
    public BarEdge Position { get; init; } = BarEdge.Right;
    public int Thickness { get; init; } = 44;
    public int WidgetWidth { get; init; } = 600;
    public int Opacity { get; init; } = 70;
    public MediaLayout MediaMode { get; init; } = MediaLayout.Separate;
    public bool Pet { get; init; } = true;
    public string PetMode { get; init; } = "auto";

    [JsonIgnore] public bool Vertical => Position is BarEdge.Left or BarEdge.Right;

    // Keeps every value inside the ranges the settings window offers.
    public PanelSettings Normalize() => this with
    {
        Mode = Enum.IsDefined(Mode) ? Mode : PanelMode.Bar,
        Position = Enum.IsDefined(Position) ? Position : BarEdge.Right,
        Thickness = Math.Clamp(Thickness, 40, 120),
        WidgetWidth = Math.Clamp(WidgetWidth, 520, 900),
        Opacity = Math.Clamp(Opacity, 0, 100),
        MediaMode = Enum.IsDefined(MediaMode) ? MediaMode : MediaLayout.Separate,
        PetMode = PetModes.Contains(PetMode) ? PetMode : "auto",
    };
}

public sealed class SettingsStore
{
    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    readonly string file;

    public SettingsStore(string directory) => file = Path.Combine(directory, "settings.json");

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sempulse");

    public PanelSettings Load()
    {
        try
        {
            if (File.Exists(file))
                return (JsonSerializer.Deserialize<PanelSettings>(File.ReadAllText(file), Options) ?? new()).Normalize();
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            // A damaged file falls back to defaults; the next save replaces it.
        }
        return new PanelSettings();
    }

    public void Save(PanelSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        string temporary = file + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings.Normalize(), Options));
        File.Move(temporary, file, overwrite: true);
    }
}
