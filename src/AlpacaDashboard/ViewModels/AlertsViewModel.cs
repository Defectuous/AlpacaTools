using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Threading;
using AlpacaDashboard.Models;
using AlpacaDashboard.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AlpacaDashboard.ViewModels;

public sealed record AlertConditionOption(AlertCondition Value, string Label)
{
    public bool IsPositionBased => PriceAlert.IsPositionCondition(Value);
}

/// <summary>
/// The Alerts tab: alerts on any stock or crypto pair's price, or on a position's P/L, checked every 30 seconds with
/// one batched snapshot request per asset type, and instantly for symbols the account on screen streams. Checks keep
/// running while the window is minimized, which is when alerts matter most.
/// </summary>
public sealed partial class AlertsViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    private readonly Func<AccountViewModel?> _selected;
    private readonly Func<IReadOnlyCollection<AccountViewModel>> _accounts;
    private readonly AlertNotifier _notifier;
    private readonly DispatcherTimer _pollTimer;
    // Previous close per symbol, from the last poll; streamed prices need it for the day-change conditions.
    private readonly Dictionary<string, decimal> _prevClose = new(StringComparer.Ordinal);
    // Positions of accounts not on screen (which don't keep theirs current), fetched for their P/L alerts.
    private readonly Dictionary<Guid, Dictionary<string, Holding>> _offscreenHoldings = [];
    private bool _polling;

    private sealed record Holding(decimal SignedQty, decimal CostBasis);

    /// <param name="selected">The account on screen: its keys fetch prices, and new P/L alerts follow its positions.</param>
    /// <param name="accounts">Every account, for P/L alerts on accounts not on screen.</param>
    public AlertsViewModel(Func<AccountViewModel?> selected, Func<IReadOnlyCollection<AccountViewModel>> accounts, AlertNotifier notifier)
    {
        _selected = selected;
        _accounts = accounts;
        _notifier = notifier;
        Search = new SymbolSearchViewModel(selected, PickSymbol) { PickHint = "Enter picks it for the alert" };
        foreach (var alert in AlertStore.Load()) Alerts.Add(new AlertRowViewModel(alert) { AccountName = AccountName(alert.AccountId) });
        _newCondition = Conditions[0];
        UpdateNotifier();

        _pollTimer = new DispatcherTimer { Interval = PollInterval };
        _pollTimer.Tick += async (_, _) => await PollAsync();
        _pollTimer.Start();
        _ = PollAsync();
    }

    public ObservableCollection<AlertRowViewModel> Alerts { get; } = [];

    /// <summary>Symbol picker for a new alert.</summary>
    public SymbolSearchViewModel Search { get; }

    public IReadOnlyList<AlertConditionOption> Conditions { get; } =
    [
        new(AlertCondition.PriceAtOrAbove, "Price at or above"),
        new(AlertCondition.PriceAtOrBelow, "Price at or below"),
        new(AlertCondition.UpTodayBy, "Up today by at least"),
        new(AlertCondition.DownTodayBy, "Down today by at least"),
        new(AlertCondition.ProfitAtLeast, "Position profit at least ($)"),
        new(AlertCondition.LossAtLeast, "Position loss at least ($)"),
        new(AlertCondition.GainPctAtLeast, "Position gain at least (%)"),
        new(AlertCondition.LossPctAtLeast, "Position loss at least (%)"),
    ];

    [ObservableProperty] private string? _status;
    [ObservableProperty] private bool _statusIsError;

    /// <summary>The account prices are fetched with: the one on screen, or any.</summary>
    private AccountViewModel? PricingAccount => _selected() ?? _accounts().FirstOrDefault();

    // ----- Tab badge -----

    /// <summary>Alerts that fired since the tab was last looked at.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(TabLabel))] private int _unseenCount;

    public string TabLabel => UnseenCount > 0 ? $"Alerts ({UnseenCount})" : "Alerts";

    private bool _isViewing;

    /// <summary>Whether the Alerts tab is on screen: new triggers don't count as unseen, and prices refresh even with nothing armed.</summary>
    public bool IsViewing
    {
        get => _isViewing;
        set
        {
            if (_isViewing == value) return;
            _isViewing = value;
            if (value)
            {
                UnseenCount = 0;
                _ = PollAsync();
            }
        }
    }

    // ----- New alert form -----

    [ObservableProperty, NotifyPropertyChangedFor(nameof(NewSymbolText), nameof(NewWarning))]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    private string? _newSymbol;

    private bool _newIsCrypto;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(NewSymbolText), nameof(NewWarning))]
    private decimal? _newSymbolPrice;

    private decimal? _newSymbolPrevClose;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(NewTargetUnit), nameof(NewSymbolText), nameof(NewWarning))]
    private AlertConditionOption _newCondition;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(NewWarning))] private string _newTarget = "";
    [ObservableProperty] private string _newNote = "";
    [ObservableProperty] private string? _formError;

    public string NewTargetUnit => PriceAlert.IsPercentCondition(NewCondition.Value) ? "%" : "$";

    public string NewSymbolText
    {
        get
        {
            if (NewSymbol is not { } symbol) return "Pick a symbol";
            if (NewSymbolPrice is not { } price) return $"{symbol} · loading price…";
            var text = $"{symbol} · now {AlertRowViewModel.FormatPrice(price)}{DayChangeText(price, _newSymbolPrevClose)}";
            if (_selected() is { } account)
            {
                text += NewHolding(account) is { } h
                    ? $" · P/L in {account.Name}: {PlText(Pl(h, price), PlPct(h, price))}"
                    : NewCondition.IsPositionBased ? $" · not held in {account.Name}" : "";
            }
            return text;
        }
    }

    /// <summary>Says so when the alert would fire on the very next check.</summary>
    public string? NewWarning
    {
        get
        {
            if (NewSymbol is null || NewSymbolPrice is not { } price || !TryParseTarget(NewTarget, out var target)) return null;
            var draft = new PriceAlert { Symbol = NewSymbol, Condition = NewCondition.Value, Target = target };
            var holding = _selected() is { } account ? NewHolding(account) : null;
            if (draft.IsPositionBased && holding is null) return null;
            return draft.IsMet(Inputs(price, _newSymbolPrevClose, holding))
                ? "That's already true, so this alert will fire on the next check."
                : null;
        }
    }

    private Holding? NewHolding(AccountViewModel account) =>
        NewSymbol is { } symbol && account.FindPosition(symbol) is { } row ? new(row.SignedQty, row.CostBasis) : null;

    /// <summary>Starts a new alert for a symbol, e.g. from a position row's Alert button.</summary>
    public void Prefill(string symbol, bool isCrypto, decimal? price)
    {
        SetNewSymbol(symbol, isCrypto);
        NewSymbolPrice = price;
        _ = LoadNewSymbolPriceAsync(symbol, isCrypto);
    }

    private void PickSymbol(SymbolAsset asset)
    {
        SetNewSymbol(asset.Symbol, asset.IsCrypto);
        _ = LoadNewSymbolPriceAsync(asset.Symbol, asset.IsCrypto);
    }

    private void SetNewSymbol(string symbol, bool isCrypto)
    {
        _newIsCrypto = isCrypto;
        _newSymbolPrevClose = null;
        NewSymbolPrice = null;
        NewSymbol = symbol;
        FormError = null;
    }

    private async Task LoadNewSymbolPriceAsync(string symbol, bool isCrypto)
    {
        if (PricingAccount is not { } account) return;
        try
        {
            var quotes = await FetchQuotesAsync(account, [(symbol, isCrypto)]);
            if (NewSymbol != symbol || !quotes.TryGetValue(symbol, out var quote)) return; // another symbol was picked meanwhile
            _newSymbolPrevClose = quote.PrevClose;
            NewSymbolPrice = quote.Price;
            OnPropertyChanged(nameof(NewSymbolText));
            OnPropertyChanged(nameof(NewWarning));
        }
        catch (Exception)
        {
            // The form works without a price; it only powers the hints.
        }
    }

    private bool CanAdd() => NewSymbol is not null;

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void Add()
    {
        if (NewSymbol is not { } symbol) return;
        if (!TryParseTarget(NewTarget, out var target) || target <= 0)
        {
            FormError = NewTargetUnit == "%" ? "Enter a percentage above 0, like 5."
                : NewCondition.IsPositionBased ? "Enter a dollar amount above 0, like 500."
                : "Enter a price above 0, like 150.25.";
            return;
        }

        Guid? accountId = null;
        if (NewCondition.IsPositionBased)
        {
            // P/L belongs to a position, so the alert follows the one in the account on screen.
            if (_selected() is not { } account || NewHolding(account) is null)
            {
                FormError = $"P/L alerts follow a position, and {symbol} isn't held in {_selected()?.Name ?? "this account"}.";
                return;
            }
            accountId = account.Profile.Id;
        }

        var alert = new PriceAlert
        {
            Symbol = symbol,
            IsCrypto = _newIsCrypto,
            Condition = NewCondition.Value,
            Target = target,
            AccountId = accountId,
            Note = string.IsNullOrWhiteSpace(NewNote) ? null : NewNote.Trim(),
        };
        var row = new AlertRowViewModel(alert) { AccountName = AccountName(accountId) };
        Alerts.Insert(0, row);
        Save();

        NewSymbol = null;
        NewSymbolPrice = null;
        NewTarget = "";
        NewNote = "";
        FormError = null;
        _ = PollAsync();
    }

    [RelayCommand]
    private void Rearm(AlertRowViewModel? row)
    {
        if (row is null) return;
        row.Alert = row.Alert with { TriggeredAt = null, TriggeredPrice = null, TriggeredPl = null };
        Save();
        _ = PollAsync();
    }

    [RelayCommand]
    private void Delete(AlertRowViewModel? row)
    {
        if (row is null) return;
        Alerts.Remove(row);
        Save();
    }

    [RelayCommand]
    private void ClearTriggered()
    {
        foreach (var row in Alerts.Where(a => !a.IsArmed).ToList()) Alerts.Remove(row);
        Save();
    }

    // ----- Checking -----

    /// <summary>A live price from the account on screen (stream, or bot-priority polling).</summary>
    public void OnLivePrice(string symbol, double price) => Apply(symbol, (decimal)price);

    private async Task PollAsync()
    {
        if (_polling || Alerts.Count == 0) return;
        if (!IsViewing && !Alerts.Any(a => a.IsArmed)) return; // nothing to watch: spend no requests
        if (PricingAccount is not { } account)
        {
            Status = "Add an account: alerts use its API keys to check prices.";
            StatusIsError = true;
            return;
        }

        _polling = true;
        try
        {
            await RefreshOffscreenHoldingsAsync();
            var symbols = Alerts.Select(a => (a.Symbol, a.IsCrypto)).Distinct().ToList();
            var quotes = await FetchQuotesAsync(account, symbols);
            foreach (var (symbol, quote) in quotes)
            {
                if (quote.PrevClose is { } prev) _prevClose[symbol] = prev;
                Apply(symbol, quote.Price);
            }
            Status = $"Checked {DateTime.Now:h:mm:ss tt} with {account.Name}'s keys · every {PollInterval.TotalSeconds:0}s, " +
                     "and instantly for streamed positions" +
                     (account.DataFeed.Plan == MarketDataPlan.AlgoTraderPlus ? "" : " · stock prices are IEX on the Basic plan");
            StatusIsError = false;
        }
        catch (Exception ex)
        {
            Status = $"Couldn't check prices: {ex.Message}. Retrying in {PollInterval.TotalSeconds:0}s.";
            StatusIsError = true;
        }
        finally
        {
            _polling = false;
        }
    }

    /// <summary>
    /// One positions request per account that has a watching P/L alert but isn't on screen. The account on screen
    /// needs none: its position rows are current.
    /// </summary>
    private async Task RefreshOffscreenHoldingsAsync()
    {
        var ids = Alerts.Where(a => a.IsArmed && a.Alert.IsPositionBased).Select(a => a.Alert.AccountId).OfType<Guid>().ToHashSet();
        foreach (var account in _accounts().Where(a => ids.Contains(a.Profile.Id) && !a.IsActive))
        {
            try
            {
                var positions = await account.Trading.GetPositionsAsync();
                _offscreenHoldings[account.Profile.Id] = positions
                    .Select(p => new PositionRowViewModel(p, account.CryptoTier))
                    .Where(p => p.HasMarketData)
                    .DistinctBy(p => p.DataSymbol)
                    .ToDictionary(p => p.DataSymbol, p => new Holding(p.SignedQty, p.CostBasis), StringComparer.Ordinal);
            }
            catch (Exception)
            {
                // Keep the last known positions; the next poll retries.
            }
        }
    }

    private void Apply(string symbol, decimal price)
    {
        if (price <= 0) return;
        foreach (var row in Alerts)
        {
            if (row.Symbol != symbol) continue;
            var holding = HoldingFor(row.Alert.AccountId, symbol);
            var now = Inputs(price, _prevClose.GetValueOrDefault(symbol), holding);
            row.AccountName = AccountName(row.Alert.AccountId);
            row.IsHeld = holding is not null;
            row.DayChange = now.DayChange;
            row.Pl = now.Pl;
            row.PlPct = now.PlPct;
            row.CurrentPrice = price;
            if (!row.IsArmed || (row.Alert.IsPositionBased && holding is null)) continue;
            if (row.Alert.IsMet(now)) Trigger(row, now);
        }
    }

    /// <summary>
    /// The position a row's P/L comes from: the alert's own account for P/L alerts; for price alerts, the account
    /// on screen, just to show P/L alongside.
    /// </summary>
    private Holding? HoldingFor(Guid? accountId, string symbol)
    {
        var account = accountId is { } id ? _accounts().FirstOrDefault(a => a.Profile.Id == id) : _selected();
        if (account is null) return null;
        if (account.IsActive)
            return account.FindPosition(symbol) is { } row ? new(row.SignedQty, row.CostBasis) : null;
        return _offscreenHoldings.GetValueOrDefault(account.Profile.Id)?.GetValueOrDefault(symbol);
    }

    private string? AccountName(Guid? accountId) => accountId is { } id
        ? _accounts().FirstOrDefault(a => a.Profile.Id == id)?.Name ?? "removed account"
        : null;

    private void Trigger(AlertRowViewModel row, AlertInputs now)
    {
        row.Alert = row.Alert with { TriggeredAt = DateTimeOffset.Now, TriggeredPrice = now.Price, TriggeredPl = now.Pl };
        Save();
        if (!IsViewing) UnseenCount++;

        var text = $"Now {AlertRowViewModel.FormatPrice(now.Price)}{DayChangeText(now.Price, _prevClose.GetValueOrDefault(row.Symbol))}";
        if (now.Pl is { } pl)
            text += $"\nP/L {PlText(pl, now.PlPct)}" + (row.AccountName is { } name ? $" in {name}" : "");
        if (row.Alert.Note is { } note) text += $"\n{note}";
        _notifier.Show($"{row.Symbol}: {row.ConditionText}", text);
    }

    private static AlertInputs Inputs(decimal price, decimal? prevClose, Holding? holding) =>
        new(price, DayChange(price, prevClose), holding is null ? null : Pl(holding, price), holding is null ? null : PlPct(holding, price));

    // Same arithmetic as the position rows: market value minus cost basis, shorts carrying negative quantity.
    private static decimal Pl(Holding h, decimal price) => h.SignedQty * price - h.CostBasis;
    private static decimal? PlPct(Holding h, decimal price) => h.CostBasis == 0 ? null : Pl(h, price) / Math.Abs(h.CostBasis);

    private static string PlText(decimal pl, decimal? pct) =>
        AlertRowViewModel.SignedMoney(pl) + (pct is { } p ? $" ({(p >= 0 ? "+" : "−")}{Math.Abs(p):P2})" : "");

    private sealed record Quote(decimal Price, decimal? PrevClose);

    /// <summary>
    /// Latest price and previous close for each symbol: one snapshot request for stocks (the plan's feed) and one
    /// for crypto. Crypto uses the bid/ask midpoint when quoted, as thin pairs can go minutes without a trade.
    /// </summary>
    private static async Task<Dictionary<string, Quote>> FetchQuotesAsync(AccountViewModel account,
        IReadOnlyCollection<(string Symbol, bool IsCrypto)> symbols)
    {
        var stocks = symbols.Where(s => !s.IsCrypto).Select(s => s.Symbol).Distinct().ToList();
        var crypto = symbols.Where(s => s.IsCrypto).Select(s => s.Symbol).Distinct().ToList();
        var stockTask = stocks.Count > 0 ? account.MarketData.GetSnapshotsAsync(stocks, account.DataFeed) : null;
        var cryptoTask = crypto.Count > 0 ? account.MarketData.GetCryptoSnapshotsAsync(crypto) : null;

        var quotes = new Dictionary<string, Quote>(StringComparer.Ordinal);
        if (stockTask is not null)
        {
            foreach (var (symbol, snap) in await stockTask)
                if (snap.LatestTrade?.Price is > 0 and var p)
                    quotes[symbol] = new((decimal)p, PrevClose(snap));
        }
        if (cryptoTask is not null)
        {
            foreach (var (pair, snap) in await cryptoTask)
            {
                double? price = snap.LatestQuote is { BidPrice: > 0, AskPrice: > 0 } q ? (q.BidPrice + q.AskPrice) / 2
                    : snap.LatestTrade?.Price is > 0 and var p ? p : null;
                if (price is { } value) quotes[pair] = new((decimal)value, PrevClose(snap));
            }
        }
        return quotes;
    }

    private static decimal? PrevClose(StockSnapshot snap) => snap.PrevDailyBar?.Close is > 0 and var c ? (decimal)c : null;

    private static decimal? DayChange(decimal price, decimal? prevClose) =>
        prevClose is > 0 and { } prev ? (price - prev) / prev : null;

    private static string DayChangeText(decimal price, decimal? prevClose) =>
        DayChange(price, prevClose) is { } change ? $" ({(change >= 0 ? "+" : "−")}{Math.Abs(change):P2} today)" : "";

    /// <summary>Accepts "150", "$150.25", "5%" and the user's own number format.</summary>
    private static bool TryParseTarget(string text, out decimal value)
    {
        var s = text.Trim().TrimStart('$').TrimEnd('%').Trim();
        return decimal.TryParse(s, NumberStyles.Number, CultureInfo.CurrentCulture, out value)
            || decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out value);
    }

    private void Save()
    {
        AlertStore.Save(Alerts.Select(a => a.Alert));
        UpdateNotifier();
    }

    private void UpdateNotifier()
    {
        var armed = Alerts.Count(a => a.IsArmed);
        _notifier.Visible = Alerts.Count > 0;
        _notifier.Tooltip = armed == 1 ? "Alpaca Dashboard · 1 alert watching" : $"Alpaca Dashboard · {armed} alerts watching";
    }

    public void Dispose() => _pollTimer.Stop();
}

