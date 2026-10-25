using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using AlpacaDashboard.Models;
using AlpacaDashboard.Services;
using AlpacaDashboard.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;

namespace AlpacaDashboard.ViewModels;

/// <summary>One followed Alpaca account: its summary, positions, activity, charts and live prices.</summary>
public sealed partial class AccountViewModel : ObservableObject, IDisposable
{
    // Max named slices in the allocation donut; the rest fold into "Other" (8 slots minus one kept free).
    private const int MaxAllocationSlices = 7;

    // ----- Refresh cadence -----
    // Positions/orders/activity change only when orders do, so with the trade-updates stream they refresh on
    // events and the timer is only a safety net. Between refreshes, equity moves with live prices.
    private static readonly TimeSpan CoreIntervalPolling = TimeSpan.FromSeconds(60);   // no event stream
    private static readonly TimeSpan CoreIntervalPushed = TimeSpan.FromMinutes(5);     // trade-updates connected
    private static readonly TimeSpan CoreIntervalBotPriority = TimeSpan.FromMinutes(2); // no streams at all
    private static readonly TimeSpan SummaryInterval = TimeSpan.FromMinutes(5);        // accounts not on screen
    private static readonly TimeSpan HistoryIntervalIntraday = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan HistoryIntervalDaily = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan CryptoTierMaxAge = TimeSpan.FromMinutes(60);
    private static readonly TimeSpan PricePollInterval = TimeSpan.FromSeconds(15);     // bot priority only
    private static readonly TimeSpan EventDebounce = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MinEventRefreshGap = TimeSpan.FromSeconds(10);   // bursts of fills → one refresh
    private static readonly TimeSpan LedgerIntervalBackground = TimeSpan.FromMinutes(30); // accounts not on screen
    private static readonly TimeSpan LedgerBackfillGap = TimeSpan.FromSeconds(3);          // ≤20 requests/min while history downloads
    private static readonly TimeSpan LedgerBackfillGapBotPriority = TimeSpan.FromSeconds(15); // ≤4/min

    private AlpacaClient _client;
    private MarketDataClient _marketData;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private DateTime _coreRefreshed = DateTime.MinValue;
    private DateTime _historyRefreshed = DateTime.MinValue;
    private DateTime _summaryRefreshed = DateTime.MinValue;
    private DateTime _lastEventRefresh = DateTime.MinValue;

    // Live prices: streams (or the bot-priority poller) write the latest price per symbol here;
    // a 1s UI timer applies them, so a busy SIP stream can't flood the dispatcher.
    private MarketDataStream? _stockStream;
    private MarketDataStream? _cryptoStream;
    private TradeUpdatesStream? _tradeStream;
    private readonly Dictionary<string, (StreamState State, string Message)> _streamStatuses = [];
    private readonly ConcurrentDictionary<string, double> _pendingPrices = new(StringComparer.Ordinal);
    private readonly DispatcherTimer _liveTimer;
    private readonly DispatcherTimer _pricePollTimer;
    private readonly DispatcherTimer _eventRefreshTimer;
    private bool _isActive;

    /// <summary>True for the account on screen, whose positions and prices are kept current.</summary>
    public bool IsActive => _isActive;
    private MarketDataPlan? _detectedPlan;
    // Alpaca refused the SIP stream (e.g. the subscription lapsed): Basic limits apply until the profile changes,
    // even when the plan is set manually, so refreshes don't keep flipping back to SIP.
    private bool _planRefused;
    private int _streamedStockCount;

    // The account's history in the local ledger; kept current from the activity page each refresh already fetches.
    private readonly AlpacaLedgerSync _ledgerSync;
    private CancellationTokenSource _ledgerCts = new();
    private DateTime _ledgerSynced = DateTime.MinValue;

    private CryptoFeeTier _cryptoTier = CryptoFeeTier.All[0];
    private DateTime _cryptoTierUpdated = DateTime.MinValue;

    public AccountViewModel(AccountProfile profile)
    {
        Profile = profile;
        _client = new AlpacaClient(profile);
        _marketData = new MarketDataClient(profile);
        _ledgerSync = new AlpacaLedgerSync(LedgerStore.Default, profile.Id);
        ApplyBudgetMode();

        _liveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _liveTimer.Tick += (_, _) => { ApplyLivePrices(); UpdateApiUsage(); };
        _pricePollTimer = new DispatcherTimer { Interval = PricePollInterval };
        _pricePollTimer.Tick += async (_, _) => await PollPricesAsync();
        _eventRefreshTimer = new DispatcherTimer();
        _eventRefreshTimer.Tick += async (_, _) =>
        {
            _eventRefreshTimer.Stop();
            _lastEventRefresh = DateTime.Now;
            await RefreshCoreAsync();
        };
        Screener = new ScreenerViewModel(this);
    }

