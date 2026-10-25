using System.Text.Json.Serialization;

namespace AlpacaDashboard.Models;

public enum AlertCondition
{
    PriceAtOrAbove, PriceAtOrBelow, UpTodayBy, DownTodayBy,
    // On a position's unrealized P/L in one account (Target in dollars, or percent of cost basis).
    ProfitAtLeast, LossAtLeast, GainPctAtLeast, LossPctAtLeast,
}

/// <summary>What an alert is checked against. P/L values are null when the account doesn't hold the symbol.</summary>
/// <param name="DayChange">Change since the previous close as a fraction (0.05 = +5%); null when unknown.</param>
/// <param name="PlPct">Unrealized P/L as a fraction of cost basis.</param>
public readonly record struct AlertInputs(decimal Price, decimal? DayChange, decimal? Pl, decimal? PlPct);

/// <summary>
/// An alert on one symbol's price, or on a position's P/L. It fires once, when its condition holds on a check,
/// then stays triggered until re-armed.
/// </summary>
public sealed record PriceAlert
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Market data symbol: "AAPL", or a pair like "BTC/USD" for crypto.</summary>
    public string Symbol { get; init; } = "";
    public bool IsCrypto { get; init; }
    public AlertCondition Condition { get; init; }

    /// <summary>A price or dollar amount, or a percentage (5 = 5%), depending on the condition.</summary>
    public decimal Target { get; init; }
    public string? Note { get; init; }

    /// <summary>The account whose position a P/L alert follows; null for price alerts.</summary>
    public Guid? AccountId { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset? TriggeredAt { get; init; }
    public decimal? TriggeredPrice { get; init; }
    public decimal? TriggeredPl { get; init; }

    [JsonIgnore] public bool IsArmed => TriggeredAt is null;

    [JsonIgnore] public bool IsPercent => IsPercentCondition(Condition);

    [JsonIgnore] public bool IsPositionBased => IsPositionCondition(Condition);

    public static bool IsPercentCondition(AlertCondition c) =>
        c is AlertCondition.UpTodayBy or AlertCondition.DownTodayBy or AlertCondition.GainPctAtLeast or AlertCondition.LossPctAtLeast;

    public static bool IsPositionCondition(AlertCondition c) =>
        c is AlertCondition.ProfitAtLeast or AlertCondition.LossAtLeast or AlertCondition.GainPctAtLeast or AlertCondition.LossPctAtLeast;

    public bool IsMet(AlertInputs now) => Condition switch
    {
        AlertCondition.PriceAtOrAbove => now.Price >= Target,
        AlertCondition.PriceAtOrBelow => now.Price <= Target,
        AlertCondition.UpTodayBy => now.DayChange >= Target / 100,
        AlertCondition.DownTodayBy => now.DayChange <= -Target / 100,
        AlertCondition.ProfitAtLeast => now.Pl >= Target,
        AlertCondition.LossAtLeast => now.Pl <= -Target,
        AlertCondition.GainPctAtLeast => now.PlPct >= Target / 100,
        AlertCondition.LossPctAtLeast => now.PlPct <= -Target / 100,
        _ => false,
    };
}
