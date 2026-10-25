using System.Windows.Threading;
using AlpacaDashboard.Models;
using AlpacaDashboard.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;

namespace AlpacaDashboard.ViewModels;

public enum ChartRange { Day, Week, Month, ThreeMonths, Year, FiveYears }

/// <summary>
/// Candlestick + volume chart for one stock or crypto pair. Stocks use the feed allowed by the
/// account's data plan; crypto data is real-time on every plan.
/// </summary>
public sealed partial class SymbolChartViewModel : ObservableObject
{
    private readonly MarketDataClient _client;

    /// <param name="known">Price and day change the caller already has (e.g. a screener row), shown until the snapshot arrives.</param>
    public SymbolChartViewModel(string symbol, bool isCrypto, string accountName, MarketDataClient client, DataFeedProfile feed,
        (double Price, double? Change, double? ChangePct)? known = null)
    {
        Symbol = symbol;
        IsCrypto = isCrypto;
        AccountName = accountName;
        _client = client;
        Feed = feed;
        if (known is { Price: > 0 } k)
        {
            LastPrice = k.Price;
            DayChange = (decimal?)k.Change;
            DayChangePct = (decimal?)k.ChangePct;
        }
        _loadingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _loadingTimer.Tick += (_, _) => UpdateLoadingMessage();
        // Crypto trades 24/7, so a week already holds plenty of bars.
        if (isCrypto) SelectedRange = ChartRange.Week;
        _ = LoadCoreAsync(force: true);
    }

    public string Symbol { get; }
    public bool IsCrypto { get; }
    public string AccountName { get; }
    public DataFeedProfile Feed { get; }

    /// <summary>Tells the user how fresh the chart is, which depends on their plan.</summary>
    public string FeedNote => IsCrypto
        ? "Crypto · real-time (included on every plan) · trades 24/7"
        : Feed.HistoricalDelay > TimeSpan.Zero
            ? $"SIP bars · delayed {Feed.HistoricalDelay.TotalMinutes:0} min (Basic plan) · last price: IEX real-time"
            : "SIP bars · real-time (Algo Trader Plus)";

    [ObservableProperty] private ChartRange _selectedRange = ChartRange.ThreeMonths;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _hasData;

    /// <summary>Shown in place of the chart while the first bars load; says why when it's slow.</summary>
    [ObservableProperty] private string? _loadingMessage;

    private readonly DispatcherTimer _loadingTimer;
    private DateTime _loadingSince;

    partial void OnIsLoadingChanged(bool value)
    {
        _loadingSince = DateTime.Now;
        if (value) _loadingTimer.Start(); else _loadingTimer.Stop();
        UpdateLoadingMessage();
    }