    /// <summary>The Screener tab for this account's keys.</summary>
    public ScreenerViewModel Screener { get; }

    // The screener calls the APIs directly; these follow key changes made with Edit.
    internal AlpacaClient Trading => _client;
    internal MarketDataClient MarketData => _marketData;

    public AccountProfile Profile { get; private set; }
    public string Name => Profile.Name;
    public string ModeLabel => Profile.IsPaper ? "PAPER" : "LIVE";
    public bool IsPaper => Profile.IsPaper;
    public bool BotPriority => Profile.BotPriority;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAccount), nameof(LiveEquity), nameof(LiveDayChange), nameof(LiveDayChangePct))]
    private AlpacaAccount? _account;

    public bool HasAccount => Account is not null;

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private DateTime? _lastUpdated;

    [ObservableProperty] private HistoryRange _selectedRange = HistoryRange.Month;
    [ObservableProperty] private decimal? _rangeChange;
    [ObservableProperty] private decimal? _rangeChangePct;

    [ObservableProperty] private decimal _totalUnrealizedPl;
    [ObservableProperty] private decimal _totalUnrealizedPlPct;
    [ObservableProperty] private decimal _totalEstimatedFees;
    [ObservableProperty] private decimal _cryptoVolume30d;

    /// <summary>Feeds and limits for this account's market data plan. Treated as Basic until detected otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlanBadge), nameof(PlanDescription), nameof(PlanTooltip), nameof(IsPremiumPlan))]
    private DataFeedProfile _dataFeed = DataFeedProfile.For(MarketDataPlan.Unknown);

    public string PlanBadge => DataFeed.PlanName.ToUpperInvariant();
    public string PlanDescription => DataFeed.Description;
    public bool IsPremiumPlan => DataFeed.Plan == MarketDataPlan.AlgoTraderPlus;
    public string PlanTooltip => _planRefused && Profile.DataPlan == MarketDataPlanSetting.AlgoTraderPlus
        ? "Set to Algo Trader Plus with Edit, but Alpaca refused the SIP feed (subscription inactive?), so the Basic plan's limits apply."
        : Profile.DataPlan == MarketDataPlanSetting.Auto
            ? "Market data plan detected automatically. Override it with Edit."
            : "Market data plan set manually with Edit.";

    partial void OnDataFeedChanged(DataFeedProfile value) =>
        ApiBudget.MarketData(Profile.KeyId).PlanLimit = value.DataRequestsPerMinute;

    [ObservableProperty] private StreamState _streamState = StreamState.Stopped;
    [ObservableProperty] private string _streamStatus = "";

    /// <summary>True while Alpaca's trade-updates stream is pushing this account's order events.</summary>
    [ObservableProperty] private bool _tradeStreamConnected;

    /// <summary>The dashboard's own request rate on this key, e.g. "API 3/min".</summary>
    [ObservableProperty] private string _apiUsage = "";

    /// <summary>The local ledger's state, e.g. "Ledger: 1,234 entries since Jan 5, 2023 · matches positions".</summary>
    [ObservableProperty] private string _ledgerStatus = "";
    [ObservableProperty] private string _ledgerTooltip = "";
    [ObservableProperty] private bool _ledgerHasMismatch;

    [ObservableProperty] private string _apiUsageTooltip = "";
    [ObservableProperty] private bool _isApiThrottled;

    /// <summary>Change in position market value from live prices since the last REST refresh.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(LiveEquity), nameof(LiveDayChange), nameof(LiveDayChangePct))]
    private decimal _liveMarketValueDelta;

    public decimal? LiveEquity => Account is null ? null : Account.Equity + LiveMarketValueDelta;
    public decimal? LiveDayChange => Account is null ? null : LiveEquity - Account.LastEquity;
    public decimal? LiveDayChangePct => Account is not { LastEquity: > 0 } a ? null : LiveDayChange / a.LastEquity;

    public ObservableCollection<PositionRowViewModel> Positions { get; } = [];
    public ObservableCollection<AlpacaActivity> Activities { get; } = [];
    public ObservableCollection<AlpacaOrder> OpenOrders { get; } = [];

    [ObservableProperty] private ISeries[] _equitySeries = [];
    [ObservableProperty] private Axis[] _equityXAxes = [new Axis()];
    [ObservableProperty] private Axis[] _equityYAxes = [new Axis()];
    [ObservableProperty] private ISeries[] _allocationSeries = [];
    public ObservableCollection<AllocationSlice> AllocationLegend { get; } = [];

    public void UpdateProfile(AccountProfile profile)
    {
        Profile = profile;
        _ledgerCts.Cancel(); // a sync in flight holds the old client
        _ledgerCts = new CancellationTokenSource();
        _client.Dispose();
        _client = new AlpacaClient(profile);
        _marketData.Dispose();
        _marketData = new MarketDataClient(profile);
        _detectedPlan = null;
        _planRefused = false;
        ApplyBudgetMode();
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(ModeLabel));
        OnPropertyChanged(nameof(IsPaper));
        OnPropertyChanged(nameof(BotPriority));
        OnPropertyChanged(nameof(PlanTooltip));
        if (_isActive)
        {
            // New keys or a new bot-priority choice: tear down connections and start over.
            Deactivate();
            Activate();
        }
    }

    private void ApplyBudgetMode()
    {
        ApiBudget.Trading(Profile.KeyId).SetYieldToOtherApps(Profile.BotPriority);
        ApiBudget.MarketData(Profile.KeyId).SetYieldToOtherApps(Profile.BotPriority);
        ApiBudget.MarketData(Profile.KeyId).PlanLimit = DataFeed.DataRequestsPerMinute;
    }

    /// <summary>Called when this account becomes the one on screen: connect, and load whatever is stale.</summary>
    public void Activate()
    {
        _isActive = true;
        _liveTimer.Start();
        if (BotPriority)
        {
            _pricePollTimer.Start();
        }
        else
        {
            _tradeStream = new TradeUpdatesStream(Profile);
            _tradeStream.TradeUpdate += OnTradeUpdate;
            _tradeStream.ConnectionChanged += OnTradeStreamConnection;
            _tradeStream.Start();
        }
        // Mark as fresh up front so the app timer doesn't fetch the same data while this load is in flight.
        _coreRefreshed = _historyRefreshed = DateTime.Now;
        _ = RefreshAllAsync();
    }

    /// <summary>Called when another account is selected: release every connection (Alpaca limits them per user).</summary>
    public void Deactivate()
    {
        _isActive = false;
        _liveTimer.Stop();
        _pricePollTimer.Stop();
        _eventRefreshTimer.Stop();
        _ = StopStreamAsync();
        _ = StopTradeStreamAsync();
    }

    // ----- Refreshing -----

    private TimeSpan CoreInterval =>
        BotPriority ? CoreIntervalBotPriority : TradeStreamConnected ? CoreIntervalPushed : CoreIntervalPolling;

    private TimeSpan HistoryInterval => SelectedRange == HistoryRange.Day ? HistoryIntervalIntraday : HistoryIntervalDaily;

    /// <summary>Called by the app timer for the account on screen; fetches only what is due.</summary>
    public async Task TickAsync()
    {
        if (DateTime.Now - _coreRefreshed >= CoreInterval) await RefreshCoreAsync();
        if (DateTime.Now - _historyRefreshed >= HistoryInterval) await RefreshHistoryAsync();
    }

    /// <summary>Sidebar refresh for accounts not on screen: account totals only, every few minutes.</summary>
    public async Task RefreshSummaryIfDueAsync()
    {
        if (DateTime.Now - _summaryRefreshed < SummaryInterval) return;
        _summaryRefreshed = DateTime.Now;
        try
        {
            Account = await _client.GetAccountAsync();
            Error = null;
            if (DateTime.Now - _ledgerSynced >= LedgerIntervalBackground) _ = SyncLedgerAsync(null);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }

    /// <summary>Everything, now (account switch, the Refresh button).</summary>
    [RelayCommand]
    public async Task RefreshAllAsync()
    {
        await RefreshCoreAsync();
        await RefreshHistoryAsync();
    }

    /// <summary>Account, positions, open orders and recent activity: the data that changes when orders do.</summary>
    private async Task RefreshCoreAsync()
    {
        if (!await _refreshLock.WaitAsync(0)) return;
        IsLoading = true;
        try
        {
            var planTask = EnsurePlanAsync();
            var accountTask = _client.GetAccountAsync();
            var positionsTask = _client.GetPositionsAsync();
            var activitiesTask = _client.GetActivitiesPageAsync(AlpacaLedgerSync.PageSize, null);
            var ordersTask = _client.GetOpenOrdersAsync();
            var tierTask = EnsureCryptoTierAsync();
            await Task.WhenAll(planTask, accountTask, positionsTask, activitiesTask, ordersTask, tierTask);

            ApplyPositions(positionsTask.Result);
            Account = accountTask.Result;
            Replace(Activities, activitiesTask.Result.OrderByDescending(a => a.When));
            Replace(OpenOrders, ordersTask.Result);

            _coreRefreshed = _summaryRefreshed = DateTime.Now;
            LastUpdated = DateTime.Now;
            Error = null;
            _ = SyncLedgerAsync(activitiesTask.Result);

            if (_isActive) await SyncStreamAsync();
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            IsLoading = false;
            _refreshLock.Release();
        }
    }

    private async Task RefreshHistoryAsync()
    {
        try
        {
            ApplyHistory(await _client.GetPortfolioHistoryAsync(SelectedRange));
            _historyRefreshed = DateTime.Now;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }

    [RelayCommand]
    private async Task SetRange(HistoryRange range)
    {
        SelectedRange = range;
        await RefreshHistoryAsync();
    }

    // ----- Trade updates (push) -----

    private void OnTradeUpdate(string evt, string symbol) =>
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            ScheduleEventRefresh();
            TradeEvent?.Invoke(this, evt, symbol);
        });

    /// <summary>An order event pushed by Alpaca (e.g. "fill"), on the UI thread; the stop-order watch checks fills at once.</summary>
    public event Action<AccountViewModel, string, string>? TradeEvent;

    private void ScheduleEventRefresh()
    {
        if (!_isActive) return;
        // Coalesce bursts (a bot can fill many orders at once) into one refresh, at most every 10s.
        var sinceLast = DateTime.Now - _lastEventRefresh;
        var wait = sinceLast >= MinEventRefreshGap ? EventDebounce : MinEventRefreshGap - sinceLast;
        if (wait < EventDebounce) wait = EventDebounce;
        if (!_eventRefreshTimer.IsEnabled)
        {
            _eventRefreshTimer.Interval = wait;
            _eventRefreshTimer.Start();
        }
    }

    // ----- Position windows: chart and trade -----

    [RelayCommand]
    private void OpenChart(PositionRowViewModel? row)
    {
        if (row is not { HasMarketData: true }) return;
        var chart = new SymbolChartViewModel(row.DataSymbol, row.IsCrypto, Name, _marketData, DataFeed);
        new SymbolChartWindow(chart) { Owner = Application.Current.MainWindow }.Show();
    }

    /// <summary>Price chart shown in place of the portfolio value card after a single click on a position; null shows portfolio value.</summary>
    [ObservableProperty] private SymbolChartViewModel? _inlineChart;

    [RelayCommand]
    private void ShowInlineChart(PositionRowViewModel? row)
    {
        if (row is not { HasMarketData: true }) return;
        ShowSymbolChart(row.DataSymbol, row.IsCrypto);
    }

    /// <summary>Any symbol's chart (a position, or a symbol search pick) in place of the portfolio value card.</summary>
    public void ShowSymbolChart(string dataSymbol, bool isCrypto)
    {
        if (InlineChart?.Symbol == dataSymbol) return; // already showing; don't refetch
        InlineChart = new SymbolChartViewModel(dataSymbol, isCrypto, Name, _marketData, DataFeed);
    }

    [RelayCommand]
    private void CloseInlineChart() => InlineChart = null;

    [RelayCommand]
    private void OpenTrade(PositionRowViewModel? row)
    {
        if (row is null) return;
        OpenTradeWindow(row, TradeMode.SellQty);
    }

    /// <summary>A position row's Alert button: the Alerts tab starts a new alert for it.</summary>
    public event Action<PositionRowViewModel>? AlertRequested;

    [RelayCommand]
    private void OpenAlert(PositionRowViewModel? row)
    {
        if (row is { HasMarketData: true }) AlertRequested?.Invoke(row);
    }

    public void OpenTradeWindow(PositionRowViewModel row, TradeMode mode) =>
        new TradePositionWindow(new TradePositionViewModel(this, row, mode)) { Owner = Application.Current.MainWindow }.Show();

    /// <summary>Buy window for any symbol (stock, or crypto pair like "BTC/USD"); <paramref name="price"/> is shown until a fresh one loads.</summary>
    public void OpenBuyWindow(string symbol, bool isCrypto, decimal price, string priceSource = "from screener") =>
        new BuyWindow(new BuyViewModel(this, symbol, isCrypto, price, priceSource)) { Owner = Application.Current.MainWindow }.Show();

    /// <summary>The open position in a symbol, matched by its market data symbol ("BTC/USD" for crypto).</summary>
    public PositionRowViewModel? FindPosition(string dataSymbol) =>
        Positions.FirstOrDefault(p => p.DataSymbol == dataSymbol);

    internal CryptoFeeTier CryptoTier => _cryptoTier;

    // Order calls for the trade dialog. Each one schedules a refresh so positions and orders catch up even
    // without the trade-updates stream (bot priority).

    public async Task<AlpacaOrder> SubmitOrderAsync(OrderRequest order)
    {
        try { return await _client.SubmitOrderAsync(order); }
        finally { ScheduleEventRefresh(); }
    }

    public async Task<AlpacaOrder> ClosePositionAsync(string symbol)
    {
        try { return await _client.ClosePositionAsync(symbol); }
        finally { ScheduleEventRefresh(); }
    }

    /// <summary>Cancels orders and waits (up to ~10s) until Alpaca has released the qty they held.</summary>
    public async Task CancelOrdersAsync(IReadOnlyList<AlpacaOrder> orders)
    {
        try
        {
            foreach (var order in orders) await _client.CancelOrderAsync(order.Id);
            var pending = orders.Select(o => o.Id).ToList();
            for (var attempt = 0; attempt < 20 && pending.Count > 0; attempt++)
            {
                await Task.Delay(500);
                var done = new List<string>();
                foreach (var id in pending)
                    if ((await _client.GetOrderAsync(id)).Status is "canceled" or "filled" or "expired" or "rejected")
                        done.Add(id);
                pending.RemoveAll(done.Contains);
            }
            if (pending.Count > 0)
                throw new TimeoutException($"{pending.Count} order(s) still cancelling. Try again in a moment.");
        }
        finally { ScheduleEventRefresh(); }
    }

    private void OnTradeStreamConnection(bool connected) =>
        Application.Current.Dispatcher.BeginInvoke(() => TradeStreamConnected = connected);

    private async Task StopTradeStreamAsync()
    {
        if (_tradeStream is not { } stream) return;
        _tradeStream = null;
        stream.TradeUpdate -= OnTradeUpdate;
        stream.ConnectionChanged -= OnTradeStreamConnection;
        TradeStreamConnected = false;
        await stream.DisposeAsync();
    }

    // ----- API usage indicator -----

    private void UpdateApiUsage()
    {
        var trading = ApiBudget.Trading(Profile.KeyId);
        var data = ApiBudget.MarketData(Profile.KeyId);
        IsApiThrottled = trading.IsThrottled || data.IsThrottled;
        ApiUsage = IsApiThrottled
            ? "API paused until the rate-limit window resets"
            : $"API {trading.CallsLastMinute + data.CallsLastMinute}/min";
        var keyLeft = trading.KeyBudgetText is null && data.KeyBudgetText is null
            ? ""
            : $"\nThis key's budget left this minute (includes other apps using the key): " +
              $"trading {trading.KeyBudgetText ?? "not reported yet"}, market data {data.KeyBudgetText ?? "not reported yet"}";
        var streamLimit = DataFeed.StreamSymbolLimit == int.MaxValue
            ? $"{_streamedStockCount} stocks streamed (no limit)"
            : $"{_streamedStockCount}/{DataFeed.StreamSymbolLimit} streamed stocks";
        var historyLimit = DataFeed.HistoricalDelay > TimeSpan.Zero
            ? $" · charts end {DataFeed.HistoricalDelay.TotalMinutes:0} min ago"
            : "";
        ApiUsageTooltip =
            $"Dashboard requests in the last minute: trading {trading.CallsLastMinute}/{trading.MaxPerMinute}, " +
            $"market data {data.CallsLastMinute}/{data.MaxPerMinute} (self-imposed caps){keyLeft}\n" +
            $"{DataFeed.PlanName} limits: {trading.PlanLimit} trading and {data.PlanLimit:N0} market data requests/min per key · " +
            $"{streamLimit}{historyLimit}\n" +
            $"Backs off when less than {trading.ReserveFraction:P0} of the key's budget is left." +
            (BotPriority ? "\nBot priority is on: no streams, prices polled every 15s." : "") +
            (TradeStreamConnected ? "\nOrder events are pushed, so positions refresh only when orders change." : "");
    }

    // ----- Ledger -----

    /// <summary>Copies new activity into the ledger (and any history not yet downloaded), then updates the status line.</summary>
    private async Task SyncLedgerAsync(IReadOnlyList<AlpacaActivity>? newestPage)
    {
        if (Account is not { } account) return;
        _ledgerSynced = DateTime.Now;
        var ct = _ledgerCts.Token;
        try
        {
            var progress = new Progress<LedgerSyncResult>(page => { _ = UpdateLedgerStatusAsync(complete: page.HistoryComplete); });
            var result = await _ledgerSync.SyncAsync(_client, account.Id, newestPage,
                BotPriority ? LedgerBackfillGapBotPriority : LedgerBackfillGap, progress, ct);
            if (result is not null) await UpdateLedgerStatusAsync(result.HistoryComplete);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Keys changed or the account was removed.
        }
        catch (Exception ex)
        {
            LedgerStatus = "Ledger: sync failed, retrying with the next refresh";
            LedgerTooltip = ex.Message;
            LedgerHasMismatch = true;
        }
    }

    private async Task UpdateLedgerStatusAsync(bool complete)
    {
        var id = Profile.Id;
        var stocks = Positions.Where(p => !p.IsCrypto)
            .GroupBy(p => p.Symbol, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Sum(p => p.SignedQty), StringComparer.Ordinal);
        var (summary, check) = await Task.Run(() =>
        {
            var store = LedgerStore.Default;
            return (store.GetSummary(id), complete ? LedgerReconciler.Check(store.GetEntries(id), stocks) : null);
        });

        var since = summary.Oldest is { } oldest ? $" since {oldest:MMM d, yyyy}" : "";
        if (check is null)
        {
            LedgerHasMismatch = false;
            LedgerStatus = $"Ledger: downloading history · {summary.Count:N0} entries{since}";
            LedgerTooltip = "Copying this account's full activity history (trades, dividends, fees, transfers) into a local " +
                            "database, newest first. It continues in the background and resumes where it left off after a restart.";
            return;
        }

        LedgerHasMismatch = check.Mismatches.Count > 0;
        LedgerStatus = $"Ledger: {summary.Count:N0} entries{since}" + (LedgerHasMismatch
            ? $" · {check.Mismatches.Count} position{(check.Mismatches.Count == 1 ? "" : "s")} differ"
            : check.Checked > 0 ? " · matches positions" : "");
        var lines = new List<string> { "Local copy of this account's history, kept current as orders fill." };
        if (check.Checked > 0)
            lines.Add(LedgerHasMismatch
                ? "Shares from the ledger's trades don't match Alpaca's positions for:\n  " + string.Join("\n  ", check.Mismatches)
                : $"Shares from the ledger's trades match Alpaca's positions for all {check.Checked} stocks checked.");
        if (check.SkippedStocks > 0)
            lines.Add($"{check.SkippedStocks} stock(s) not checked: shares also moved through splits, mergers, transfers or option events.");
        lines.Add("Crypto isn't checked (fees are taken in the coin).");
        LedgerTooltip = string.Join("\n", lines);
    }

    // ----- Fees -----

    private async Task EnsureCryptoTierAsync()
    {
        if (DateTime.Now - _cryptoTierUpdated < CryptoTierMaxAge) return;
        try
        {
            var fills = await _client.GetFillsSinceAsync(DateTimeOffset.UtcNow.AddDays(-30));
            // Crypto fills use "BASE/QUOTE" symbols; equities volume doesn't count toward the tier.
            CryptoVolume30d = fills
                .Where(f => f.Symbol?.Contains('/') == true && f.Qty is { } && f.Price is { })
                .Sum(f => Math.Abs(f.Qty!.Value * f.Price!.Value));
            _cryptoTier = CryptoFeeTier.ForVolume(CryptoVolume30d);
            _cryptoTierUpdated = DateTime.Now;
        }
        catch (Exception)
        {
            // Keep the previous tier (lowest tier by default); retried on the next refresh.
        }
    }

    // ----- Market data plan -----

    private async Task EnsurePlanAsync()
    {
        var plan = _planRefused ? MarketDataPlan.Basic : Profile.DataPlan switch
        {
            MarketDataPlanSetting.Basic => MarketDataPlan.Basic,
            MarketDataPlanSetting.AlgoTraderPlus => MarketDataPlan.AlgoTraderPlus,
            _ => _detectedPlan ?? await DetectPlanSafeAsync(),
        };
        if (plan != DataFeed.Plan) DataFeed = DataFeedProfile.For(plan);
    }

    private async Task<MarketDataPlan> DetectPlanSafeAsync()
    {
        try
        {
            var plan = await _marketData.DetectPlanAsync();
            _detectedPlan = plan; // cache only a definite answer
            return plan;
        }
        catch (Exception)
        {
            return MarketDataPlan.Unknown; // behaves like Basic; detection is retried on the next refresh
        }
    }

    // ----- Live prices -----
    // Normally: stocks stream on the plan's feed (IEX on Basic, SIP on Algo Trader Plus) and crypto on its
    // own endpoint. With bot priority: no streams; one batched snapshot request per asset type every 15s.

    private async Task SyncStreamAsync()
    {
        if (BotPriority)
        {
            await StopStreamAsync();
            StreamState = Positions.Any(p => p.HasMarketData) ? StreamState.Live : StreamState.Stopped;
            StreamStatus = "Bot priority: no streams on this key · prices polled every 15s";
            _streamedStockCount = 0;
            return;
        }
        var stockPath = DataFeed.StreamFeed == "sip" ? MarketDataStream.SipPath : MarketDataStream.IexPath;
        var stockSymbols = StockStreamSymbols();
        _streamedStockCount = stockSymbols.Count;
        _stockStream = await SyncOneStreamAsync(_stockStream, stockPath, stockSymbols);
        _cryptoStream = await SyncOneStreamAsync(_cryptoStream, MarketDataStream.CryptoPath,
            Positions.Where(p => p.IsCrypto).Select(p => p.DataSymbol).Distinct().ToList());
        UpdateStreamStatus();
    }

    private async Task PollPricesAsync()
    {
        var stocks = Positions.Where(p => p.IsEquity).Select(p => p.DataSymbol).Distinct().ToList();
        var crypto = Positions.Where(p => p.IsCrypto).Select(p => p.DataSymbol).Distinct().ToList();
        if (stocks.Count + crypto.Count == 0) return;
        try
        {
            foreach (var (symbol, price) in await _marketData.GetLatestPricesAsync(stocks, crypto, DataFeed))
                _pendingPrices[symbol] = price;
        }
        catch (Exception)
        {
            // Missed a poll; the next one (or the next refresh) catches up.
        }
    }

    /// <summary>Stock symbols to stream, largest first, capped at the plan's limit (30 on Basic).</summary>
    private List<string> StockStreamSymbols()
    {
        var equities = Positions.Where(p => p.IsEquity).OrderByDescending(p => Math.Abs(p.MarketValue)).ToList();
        var included = equities.Take(DataFeed.StreamSymbolLimit).Select(p => p.DataSymbol).ToHashSet(StringComparer.Ordinal);
        foreach (var row in equities) row.IsOverStreamLimit = !included.Contains(row.DataSymbol);
        return [.. included];
    }

    /// <summary>Starts, retargets or stops one stream so it carries exactly <paramref name="symbols"/>.</summary>
    private async Task<MarketDataStream?> SyncOneStreamAsync(MarketDataStream? stream, string path, List<string> symbols)
    {
        if (symbols.Count == 0 || (stream is not null && stream.Path != path))
        {
            await StopOneStreamAsync(stream);
            stream = null;
        }
        if (symbols.Count == 0) return null;

        if (stream is null)
        {
            stream = new MarketDataStream(Profile, path);
            stream.PriceReceived += OnPrice;
            stream.StatusChanged += OnStreamStatus;
            stream.InsufficientSubscription += OnInsufficientSubscription;
            stream.Start(symbols);
        }
        else
        {
            await stream.SetSymbolsAsync(symbols);
        }
        return stream;
    }

    private async Task StopStreamAsync()
    {
        var stock = _stockStream;
        var crypto = _cryptoStream;
        _stockStream = _cryptoStream = null;
        _streamStatuses.Clear();
        await StopOneStreamAsync(stock);
        await StopOneStreamAsync(crypto);
        _pendingPrices.Clear();
        UpdateStreamStatus();
    }

    private async Task StopOneStreamAsync(MarketDataStream? stream)
    {
        if (stream is null) return;
        stream.PriceReceived -= OnPrice;
        stream.StatusChanged -= OnStreamStatus;
        stream.InsufficientSubscription -= OnInsufficientSubscription;
        _streamStatuses.Remove(stream.Path);
        await stream.DisposeAsync();
    }

    private void OnPrice(string symbol, double price, DateTimeOffset time) => _pendingPrices[symbol] = price;

    private void OnStreamStatus(MarketDataStream stream, StreamState state, string message) =>
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (stream != _stockStream && stream != _cryptoStream) return; // late event from a stopped stream
            _streamStatuses[stream.Path] = (state, message);
            UpdateStreamStatus();
        });

    /// <summary>Combines per-stream statuses into one line; the dot shows the worst state.</summary>
    private void UpdateStreamStatus()
    {
        if (_streamStatuses.Count == 0)
        {
            StreamState = StreamState.Stopped;
            StreamStatus = Positions.Any(p => p.HasMarketData) ? "" : "No equity or crypto positions to stream";
            return;
        }
        StreamState = _streamStatuses.Values.Select(v => v.State).Max();
        var text = string.Join(" · ", _streamStatuses.Values.Select(v => v.Message));
        var excluded = Positions.Count(p => p.IsOverStreamLimit);
        if (excluded > 0)
            text += $" · {excluded} more stocks update with each refresh ({DataFeed.StreamSymbolLimit}-symbol limit on Basic)";
        StreamStatus = text;
    }

    /// <summary>The stock stream says the plan lacks SIP (e.g. set manually on a Basic account): fall back to Basic.</summary>
    private void OnInsufficientSubscription() =>
        Application.Current.Dispatcher.BeginInvoke(async () =>
        {
            _detectedPlan = MarketDataPlan.Basic;
            _planRefused = true;
            DataFeed = DataFeedProfile.For(MarketDataPlan.Basic);
            OnPropertyChanged(nameof(PlanTooltip));
            if (_isActive) await SyncStreamAsync();
        });

    private void ApplyLivePrices()
    {
        if (_pendingPrices.IsEmpty) return;
        foreach (var row in Positions)
        {
            if (_pendingPrices.TryRemove(row.DataSymbol, out var price))
            {
                row.CurrentPrice = (decimal)price;
                row.IsLive = true;
                LivePriceApplied?.Invoke(row.DataSymbol, price);
            }
        }
        RecalculateTotals();
    }

    /// <summary>A streamed (or bot-priority polled) price reached a position row; price alerts check it at once.</summary>
    public event Action<string, double>? LivePriceApplied;

    private void RecalculateTotals()
    {
        TotalUnrealizedPl = Positions.Sum(p => p.UnrealizedPl);
        var totalCost = Positions.Sum(p => Math.Abs(p.CostBasis));
        TotalUnrealizedPlPct = totalCost == 0 ? 0 : TotalUnrealizedPl / totalCost;
        LiveMarketValueDelta = Positions.Sum(p => p.MarketValue - p.SnapshotMarketValue);
        TotalEstimatedFees = Positions.Sum(p => p.EstimatedFees);
    }

    // ----- Positions & charts -----

    private void ApplyPositions(List<AlpacaPosition> positions)
    {
        Replace(Positions, positions.OrderByDescending(p => Math.Abs(p.MarketValue)).Select(p => new PositionRowViewModel(p, _cryptoTier)));
        _pendingPrices.Clear();
        RecalculateTotals();

        // Top N by absolute value, then ordered alphabetically so a symbol keeps its color
        // across refreshes as long as it stays in the top N (color follows entity, not rank).
        var ranked = positions.OrderByDescending(p => Math.Abs(p.MarketValue)).ToList();
        var top = ranked.Take(MaxAllocationSlices).OrderBy(p => p.Symbol, StringComparer.Ordinal).ToList();
        var otherValue = ranked.Skip(MaxAllocationSlices).Sum(p => Math.Abs(p.MarketValue));

        var slices = top
            .Select((p, i) => (Name: p.Side == "short" ? $"{p.Symbol} (short)" : p.Symbol,
                               Value: Math.Abs(p.MarketValue), Color: Palette.Categorical[i]))
            .ToList();
        if (otherValue > 0)
            slices.Add(($"Other ({ranked.Count - MaxAllocationSlices})", otherValue, Palette.OtherSlice));

        var total = slices.Sum(s => s.Value);
        AllocationSeries = [.. slices.Select(s => MakeSlice(s.Name, (double)s.Value, s.Color))];
        Replace(AllocationLegend, slices.Select(s => new AllocationSlice(
            s.Name, s.Value, total == 0 ? 0 : s.Value / total, Palette.ToBrush(s.Color))));
    }

    private static PieSeries<double> MakeSlice(string name, double value, SKColor color) => new()
    {
        Name = name,
        Values = [value],
        Fill = new SolidColorPaint(color),
        Stroke = new SolidColorPaint(Palette.Surface, 2), // 2px surface gap between slices
        InnerRadius = 55,
        ToolTipLabelFormatter = point => $"{point.Model:C0} · {point.StackedValue!.Share:P1}",
    };

    private void ApplyHistory(AlpacaPortfolioHistory history)
    {
        // Alpaca pads the series with nulls outside trading hours; keep only real points.
        var points = history.Timestamp
            .Zip(history.Equity, (t, e) => (Time: DateTimeOffset.FromUnixTimeSeconds(t).LocalDateTime, Equity: e))
            .Where(p => p.Equity is > 0)
            .Select(p => (p.Time, Equity: p.Equity!.Value))
            .ToList();

        if (points.Count == 0)
        {
            EquitySeries = [];
            RangeChange = RangeChangePct = null;
            return;
        }

        var start = SelectedRange == HistoryRange.Day && history.BaseValue > 0 ? history.BaseValue : points[0].Equity;
        var end = points[^1].Equity;
        RangeChange = (decimal)(end - start);
        RangeChangePct = start == 0 ? null : (decimal)((end - start) / start);

        // Plot against the point index rather than wall-clock time so nights and weekends
        // don't render as long flat segments; the axis labeler maps the index back to a time.
        var times = points.Select(p => p.Time).ToArray();
        var intraday = SelectedRange is HistoryRange.Day or HistoryRange.Week;
        var accent = Palette.Categorical[0];

        EquitySeries =
        [
            new LineSeries<double>
            {
                Name = "Equity",
                Values = points.Select(p => p.Equity).ToArray(),
                Stroke = new SolidColorPaint(accent, 2),
                Fill = new LinearGradientPaint(
                    [accent.WithAlpha(70), accent.WithAlpha(0)],
                    new SKPoint(0.5f, 0), new SKPoint(0.5f, 1)),
                GeometrySize = 0,
                LineSmoothness = 0,
                YToolTipLabelFormatter = point => point.Model.ToString("C2"),
            }
        ];

        EquityXAxes =
        [
            new Axis
            {
                Labeler = v => Palette.FormatIndexTime(times, v, intraday),
                LabelsPaint = new SolidColorPaint(Palette.Muted),
                TextSize = 12,
                MinStep = 1,
                SeparatorsPaint = null,
            }
        ];
        EquityYAxes =
        [
            new Axis
            {
                Labeler = v => v.ToString("C0"),
                LabelsPaint = new SolidColorPaint(Palette.Muted),
                TextSize = 12,
                SeparatorsPaint = new SolidColorPaint(Palette.Grid, 1),
            }
        ];
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items) target.Add(item);
    }

    public void Dispose()
    {
        _ledgerCts.Cancel();
        Deactivate();
        _client.Dispose();
        _marketData.Dispose();
    }
}

public sealed record AllocationSlice(string Name, decimal Value, decimal Share, System.Windows.Media.Brush Brush);
