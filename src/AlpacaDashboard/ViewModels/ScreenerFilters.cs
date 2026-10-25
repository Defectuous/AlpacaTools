using AlpacaDashboard.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AlpacaDashboard.ViewModels;

/// <summary>
/// One screened symbol. Fractions throughout (0.05 = +5%). Values from daily history are null when there
/// isn't enough of it (new listings, or the history request failed), and a null never matches a filter.
/// </summary>
public sealed record ScreenerRow
{
    public required string Symbol { get; init; }
    public bool IsCrypto { get; init; }
    /// <summary>Crypto quote currency ("USD", "USDT", "USDC"); null for stocks.</summary>
    public string? Quote { get; init; }
    public bool IsStablecoin { get; init; }
    public bool IsHeld { get; init; }

    // From the snapshot (today)
    public double Price { get; init; }
    public double? Change { get; init; }
    public double? ChangePct { get; init; }
    public double? GapPct { get; init; }
    public double Volume { get; init; }
    public double TradeCount { get; init; }
    public double? DayLow { get; init; }
    public double? DayHigh { get; init; }
    /// <summary>Start of today's daily bar; history bars from this time on are replaced by the live values.</summary>
    public DateTimeOffset? SessionStart { get; init; }

    // From daily history
    public double? Perf1W { get; init; }
    public double? Perf1M { get; init; }
    public double? Perf3M { get; init; }
    public double? Rsi14 { get; init; }
    public double? VsSma20 { get; init; }
    public double? VsSma50 { get; init; }
    public double? VsSma200 { get; init; }
    public bool? Sma50AboveSma200 { get; init; }
    public double? FromHigh52W { get; init; }
    public double? FromLow52W { get; init; }
    public bool IsNewHigh52W { get; init; }
    public bool IsNewLow52W { get; init; }
    public double? AvgDollarVolume { get; init; }
    public double? RelVolume { get; init; }
    public double? Volatility { get; init; }

    public double DollarVolume => Volume * Price;

    public string PriceText => SymbolChartViewModel.FormatPrice(Price);
    public string VolumeText => SymbolChartViewModel.FormatVolume(Volume);
    public string DollarVolumeText => "$" + SymbolChartViewModel.FormatVolume(DollarVolume);
    public string AvgDollarVolumeText => AvgDollarVolume is { } v ? "$" + SymbolChartViewModel.FormatVolume(v) : "—";
    public string RelVolumeText => RelVolume is { } v ? $"{v:0.00}×" : "—";
    public string RsiText => Rsi14 is { } v ? v.ToString("0") : "—";
    public string VolatilityText => Volatility is { } v ? v.ToString("P1") : "—";

    /// <summary>Adds the indicators computed from completed daily bars plus today's live price.</summary>
    public ScreenerRow WithHistory(List<StockBar>? bars)
    {
        if (bars is not { Count: > 0 } || Price <= 0) return this;
        var past = SessionStart is { } start ? bars.Where(b => b.Time < start).ToList() : bars;
        if (past.Count == 0) return this;

        var now = DateTimeOffset.UtcNow;
        var closes = past.Select(b => b.Close).Append(Price).ToArray();

        double? Perf(int days) =>
            past.LastOrDefault(b => b.Time <= now.AddDays(-days)) is { Close: > 0 } then ? Price / then.Close - 1 : null;
        double? Sma(int n) => closes.Length >= n ? closes[^n..].Average() : null;
        double? Vs(double? sma) => sma is > 0 ? Price / sma - 1 : null;

        var sma50 = Sma(50);
        var sma200 = Sma(200);

        // 52-week range needs a few weeks of history to mean anything.
        var year = past.Where(b => b.Time >= now.AddDays(-365) && b.Low > 0).ToList();
        double? fromHigh = null, fromLow = null;
        bool newHigh = false, newLow = false;
        if (year.Count >= 20)
        {
            var priorHigh = year.Max(b => b.High);
            var priorLow = year.Min(b => b.Low);
            var high = Math.Max(priorHigh, DayHigh ?? Price);
            var low = Math.Min(priorLow, DayLow ?? Price);
            fromHigh = Price / high - 1;
            fromLow = low > 0 ? Price / low - 1 : null;
            newHigh = (DayHigh ?? Price) >= priorHigh;
            newLow = (DayLow ?? Price) <= priorLow;
        }

        var last20 = past.TakeLast(20).ToList();
        var avgVolume = last20.Count >= 5 ? last20.Average(b => b.Volume) : (double?)null;
        var last14 = past.TakeLast(14).Where(b => b.Close > 0).ToList();

        return this with
        {
            Perf1W = Perf(7),
            Perf1M = Perf(30),
            Perf3M = Perf(91),
            Rsi14 = Rsi(closes, 14),
            VsSma20 = Vs(Sma(20)),
            VsSma50 = Vs(sma50),
            VsSma200 = Vs(sma200),
            Sma50AboveSma200 = sma50 is { } a && sma200 is { } b ? a > b : null,
            FromHigh52W = fromHigh,
            FromLow52W = fromLow,
            IsNewHigh52W = newHigh,
            IsNewLow52W = newLow,
            AvgDollarVolume = last20.Count >= 5 ? last20.Average(b => b.Volume * b.Close) : null,
            RelVolume = avgVolume is > 0 ? Volume / avgVolume : null,
            Volatility = last14.Count >= 5 ? last14.Average(b => (b.High - b.Low) / b.Close) : null,
        };
    }

