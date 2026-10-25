using System.Globalization;
using System.Windows;
using AlpacaDashboard.Models;
using AlpacaDashboard.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AlpacaDashboard.ViewModels;

/// <summary>
/// Opens or adds to a long position: buy a number of shares/coins or a dollar amount, at market or a limit.
/// Fetches a fresh price on open and shows estimates before anything is sent.
/// </summary>
public sealed partial class BuyViewModel : ObservableObject
{
    private readonly AccountViewModel _account;

    /// <param name="priceSource">Where <paramref name="price"/> came from, shown until the fresh price arrives.</param>
    public BuyViewModel(AccountViewModel account, string symbol, bool isCrypto, decimal price, string priceSource = "from screener")
    {
        _account = account;
        _priceSource = priceSource;
        _priceNote = priceSource;
        Symbol = symbol;
        IsCrypto = isCrypto;
        _price = price;
        TifOptions = isCrypto ? [TradePositionViewModel.Gtc, TradePositionViewModel.Ioc] : [TradePositionViewModel.Day, TradePositionViewModel.Gtc];
        _tif = TifOptions[0].Code;

        var held = account.FindPosition(symbol);
        HeldText = held is null ? null : $"You hold {FormatQty(held.Qty)} {QtyUnit} at {held.AvgEntryText}".Replace("  ", " ");

        Recompute();
        _ = RefreshPriceAsync();
    }

    // ----- Details -----

