using System.Collections.ObjectModel;
using System.Windows;
using AlpacaDashboard.Models;
using AlpacaDashboard.Services;
using AlpacaDashboard.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AlpacaDashboard.ViewModels;

public enum ScreenerSource { Gainers, Losers, MostActiveVolume, MostActiveTrades }

/// <summary>
/// Screens a universe of symbols, then filters client-side so changing a filter never costs an API call.
/// Stocks: one of Alpaca's movers / most-active lists. Crypto: every tradable USD / USDT / USDC pair.
/// Each run is a snapshot request plus a batched daily-bars request for the indicators (cached 30 minutes).
/// </summary>
public sealed partial class ScreenerViewModel : ObservableObject
{
    private const int MoversTop = 50;        // Alpaca's max for movers
    private const int MostActivesTop = 100;  // Alpaca's max for most-actives
    private static readonly TimeSpan CryptoPairsMaxAge = TimeSpan.FromHours(6);
    private static readonly TimeSpan HistoryMaxAge = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan HistoryLookback = TimeSpan.FromDays(400); // 200-day SMA + 52 weeks, with weekends
    private static readonly string[] CryptoQuotes = ["USD", "USDT", "USDC"];
    private static readonly HashSet<string> Stablecoins =
        ["USDT", "USDC", "DAI", "PYUSD", "USDP", "TUSD", "BUSD", "FDUSD", "USDG", "USDE", "GUSD", "USDS", "RLUSD"];

    private readonly AccountViewModel _account;
    private List<ScreenerRow> _all = [];
    private List<string>? _cryptoPairs;
    private DateTime _cryptoPairsFetched = DateTime.MinValue;
    private readonly Dictionary<string, (DateTime Fetched, List<StockBar> Bars)> _history = new(StringComparer.Ordinal);
    private int _runId; // a newer run supersedes the results of an older one still in flight

    private readonly ScreenerFilter _price = ScreenerFilters.Price();
    // Stocks and crypto keep their own price choice: "Over $1" weeds out penny stocks but would hide most altcoins.
    private FilterOption _otherPrice;