    /// <summary>Wilder's RSI over the last <paramref name="period"/> changes (seeded with a simple average).</summary>
    private static double? Rsi(double[] closes, int period)
    {
        if (closes.Length <= period) return null;
        double gain = 0, loss = 0;
        for (var i = 1; i <= period; i++)
        {
            var d = closes[i] - closes[i - 1];
            if (d > 0) gain += d; else loss -= d;
        }
        gain /= period;
        loss /= period;
        for (var i = period + 1; i < closes.Length; i++)
        {
            var d = closes[i] - closes[i - 1];
            gain = (gain * (period - 1) + Math.Max(d, 0)) / period;
            loss = (loss * (period - 1) + Math.Max(-d, 0)) / period;
        }
        return loss == 0 ? 100 : 100 - 100 / (1 + gain / loss);
    }
}

/// <summary>One choice in a filter dropdown; a null <see cref="Match"/> means "Any".</summary>
public sealed record FilterOption(string Label, Func<ScreenerRow, bool>? Match)
{
    public override string ToString() => Label;
}

[Flags]
public enum FilterScope { Stocks = 1, Crypto = 2, Both = Stocks | Crypto }

/// <summary>A screener dropdown. Highlighted in the UI while set to anything but its default.</summary>
public sealed partial class ScreenerFilter : ObservableObject
{
    private readonly int _defaultIndex;

    public ScreenerFilter(string name, string tooltip, IReadOnlyList<FilterOption> options, FilterScope scope = FilterScope.Both, int defaultIndex = 0)
    {
        Name = name;
        Tooltip = tooltip;
        Options = options;
        Scope = scope;
        _defaultIndex = defaultIndex;
        _selected = options[defaultIndex];
    }

    public string Name { get; }
    public string Tooltip { get; }
    public IReadOnlyList<FilterOption> Options { get; }
    public FilterScope Scope { get; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsActive))]
    private FilterOption _selected;

    /// <summary>False when the filter doesn't apply to the current mode (stocks vs crypto).</summary>
    [ObservableProperty] private bool _isVisible = true;

    public bool IsActive => Selected != Options[_defaultIndex];

    public event Action? Changed;
    partial void OnSelectedChanged(FilterOption value) => Changed?.Invoke();

    public bool Matches(ScreenerRow row) => Selected.Match?.Invoke(row) ?? true;

    public void Reset() => Selected = Options[_defaultIndex];
}

public sealed record FilterGroup(string Title, IReadOnlyList<ScreenerFilter> Filters);

/// <summary>The screener's dropdowns, in the categories most screeners use.</summary>
public static class ScreenerFilters
{
    private static readonly FilterOption Any = new("Any", null);

    private static FilterOption When(string label, Func<ScreenerRow, double?> value, Func<double, bool> test) =>
        new(label, r => value(r) is { } v && test(v));

