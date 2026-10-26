using System.Collections.ObjectModel;
using System.Windows.Threading;
using AlpacaDashboard.Models;
using AlpacaDashboard.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.Painting.Effects;
using SkiaSharp;

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
        _lines = ChartSettingsStore.LoadLines(symbol);
        _previewSeries = Line("New line", Palette.Ink, LineDash.Long, _preview, hoverable: false);
        var saved = ChartSettingsStore.LoadOverlays();
        foreach (var option in Overlays)
        {
            option.IsChecked = saved.Contains(option.Kind);
            option.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(OverlayOption.IsChecked)) return;
                ChartSettingsStore.SaveOverlays(Overlays.Where(o => o.IsChecked).Select(o => o.Kind));
                ApplyOverlays();
            };
        }
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

    public OverlayOption[] Overlays { get; } = OverlayOption.CreateAll();
    [ObservableProperty] private IReadOnlyList<OverlayLegendItem> _legend = [];
    [ObservableProperty] private string _overlaysLabel = "Overlays ▾";

    // Bars already fetched in this window, per range, so flipping between ranges doesn't refetch. Each fetch
    // starts early enough to warm up a 200-bar average; FirstShown is the first bar on screen.
    private static readonly TimeSpan CacheMaxAge = TimeSpan.FromMinutes(5);
    private readonly Dictionary<ChartRange, (DateTime Fetched, List<StockBar> Bars, int FirstShown)> _barsCache = [];
    private List<StockBar> _bars = [];
    private int _firstShown;
    private ISeries? _candles;
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
            ApplyBars(cached.Bars, cached.FirstShown);
            HasData = cached.Bars.Count > cached.FirstShown;
            Error = HasData ? null : "No bars returned for this range.";
            return;
        }

        IsLoading = true;
        try
        {
            // Warm-up: extra history before the range so averages are already running at its left edge
            // (about 200 bars; stocks have fewer bars a day than crypto, which trades around the clock).
            var (timeframe, lookback, warmup) = SelectedRange switch
            {
                // Stocks: the latest session is shown; the days before it are the warm-up.
                ChartRange.Day => ("5Min", TimeSpan.FromDays(IsCrypto ? 1 : 5), TimeSpan.FromDays(IsCrypto ? 1 : 0)),
                ChartRange.Week => ("30Min", TimeSpan.FromDays(7), TimeSpan.FromDays(IsCrypto ? 5 : 40)),
                ChartRange.Month => ("1Hour", TimeSpan.FromDays(31), TimeSpan.FromDays(IsCrypto ? 10 : 60)),
                ChartRange.ThreeMonths => ("1Day", TimeSpan.FromDays(92), TimeSpan.FromDays(300)),
                ChartRange.Year => ("1Day", TimeSpan.FromDays(366), TimeSpan.FromDays(300)),
                _ => ("1Week", TimeSpan.FromDays(366 * 5), TimeSpan.FromDays(1500)),
            };

            var shownFrom = DateTimeOffset.UtcNow - lookback;
            var start = shownFrom - warmup;
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
            int firstShown;
            if (SelectedRange == ChartRange.Day && !IsCrypto && bars.Count > 0)
            {
                var lastSession = bars[^1].Time.ToLocalTime().Date;
                firstShown = bars.FindIndex(b => b.Time.ToLocalTime().Date == lastSession);
            }
            else
            {
                firstShown = bars.FindIndex(b => b.Time >= shownFrom);
                if (firstShown < 0) firstShown = bars.Count;
            }

            _barsCache[SelectedRange] = (DateTime.Now, bars, firstShown);
            ApplyBars(bars, firstShown);
            HasData = bars.Count > firstShown;
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

    /// <param name="all">The fetched bars, warm-up included.</param>
    /// <param name="firstShown">Index of the first bar on screen.</param>
    private void ApplyBars(List<StockBar> all, int firstShown)
    {
        _bars = all;
        _firstShown = firstShown;
        var bars = all.GetRange(firstShown, all.Count - firstShown);

        RangeChangePct = bars.Count > 1 && bars[0].Open > 0
            ? (decimal)((bars[^1].Close - bars[0].Open) / bars[0].Open)
            : null;

        // Index-based X so overnight/weekend gaps collapse; labels map the index back to a time.
        var times = bars.Select(b => b.Time.LocalDateTime).ToArray();
        var intraday = SelectedRange is ChartRange.Day or ChartRange.Week or ChartRange.Month;
        Func<double, string> labeler = v => Palette.FormatIndexTime(times, v, intraday);

        _candles = new CandlesticksSeries<FinancialPointI>
        {
            Name = Symbol,
            Values = bars.Select(b => new FinancialPointI(b.High, b.Open, b.Close, b.Low)).ToArray(),
            UpFill = new SolidColorPaint(Palette.Up),
            UpStroke = new SolidColorPaint(Palette.Up, 1),
            DownFill = new SolidColorPaint(Palette.Down),
            DownStroke = new SolidColorPaint(Palette.Down, 1),
            MaxBarWidth = 12,
            YToolTipLabelFormatter = p => FormatCandleTooltip(bars, p.Index),
        };
        ApplyOverlays();

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

    /// <summary>
    /// Candles plus the overlays ticked in the dropdown and the user's own lines. Computed from the cached bars,
    /// so toggling one costs no request.
    /// </summary>
    private void ApplyOverlays()
    {
        var intraday = SelectedRange is ChartRange.Day or ChartRange.Week or ChartRange.Month;
        foreach (var option in Overlays) option.IsAvailable = option.Kind != ChartOverlay.Vwap || intraday;
        var active = Overlays.Where(o => o.IsChecked && o.IsAvailable).ToList();
        OverlaysLabel = active.Count > 0 ? $"Overlays · {active.Count} ▾" : "Overlays ▾";
        if (_candles is null) return;

        var series = new List<ISeries> { _candles };
        var sections = new List<CoreSection>();
        var legend = new List<OverlayLegendItem>();
        var closes = _bars.Select(b => b.Close).ToArray();
        var shownBars = _bars.GetRange(_firstShown, _bars.Count - _firstShown);
        var n = shownBars.Count;
        var rightEdge = RightEdge;
        // Swings and "close enough" scale with the chart: a swing beats ~1/40 of the bars on each side, and
        // half an average bar's range counts as touching a line.
        var swingK = Math.Clamp(n / 40, 2, 10);
        var tolerance = n > 0 ? shownBars.Average(b => b.High - b.Low) * 0.5 : 0;
        var fit = Indicators.Regression(closes[_firstShown..]);

        foreach (var option in active)
        {
            var text = option.Name;
            string? note = null;
            switch (option.Kind)
            {
                case ChartOverlay.Trend:
                    if (fit is { } f)
                    {
                        series.Add(Line(option, Across(f, 0), hoverable: false));
                        if (f.At(0) > 0) text += $" {f.At(n - 1) / f.At(0) - 1:+0.0%;−0.0%;0.0%}";
                    }
                    else note = "not enough history";
                    break;
                case ChartOverlay.TrendChannel:
                    if (fit is { } c)
                    {
                        series.Add(Line(option, Across(c, 2 * c.Sd), name: "Channel upper"));
                        series.Add(Line(option, Across(c, -2 * c.Sd), name: "Channel lower"));
                        // The middle is the trend line; drawn here only when that isn't shown already.
                        if (!active.Any(o => o.Kind == ChartOverlay.Trend))
                            series.Add(Line(option.Name, option.Color, LineDash.Long, Across(c, 0), hoverable: false));
                        text = "Trend channel ±2σ";
                    }
                    else note = "not enough history";
                    break;
                case ChartOverlay.AutoTrend:
                    var (highs, lows) = Indicators.Swings(shownBars, swingK);
                    var support = Indicators.TrendLine(shownBars, lows, support: true, tolerance);
                    var resistance = Indicators.TrendLine(shownBars, highs, support: false, tolerance);
                    if (support is { } s) series.Add(Line("Support trendline", option.Color, option.Dash, Ray(s, rightEdge), hoverable: false));
                    if (resistance is { } r) series.Add(Line("Resistance trendline", option.Color, option.Dash, Ray(r, rightEdge), hoverable: false));
                    note = (support, resistance) switch
                    {
                        (null, null) => "none found",
                        (null, _) => "resistance only",
                        (_, null) => "support only",
                        _ => null,
                    };
                    break;
                case ChartOverlay.Levels:
                    if (n == 0) break;
                    var levels = Indicators.Levels(shownBars, swingK, tolerance);
                    var last = shownBars[^1].Close;
                    var below = levels.Where(l => l.Price < last).OrderByDescending(l => l.Price).Take(2).ToList();
                    var above = levels.Where(l => l.Price >= last).OrderBy(l => l.Price).Take(2).ToList();
                    foreach (var level in below) sections.Add(LevelSection(option, level.Price, "S"));
                    foreach (var level in above) sections.Add(LevelSection(option, level.Price, "R"));
                    if (below.Count + above.Count == 0) note = "none found";
                    break;
                case ChartOverlay.Sma20: note = AddLine(Indicators.Sma(closes, 20)); break;
                case ChartOverlay.Sma50: note = AddLine(Indicators.Sma(closes, 50)); break;
                case ChartOverlay.Sma200: note = AddLine(Indicators.Sma(closes, 200)); break;
                case ChartOverlay.Ema9: note = AddLine(Indicators.Ema(closes, 9)); break;
                case ChartOverlay.Ema21: note = AddLine(Indicators.Ema(closes, 21)); break;
                case ChartOverlay.Vwap:
                    // Stocks restart at each local trading day; crypto at midnight UTC, as exchanges do.
                    note = AddLine(Indicators.Vwap(_bars, b => IsCrypto ? b.Time.UtcDateTime.Date : b.Time.ToLocalTime().Date));
                    break;
                case ChartOverlay.Bollinger:
                    var (middle, upper, lower) = Indicators.Bollinger(closes, 20, 2);
                    note = AddLine(upper, "BB upper") ?? AddLine(lower, "BB lower");
                    if (note is null) series.Add(Line(option.Name, option.Color, LineDash.Long, Points(middle), hoverable: false));
                    text = "BB 20, 2σ";
                    break;
            }
            legend.Add(new OverlayLegendItem(note is null ? text : $"{text} · {note}", option.Swatch, option.DashArray));

            // Null when drawn, otherwise why not.
            string? AddLine(double?[] values, string? name = null)
            {
                var points = Points(values);
                if (points.Count == 0) return "not enough history";
                series.Add(Line(name ?? option.Name, option.Color, option.Dash, points));
                return null;
            }
        }

        foreach (var line in _lines)
            series.Add(Line("My line", Palette.Ink, LineDash.Solid, Endpoints(Rendered(line)), hoverable: false));
        series.Add(_previewSeries);

        PriceSeries = series.ToArray();
        PriceSections = sections.ToArray();
        Legend = legend;

        List<ObservablePoint> Across(LinearFit line, double offset) =>
            [new ObservablePoint(0, line.At(0) + offset), new ObservablePoint(n - 1, line.At(n - 1) + offset)];
    }

    /// <summary>Lines that project (trendlines, drawn lines) run a little past the last bar.</summary>
    private double RightEdge
    {
        get
        {
            var n = _bars.Count - _firstShown;
            return n - 1 + Math.Max(2, Math.Round(n * 0.05));
        }
    }

    private static List<ObservablePoint> Ray(Segment line, double rightEdge) =>
        Endpoints(new Segment(line.X1, line.Y1, rightEdge, line.At(rightEdge)));

    private static List<ObservablePoint> Endpoints(Segment line) =>
        [new ObservablePoint(line.X1, line.Y1), new ObservablePoint(line.X2, line.Y2)];

    private static RectangularSection LevelSection(OverlayOption option, double price, string tag) => new()
    {
        Yi = price,
        Yj = price,
        Stroke = new SolidColorPaint(option.Color, 1.5f) { PathEffect = Dashes.Chart(option.Dash) is { } d ? new DashEffect(d) : null },
        Label = $"{tag} {FormatPrice(price)}",
        LabelPaint = new SolidColorPaint(Palette.Secondary),
        LabelSize = 11,
    };

    // ----- Drawn lines -----

    private readonly List<DrawnLine> _lines;
    private (double X, double Y)? _drawStart;
    private readonly ObservableCollection<ObservablePoint> _preview = [];
    private readonly LineSeries<ObservablePoint> _previewSeries;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(ChartZoom))] private bool _isDrawing;
    [ObservableProperty] private CoreSection[] _priceSections = [];
    [ObservableProperty] private string? _drawHint;

    /// <summary>Panning would fight the clicks while drawing.</summary>
    public ZoomAndPanMode ChartZoom => IsDrawing ? ZoomAndPanMode.None : ZoomAndPanMode.X;
    public bool HasLines => _lines.Count > 0;
    public string ClearLinesLabel => _lines.Count == 1 ? "Clear my line" : $"Clear my {_lines.Count} lines";

    partial void OnIsDrawingChanged(bool value)
    {
        _drawStart = null;
        _preview.Clear();
        DrawHint = value ? "Click the line's first point · right-click or Esc to cancel" : null;
    }

    /// <summary>A click on the price chart while drawing, in chart coordinates (x = bar index).</summary>
    public void DrawClick(double x, double y)
    {
        if (!IsDrawing || _bars.Count - _firstShown < 2) return;
        if (_drawStart is not { } start)
        {
            _drawStart = (x, y);
            _preview.Add(new ObservablePoint(x, y));
            _preview.Add(new ObservablePoint(x, y));
            DrawHint = "Click the second point · right-click or Esc to cancel";
            return;
        }
        if (Math.Abs(x - start.X) < 0.5) return; // same bar: a vertical line isn't a trendline

        var (a, b) = start.X < x ? (start, (X: x, Y: y)) : ((X: x, Y: y), start);
        _lines.Add(new DrawnLine(Guid.NewGuid(), TimeAt(a.X), a.Y, TimeAt(b.X), b.Y));
        SaveLines();
        IsDrawing = false;
    }

    public void DrawMove(double x, double y)
    {
        if (_drawStart is null || _preview.Count < 2) return;
        _preview[1].X = x;
        _preview[1].Y = y;
    }

    /// <summary>Where each drawn line is on screen, for right-click hit testing.</summary>
    public IEnumerable<(Guid Id, Segment Line)> DrawnSegments => _lines.Select(l => (l.Id, Rendered(l)));

    public void DeleteLine(Guid id)
    {
        if (_lines.RemoveAll(l => l.Id == id) > 0) SaveLines();
    }

    [RelayCommand]
    private void ClearLines()
    {
        _lines.Clear();
        SaveLines();
    }

    /// <summary>The bar's high, low, open and close, which a click near them snaps to.</summary>
    public IReadOnlyList<double> PricesAt(double x)
    {
        var i = _firstShown + (int)Math.Round(x);
        if (x < 0 || i >= _bars.Count) return [];
        var b = _bars[i];
        return [b.High, b.Low, b.Open, b.Close];
    }

    private void SaveLines()
    {
        ChartSettingsStore.SaveLines(Symbol, _lines);
        OnPropertyChanged(nameof(HasLines));
        OnPropertyChanged(nameof(ClearLinesLabel));
        ApplyOverlays();
    }

    /// <summary>A drawn line on the current range: through its two anchors, from the first (or the left edge) to the right edge.</summary>
    private Segment Rendered(DrawnLine line)
    {
        var through = new Segment(IndexAt(line.Time1), line.Price1, IndexAt(line.Time2), line.Price2);
        var start = Math.Max(through.X1, 0);
        var end = RightEdge;
        return new Segment(start, through.At(start), end, through.At(end));
    }

    /// <summary>
    /// A time's x on screen: fractional between bars, and beyond either end at the average bar spacing.
    /// Lines are stored by time, so one drawn on 3M lands on the same bars on 1Y.
    /// </summary>
    private double IndexAt(DateTimeOffset time)
    {
        var n = _bars.Count - _firstShown;
        if (n < 2) return 0;
        var first = _bars[_firstShown].Time;
        var last = _bars[^1].Time;
        var spacing = (last - first).Ticks / (double)(n - 1);
        if (time <= first) return (time - first).Ticks / spacing;
        if (time >= last) return n - 1 + (time - last).Ticks / spacing;

        int lo = _firstShown, hi = _bars.Count - 1; // the last bar at or before `time` is lo
        while (hi - lo > 1)
        {
            var mid = (lo + hi) / 2;
            if (_bars[mid].Time <= time) lo = mid; else hi = mid;
        }
        var span = (_bars[hi].Time - _bars[lo].Time).Ticks;
        return lo - _firstShown + (span > 0 ? (time - _bars[lo].Time).Ticks / (double)span : 0);
    }

    /// <summary>The inverse of <see cref="IndexAt"/>.</summary>
    private DateTimeOffset TimeAt(double x)
    {
        var n = _bars.Count - _firstShown;
        var first = _bars[_firstShown].Time;
        var last = _bars[^1].Time;
        var spacing = (last - first).Ticks / (double)(n - 1);
        if (x <= 0) return first.AddTicks((long)(x * spacing));
        if (x >= n - 1) return last.AddTicks((long)((x - (n - 1)) * spacing));
        var i = (int)Math.Floor(x);
        var a = _bars[_firstShown + i].Time;
        var b = _bars[_firstShown + i + 1].Time;
        return a.AddTicks((long)((b - a).Ticks * (x - i)));
    }

    /// <summary>The on-screen part of an indicator, at the candles' x positions.</summary>
    private List<ObservablePoint> Points(double?[] values)
    {
        var points = new List<ObservablePoint>();
        for (var i = _firstShown; i < values.Length; i++)
            if (values[i] is { } v) points.Add(new ObservablePoint(i - _firstShown, v));
        return points;
    }

    private static LineSeries<ObservablePoint> Line(OverlayOption option, IReadOnlyCollection<ObservablePoint> points,
        bool hoverable = true, string? name = null) =>
        Line(name ?? option.Name, option.Color, option.Dash, points, hoverable);

    private static LineSeries<ObservablePoint> Line(string name, SKColor color, LineDash dash, IReadOnlyCollection<ObservablePoint> points,
        bool hoverable = true) => new()
    {
        Name = name,
        Values = points,
        Fill = null,
        GeometrySize = 0,
        GeometryFill = null,
        GeometryStroke = null,
        LineSmoothness = 0,
        Stroke = new SolidColorPaint(color, 2) { PathEffect = Dashes.Chart(dash) is { } d ? new DashEffect(d) : null },
        IsHoverable = hoverable,
        YToolTipLabelFormatter = p => FormatPrice(p.Model?.Y ?? 0),
    };

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