    private void UpdateLoadingMessage()
    {
        var wait = _client.Budget.CurrentWait;
        LoadingMessage = !IsLoading || HasData ? null
            : wait > TimeSpan.FromSeconds(1) ? $"Waiting for the API rate limit · resumes in {Math.Ceiling(wait.TotalSeconds):0}s"
            : DateTime.Now - _loadingSince > TimeSpan.FromSeconds(5) ? $"Still loading {Symbol}… Alpaca is slow to respond"
            : $"Loading {Symbol}…";
    }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(LastPriceText))] private double? _lastPrice;

    /// <summary>Header price: at least 4 decimals, 8 for sub-cent coins.</summary>
    public string LastPriceText => LastPrice is { } p ? (Math.Abs(p) >= 0.01 ? p.ToString("C4") : p.ToString("C8")) : "—";
    [ObservableProperty] private decimal? _dayChange;
    [ObservableProperty] private decimal? _dayChangePct;
    [ObservableProperty] private decimal? _rangeChangePct;

    [ObservableProperty] private ISeries[] _priceSeries = [];
    [ObservableProperty] private ISeries[] _volumeSeries = [];
    [ObservableProperty] private Axis[] _priceXAxes = [new Axis()];
    [ObservableProperty] private Axis[] _volumeXAxes = [new Axis()];
    [ObservableProperty] private Axis[] _priceYAxes = [new Axis()];
    [ObservableProperty] private Axis[] _volumeYAxes = [new Axis()];

    // Bars already fetched in this window, per range, so flipping between ranges doesn't refetch.
    private static readonly TimeSpan CacheMaxAge = TimeSpan.FromMinutes(5);
    private readonly Dictionary<ChartRange, (DateTime Fetched, List<StockBar> Bars)> _barsCache = [];
    private DateTime _snapshotFetched = DateTime.MinValue;

    [RelayCommand]
    private async Task SetRange(ChartRange range)
    {
        SelectedRange = range;
        await LoadCoreAsync(force: false);
    }

    /// <summary>The Refresh button: always fetch fresh data.</summary>
    [RelayCommand]
    private Task LoadAsync() => LoadCoreAsync(force: true);

    private async Task LoadCoreAsync(bool force)
    {
        if (!force && _barsCache.TryGetValue(SelectedRange, out var cached) && DateTime.Now - cached.Fetched < CacheMaxAge)
        {
            ApplyBars(cached.Bars);
            HasData = cached.Bars.Count > 0;
            Error = HasData ? null : "No bars returned for this range.";
            return;
        }

        IsLoading = true;
        try
        {
            var (timeframe, lookback) = SelectedRange switch
            {
                ChartRange.Day => ("5Min", IsCrypto ? TimeSpan.FromDays(1) : TimeSpan.FromDays(5)), // stocks: trimmed to the latest session below
                ChartRange.Week => ("30Min", TimeSpan.FromDays(7)),
                ChartRange.Month => ("1Hour", TimeSpan.FromDays(31)),
                ChartRange.ThreeMonths => ("1Day", TimeSpan.FromDays(92)),
                ChartRange.Year => ("1Day", TimeSpan.FromDays(366)),
                _ => ("1Week", TimeSpan.FromDays(366 * 5)),
            };

            var start = DateTimeOffset.UtcNow - lookback;
            var barsTask = IsCrypto
                ? _client.GetCryptoBarsAsync(Symbol, timeframe, start)
                : _client.GetBarsAsync(Symbol, timeframe, start, Feed);
            // The header price is a nice-to-have: refetch it at most once a minute, and if it fails, still show the chart.
            if (force || DateTime.Now - _snapshotFetched > TimeSpan.FromMinutes(1))
            {
                try
                {
                    ApplySnapshot(await (IsCrypto ? _client.GetCryptoSnapshotAsync(Symbol) : _client.GetSnapshotAsync(Symbol, Feed)));
                    _snapshotFetched = DateTime.Now;
                }
                catch (Exception) { LastPrice = null; DayChange = DayChangePct = null; }
            }

            var bars = await barsTask;
            if (SelectedRange == ChartRange.Day && !IsCrypto && bars.Count > 0)
            {
                var lastSession = bars[^1].Time.ToLocalTime().Date;
                bars = bars.Where(b => b.Time.ToLocalTime().Date == lastSession).ToList();
            }

            _barsCache[SelectedRange] = (DateTime.Now, bars);
            ApplyBars(bars);
            HasData = bars.Count > 0;
            Error = HasData ? null : "No bars returned for this range.";
        }
        catch (AlpacaApiException ex) when (ex.StatusCode == 403)
        {
            HasData = false;
            Error = $"Your market data plan doesn't allow this request. {ex.Message}";
        }
        catch (Exception ex)
        {
            HasData = false;
            Error = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ApplySnapshot(StockSnapshot snapshot)
    {
        LastPrice = snapshot.LatestTrade?.Price ?? snapshot.DailyBar?.Close;
        var prevClose = snapshot.PrevDailyBar?.Close;
        if (LastPrice is { } last && prevClose is > 0)
        {
            DayChange = (decimal)(last - prevClose.Value);
            DayChangePct = (decimal)((last - prevClose.Value) / prevClose.Value);
        }
        else
        {
            DayChange = DayChangePct = null;
        }
    }

    private void ApplyBars(List<StockBar> bars)
    {
        RangeChangePct = bars.Count > 1 && bars[0].Open > 0
            ? (decimal)((bars[^1].Close - bars[0].Open) / bars[0].Open)
            : null;

        // Index-based X so overnight/weekend gaps collapse; labels map the index back to a time.
        var times = bars.Select(b => b.Time.LocalDateTime).ToArray();
        var intraday = SelectedRange is ChartRange.Day or ChartRange.Week or ChartRange.Month;
        Func<double, string> labeler = v => Palette.FormatIndexTime(times, v, intraday);

        PriceSeries =
        [
            new CandlesticksSeries<FinancialPointI>
            {
                Name = Symbol,
                Values = bars.Select(b => new FinancialPointI(b.High, b.Open, b.Close, b.Low)).ToArray(),
                UpFill = new SolidColorPaint(Palette.Up),
                UpStroke = new SolidColorPaint(Palette.Up, 1),
                DownFill = new SolidColorPaint(Palette.Down),
                DownStroke = new SolidColorPaint(Palette.Down, 1),
                MaxBarWidth = 12,
                YToolTipLabelFormatter = p => FormatCandleTooltip(bars, p.Index),
            }
        ];

        // Volume gets its own chart below (never a second y-axis on the price chart).
        VolumeSeries =
        [
            new ColumnSeries<double>
            {
                Name = "Volume",
                Values = bars.Select(b => b.Volume).ToArray(),
                Fill = new SolidColorPaint(Palette.Muted.WithAlpha(150)),
                MaxBarWidth = 12,
                Padding = 1,
                YToolTipLabelFormatter = p => p.Model.ToString("N0"),
            }
        ];

        PriceXAxes = [MakeXAxis(labeler, showLabels: false)];
        VolumeXAxes = [MakeXAxis(labeler, showLabels: true)];
        PriceYAxes =
        [
            new Axis
            {
                Labeler = FormatPrice,
                LabelsPaint = new SolidColorPaint(Palette.Muted),
                TextSize = 12,
                SeparatorsPaint = new SolidColorPaint(Palette.Grid, 1),
                Position = LiveChartsCore.Measure.AxisPosition.End,
            }
        ];
        VolumeYAxes =
        [
            new Axis
            {
                Labeler = FormatVolume,
                LabelsPaint = new SolidColorPaint(Palette.Muted),
                TextSize = 11,
                MinLimit = 0,
                SeparatorsPaint = null,
                Position = LiveChartsCore.Measure.AxisPosition.End,
            }
        ];
    }

    /// <summary>OHLC plus volume and change vs. the previous bar's close (the bar's own open for the first bar).</summary>
    private static string FormatCandleTooltip(List<StockBar> bars, int index)
    {
        if (index < 0 || index >= bars.Count) return "";
        var b = bars[index];
        var reference = index > 0 ? bars[index - 1].Close : b.Open;
        var change = b.Close - reference;
        var changeText = reference > 0
            ? $"{(change >= 0 ? "+" : "−")}{FormatPrice(Math.Abs(change))} ({change / reference:+0.00%;−0.00%;0.00%})"
            : "—";
        return $"O {FormatPrice(b.Open)}  H {FormatPrice(b.High)}{Environment.NewLine}" +
               $"L {FormatPrice(b.Low)}  C {FormatPrice(b.Close)}{Environment.NewLine}" +
               $"Chg {changeText}{Environment.NewLine}" +
               $"Vol {FormatVolume(b.Volume)}";
    }

    private static Axis MakeXAxis(Func<double, string> labeler, bool showLabels) => new()
    {
        Labeler = labeler,
        LabelsPaint = showLabels ? new SolidColorPaint(Palette.Muted) : null,
        TextSize = 12,
        MinStep = 1,
        SeparatorsPaint = null,
    };

    /// <summary>Sub-dollar crypto needs more decimals than "C2".</summary>
    public static string FormatPrice(double v) => Math.Abs(v) switch
    {
        >= 1 => v.ToString("C2"),
        >= 0.01 => v.ToString("$0.0000"),
        _ => v.ToString("$0.00000000"),
    };

    /// <summary>Compact count: 1.2K, 3.4M, 5.6B; small crypto volumes keep decimals (0.25, not 0).</summary>
    public static string FormatVolume(double v) => Math.Abs(v) switch
    {
        >= 1e9 => $"{v / 1e9:0.#}B",
        >= 1e6 => $"{v / 1e6:0.#}M",
        >= 1e3 => $"{v / 1e3:0.#}K",
        >= 10 or 0 => v.ToString("0"),
        >= 0.01 => v.ToString("0.##"),
        _ => v.ToString("0.####"),
    };
}
