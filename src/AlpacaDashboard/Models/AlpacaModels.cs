using System.Text.Json;
using System.Text.Json.Serialization;

namespace AlpacaDashboard.Models;

// DTOs for the Alpaca Trading API (v2). Alpaca returns most numbers as JSON strings,
// so the client's serializer options allow reading numbers from strings.

public sealed class AlpacaAccount
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("account_number")] public string AccountNumber { get; set; } = "";
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("currency")] public string Currency { get; set; } = "USD";
    [JsonPropertyName("cash")] public decimal Cash { get; set; }
    [JsonPropertyName("equity")] public decimal Equity { get; set; }
    [JsonPropertyName("last_equity")] public decimal LastEquity { get; set; }
    [JsonPropertyName("buying_power")] public decimal BuyingPower { get; set; }
    /// <summary>Cash-only buying power; crypto can't be bought on margin.</summary>
    [JsonPropertyName("non_marginable_buying_power")] public decimal NonMarginableBuyingPower { get; set; }
    [JsonPropertyName("portfolio_value")] public decimal PortfolioValue { get; set; }
    [JsonPropertyName("long_market_value")] public decimal LongMarketValue { get; set; }
    [JsonPropertyName("short_market_value")] public decimal ShortMarketValue { get; set; }
    [JsonPropertyName("pattern_day_trader")] public bool PatternDayTrader { get; set; }
    [JsonPropertyName("daytrade_count")] public int DaytradeCount { get; set; }

    public decimal DayChange => Equity - LastEquity;
    public decimal DayChangePct => LastEquity == 0 ? 0 : DayChange / LastEquity;
}

public sealed class AlpacaPosition
{
    [JsonPropertyName("symbol")] public string Symbol { get; set; } = "";
    [JsonPropertyName("asset_class")] public string AssetClass { get; set; } = "";
    [JsonPropertyName("side")] public string Side { get; set; } = "";
    [JsonPropertyName("qty")] public decimal Qty { get; set; }
    /// <summary>Qty not held by open orders; a sell beyond this is rejected.</summary>
    [JsonPropertyName("qty_available")] public decimal? QtyAvailable { get; set; }
    [JsonPropertyName("avg_entry_price")] public decimal AvgEntryPrice { get; set; }
    [JsonPropertyName("current_price")] public decimal CurrentPrice { get; set; }
    [JsonPropertyName("market_value")] public decimal MarketValue { get; set; }
    [JsonPropertyName("cost_basis")] public decimal CostBasis { get; set; }
    [JsonPropertyName("unrealized_pl")] public decimal UnrealizedPl { get; set; }
    [JsonPropertyName("unrealized_plpc")] public decimal UnrealizedPlPct { get; set; }
    [JsonPropertyName("unrealized_intraday_pl")] public decimal UnrealizedIntradayPl { get; set; }
    [JsonPropertyName("change_today")] public decimal ChangeToday { get; set; }
    [JsonPropertyName("lastday_price")] public decimal LastdayPrice { get; set; }
}

public sealed class AlpacaPortfolioHistory
{
    [JsonPropertyName("timestamp")] public long[] Timestamp { get; set; } = [];
    [JsonPropertyName("equity")] public double?[] Equity { get; set; } = [];
    [JsonPropertyName("profit_loss")] public double?[] ProfitLoss { get; set; } = [];
    [JsonPropertyName("profit_loss_pct")] public double?[] ProfitLossPct { get; set; } = [];
    [JsonPropertyName("base_value")] public double BaseValue { get; set; }
    [JsonPropertyName("timeframe")] public string Timeframe { get; set; } = "";
}

