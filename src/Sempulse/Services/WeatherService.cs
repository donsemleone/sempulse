using System.IO;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;

namespace Sempulse.Services;

public sealed record WeatherInfo(double Temp, int Code, bool IsDay, double? Max, double? Min, int? DailyCode);

public enum WeatherIcon { Sun, Moon, CloudSun, CloudMoon, Cloud, CloudFog, CloudDrizzle, CloudRain, CloudSnow, CloudLightning }

// Today's Istanbul weather from Open-Meteo (no API key, nothing about the user is sent).
public sealed class WeatherService : IDisposable
{
    const string Url = "https://api.open-meteo.com/v1/forecast?latitude=41.01&longitude=28.98"
        + "&current=temperature_2m,weather_code,is_day&daily=temperature_2m_max,temperature_2m_min,weather_code"
        + "&timezone=Europe%2FIstanbul&forecast_days=1";

    readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(10) };
    WeatherInfo? cache;
    DateTime retryAt = DateTime.MinValue;

    public WeatherService() => http.DefaultRequestHeaders.UserAgent.ParseAdd("Sempulse");

    // Cached for 30 minutes; a failure retries after 5 and keeps the last good value.
    public async Task<WeatherInfo?> GetAsync()
    {
        if (DateTime.UtcNow < retryAt) return cache;
        try
        {
            string json = await http.GetStringAsync(Url);
            cache = Parse(json) ?? throw new InvalidDataException("Unexpected weather response");
            retryAt = DateTime.UtcNow.AddMinutes(30);
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Weather: {exception.Message}");
            retryAt = DateTime.UtcNow.AddMinutes(5);
        }
        return cache;
    }

    public static WeatherInfo? Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("current", out var current) ||
            !current.TryGetProperty("temperature_2m", out var temp) || temp.ValueKind != JsonValueKind.Number) return null;
        root.TryGetProperty("daily", out var daily);
        return new WeatherInfo(
            temp.GetDouble(),
            Int(current, "weather_code") ?? 0,
            Int(current, "is_day") != 0,
            First(daily, "temperature_2m_max"),
            First(daily, "temperature_2m_min"),
            (int?)First(daily, "weather_code"));
    }

    static int? Int(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32() : null;

    static double? First(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var array) &&
        array.ValueKind == JsonValueKind.Array && array.GetArrayLength() > 0 && array[0].ValueKind == JsonValueKind.Number
            ? array[0].GetDouble() : null;

    // Open-Meteo WMO weather codes grouped into an icon and a Turkish description.
    public static (WeatherIcon Icon, string Text) Describe(int code, bool isDay) => code switch
    {
        0 => (isDay ? WeatherIcon.Sun : WeatherIcon.Moon, "Açık"),
        1 => (isDay ? WeatherIcon.CloudSun : WeatherIcon.CloudMoon, "Az bulutlu"),
        2 => (isDay ? WeatherIcon.CloudSun : WeatherIcon.CloudMoon, "Parçalı bulutlu"),
        3 => (WeatherIcon.Cloud, "Kapalı"),
        <= 48 => (WeatherIcon.CloudFog, "Sisli"),
        <= 57 => (WeatherIcon.CloudDrizzle, "Çisenti"),
        <= 67 => (WeatherIcon.CloudRain, "Yağmurlu"),
        >= 80 and <= 82 => (WeatherIcon.CloudRain, "Sağanak"),
        <= 77 or 85 or 86 => (WeatherIcon.CloudSnow, "Karlı"),
        >= 95 => (WeatherIcon.CloudLightning, "Gök gürültülü"),
        _ => (WeatherIcon.Cloud, "Bulutlu"),
    };

    public void Dispose() => http.Dispose();
}