    public string Symbol { get; }
    public bool IsCrypto { get; }
    public string AccountName => _account.Name;
    public string ModeLabel => _account.ModeLabel;
    public bool IsLiveAccount => !_account.IsPaper;
    public string QtyUnit => IsCrypto ? "" : "shares";
    public string? HeldText { get; }
    private string AssetClass => IsCrypto ? "crypto" : "us_equity";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PriceText))]
    private decimal _price;

    public string PriceText => Price > 0 ? Fmt(Price) : "—";

    private readonly string _priceSource;

    /// <summary>Where the opening price came from ("from screener") until the fresh price arrives.</summary>
    [ObservableProperty] private string _priceNote;

    /// <summary>Crypto can't be bought on margin, so it draws on cash-only buying power.</summary>
    public decimal? BuyingPower => _account.Account is { } a ? IsCrypto ? a.NonMarginableBuyingPower : a.BuyingPower : null;

    private async Task RefreshPriceAsync()
    {
        try
        {
            var prices = await _account.MarketData.GetLatestPricesAsync(
                IsCrypto ? [] : [Symbol], IsCrypto ? [Symbol] : [], _account.DataFeed);
            if (prices.TryGetValue(Symbol, out var p) && p > 0)
            {
                Price = (decimal)p;
                PriceNote = $"latest · {DateTime.Now:h:mm:ss tt}";
                Recompute();
            }
        }
        catch
        {
            PriceNote = $"{_priceSource} (refresh failed)";
        }
    }

    // ----- Inputs -----

    public IReadOnlyList<TifOption> TifOptions { get; }

    /// <summary>Amount is dollars (notional) rather than shares/coins.</summary>
    [ObservableProperty] private bool _inDollars = true;
    [ObservableProperty] private string _amountText = "";
    [ObservableProperty] private bool _isLimit;
    [ObservableProperty] private string _limitText = "";
    [ObservableProperty] private string _tif;

    partial void OnInDollarsChanged(bool value) => Recompute();
    partial void OnAmountTextChanged(string value) => Recompute();
    partial void OnLimitTextChanged(string value) => Recompute();
    partial void OnTifChanged(string value) => Recompute();

    partial void OnIsLimitChanged(bool value)
    {
        if (value && LimitText.Length == 0 && Price > 0) LimitText = FormatPrice(Price);
        Recompute();
    }

    /// <summary>Quick dollar amounts ("100", "500", …).</summary>
    [RelayCommand]
    private void SetDollars(string amount)
    {
        InDollars = true;
        AmountText = amount;
    }

    // ----- Preview and validation -----

    [ObservableProperty] private IReadOnlyList<PreviewLine> _preview = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    private string? _problem;

    [ObservableProperty] private string? _warning;
    [ObservableProperty] private string? _status;
    [ObservableProperty] private bool _statusIsError;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    private bool _isBusy;

    public string SubmitLabel => $"Buy {Symbol}";

    /// <summary>What will be sent: a qty, or a notional for dollar amounts at market.</summary>
    private (decimal? Qty, decimal? Notional, decimal OrderPrice) _order;

    private void Recompute()
    {
        var lines = new List<PreviewLine>();
        string? problem = null, warning = null;
        Build(lines, ref problem, ref warning);
        Preview = problem is null ? lines : [];
        Problem = problem;
        Warning = warning;
    }

    private void Build(List<PreviewLine> lines, ref string? problem, ref string? warning)
    {
        if (!TryParse(AmountText, out var amount) || amount <= 0) { problem = InDollars ? "Enter a dollar amount." : "Enter a quantity."; return; }

        var price = Price;
        var priceText = "at market";
        if (IsLimit)
        {
            if (!TryParse(LimitText, out var limit) || limit <= 0) { problem = "Enter a limit price."; return; }
            price = RoundPrice(limit);
            priceText = $"limit {Fmt(price)}";
            if (Price > 0 && price >= Price) warning = $"The limit is at or above the current price ({Fmt(Price)}), so it will likely fill right away.";
        }
        if (price <= 0) { problem = "No price yet for this symbol."; return; }

        decimal qty;
        decimal? notional = null;
        if (InDollars && !IsLimit)
        {
            // Alpaca fills a dollar amount at market directly (fractional shares).
            notional = Math.Round(amount, 2);
            if (notional < 1) { problem = "Alpaca's minimum order is $1."; return; }
            qty = notional.Value / price;
        }
        else if (InDollars)
        {
            // A limit order needs a quantity: as much as the amount buys at the limit. Stocks stay whole shares.
            qty = IsCrypto ? Math.Floor(amount / price * 1_000_000_000m) / 1_000_000_000m : Math.Floor(amount / price);
            if (qty <= 0) { problem = $"{amount:C2} is less than one share at {Fmt(price)}. Use a market order or a larger amount."; return; }
        }
        else
        {
            qty = amount;
        }

        var tif = IsLimit ? Tif : IsCrypto ? "gtc" : "day";
        if (!IsCrypto && (notional is not null || qty % 1 != 0) && tif != "day") { problem = "Fractional share orders must be Day orders."; return; }

        var cost = notional ?? qty * price;
        var fees = FeeEstimator.Estimate(AssetClass, isShort: true /* a buy: no SEC/TAF */, qty, cost, _account.CryptoTier).Total;
        _order = (notional is null ? qty : null, notional, price);

        var what = notional is { } n ? $"{n:C2} of {Symbol}" : $"{FormatQty(qty)} {QtyUnit} {Symbol}".Replace("  ", " ");
        lines.Add(new("Order", $"Buy {what} · {priceText} · {tif.ToUpperInvariant()}"));
        if (notional is not null) lines.Add(new("Est. quantity", $"≈ {FormatQty(Math.Round(qty, IsCrypto ? 9 : 4))} {QtyUnit}".Trim()));
        lines.Add(new(IsLimit ? "Est. cost at the limit" : "Est. cost at market", cost.ToString("C2")));
        lines.Add(new("Est. fees", fees.ToString("C2")));
        lines.Add(new("Est. total", (cost + fees).ToString("C2")));
        if (BuyingPower is { } bp)
        {
            var left = bp - cost - fees;
            lines.Add(new(IsCrypto ? "Cash buying power after" : "Buying power after", left.ToString("C2"), left < 0 ? left : null));
            if (left < 0) warning = $"This is more than your {(IsCrypto ? "cash " : "")}buying power ({bp:C2}); Alpaca will likely reject it.";
        }
    }

    // ----- Actions -----

    private bool CanSubmit() => Problem is null && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanSubmit))]
    private async Task SubmitAsync()
    {
        var summary = Preview.Count > 0 ? Preview[0].Value : SubmitLabel;
        var message = $"{(IsLiveAccount ? "LIVE account (real money)" : "Paper account")} · {AccountName}\n\n{summary}\n\nSubmit this order?";
        if (MessageBox.Show(message, "Confirm order", MessageBoxButton.YesNo,
                IsLiveAccount ? MessageBoxImage.Warning : MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        IsBusy = true;
        SetStatus("Submitting…", isError: false);
        try
        {
            var order = await _account.SubmitOrderAsync(BuildOrder());
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
        var order = new OrderRequest
        {
            Symbol = Symbol,
            Side = "buy",
            Qty = _order.Qty is { } q ? Invariant(q) : null,
            Notional = _order.Notional is { } n ? Invariant(n) : null,
        };
        if (IsLimit)
        {
            order.Type = "limit";
            order.LimitPrice = Invariant(_order.OrderPrice);
            order.TimeInForce = Tif;
        }
        else
        {
            order.Type = "market";
            order.TimeInForce = IsCrypto ? "gtc" : "day";
        }
        return order;
    }

    private void SetStatus(string text, bool isError)
    {
        Status = text;
        StatusIsError = isError;
    }

    // ----- Formatting -----

    /// <summary>Alpaca's price increments: equities $0.01 at $1 and up, $0.0001 below; crypto up to 9 decimals.</summary>
    private decimal RoundPrice(decimal price) =>
        Math.Round(price, IsCrypto ? 9 : price >= 1 ? 2 : 4, MidpointRounding.AwayFromZero);

    private static bool TryParse(string? text, out decimal value) =>
        decimal.TryParse(text?.Replace("$", "").Trim(), NumberStyles.Number, CultureInfo.CurrentCulture, out value);

    private static string FormatQty(decimal qty) => qty.ToString("0.#########", CultureInfo.CurrentCulture);
    private static string FormatPrice(decimal price) => price.ToString("0.#########", CultureInfo.CurrentCulture);
    private static string Invariant(decimal value) => value.ToString("0.#########", CultureInfo.InvariantCulture);
    private static string Fmt(decimal price) => SymbolChartViewModel.FormatPrice((double)price);
}
