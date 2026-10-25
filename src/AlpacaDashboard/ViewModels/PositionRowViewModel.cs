using AlpacaDashboard.Models;
using AlpacaDashboard.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AlpacaDashboard.ViewModels;

/// <summary>A position row whose price can be updated live from the market data stream.</summary>
public sealed partial class PositionRowViewModel : ObservableObject
{
    private readonly AlpacaPosition _position;
    private readonly decimal _signedQty;

    public PositionRowViewModel(AlpacaPosition position, CryptoFeeTier cryptoTier)
    {
        _position = position;
        _cryptoTier = cryptoTier;
        _signedQty = position.Side == "short" ? -Math.Abs(position.Qty) : position.Qty;
        _currentPrice = position.CurrentPrice;
        SnapshotMarketValue = position.MarketValue;
    }

    public string Symbol => _position.Symbol;
    public string AssetClass => _position.AssetClass;
    public bool IsEquity => _position.AssetClass == "us_equity";
    public bool IsShort => _signedQty < 0;
    public bool IsCrypto => _position.AssetClass == "crypto";
    public bool HasMarketData => IsEquity || IsCrypto;

    /// <summary>Symbol as the market data API knows it: positions report crypto as "BTCUSD", data uses "BTC/USD".</summary>
    public string DataSymbol => IsCrypto ? ToCryptoPair(Symbol) : Symbol;

    private static readonly string[] CryptoQuotes = ["USDT", "USDC", "USD", "BTC"];

    private static string ToCryptoPair(string symbol)
    {
        if (symbol.Contains('/')) return symbol;
        foreach (var quote in CryptoQuotes)
            if (symbol.Length > quote.Length && symbol.EndsWith(quote, StringComparison.Ordinal))
                return $"{symbol[..^quote.Length]}/{quote}";
        return symbol;
    }

    public decimal Qty => _position.Qty;
    /// <summary>Quantity with shorts negative.</summary>
    public decimal SignedQty => _signedQty;
    /// <summary>Unsigned qty free to close (not held by open orders).</summary>
    public decimal QtyAvailable => Math.Abs(_position.QtyAvailable ?? _position.Qty);
    public decimal AvgEntryPrice => _position.AvgEntryPrice;
    public decimal CostBasis => _position.CostBasis;

    /// <summary>Market value as of the last REST refresh; live equity is computed from the difference.</summary>
    public decimal SnapshotMarketValue { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MarketValue), nameof(UnrealizedPl), nameof(UnrealizedPlPct), nameof(ChangeToday), nameof(PriceText),
        nameof(FeeEstimate), nameof(EstimatedFees), nameof(NetIfClosed), nameof(FeeTooltip))]
    private decimal _currentPrice;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FeeEstimate), nameof(EstimatedFees), nameof(NetIfClosed), nameof(FeeTooltip))]
    private CryptoFeeTier _cryptoTier;

    /// <summary>Estimated fees to close this position at market, at the current price.</summary>
    public FeeEstimate FeeEstimate => FeeEstimator.Estimate(AssetClass, IsShort, Qty, MarketValue, CryptoTier);
    public decimal EstimatedFees => FeeEstimate.Total;

    /// <summary>Cash received (long) or paid (short) when closing, after estimated fees.</summary>
    public decimal NetIfClosed => MarketValue - EstimatedFees;

    public string FeeTooltip =>
        $"Estimated fees to {(IsShort ? "buy to cover" : "sell")} at market:\n{FeeEstimate.Breakdown}\n\n" +
        (IsShort ? $"Cost to cover incl. fees: {Math.Abs(MarketValue) + EstimatedFees:C2}"
                 : $"Net proceeds after fees: {NetIfClosed:C2}");

    /// <summary>True once a streamed trade has updated this row since the last refresh.</summary>
    [ObservableProperty] private bool _isLive;

    /// <summary>True when the Basic plan's 30-symbol stock stream limit left this symbol out.</summary>
    [ObservableProperty] private bool _isOverStreamLimit;

    public string PriceText => SymbolChartViewModel.FormatPrice((double)CurrentPrice);
    public string AvgEntryText => SymbolChartViewModel.FormatPrice((double)AvgEntryPrice);

    public decimal MarketValue => _signedQty * CurrentPrice;
    public decimal UnrealizedPl => MarketValue - CostBasis;
    public decimal UnrealizedPlPct => CostBasis == 0 ? 0 : UnrealizedPl / Math.Abs(CostBasis);
    public decimal ChangeToday => _position.LastdayPrice == 0
        ? _position.ChangeToday
        : (CurrentPrice - _position.LastdayPrice) / _position.LastdayPrice;
}
