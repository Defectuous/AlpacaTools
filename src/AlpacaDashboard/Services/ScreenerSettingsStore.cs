using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AlpacaDashboard.ViewModels;

namespace AlpacaDashboard.Services;

/// <summary>One account's screener selections.</summary>
public sealed record ScreenerSettings
{
    public ScreenerSource Source { get; init; } = ScreenerSource.Gainers;
    public bool CryptoOnly { get; init; }
    public bool HideHeld { get; init; }
    public bool OnlyHeld { get; init; }
    public bool ExcludeStablecoins { get; init; } = true;
    /// <summary>Stocks and crypto keep their own price choice; null means the default.</summary>
    public string? StockPrice { get; init; }
    public string? CryptoPrice { get; init; }
    /// <summary>Filter name → selected option label, for every filter but price.</summary>
    public Dictionary<string, string> Filters { get; init; } = [];
}

/// <summary>
/// Persists each account's screener selections to %APPDATA%\AlpacaTools\screener.json, keyed by account id, so
/// every API key keeps its own screen (and editing an account's keys keeps it). Filters and options are stored by
/// their labels, so a renamed or removed one just falls back to its default.
/// </summary>
public static class ScreenerSettingsStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AlpacaTools", "screener.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static ScreenerSettings Load(Guid accountId) => ReadAll().GetValueOrDefault(accountId) ?? new();

    public static void Save(Guid accountId, ScreenerSettings settings)
    {
        var all = ReadAll();
        all[accountId] = settings;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(all, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing a filter choice isn't worth interrupting the user over.
        }
    }

    private static Dictionary<Guid, ScreenerSettings> ReadAll()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<Dictionary<Guid, ScreenerSettings>>(File.ReadAllText(FilePath), JsonOptions) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return []; // unreadable or corrupt: start from the defaults
        }
    }
}
