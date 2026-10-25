using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AlpacaDashboard.Models;

namespace AlpacaDashboard.Services;

/// <summary>
/// Persists price alerts to %APPDATA%\AlpacaTools\alerts.json. Alerts belong to the app, not an account:
/// a symbol's price is the same whichever key fetches it.
/// </summary>
public static class AlertStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AlpacaTools", "alerts.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static List<PriceAlert> Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<List<PriceAlert>>(File.ReadAllText(FilePath), JsonOptions) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            App.LogError("Alerts", ex);
            return [];
        }
    }

    public static void Save(IEnumerable<PriceAlert> alerts)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(alerts.ToList(), JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            App.LogError("Alerts", ex);
        }
    }
}
