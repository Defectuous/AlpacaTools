using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using AlpacaDashboard.Models;
using AlpacaDashboard.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AlpacaDashboard.ViewModels;

public enum TradeMode { Liquidate, SellQty, StopLimit }

public sealed record TifOption(string Code, string Label);

/// <summary>One line of the order preview; <see cref="Signed"/> values are colored up/down.</summary>
public sealed record PreviewLine(string Label, string Value, decimal? Signed = null);

/// <summary>
/// Closes or protects one position: liquidate at market, sell part of it (market or limit), or place a stop limit.
/// Follows the live row (price ticks, refreshes) and shows estimates before anything is sent.
/// </summary>
public sealed partial class TradePositionViewModel : ObservableObject, IDisposable
{
    private const decimal DefaultStopLimitGap = 0.005m; // limit sits 0.5% past the stop when set by a shortcut

    internal static readonly TifOption Day = new("day", "Day");
    internal static readonly TifOption Gtc = new("gtc", "Good till canceled");
    internal static readonly TifOption Ioc = new("ioc", "Immediate or cancel");

    private readonly AccountViewModel _account;
    private bool _syncQueued;

    public TradePositionViewModel(AccountViewModel account, PositionRowViewModel row, TradeMode mode = TradeMode.SellQty)
    {
        _account = account;
        _row = row;
        _mode = mode;
        row.PropertyChanged += OnRowChanged;
        account.Positions.CollectionChanged += OnAccountCollectionChanged;
        account.OpenOrders.CollectionChanged += OnAccountCollectionChanged;

        TifOptions = row.IsCrypto ? [Gtc, Ioc] : IsFractionalPosition ? [Day] : [Day, Gtc];
        _sellTif = TifOptions[0].Code;
        _stopTif = TifOptions.Contains(Gtc) ? Gtc.Code : TifOptions[0].Code;
        _sellQtyText = FormatQty(row.QtyAvailable);
        _stopQtyText = FormatQty(row.QtyAvailable);

        SyncFromAccount();
    }

    // ----- Position details -----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SideLabel), nameof(HeldQty), nameof(QtyUnit), nameof(CloseVerb), nameof(SubmitLabel))]
    private PositionRowViewModel _row;

    public string Symbol => Row.Symbol;
    public string AccountName => _account.Name;
    public string ModeLabel => _account.ModeLabel;
    public bool IsLiveAccount => !_account.IsPaper;
    public string SideLabel => Row.IsShort ? "SHORT" : "LONG";
    public string QtyUnit => Row.IsCrypto ? "" : "shares";
    public decimal HeldQty => Math.Abs(Row.Qty) - Row.QtyAvailable;
    public string CloseVerb => Row.IsShort ? "Buy to cover" : "Sell";
    private string CloseSide => Row.IsShort ? "buy" : "sell";
    private bool IsFractionalPosition => !Row.IsCrypto && Math.Abs(Row.Qty) % 1 != 0;

    /// <summary>The position disappeared on a refresh (closed by a fill, here or elsewhere).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    private bool _isPositionClosed;

    public ObservableCollection<AlpacaOrder> SymbolOrders { get; } = [];

    // ----- Mode -----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLiquidate), nameof(IsSellQty), nameof(IsStopLimit), nameof(SubmitLabel))]
    private TradeMode _mode = TradeMode.SellQty;

    public bool IsLiquidate => Mode == TradeMode.Liquidate;
    public bool IsSellQty => Mode == TradeMode.SellQty;
    public bool IsStopLimit => Mode == TradeMode.StopLimit;

    public string SubmitLabel => Mode switch
    {
        TradeMode.Liquidate => $"Liquidate {Symbol}",
        TradeMode.SellQty => $"{CloseVerb} {Symbol}",
        _ => "Place stop limit",
    };

    [RelayCommand]
    private void SetMode(TradeMode mode) => Mode = mode;

