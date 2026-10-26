using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AlpacaDashboard.ViewModels;

namespace AlpacaDashboard.Services;

/// <summary>A trendline the user drew, anchored to bar times so it stays put when the range changes.</summary>
public sealed record DrawnLine(Guid Id, DateTimeOffset Time1, double Price1, DateTimeOffset Time2, double Price2);

/// <summary>
/// Persists price-chart settings to %APPDATA%\AlpacaTools\chart.json: which overlays are shown (one choice for
/// every chart, so a new chart window opens the way the last one was left) and the lines drawn on each symbol.
/// </summary>
public static class ChartSettingsStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AlpacaTools", "chart.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    // Both parts are optional so a file from an older version (overlays only) still loads.
    private sealed record Settings(List<ChartOverlay>? Overlays, Dictionary<string, List<DrawnLine>>? Lines);

    public static HashSet<ChartOverlay> LoadOverlays() => Read().Overlays?.ToHashSet() ?? [];

    public static void SaveOverlays(IEnumerable<ChartOverlay> overlays) => Write(Read() with { Overlays = overlays.ToList() });

    public static List<DrawnLine> LoadLines(string symbol) => Read().Lines?.GetValueOrDefault(symbol)?.ToList() ?? [];

    /// <summary>Re-reads the file first, so lines saved by another chart window aren't lost.</summary>
    public static void SaveLines(string symbol, IEnumerable<DrawnLine> lines)
    {
        var settings = Read();
        var all = settings.Lines ?? new Dictionary<string, List<DrawnLine>>(StringComparer.Ordinal);
        var list = lines.ToList();
        if (list.Count > 0) all[symbol] = list; else all.Remove(symbol);
        Write(settings with { Lines = all });
    }

    private static Settings Read()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), JsonOptions) ?? new Settings(null, null)
                : new Settings(null, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            App.LogError("Chart settings", ex);
            return new Settings(null, null);
        }
    }

    private static void Write(Settings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            App.LogError("Chart settings", ex);
        }
    }
}
