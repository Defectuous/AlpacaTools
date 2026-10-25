namespace AlpacaDashboard.Services;

/// <summary>The user's Alpaca market data subscription. Market data entitlements are per Alpaca user, not per account.</summary>
public enum MarketDataPlan
{
    /// <summary>Not yet detected (or detection failed).</summary>
    Unknown,
    /// <summary>Free plan: real-time IEX only, 30-symbol stream limit, SIP history must end ≥15 minutes ago.</summary>
    Basic,
    /// <summary>Paid plan: real-time SIP (all US exchanges), unlimited stream symbols, no history restriction.</summary>
    AlgoTraderPlus,
}

/// <summary>What the profile says to use: detect automatically, or force a plan.</summary>
public enum MarketDataPlanSetting { Auto, Basic, AlgoTraderPlus }

/// <summary>Which feeds and limits apply for a given plan.</summary>
public sealed record DataFeedProfile(
    MarketDataPlan Plan,
    string HistoricalFeed,
    TimeSpan HistoricalDelay,
    string StreamFeed,
    int StreamSymbolLimit,
    string SnapshotFeed)
{
    public static DataFeedProfile For(MarketDataPlan plan) => plan switch
    {
        MarketDataPlan.AlgoTraderPlus => new(plan, "sip", TimeSpan.Zero, "sip", int.MaxValue, "sip"),
        // Basic (and Unknown, the safe assumption): consolidated SIP bars up to 15 minutes ago for accurate
        // charts, real-time IEX for live prices.
        _ => new(plan, "sip", TimeSpan.FromMinutes(15), "iex", 30, "iex"),
    };

    public string PlanName => Plan switch
    {
        MarketDataPlan.AlgoTraderPlus => "Algo Trader Plus",
        MarketDataPlan.Basic => "Basic (free)",
        _ => "Unknown plan",
    };

    public string Description => Plan == MarketDataPlan.AlgoTraderPlus
        ? "Real-time SIP (all US exchanges)"
        : "Live prices: IEX real-time · Charts: SIP, 15-min delayed";

    /// <summary>Alpaca's market data REST limit per key on this plan.</summary>
    public int DataRequestsPerMinute => Plan == MarketDataPlan.AlgoTraderPlus ? 10_000 : 200;
}