/// <summary>
/// Account activity. Trade activities (FILL) populate symbol/side/qty/price/transaction_time;
/// non-trade activities (DIV, CSD, FEE, ...) populate date/net_amount/description.
/// </summary>
public sealed class AlpacaActivity
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("activity_type")] public string ActivityType { get; set; } = "";
    [JsonPropertyName("symbol")] public string? Symbol { get; set; }
    [JsonPropertyName("side")] public string? Side { get; set; }
    [JsonPropertyName("qty")] public decimal? Qty { get; set; }
    [JsonPropertyName("price")] public decimal? Price { get; set; }
    [JsonPropertyName("transaction_time")] public DateTimeOffset? TransactionTime { get; set; }
    [JsonPropertyName("date")] public string? Date { get; set; }
    [JsonPropertyName("net_amount")] public decimal? NetAmount { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("order_id")] public string? OrderId { get; set; }

    /// <summary>Every other field Alpaca sent (cum_qty, per_share_amount, status, ...), kept for the ledger's raw copy.</summary>
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    [JsonIgnore]
    public DateTime When =>
        TransactionTime?.LocalDateTime
        ?? (DateTime.TryParse(Date, out var d) ? d : DateTime.MinValue);

    [JsonIgnore]
    public string Summary => ActivityType switch
    {
        "FILL" => $"{Side?.ToUpperInvariant()} {Qty:0.####} {Symbol} @ {Price:C}",
        _ => string.IsNullOrWhiteSpace(Description)
            ? $"{ActivityType} {Symbol}".Trim()
            : Description!
    };

    [JsonIgnore]
    public decimal? Amount => ActivityType == "FILL"
        ? (Qty * Price) * (Side == "buy" ? -1 : 1)
        : NetAmount;
}

public sealed class AlpacaOrder
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("symbol")] public string Symbol { get; set; } = "";
    [JsonPropertyName("side")] public string Side { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("time_in_force")] public string TimeInForce { get; set; } = "";
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("qty")] public decimal? Qty { get; set; }
    [JsonPropertyName("notional")] public decimal? Notional { get; set; }
    [JsonPropertyName("filled_qty")] public decimal FilledQty { get; set; }
    [JsonPropertyName("filled_avg_price")] public decimal? FilledAvgPrice { get; set; }
    [JsonPropertyName("limit_price")] public decimal? LimitPrice { get; set; }
    [JsonPropertyName("stop_price")] public decimal? StopPrice { get; set; }
    [JsonPropertyName("submitted_at")] public DateTimeOffset? SubmittedAt { get; set; }

    public DateTime SubmittedLocal => SubmittedAt?.LocalDateTime ?? DateTime.MinValue;

    public string PriceText => (LimitPrice, StopPrice) switch
    {
        ({ } l, { } s) => $"stop {Fmt(s)} / limit {Fmt(l)}",
        ({ } l, null) => $"limit {Fmt(l)}",
        (null, { } s) => $"stop {Fmt(s)}",
        _ => "market"
    };

    private static string Fmt(decimal price) => ViewModels.SymbolChartViewModel.FormatPrice((double)price);

    public string QtyText => Qty is { } q ? $"{FilledQty:0.####} / {q:0.####}" : $"{Notional:C} notional";

    public string Summary => $"{Side.ToUpperInvariant()} {QtyText} · {Type.Replace('_', ' ')} · {PriceText} · {TimeInForce.ToUpperInvariant()}";
}

/// <summary>Body of POST v2/orders. Quantities and prices go as strings so decimals survive exactly.</summary>
public sealed class OrderRequest
{
    [JsonPropertyName("symbol")] public string Symbol { get; set; } = "";
    [JsonPropertyName("qty")] public string? Qty { get; set; }
    /// <summary>A dollar amount instead of <see cref="Qty"/> (market orders).</summary>
    [JsonPropertyName("notional")] public string? Notional { get; set; }
    [JsonPropertyName("side")] public string Side { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("time_in_force")] public string TimeInForce { get; set; } = "";
    [JsonPropertyName("limit_price")] public string? LimitPrice { get; set; }
    [JsonPropertyName("stop_price")] public string? StopPrice { get; set; }
}

/// <summary>A tradable instrument from GET v2/assets (crypto symbols come as "BTC/USD").</summary>
public sealed class AlpacaAsset
{
    [JsonPropertyName("symbol")] public string Symbol { get; set; } = "";
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("class")] public string AssetClass { get; set; } = "";
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("tradable")] public bool Tradable { get; set; }
}

/// <summary>A saved account connection. Secrets are stored DPAPI-encrypted; see AccountStore.</summary>
public sealed class AccountProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public bool IsPaper { get; set; } = true;
    public string KeyId { get; set; } = "";
    public string SecretKey { get; set; } = "";
    public Services.MarketDataPlanSetting DataPlan { get; set; } = Services.MarketDataPlanSetting.Auto;

    /// <summary>
    /// The key is shared with a bot that should come first: the dashboard opens no websockets
    /// (leaving Alpaca's stream slots to the bot), polls prices instead, and uses little of the key's request budget.
    /// </summary>
    public bool BotPriority { get; set; }
}