    private static FilterOption When(string label, Func<ScreenerRow, bool> test) => new(label, test);

    /// <summary>Up / Down / Up x%+ / Down x%+ for a fractional change.</summary>
    private static List<FilterOption> Moves(Func<ScreenerRow, double?> value, params double[] steps)
    {
        List<FilterOption> options = [Any, When("Up", value, v => v > 0), When("Down", value, v => v < 0)];
        options.AddRange(steps.Select(s => When($"Up {s:0.#}%+", value, v => v >= s / 100)));
        options.AddRange(steps.Select(s => When($"Down {s:0.#}%+", value, v => v <= -s / 100)));
        return options;
    }

    private static List<FilterOption> Dollars(Func<ScreenerRow, double?> value) =>
    [
        Any,
        When("Over $100K", value, v => v > 1e5),
        When("Over $1M", value, v => v > 1e6),
        When("Over $10M", value, v => v > 1e7),
        When("Over $100M", value, v => v > 1e8),
        When("Over $1B", value, v => v > 1e9),
        When("Under $1M", value, v => v < 1e6),
    ];

    /// <summary>Price vs. a moving average (fractional distance).</summary>
    private static List<FilterOption> VsAverage(Func<ScreenerRow, double?> value) =>
    [
        Any,
        When("Price above", value, v => v > 0),
        When("Price below", value, v => v < 0),
        When("5%+ above", value, v => v >= 0.05),
        When("10%+ above", value, v => v >= 0.10),
        When("20%+ above", value, v => v >= 0.20),
        When("5%+ below", value, v => v <= -0.05),
        When("10%+ below", value, v => v <= -0.10),
        When("20%+ below", value, v => v <= -0.20),
        When("Within 2%", value, v => Math.Abs(v) <= 0.02),
    ];

    /// <summary>Index of "Over $1" in the price dropdown: the stock default, to keep penny stocks out of movers lists.</summary>
    public const int PriceOverOneDollar = 6;

    public static ScreenerFilter Price() => new("Price", "Last price", [
        Any,
        When("Under $0.01", r => r.Price, v => v < 0.01),
        When("Under $1", r => r.Price, v => v < 1),
        When("Under $5", r => r.Price, v => v < 5),
        When("Under $10", r => r.Price, v => v < 10),
        When("Under $50", r => r.Price, v => v < 50),
        When("Over $1", r => r.Price, v => v >= 1),
        When("Over $5", r => r.Price, v => v >= 5),
        When("Over $10", r => r.Price, v => v >= 10),
        When("Over $20", r => r.Price, v => v >= 20),
        When("Over $50", r => r.Price, v => v >= 50),
        When("Over $100", r => r.Price, v => v >= 100),
        When("Over $1,000", r => r.Price, v => v >= 1000),
        When("$1 to $10", r => r.Price, v => v is >= 1 and <= 10),
        When("$10 to $50", r => r.Price, v => v is >= 10 and <= 50),
        When("$50 to $200", r => r.Price, v => v is >= 50 and <= 200),
    ]);

    public static ScreenerFilter Quote() => new("Quote currency", "Which quote currency's pairs to show", [
        When("USD", r => r.Quote == "USD"),
        When("USDT", r => r.Quote == "USDT"),
        When("USDC", r => r.Quote == "USDC"),
        new("Any", null),
    ], FilterScope.Crypto);

