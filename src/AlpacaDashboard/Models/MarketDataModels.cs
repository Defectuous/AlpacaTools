using System.Text.Json.Serialization;

namespace AlpacaDashboard.Models;

// DTOs for the Alpaca Market Data API (data.alpaca.markets): v2 stocks and v1beta3 crypto.
// Crypto bars, trades and snapshots use the same field names as stocks, so they share these types.

public sealed class StockBar
{
    [JsonPropertyName("t")] public DateTimeOffset Time { get; set; }
    [JsonPropertyName("o")] public double Open { get; set; }
    [JsonPropertyName("h")] public double High { get; set; }
    [JsonPropertyName("l")] public double Low { get; set; }
    [JsonPropertyName("c")] public double Close { get; set; }
    [JsonPropertyName("v")] public double Volume { get; set; }
    [JsonPropertyName("n")] public double TradeCount { get; set; }
}

public sealed class StockBarsPage
{
    [JsonPropertyName("bars")] public List<StockBar>? Bars { get; set; }
    [JsonPropertyName("next_page_token")] public string? NextPageToken { get; set; }
}

/// <summary>Multi-symbol bars (crypto, and stocks queried with ?symbols=): keyed by symbol.</summary>
public sealed class MultiBarsPage
{
    [JsonPropertyName("bars")] public Dictionary<string, List<StockBar>>? Bars { get; set; }
    [JsonPropertyName("next_page_token")] public string? NextPageToken { get; set; }
}

public sealed class CryptoSnapshots
{
    [JsonPropertyName("snapshots")] public Dictionary<string, StockSnapshot>? Snapshots { get; set; }
}

public sealed class StockTrade
{
    [JsonPropertyName("t")] public DateTimeOffset Time { get; set; }
    [JsonPropertyName("p")] public double Price { get; set; }
}

public sealed class StockQuote
{
    [JsonPropertyName("bp")] public double BidPrice { get; set; }
    [JsonPropertyName("ap")] public double AskPrice { get; set; }
}

public sealed class StockSnapshot
{
    [JsonPropertyName("latestQuote")] public StockQuote? LatestQuote { get; set; }
    [JsonPropertyName("latestTrade")] public StockTrade? LatestTrade { get; set; }
    [JsonPropertyName("dailyBar")] public StockBar? DailyBar { get; set; }
    [JsonPropertyName("prevDailyBar")] public StockBar? PrevDailyBar { get; set; }
}

// Screener endpoints (v1beta1/screener): ranked lists computed by Alpaca from SIP data, on every plan.

public sealed class ScreenerMover
{
    [JsonPropertyName("symbol")] public string Symbol { get; set; } = "";
    [JsonPropertyName("percent_change")] public double PercentChange { get; set; }
    [JsonPropertyName("change")] public double Change { get; set; }
    [JsonPropertyName("price")] public double Price { get; set; }
}

public sealed class ScreenerMovers
{
    [JsonPropertyName("gainers")] public List<ScreenerMover>? Gainers { get; set; }
    [JsonPropertyName("losers")] public List<ScreenerMover>? Losers { get; set; }
}

public sealed class ScreenerMostActive
{
    [JsonPropertyName("symbol")] public string Symbol { get; set; } = "";
    [JsonPropertyName("volume")] public double Volume { get; set; }
    [JsonPropertyName("trade_count")] public double TradeCount { get; set; }
}

public sealed class ScreenerMostActives
{
    [JsonPropertyName("most_actives")] public List<ScreenerMostActive>? MostActives { get; set; }
}