    public IReadOnlyList<TifOption> TifOptions { get; }

    // ----- Inputs -----

    [ObservableProperty] private bool _cancelOrdersFirst = true;

    [ObservableProperty] private string _sellQtyText;
    [ObservableProperty] private bool _sellIsLimit;
    [ObservableProperty] private string _sellLimitText = "";
    [ObservableProperty] private string _sellTif;

    [ObservableProperty] private string _stopQtyText;
    [ObservableProperty] private string _stopPriceText = "";
    [ObservableProperty] private string _stopLimitText = "";
    [ObservableProperty] private string _stopTif;

    partial void OnModeChanged(TradeMode value) => Recompute();
    partial void OnCancelOrdersFirstChanged(bool value) => Recompute();
    partial void OnSellQtyTextChanged(string value) => Recompute();
    partial void OnSellLimitTextChanged(string value) => Recompute();
    partial void OnSellTifChanged(string value) => Recompute();
    partial void OnStopQtyTextChanged(string value) => Recompute();
    partial void OnStopPriceTextChanged(string value) => Recompute();
    partial void OnStopLimitTextChanged(string value) => Recompute();
    partial void OnStopTifChanged(string value) => Recompute();

    partial void OnSellIsLimitChanged(bool value)
    {
        if (value && SellLimitText.Length == 0) SellLimitText = FormatPrice(Row.CurrentPrice);
        Recompute();
    }

    /// <summary>Quick quantity buttons: a fraction of the available qty ("0.25", "0.5", "1").</summary>
    [RelayCommand]
    private void SetSellFraction(string fraction) => SellQtyText = FormatQty(FractionOfAvailable(fraction));

    [RelayCommand]
    private void SetStopFraction(string fraction) => StopQtyText = FormatQty(FractionOfAvailable(fraction));

    private decimal FractionOfAvailable(string fraction)
    {
        var f = decimal.Parse(fraction, CultureInfo.InvariantCulture);
        var qty = Row.QtyAvailable * f;
        if (f >= 1) return Row.QtyAvailable;
        // Whole-share positions stay whole; fractional and crypto keep Alpaca's 9 decimals.
        return Row.IsCrypto || IsFractionalPosition
            ? Math.Floor(qty * 1_000_000_000m) / 1_000_000_000m
            : Math.Floor(qty);
    }

    /// <summary>Stop shortcuts: "0.02" puts the stop 2% past the current price (below for a long); "entry" at break-even.</summary>
    [RelayCommand]
    private void SetStopOffset(string offset)
    {
        var dir = Row.IsShort ? 1m : -1m;
        var stop = offset == "entry"
            ? Row.AvgEntryPrice
            : Row.CurrentPrice * (1 + dir * decimal.Parse(offset, CultureInfo.InvariantCulture));
        stop = RoundPrice(stop);
        StopPriceText = FormatPrice(stop);
        StopLimitText = FormatPrice(RoundPrice(stop * (1 + dir * DefaultStopLimitGap)));
    }

    // ----- Preview and validation -----

    [ObservableProperty] private IReadOnlyList<PreviewLine> _preview = [];

    /// <summary>Blocks submitting.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    private string? _problem;

    /// <summary>Worth knowing, but doesn't block.</summary>
    [ObservableProperty] private string? _warning;

    [ObservableProperty] private string? _status;
    [ObservableProperty] private bool _statusIsError;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    private bool _isBusy;

    private void Recompute()
    {
        var lines = new List<PreviewLine>();
        string? problem = null, warning = null;
        switch (Mode)
        {
            case TradeMode.Liquidate: BuildLiquidate(lines, ref problem, ref warning); break;
            case TradeMode.SellQty: BuildSell(lines, ref problem, ref warning); break;
            case TradeMode.StopLimit: BuildStop(lines, ref problem, ref warning); break;
        }
        Preview = lines;
        Problem = IsPositionClosed ? "This position is closed." : problem;
        Warning = warning;
    }