    public static ScreenerFilter[] Descriptive(ScreenerFilter price, ScreenerFilter quote) =>
    [
        price,
        quote,
        new("Change today", "Price change since the previous close (crypto: previous UTC day)", Moves(r => r.ChangePct, 2, 5, 10, 20)),
        new("Gap", "Today's open vs. the previous close", Moves(r => r.GapPct, 2, 5, 10), FilterScope.Stocks),
        new("$ volume today", "Volume × price, today so far", Dollars(r => r.DollarVolume)),
        new("Avg $ volume (20d)", "Average daily volume × close over the last 20 sessions", Dollars(r => r.AvgDollarVolume)),
        new("Relative volume", "Today's volume ÷ the 20-day average (crypto: today is a partial UTC day)", [
            Any,
            When("Over 0.5×", r => r.RelVolume, v => v > 0.5),
            When("Over 1×", r => r.RelVolume, v => v > 1),
            When("Over 1.5×", r => r.RelVolume, v => v > 1.5),
            When("Over 2×", r => r.RelVolume, v => v > 2),
            When("Over 3×", r => r.RelVolume, v => v > 3),
            When("Over 5×", r => r.RelVolume, v => v > 5),
            When("Under 1×", r => r.RelVolume, v => v < 1),
            When("Under 0.5×", r => r.RelVolume, v => v < 0.5),
        ]),
        new("Share volume", "Shares traded today", [
            Any,
            When("Over 100K", r => r.Volume, v => v > 1e5),
            When("Over 500K", r => r.Volume, v => v > 5e5),
            When("Over 1M", r => r.Volume, v => v > 1e6),
            When("Over 5M", r => r.Volume, v => v > 5e6),
            When("Over 10M", r => r.Volume, v => v > 1e7),
        ], FilterScope.Stocks),
    ];

    public static ScreenerFilter[] Performance() =>
    [
        new("Week", "Price change over the last 7 days", Moves(r => r.Perf1W, 5, 10, 20, 50)),
        new("Month", "Price change over the last 30 days", Moves(r => r.Perf1M, 10, 20, 50, 100)),
        new("3 months", "Price change over the last 91 days", Moves(r => r.Perf3M, 10, 20, 50, 100)),
        new("Volatility", "Average daily range (high − low) ÷ close over the last 14 sessions", [
            Any,
            When("Over 2%", r => r.Volatility, v => v > 0.02),
            When("Over 3%", r => r.Volatility, v => v > 0.03),
            When("Over 5%", r => r.Volatility, v => v > 0.05),
            When("Over 10%", r => r.Volatility, v => v > 0.10),
            When("Under 2%", r => r.Volatility, v => v < 0.02),
            When("Under 1%", r => r.Volatility, v => v < 0.01),
        ]),
    ];

    public static ScreenerFilter[] Technical() =>
    [
        new("RSI (14)", "Relative Strength Index on daily closes", [
            Any,
            When("Overbought (70+)", r => r.Rsi14, v => v >= 70),
            When("Oversold (30−)", r => r.Rsi14, v => v <= 30),
            When("Above 60", r => r.Rsi14, v => v > 60),
            When("Above 50", r => r.Rsi14, v => v > 50),
            When("Below 50", r => r.Rsi14, v => v < 50),
            When("Below 40", r => r.Rsi14, v => v < 40),
            When("Neutral (40–60)", r => r.Rsi14, v => v is >= 40 and <= 60),
        ]),
        new("20-day SMA", "Price vs. the 20-day simple moving average", VsAverage(r => r.VsSma20)),
        new("50-day SMA", "Price vs. the 50-day simple moving average", VsAverage(r => r.VsSma50)),
        new("200-day SMA", "Price vs. the 200-day simple moving average", VsAverage(r => r.VsSma200)),
        new("50 vs 200 SMA", "Trend: the 50-day average above or below the 200-day", [
            Any,
            When("50 above 200 (bullish)", r => r.Sma50AboveSma200 == true),
            When("50 below 200 (bearish)", r => r.Sma50AboveSma200 == false),
        ]),
        new("52-week high/low", "Where the price sits in its 52-week range", [
            Any,
            When("New high today", r => r.IsNewHigh52W),
            When("Within 5% of high", r => r.FromHigh52W, v => v >= -0.05),
            When("Within 10% of high", r => r.FromHigh52W, v => v >= -0.10),
            When("10%+ below high", r => r.FromHigh52W, v => v <= -0.10),
            When("30%+ below high", r => r.FromHigh52W, v => v <= -0.30),
            When("50%+ below high", r => r.FromHigh52W, v => v <= -0.50),
            When("New low today", r => r.IsNewLow52W),
            When("Within 5% of low", r => r.FromLow52W, v => v <= 0.05),
            When("Within 10% of low", r => r.FromLow52W, v => v <= 0.10),
            When("50%+ above low", r => r.FromLow52W, v => v >= 0.50),
            When("100%+ above low", r => r.FromLow52W, v => v >= 1.00),
        ]),
    ];
}
