using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AlpacaDashboard.Services;

/// <summary>How far a watched stop-limit sell has gone; each step is notified once.</summary>
public enum StopOrderStage { Watching, NearStop, StopHit, BelowLimit, Filled }

/// <summary>What has already been notified for one order, so a restart doesn't notify it again.</summary>
public sealed record StopOrderState
{
    public StopOrderStage Notified { get; init; }

    /// <summary>The position's average entry when the order was first seen, for the P/L on fill.</summary>
    public decimal? EntryPrice { get; init; }
}

public sealed record OrderWatchSettings
{
    public bool Enabled { get; init; } = true;
    public Dictionary<string, StopOrderState> Orders { get; init; } = [];
}

/// <summary>Persists the stop-limit order watch to %APPDATA%\AlpacaTools\order-watch.json.</summary>
public static class OrderWatchStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AlpacaTools", "order-watch.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static OrderWatchSettings Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<OrderWatchSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new()
                : new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            App.LogError("Order watch", ex);
            return new();
        }
    }

    public static void Save(OrderWatchSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            App.LogError("Order watch", ex);
        }
    }
}
