using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AlpacaDashboard.Models;

namespace AlpacaDashboard.Services;

/// <summary>REST client for Alpaca stock market data. Works on both the Basic and Algo Trader Plus plans.</summary>
public sealed class MarketDataClient : IDisposable
{
    private const string DataBaseUrl = "https://data.alpaca.markets/";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;

    /// <summary>This key's market data budget, so a slow request can say it's waiting on the rate limit.</summary>
    public ApiBudget Budget { get; }

    public MarketDataClient(AccountProfile profile)
    {
        Budget = ApiBudget.MarketData(profile.KeyId);
        _http = new HttpClient(new RateLimitingHandler(Budget))
        {
            // ALPACA_DATA_URL_OVERRIDE is for local testing against a mock server.
            BaseAddress = new Uri(Environment.GetEnvironmentVariable("ALPACA_DATA_URL_OVERRIDE") ?? DataBaseUrl),
            Timeout = Timeout.InfiniteTimeSpan, // RateLimitingHandler times the network call, not the budget wait
        };
        _http.DefaultRequestHeaders.Add("APCA-API-KEY-ID", profile.KeyId);
        _http.DefaultRequestHeaders.Add("APCA-API-SECRET-KEY", profile.SecretKey);
    }

    /// <summary>
    /// Alpaca has no "what is my plan" endpoint, so probe it. Without a subscription, a SIP query's
    /// end time must be at least 15 minutes old, so asking for SIP bars ending *now* returns 403 on
    /// Basic and 200 on Algo Trader Plus, regardless of market hours. (A "latest trade" probe is not
    /// reliable: when the market is closed the latest trade is old enough for Basic users too.)
    /// </summary>
    public async Task<MarketDataPlan> DetectPlanAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var url = "v2/stocks/bars?symbols=SPY&timeframe=1Min&feed=sip&limit=1" +
                  $"&start={Uri.EscapeDataString(now.AddHours(-1).UtcDateTime.ToString("O"))}" +
                  $"&end={Uri.EscapeDataString(now.UtcDateTime.ToString("O"))}";
        using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
        return response.StatusCode switch
        {
            HttpStatusCode.OK => MarketDataPlan.AlgoTraderPlus,
            HttpStatusCode.Forbidden => MarketDataPlan.Basic,
            _ => throw new AlpacaApiException((int)response.StatusCode, "Could not determine market data plan"),
        };
    }

    /// <summary>
    /// Historical bars for one symbol, following pagination. The end time is clamped by the
    /// feed profile's delay (15 minutes for SIP on the Basic plan).
    /// </summary>
    public async Task<List<StockBar>> GetBarsAsync(string symbol, string timeframe, DateTimeOffset start,
        DataFeedProfile feed, CancellationToken ct = default)
    {
        var end = DateTimeOffset.UtcNow - feed.HistoricalDelay - TimeSpan.FromSeconds(30);
        var bars = new List<StockBar>();
        string? pageToken = null;
        do
        {
            var url = $"v2/stocks/{Uri.EscapeDataString(symbol)}/bars?timeframe={timeframe}" +
                      $"&start={Uri.EscapeDataString(start.UtcDateTime.ToString("O"))}" +
                      $"&end={Uri.EscapeDataString(end.UtcDateTime.ToString("O"))}" +
                      $"&feed={feed.HistoricalFeed}&adjustment=all&limit=10000" +
                      (pageToken is null ? "" : $"&page_token={Uri.EscapeDataString(pageToken)}");
            var page = await GetAsync<StockBarsPage>(url, ct).ConfigureAwait(false);
            if (page.Bars is not null) bars.AddRange(page.Bars);
            pageToken = page.NextPageToken;
        } while (!string.IsNullOrEmpty(pageToken));
        return bars;
    }

    public Task<StockSnapshot> GetSnapshotAsync(string symbol, DataFeedProfile feed, CancellationToken ct = default) =>
        GetAsync<StockSnapshot>($"v2/stocks/{Uri.EscapeDataString(symbol)}/snapshot?feed={feed.SnapshotFeed}", ct);

    /// <summary>
    /// Latest prices for many symbols in at most two requests (one for stocks, one for crypto), used
    /// instead of streaming when the key's stream slot belongs to a bot. Crypto uses the bid/ask midpoint
    /// when there's a quote, because thin pairs can go minutes without a trade.
    /// </summary>
    public async Task<Dictionary<string, double>> GetLatestPricesAsync(IReadOnlyCollection<string> stocks,
        IReadOnlyCollection<string> cryptoPairs, DataFeedProfile feed, CancellationToken ct = default)
    {
        var prices = new Dictionary<string, double>(StringComparer.Ordinal);
        if (stocks.Count > 0)
        {
            foreach (var (symbol, snap) in await GetSnapshotsAsync(stocks, feed, ct).ConfigureAwait(false))
                if (snap.LatestTrade?.Price is > 0 and var p) prices[symbol] = p;
        }
        if (cryptoPairs.Count > 0)
        {
            foreach (var (pair, snap) in await GetCryptoSnapshotsAsync(cryptoPairs, ct).ConfigureAwait(false))
            {
                if (snap.LatestQuote is { BidPrice: > 0, AskPrice: > 0 } q) prices[pair] = (q.BidPrice + q.AskPrice) / 2;
                else if (snap.LatestTrade?.Price is > 0 and var p) prices[pair] = p;
            }
        }
        return prices;
    }

    /// <summary>Snapshots for many stocks in one request; the response is keyed by symbol at the root.</summary>
    public Task<Dictionary<string, StockSnapshot>> GetSnapshotsAsync(IReadOnlyCollection<string> symbols, DataFeedProfile feed, CancellationToken ct = default) =>
        GetAsync<Dictionary<string, StockSnapshot>>(
            $"v2/stocks/snapshots?symbols={Uri.EscapeDataString(string.Join(',', symbols))}&feed={feed.SnapshotFeed}", ct);

    /// <summary>Daily bars for many stocks in one paginated request (the page limit counts bars across all symbols).</summary>
    public Task<Dictionary<string, List<StockBar>>> GetDailyBarsAsync(IReadOnlyCollection<string> symbols, DateTimeOffset start,
        DataFeedProfile feed, CancellationToken ct = default)
    {
        var end = DateTimeOffset.UtcNow - feed.HistoricalDelay - TimeSpan.FromSeconds(30);
        return GetMultiBarsAsync($"v2/stocks/bars?symbols={Uri.EscapeDataString(string.Join(',', symbols))}&timeframe=1Day" +
                                 $"&start={Uri.EscapeDataString(start.UtcDateTime.ToString("O"))}" +
                                 $"&end={Uri.EscapeDataString(end.UtcDateTime.ToString("O"))}" +
                                 $"&feed={feed.HistoricalFeed}&adjustment=all&limit=10000", ct);
    }

    /// <summary>Daily bars for many crypto pairs in one paginated request.</summary>
    public Task<Dictionary<string, List<StockBar>>> GetCryptoDailyBarsAsync(IReadOnlyCollection<string> pairs, DateTimeOffset start,
        CancellationToken ct = default) =>
        GetMultiBarsAsync($"v1beta3/crypto/us/bars?symbols={Uri.EscapeDataString(string.Join(',', pairs))}&timeframe=1Day" +
                          $"&start={Uri.EscapeDataString(start.UtcDateTime.ToString("O"))}&limit=10000", ct);

    private async Task<Dictionary<string, List<StockBar>>> GetMultiBarsAsync(string baseUrl, CancellationToken ct)
    {
        var bars = new Dictionary<string, List<StockBar>>(StringComparer.Ordinal);
        string? pageToken = null;
        do
        {
            var url = baseUrl + (pageToken is null ? "" : $"&page_token={Uri.EscapeDataString(pageToken)}");
            var page = await GetAsync<MultiBarsPage>(url, ct).ConfigureAwait(false);
            foreach (var (symbol, symbolBars) in page.Bars ?? [])
            {
                if (!bars.TryGetValue(symbol, out var list)) bars[symbol] = list = [];
                list.AddRange(symbolBars);
            }
            pageToken = page.NextPageToken;
        } while (!string.IsNullOrEmpty(pageToken));
        return bars;
    }

    // ----- Screener (v1beta1): ranked lists from SIP data, available on every plan -----

    /// <summary>Top stock gainers and losers today by percent change (Alpaca allows up to 50 each).</summary>
    public Task<ScreenerMovers> GetStockMoversAsync(int top, CancellationToken ct = default) =>
        GetAsync<ScreenerMovers>($"v1beta1/screener/stocks/movers?top={top}", ct);

    /// <summary>Most active stocks today, ranked by "volume" or "trades" (Alpaca allows up to 100).</summary>
    public Task<ScreenerMostActives> GetMostActivesAsync(string by, int top, CancellationToken ct = default) =>
        GetAsync<ScreenerMostActives>($"v1beta1/screener/stocks/most-actives?by={by}&top={top}", ct);

    // Crypto data (v1beta3, "us" location) is free and real-time on every plan.

    /// <summary>Snapshots for many crypto pairs (e.g. "BTC/USD") in one request.</summary>
    public async Task<Dictionary<string, StockSnapshot>> GetCryptoSnapshotsAsync(IReadOnlyCollection<string> pairs, CancellationToken ct = default)
    {
        var result = await GetAsync<CryptoSnapshots>(
            $"v1beta3/crypto/us/snapshots?symbols={Uri.EscapeDataString(string.Join(',', pairs))}", ct).ConfigureAwait(false);
        return result.Snapshots ?? [];
    }

    /// <summary>Historical crypto bars for one pair (e.g. "BTC/USD"), following pagination.</summary>
    public async Task<List<StockBar>> GetCryptoBarsAsync(string pair, string timeframe, DateTimeOffset start, CancellationToken ct = default)
    {
        var bars = new List<StockBar>();
        string? pageToken = null;
        do
        {
            var url = $"v1beta3/crypto/us/bars?symbols={Uri.EscapeDataString(pair)}&timeframe={timeframe}" +
                      $"&start={Uri.EscapeDataString(start.UtcDateTime.ToString("O"))}&limit=10000" +
                      (pageToken is null ? "" : $"&page_token={Uri.EscapeDataString(pageToken)}");
            var page = await GetAsync<MultiBarsPage>(url, ct).ConfigureAwait(false);
            if (page.Bars?.TryGetValue(pair, out var pairBars) == true) bars.AddRange(pairBars);
            pageToken = page.NextPageToken;
        } while (!string.IsNullOrEmpty(pageToken));
        return bars;
    }

    public async Task<StockSnapshot> GetCryptoSnapshotAsync(string pair, CancellationToken ct = default)
    {
        var result = await GetAsync<CryptoSnapshots>($"v1beta3/crypto/us/snapshots?symbols={Uri.EscapeDataString(pair)}", ct).ConfigureAwait(false);
        return result.Snapshots?.GetValueOrDefault(pair) ?? new StockSnapshot();
    }

    private async Task<T> GetAsync<T>(string path, CancellationToken ct)
    {
        using var response = await _http.GetAsync(path, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new AlpacaApiException((int)response.StatusCode, AlpacaClient.ExtractMessage(body) ?? response.ReasonPhrase ?? "Request failed");
        }
        return (await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct).ConfigureAwait(false))!;
    }

    public void Dispose() => _http.Dispose();
}