/// <summary>One alert in the list, with the latest price (and position P/L) seen for its symbol.</summary>
public sealed partial class AlertRowViewModel(PriceAlert alert) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsArmed), nameof(ConditionText), nameof(StatusText), nameof(DistanceText))]
    private PriceAlert _alert = alert;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(PriceText), nameof(DistanceText))]
    private decimal? _currentPrice;

    /// <summary>Change since the previous close, as a fraction; null until known.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DistanceText))]
    private decimal? _dayChange;

    /// <summary>Unrealized P/L of the position (the alert's account, or the one on screen); null when not held.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DistanceText), nameof(PlText))] private decimal? _pl;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DistanceText), nameof(PlText))] private decimal? _plPct;

    public string PlText => Pl is { } pl
        ? SignedMoney(pl) + (PlPct is { } p ? $" ({Signed(p, "P2")})" : "")
        : "—";

    [ObservableProperty, NotifyPropertyChangedFor(nameof(DistanceText))] private bool _isHeld;

    /// <summary>The account a P/L alert follows; null for price alerts.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ConditionText), nameof(DistanceText))] private string? _accountName;

    public string Symbol => Alert.Symbol;
    public bool IsCrypto => Alert.IsCrypto;
    public bool IsArmed => Alert.IsArmed;

    public string ConditionText => Alert.Condition switch
    {
        AlertCondition.PriceAtOrAbove => $"at or above {FormatPrice(Alert.Target)}",
        AlertCondition.PriceAtOrBelow => $"at or below {FormatPrice(Alert.Target)}",
        AlertCondition.UpTodayBy => $"up {Alert.Target:0.##}% or more today",
        AlertCondition.DownTodayBy => $"down {Alert.Target:0.##}% or more today",
        AlertCondition.ProfitAtLeast => $"profit of {Alert.Target:C2} or more{InAccount}",
        AlertCondition.LossAtLeast => $"loss of {Alert.Target:C2} or more{InAccount}",
        AlertCondition.GainPctAtLeast => $"gain of {Alert.Target:0.##}% or more{InAccount}",
        AlertCondition.LossPctAtLeast => $"loss of {Alert.Target:0.##}% or more{InAccount}",
        _ => "",
    };

    private string InAccount => AccountName is { } name ? $" in {name}" : "";

    public string PriceText => CurrentPrice is { } p ? FormatPrice(p) : "—";

    /// <summary>How far the price, today's move, or the P/L still has to go, while armed.</summary>
    public string DistanceText
    {
        get
        {
            if (!IsArmed) return "";
            if (Alert.IsPositionBased && !IsHeld) return CurrentPrice is null ? "" : "not held";
            var t = Alert.Target;
            return Alert.Condition switch
            {
                AlertCondition.PriceAtOrAbove or AlertCondition.PriceAtOrBelow when CurrentPrice is > 0 and { } p =>
                    Alert.IsMet(new(p, null, null, null)) ? "" : $"{Signed((t - p) / p, "P2")} to go",
                AlertCondition.UpTodayBy when DayChange is { } c => c >= t / 100 ? "" : $"{t / 100 - c:P2} to go",
                AlertCondition.DownTodayBy when DayChange is { } c => c <= -t / 100 ? "" : $"{c + t / 100:P2} to go",
                AlertCondition.ProfitAtLeast when Pl is { } pl => pl >= t ? "" : $"{t - pl:C2} to go",
                AlertCondition.LossAtLeast when Pl is { } pl => pl <= -t ? "" : $"{pl + t:C2} to go",
                AlertCondition.GainPctAtLeast when PlPct is { } pct => pct >= t / 100 ? "" : $"{t / 100 - pct:P2} to go",
                AlertCondition.LossPctAtLeast when PlPct is { } pct => pct <= -t / 100 ? "" : $"{pct + t / 100:P2} to go",
                _ => "",
            };
        }
    }

    public string StatusText => Alert.TriggeredAt is { } at
        ? $"Fired {at.LocalDateTime:MMM d, h:mm tt}" +
          (Alert.TriggeredPrice is { } p ? $" at {FormatPrice(p)}" : "") +
          (Alert.TriggeredPl is { } pl ? $" · P/L {SignedMoney(pl)}" : "")
        : "Watching";

    /// <summary>Cents for most prices, more digits for sub-dollar stocks and coins.</summary>
    public static string FormatPrice(decimal price) =>
        Math.Abs(price) >= 1 ? price.ToString("C2") : Math.Abs(price) >= 0.01m ? price.ToString("C4") : price.ToString("C8");

    public static string SignedMoney(decimal amount) => $"{(amount >= 0 ? "+" : "−")}{Math.Abs(amount):C2}";

    private static string Signed(decimal value, string format) => $"{(value >= 0 ? "+" : "−")}{Math.Abs(value).ToString(format)}";
}