    private void BuildLiquidate(List<PreviewLine> lines, ref string? problem, ref string? warning)
    {
        var qty = Math.Abs(Row.Qty);
        lines.Add(new("Order", $"{CloseVerb} all {FormatQty(qty)} {Symbol} at market"));
        AddFillEstimate(lines, qty, Row.CurrentPrice, "at market");
        lines.Add(new("Remaining position", "none"));

        if (SymbolOrders.Count == 0) return;
        if (CancelOrdersFirst)
            warning = $"{SymbolOrders.Count} open order(s) on {Symbol} will be cancelled first.";
        else if (HeldQty > 0)
            problem = $"{FormatQty(HeldQty)} is held by open orders; Alpaca rejects the close unless they're cancelled.";
        else
            warning = $"{SymbolOrders.Count} open order(s) on {Symbol} stay open and could reopen a position.";
    }

    private void BuildSell(List<PreviewLine> lines, ref string? problem, ref string? warning)
    {
        if (!TryValidateQty(SellQtyText, out var qty, ref problem)) return;

        var price = Row.CurrentPrice;
        string priceText = "at market";
        if (SellIsLimit)
        {
            if (!TryParse(SellLimitText, out var limit) || limit <= 0) { problem = "Enter a limit price."; return; }
            price = RoundPrice(limit);
            priceText = $"limit {Fmt(price)}";
            var crossing = Row.IsShort ? price >= Row.CurrentPrice : price <= Row.CurrentPrice;
            if (crossing) warning = $"The limit is through the current price ({Fmt(Row.CurrentPrice)}), so it will likely fill right away.";
        }
        var tif = SellIsLimit ? SellTif : MarketTif;
        if (!Row.IsCrypto && qty % 1 != 0 && tif != "day") { problem = "Fractional share orders must be Day orders."; return; }

        lines.Add(new("Order", $"{CloseVerb} {FormatQty(qty)} {Symbol} · {priceText} · {tif.ToUpperInvariant()}"));
        AddFillEstimate(lines, qty, price, SellIsLimit ? "at the limit" : "at market");
        lines.Add(new("Remaining position", $"{FormatQty(Math.Abs(Row.Qty) - qty)} {QtyUnit}".Trim()));
    }

    private void BuildStop(List<PreviewLine> lines, ref string? problem, ref string? warning)
    {
        if (!TryValidateQty(StopQtyText, out var qty, ref problem)) return;
        if (!TryParse(StopPriceText, out var stop) || stop <= 0) { problem = "Enter a stop price, or use a shortcut."; return; }
        if (!TryParse(StopLimitText, out var limit) || limit <= 0) { problem = "Enter a limit price."; return; }
        stop = RoundPrice(stop);
        limit = RoundPrice(limit);

        var current = Row.CurrentPrice;
        if (Row.IsShort ? stop <= current : stop >= current)
        {
            problem = $"A {CloseSide} stop must be {(Row.IsShort ? "above" : "below")} the current price ({Fmt(current)}).";
            return;
        }
        if (Row.IsShort ? limit < stop : limit > stop)
            warning = $"The limit is {(Row.IsShort ? "below" : "above")} the stop: once triggered it fills only if the price comes back to the limit.";
        if (!Row.IsCrypto && qty % 1 != 0 && StopTif != "day") { problem = "Fractional share orders must be Day orders."; return; }

        lines.Add(new("Order", $"{CloseVerb} {FormatQty(qty)} {Symbol} · stop {Fmt(stop)} · limit {Fmt(limit)} · {StopTif.ToUpperInvariant()}"));
        lines.Add(new("Triggers at", $"{Fmt(stop)} ({(stop - current) / current:+0.00%;-0.00%} from {Fmt(current)})"));
        lines.Add(new("Est. P/L if filled at stop", Signed(RealizedPl(qty, stop)), RealizedPl(qty, stop)));
        lines.Add(new("Est. P/L if filled at limit", Signed(RealizedPl(qty, limit)), RealizedPl(qty, limit)));
        lines.Add(new("Est. fees", Fees(qty, stop).ToString("C2")));
        warning ??= "If the price gaps past the limit, the order won't fill and you keep the position.";
    }