    public ScreenerViewModel(AccountViewModel account)
    {
        _account = account;
        _price.Selected = _price.Options[ScreenerFilters.PriceOverOneDollar];
        _otherPrice = _price.Options[0];
        FilterGroups =
        [
            new("Descriptive", ScreenerFilters.Descriptive(_price, ScreenerFilters.Quote())),
            new("Performance", ScreenerFilters.Performance()),
            new("Technical", ScreenerFilters.Technical()),
        ];
        Restore(ScreenerSettingsStore.Load(account.Profile.Id));
        foreach (var filter in AllFilters)
        {
            filter.Changed += ApplyFilters;
            filter.Changed += SaveSettings;
        }
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(Source) or nameof(CryptoOnly) or nameof(HideHeld) or nameof(OnlyHeld) or nameof(ExcludeStablecoins))
                SaveSettings();
        };
        UpdateFilterVisibility();
    }

    // ----- Saved selections -----

    private bool _restoring;

    private void Restore(ScreenerSettings settings)
    {
        FilterOption Find(ScreenerFilter filter, string? label, FilterOption fallback) =>
            filter.Options.FirstOrDefault(o => o.Label == label) ?? fallback;

        _restoring = true;
        try
        {
            Source = settings.Source;
            CryptoOnly = settings.CryptoOnly;
            HideHeld = settings.HideHeld;
            OnlyHeld = settings.OnlyHeld && !settings.HideHeld;
            ExcludeStablecoins = settings.ExcludeStablecoins;

            var stockPrice = Find(_price, settings.StockPrice, _price.Options[ScreenerFilters.PriceOverOneDollar]);
            var cryptoPrice = Find(_price, settings.CryptoPrice, _price.Options[0]);
            (_price.Selected, _otherPrice) = CryptoOnly ? (cryptoPrice, stockPrice) : (stockPrice, cryptoPrice);

            foreach (var filter in AllFilters.Where(f => f != _price))
                if (settings.Filters.TryGetValue(filter.Name, out var label))
                    filter.Selected = Find(filter, label, filter.Selected);
        }
        finally
        {
            _restoring = false;
        }
    }

    private void SaveSettings()
    {
        if (_restoring) return;
        ScreenerSettingsStore.Save(_account.Profile.Id, new ScreenerSettings
        {
            Source = Source,
            CryptoOnly = CryptoOnly,
            HideHeld = HideHeld,
            OnlyHeld = OnlyHeld,
            ExcludeStablecoins = ExcludeStablecoins,
            StockPrice = (CryptoOnly ? _otherPrice : _price.Selected).Label,
            CryptoPrice = (CryptoOnly ? _price.Selected : _otherPrice).Label,
            Filters = AllFilters.Where(f => f != _price).ToDictionary(f => f.Name, f => f.Selected.Label),
        });
    }

    public IReadOnlyList<FilterGroup> FilterGroups { get; }
    private IEnumerable<ScreenerFilter> AllFilters => FilterGroups.SelectMany(g => g.Filters);

    [ObservableProperty] private ScreenerSource _source = ScreenerSource.Gainers;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _loadingText = "";
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string? _warning;
    [ObservableProperty] private DateTime? _lastRun;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _feedNote = "";
    [ObservableProperty] private int _activeFilterCount;

    // ----- Checkboxes and search (client-side, like the dropdowns) -----

    /// <summary>Screen every crypto pair instead of a stock list.</summary>
    [ObservableProperty] private bool _cryptoOnly;
    [ObservableProperty] private bool _hideHeld;
    [ObservableProperty] private bool _onlyHeld;
    [ObservableProperty] private bool _excludeStablecoins = true;
    [ObservableProperty] private string _searchText = "";

    partial void OnCryptoOnlyChanged(bool value)
    {
        if (_restoring) return; // Restore() sets the price for the restored mode and runs nothing
        (_price.Selected, _otherPrice) = (_otherPrice, _price.Selected);
        UpdateFilterVisibility();
        _ = RunAsync();
    }

    partial void OnHideHeldChanged(bool value)
    {
        if (value) OnlyHeld = false;
        ApplyFilters();
    }

    partial void OnOnlyHeldChanged(bool value)
    {
        if (value) HideHeld = false;
        ApplyFilters();
    }

    partial void OnExcludeStablecoinsChanged(bool value) => ApplyFilters();
    partial void OnSearchTextChanged(string value) => ApplyFilters();

    private void UpdateFilterVisibility()
    {
        var scope = CryptoOnly ? FilterScope.Crypto : FilterScope.Stocks;
        foreach (var filter in AllFilters) filter.IsVisible = filter.Scope.HasFlag(scope);
    }

    [RelayCommand]
    private void ResetFilters()
    {
        foreach (var filter in AllFilters) filter.Reset();
        // Reset() restores "Any"; stocks default to "Over $1".
        if (!CryptoOnly) _price.Selected = _price.Options[ScreenerFilters.PriceOverOneDollar];
        HideHeld = OnlyHeld = false;
        ExcludeStablecoins = true;
        SearchText = "";
    }

    public ObservableCollection<ScreenerRow> Results { get; } = [];

    /// <summary>Chart of the clicked row, shown beside the results.</summary>
    [ObservableProperty] private SymbolChartViewModel? _chart;

    /// <summary>Runs the screen the first time the tab is shown.</summary>
    public void EnsureLoaded()
    {
        if (LastRun is null && !IsLoading) _ = RunAsync();
    }

    [RelayCommand]
    private Task SetSource(ScreenerSource source)
    {
        Source = source;
        return RunAsync();
    }

    [RelayCommand]
    private async Task RunAsync()
    {
        var runId = ++_runId;
        IsLoading = true;
        LoadingText = "Screening…";
        try
        {
            var rows = CryptoOnly ? await ScreenCryptoAsync() : await ScreenStocksAsync(Source);
            if (runId != _runId) return;

            // Show today's values right away, then again with indicators once history arrives.
            _all = rows;
            LastRun = DateTime.Now;
            Error = null;
            ApplyFilters();

            LoadingText = "Loading price history…";
            var history = await GetHistoryAsync(rows);
            if (runId != _runId) return;
            _all = [.. rows.Select(r => r.WithHistory(history.GetValueOrDefault(r.Symbol)))];
            ApplyFilters();
        }
        catch (Exception ex)
        {
            if (runId != _runId) return;
            Error = ex is AlpacaApiException { StatusCode: 403 }
                ? $"Your market data plan doesn't allow this request. {ex.Message}"
                : ex.Message;
        }
        finally
        {
            if (runId == _runId) IsLoading = false;
        }
    }

    // ----- Universe + today's values -----

    private async Task<List<ScreenerRow>> ScreenStocksAsync(ScreenerSource source)
    {
        var data = _account.MarketData;
        // Screener-provided values are the fallback when a symbol has no snapshot.
        Dictionary<string, (double? Price, double? Change, double? Pct, double? Volume, double? Trades)> candidates;
        switch (source)
        {
            case ScreenerSource.Gainers or ScreenerSource.Losers:
                var movers = await data.GetStockMoversAsync(MoversTop);
                var list = (source == ScreenerSource.Gainers ? movers.Gainers : movers.Losers) ?? [];
                candidates = list.DistinctBy(m => m.Symbol).ToDictionary(m => m.Symbol,
                    m => ((double?)m.Price, (double?)m.Change, (double?)(m.PercentChange / 100), (double?)null, (double?)null));
                break;
            default:
                var by = source == ScreenerSource.MostActiveTrades ? "trades" : "volume";
                var actives = (await data.GetMostActivesAsync(by, MostActivesTop)).MostActives ?? [];
                candidates = actives.DistinctBy(a => a.Symbol).ToDictionary(a => a.Symbol,
                    a => ((double?)null, (double?)null, (double?)null, (double?)a.Volume, (double?)a.TradeCount));
                break;
        }
        if (candidates.Count == 0) return [];

        var (snapshots, note) = await GetStockSnapshotsAsync(candidates.Keys.ToList());
        FeedNote = note;
        var held = HeldSymbols();
        var rows = candidates.Select(c =>
        {
            snapshots.TryGetValue(c.Key, out var snap);
            var row = FromSnapshot(c.Key, quote: null, snap, held);
            return row with
            {
                Price = row.Price > 0 ? row.Price : c.Value.Price ?? 0,
                Change = row.Change ?? c.Value.Change,
                ChangePct = row.ChangePct ?? c.Value.Pct,
                Volume = c.Value.Volume ?? row.Volume,
                TradeCount = c.Value.Trades ?? row.TradeCount,
            };
        }).ToList();

        return source switch
        {
            ScreenerSource.Gainers => [.. rows.OrderByDescending(r => r.ChangePct ?? double.MinValue)],
            ScreenerSource.Losers => [.. rows.OrderBy(r => r.ChangePct ?? double.MaxValue)],
            ScreenerSource.MostActiveTrades => [.. rows.OrderByDescending(r => r.TradeCount)],
            _ => [.. rows.OrderByDescending(r => r.Volume)],
        };
    }

    /// <summary>
    /// Full-market (SIP) snapshots. On Algo Trader Plus that's real-time; on Basic, 15-minute delayed SIP, since
    /// IEX alone carries only a few percent of volume and would make volume filters meaningless.
    /// Falls back to IEX if the delayed feed is refused.
    /// </summary>
    private async Task<(Dictionary<string, StockSnapshot> Snapshots, string Note)> GetStockSnapshotsAsync(List<string> symbols)
    {
        var feed = _account.DataFeed;
        if (feed.Plan == MarketDataPlan.AlgoTraderPlus)
            return (await _account.MarketData.GetSnapshotsAsync(symbols, feed), "Real-time SIP (all US exchanges)");
        try
        {
            return (await _account.MarketData.GetSnapshotsAsync(symbols, feed with { SnapshotFeed = "delayed_sip" }),
                "Basic plan: prices and volume are 15-min delayed SIP (all US exchanges)");
        }
        catch (AlpacaApiException ex) when (ex.StatusCode is 400 or 403 or 422)
        {
            return (await _account.MarketData.GetSnapshotsAsync(symbols, feed),
                "Basic plan: IEX real-time — volume covers IEX only (a few % of the market)");
        }
    }

    /// <summary>Every tradable pair quoted in USD, USDT or USDC, in one snapshot request.</summary>
    private async Task<List<ScreenerRow>> ScreenCryptoAsync()
    {
        if (_cryptoPairs is null || DateTime.Now - _cryptoPairsFetched > CryptoPairsMaxAge)
        {
            var assets = await _account.Trading.GetAssetsAsync("crypto");
            _cryptoPairs = assets
                .Where(a => a.Tradable && QuoteOf(a.Symbol) is not null)
                .Select(a => a.Symbol).Distinct().Order(StringComparer.Ordinal).ToList();
            _cryptoPairsFetched = DateTime.Now;
        }
        if (_cryptoPairs.Count == 0) return [];

        var snapshots = await _account.MarketData.GetCryptoSnapshotsAsync(_cryptoPairs);
        FeedNote = $"Crypto · real-time · {_cryptoPairs.Count} pairs · change and volume are per UTC day; volume is in coins";
        var held = HeldSymbols();
        return [.. _cryptoPairs
            .Select(p => FromSnapshot(p, QuoteOf(p), snapshots.GetValueOrDefault(p), held))
            .OrderByDescending(r => r.DollarVolume)];
    }

    private static string? QuoteOf(string pair) =>
        pair.Split('/') is [_, var quote] && CryptoQuotes.Contains(quote) ? quote : null;

    private static ScreenerRow FromSnapshot(string symbol, string? quote, StockSnapshot? snap, HashSet<string> held)
    {
        var isCrypto = quote is not null;
        var price = snap?.LatestTrade?.Price is > 0 and var p ? p : snap?.DailyBar?.Close ?? 0;
        if (isCrypto && snap?.LatestQuote is { BidPrice: > 0, AskPrice: > 0 } q) price = (q.BidPrice + q.AskPrice) / 2;
        var prevClose = snap?.PrevDailyBar?.Close;
        double? change = price > 0 && prevClose is > 0 ? price - prevClose.Value : null;
        return new ScreenerRow
        {
            Symbol = symbol,
            IsCrypto = isCrypto,
            Quote = quote,
            IsStablecoin = isCrypto && Stablecoins.Contains(symbol.Split('/')[0]),
            IsHeld = held.Contains(symbol),
            Price = price,
            Change = change,
            ChangePct = change / prevClose,
            GapPct = snap?.DailyBar?.Open is > 0 and var open && prevClose is > 0 ? open / prevClose.Value - 1 : null,
            Volume = snap?.DailyBar?.Volume ?? 0,
            TradeCount = snap?.DailyBar?.TradeCount ?? 0,
            DayLow = snap?.DailyBar?.Low,
            DayHigh = snap?.DailyBar?.High,
            SessionStart = snap?.DailyBar?.Time,
        };
    }

    private HashSet<string> HeldSymbols() => _account.Positions.Select(p => p.DataSymbol).ToHashSet(StringComparer.Ordinal);

    // ----- Daily history for the indicators -----

    /// <summary>
    /// Daily bars for every row, fetching only symbols not cached in the last 30 minutes. A failure leaves the
    /// indicator columns blank (with a warning) rather than failing the whole screen.
    /// </summary>
    private async Task<Dictionary<string, List<StockBar>>> GetHistoryAsync(List<ScreenerRow> rows)
    {
        var missing = rows.Where(r => !_history.TryGetValue(r.Symbol, out var h) || DateTime.Now - h.Fetched > HistoryMaxAge)
                          .Select(r => r.Symbol).ToList();
        Warning = null;
        if (missing.Count > 0)
        {
            try
            {
                var start = DateTimeOffset.UtcNow - HistoryLookback;
                var fetched = CryptoOnly
                    ? await _account.MarketData.GetCryptoDailyBarsAsync(missing, start)
                    : await _account.MarketData.GetDailyBarsAsync(missing, start, _account.DataFeed);
                foreach (var symbol in missing)
                    _history[symbol] = (DateTime.Now, fetched.GetValueOrDefault(symbol) ?? []);
            }
            catch (Exception ex)
            {
                Warning = $"Price history unavailable, so performance and technical filters can't match: {ex.Message}";
            }
        }
        return rows.Where(r => _history.ContainsKey(r.Symbol))
                   .ToDictionary(r => r.Symbol, r => _history[r.Symbol].Bars, StringComparer.Ordinal);
    }

    // ----- Filtering -----

    private void ApplyFilters()
    {
        var search = SearchText.Trim();
        var filters = AllFilters.Where(f => f.IsVisible).ToList();
        var matches = _all.Where(r =>
            (search.Length == 0 || r.Symbol.Contains(search, StringComparison.OrdinalIgnoreCase)) &&
            !(HideHeld && r.IsHeld) &&
            (!OnlyHeld || r.IsHeld) &&
            !(CryptoOnly && ExcludeStablecoins && r.IsStablecoin) &&
            filters.All(f => f.Matches(r))).ToList();

        Results.Clear();
        foreach (var row in matches) Results.Add(row);
        ActiveFilterCount = filters.Count(f => f.IsActive);
        Status = LastRun is { } t ? $"{matches.Count} of {_all.Count} match · updated {t:h:mm:ss tt}" : "";
    }

    // ----- Charts -----

    [RelayCommand]
    private void ShowChart(ScreenerRow? row)
    {
        if (row is null || Chart?.Symbol == row.Symbol) return;
        Chart = new SymbolChartViewModel(row.Symbol, row.IsCrypto, _account.Name, _account.MarketData, _account.DataFeed,
            (row.Price, row.Change, row.ChangePct));
    }

    /// <summary>A symbol search pick's chart beside the results.</summary>
    public void ShowSymbolChart(string symbol, bool isCrypto)
    {
        if (Chart?.Symbol == symbol) return;
        Chart = new SymbolChartViewModel(symbol, isCrypto, _account.Name, _account.MarketData, _account.DataFeed);
    }

    [RelayCommand]
    private void CloseChart() => Chart = null;

    // ----- Trading from a result (right-click menu) -----

    /// <summary>The open position in this row's symbol, looked up live (the row's IsHeld is as of the last run).</summary>
    public PositionRowViewModel? PositionFor(ScreenerRow row) => _account.FindPosition(row.Symbol);

    [RelayCommand]
    private void Buy(ScreenerRow? row)
    {
        if (row is null) return;
        _account.OpenBuyWindow(row.Symbol, row.IsCrypto, (decimal)row.Price);
    }

    public void OpenTrade(PositionRowViewModel position, TradeMode mode) => _account.OpenTradeWindow(position, mode);

    [RelayCommand]
    private void OpenChartWindow(ScreenerRow? row)
    {
        if (row is null) return;
        var chart = new SymbolChartViewModel(row.Symbol, row.IsCrypto, _account.Name, _account.MarketData, _account.DataFeed,
            (row.Price, row.Change, row.ChangePct));
        new SymbolChartWindow(chart) { Owner = Application.Current.MainWindow }.Show();
    }
}