    private bool TryValidateQty(string text, out decimal qty, ref string? problem)
    {
        if (!TryParse(text, out qty) || qty <= 0) { problem = "Enter a quantity."; return false; }
        var held = Math.Abs(Row.Qty);
        if (qty > held) { problem = $"The position is {FormatQty(held)} {QtyUnit}.".Replace("  ", " "); return false; }
        if (qty > Row.QtyAvailable)
        {
            problem = $"Only {FormatQty(Row.QtyAvailable)} is available; {FormatQty(HeldQty)} is held by the open orders below. Cancel them first.";
            return false;
        }
        return true;
    }

    private void AddFillEstimate(List<PreviewLine> lines, decimal qty, decimal price, string where)
    {
        var gross = qty * price;
        var fees = Fees(qty, price);
        var pl = RealizedPl(qty, price);
        lines.Add(new(Row.IsShort ? $"Est. cost {where}" : $"Est. proceeds {where}", gross.ToString("C2")));
        lines.Add(new("Est. fees", fees.ToString("C2")));
        lines.Add(new(Row.IsShort ? "Est. total cost" : "Est. net proceeds", (Row.IsShort ? gross + fees : gross - fees).ToString("C2")));
        lines.Add(new("Est. realized P/L", Signed(pl), pl));
    }

    private decimal Fees(decimal qty, decimal price) =>
        FeeEstimator.Estimate(Row.AssetClass, Row.IsShort, qty, qty * price, Row.CryptoTier).Total;

    private decimal RealizedPl(decimal qty, decimal price) =>
        (price - Row.AvgEntryPrice) * qty * (Row.IsShort ? -1 : 1) - Fees(qty, price);

    private string MarketTif => Row.IsCrypto ? "gtc" : "day";

    // ----- Actions -----

    private bool CanSubmit() => Problem is null && !IsBusy && !IsPositionClosed;

    [RelayCommand(CanExecute = nameof(CanSubmit))]
    private async Task SubmitAsync()
    {
        var summary = Preview.Count > 0 ? Preview[0].Value : SubmitLabel;
        var cancels = Mode == TradeMode.Liquidate && CancelOrdersFirst ? SymbolOrders.ToList() : [];
        var message = $"{(IsLiveAccount ? "LIVE account (real money)" : "Paper account")} · {AccountName}\n\n{summary}" +
                      (cancels.Count > 0 ? $"\n\nFirst cancels {cancels.Count} open order(s) on {Symbol}." : "") +
                      "\n\nSubmit this order?";
        if (MessageBox.Show(message, "Confirm order", MessageBoxButton.YesNo,
                IsLiveAccount ? MessageBoxImage.Warning : MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        IsBusy = true;
        SetStatus("Submitting…", isError: false);
        try
        {
            AlpacaOrder order;
            if (Mode == TradeMode.Liquidate)
            {
                if (cancels.Count > 0)
                {
                    SetStatus($"Cancelling {cancels.Count} open order(s)…", isError: false);
                    await _account.CancelOrdersAsync(cancels);
                }
                order = await _account.ClosePositionAsync(Row.Symbol);
            }
            else
            {
                order = await _account.SubmitOrderAsync(BuildOrder());
            }
            SetStatus($"✓ Submitted ({order.Status}): {order.Summary}", isError: false);
        }
        catch (Exception ex)
        {
            SetStatus($"✕ {ex.Message}", isError: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private OrderRequest BuildOrder()
    {
        // Crypto orders use the pair ("BTC/USD"); positions report it without the slash.
        var order = new OrderRequest { Symbol = Row.IsCrypto ? Row.DataSymbol : Row.Symbol, Side = CloseSide };
        if (Mode == TradeMode.SellQty)
        {
            TryParse(SellQtyText, out var qty);
            order.Qty = Invariant(qty);
            if (SellIsLimit)
            {
                TryParse(SellLimitText, out var limit);
                order.Type = "limit";
                order.LimitPrice = Invariant(RoundPrice(limit));
                order.TimeInForce = SellTif;
            }
            else
            {
                order.Type = "market";
                order.TimeInForce = MarketTif;
            }
        }
        else
        {
            TryParse(StopQtyText, out var qty);
            TryParse(StopPriceText, out var stop);
            TryParse(StopLimitText, out var limit);
            order.Qty = Invariant(qty);
            order.Type = "stop_limit";
            order.StopPrice = Invariant(RoundPrice(stop));
            order.LimitPrice = Invariant(RoundPrice(limit));
            order.TimeInForce = StopTif;
        }
        return order;
    }

    [RelayCommand]
    private async Task CancelOrderAsync(AlpacaOrder order)
    {
        IsBusy = true;
        SetStatus($"Cancelling {order.Summary}…", isError: false);
        try
        {
            await _account.CancelOrdersAsync([order]);
            SymbolOrders.Remove(order);
            SetStatus("✓ Order cancelled. Available qty updates with the next refresh.", isError: false);
        }
        catch (Exception ex)
        {
            SetStatus($"✕ {ex.Message}", isError: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void SetStatus(string text, bool isError)
    {
        Status = text;
        StatusIsError = isError;
    }

    // ----- Following the account's live data -----

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PositionRowViewModel.CurrentPrice)) Recompute();
    }

    // A refresh clears and refills the collections; sync once after it finishes.
    private void OnAccountCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (_syncQueued) return;
        _syncQueued = true;
        Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _syncQueued = false;
            SyncFromAccount();
        });
    }

    private void SyncFromAccount()
    {
        var match = _account.Positions.FirstOrDefault(p => p.Symbol == Symbol);
        IsPositionClosed = match is null;
        if (match is not null && match != Row)
        {
            Row.PropertyChanged -= OnRowChanged;
            Row = match;
            Row.PropertyChanged += OnRowChanged;
        }

        var key = Symbol.Replace("/", "");
        SymbolOrders.Clear();
        foreach (var order in _account.OpenOrders.Where(o => o.Symbol.Replace("/", "") == key))
            SymbolOrders.Add(order);
        Recompute();
    }

    public void Dispose()
    {
        Row.PropertyChanged -= OnRowChanged;
        _account.Positions.CollectionChanged -= OnAccountCollectionChanged;
        _account.OpenOrders.CollectionChanged -= OnAccountCollectionChanged;
    }

    // ----- Formatting -----

    /// <summary>Alpaca's price increments: equities $0.01 at $1 and up, $0.0001 below; crypto up to 9 decimals.</summary>
    private decimal RoundPrice(decimal price) =>
        Math.Round(price, Row.IsCrypto ? 9 : price >= 1 ? 2 : 4, MidpointRounding.AwayFromZero);

    private static bool TryParse(string? text, out decimal value) =>
        decimal.TryParse(text?.Replace("$", "").Trim(), NumberStyles.Number, CultureInfo.CurrentCulture, out value);

    private static string FormatQty(decimal qty) => qty.ToString("0.#########", CultureInfo.CurrentCulture);
    private static string FormatPrice(decimal price) => price.ToString("0.#########", CultureInfo.CurrentCulture);
    private static string Invariant(decimal value) => value.ToString("0.#########", CultureInfo.InvariantCulture);
    private static string Fmt(decimal price) => SymbolChartViewModel.FormatPrice((double)price);
    private static string Signed(decimal v) => v >= 0 ? $"+{v:C2}" : $"−{Math.Abs(v):C2}";
}
