using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AlpacaDashboard.Models;

namespace AlpacaDashboard.Services;

/// <summary>
/// Client for the Alpaca Trading API. One instance per account profile. Reads by default; orders are
/// placed only from the position trade dialog, after the user confirms.
/// </summary>
public sealed class AlpacaClient : IDisposable
{
    private const string LiveBaseUrl = "https://api.alpaca.markets/";
    private const string PaperBaseUrl = "https://paper-api.alpaca.markets/";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, // omit unset order fields
    };

    private readonly HttpClient _http;

    public AlpacaClient(AccountProfile profile)
    {
        _http = new HttpClient(new RateLimitingHandler(ApiBudget.Trading(profile.KeyId)))
        {
            // ALPACA_BASE_URL_OVERRIDE is for local testing against a mock server.
            BaseAddress = new Uri(Environment.GetEnvironmentVariable("ALPACA_BASE_URL_OVERRIDE")
                                  ?? (profile.IsPaper ? PaperBaseUrl : LiveBaseUrl)),
            Timeout = Timeout.InfiniteTimeSpan, // RateLimitingHandler times the network call, not the budget wait
        };
        _http.DefaultRequestHeaders.Add("APCA-API-KEY-ID", profile.KeyId);
        _http.DefaultRequestHeaders.Add("APCA-API-SECRET-KEY", profile.SecretKey);
    }

    public Task<AlpacaAccount> GetAccountAsync(CancellationToken ct = default) =>
        GetAsync<AlpacaAccount>("v2/account", ct);

    public Task<List<AlpacaPosition>> GetPositionsAsync(CancellationToken ct = default) =>
        GetAsync<List<AlpacaPosition>>("v2/positions", ct);

    public Task<List<AlpacaOrder>> GetOpenOrdersAsync(CancellationToken ct = default) =>
        GetAsync<List<AlpacaOrder>>("v2/orders?status=open&limit=100&direction=desc", ct);

    public Task<List<AlpacaActivity>> GetActivitiesAsync(int pageSize = 50, CancellationToken ct = default) =>
        GetAsync<List<AlpacaActivity>>($"v2/account/activities?direction=desc&page_size={pageSize}", ct);

    /// <summary>
    /// One page of all activity types, newest first. <paramref name="pageToken"/> is the id of the last activity
    /// on the previous page; the page then holds the activities after it (older).
    /// </summary>
    public Task<List<AlpacaActivity>> GetActivitiesPageAsync(int pageSize, string? pageToken, CancellationToken ct = default) =>
        GetAsync<List<AlpacaActivity>>($"v2/account/activities?direction=desc&page_size={pageSize}" +
                                       (pageToken is null ? "" : $"&page_token={Uri.EscapeDataString(pageToken)}"), ct);

    /// <summary>All fills since <paramref name="after"/>, following pagination (oldest first).</summary>
    public async Task<List<AlpacaActivity>> GetFillsSinceAsync(DateTimeOffset after, CancellationToken ct = default)
    {
        const int pageSize = 100;
        var fills = new List<AlpacaActivity>();
        string? pageToken = null;
        while (true)
        {
            var url = $"v2/account/activities/FILL?direction=asc&page_size={pageSize}" +
                      $"&after={Uri.EscapeDataString(after.UtcDateTime.ToString("O"))}" +
                      (pageToken is null ? "" : $"&page_token={Uri.EscapeDataString(pageToken)}");
            var page = await GetAsync<List<AlpacaActivity>>(url, ct).ConfigureAwait(false);
            fills.AddRange(page);
            if (page.Count < pageSize) return fills;
            pageToken = page[^1].Id; // activities paginate by the last activity id
        }
    }

    public Task<AlpacaPortfolioHistory> GetPortfolioHistoryAsync(HistoryRange range, CancellationToken ct = default)
    {
        var (period, timeframe) = range switch
        {
            HistoryRange.Day => ("1D", "5Min"),
            HistoryRange.Week => ("1W", "1H"),
            HistoryRange.Month => ("1M", "1D"),
            HistoryRange.ThreeMonths => ("3M", "1D"),
            HistoryRange.Year => ("1A", "1D"),
            _ => ("all", "1D"),
        };
        return GetAsync<AlpacaPortfolioHistory>(
            $"v2/account/portfolio/history?period={period}&timeframe={timeframe}", ct);
    }

    /// <summary>Active assets of one class ("us_equity" or "crypto").</summary>
    public Task<List<AlpacaAsset>> GetAssetsAsync(string assetClass, CancellationToken ct = default) =>
        GetAsync<List<AlpacaAsset>>($"v2/assets?status=active&asset_class={Uri.EscapeDataString(assetClass)}", ct);

    // ----- Orders (the only calls that change the account) -----

    public Task<AlpacaOrder> GetOrderAsync(string orderId, CancellationToken ct = default) =>
        GetAsync<AlpacaOrder>($"v2/orders/{Uri.EscapeDataString(orderId)}", ct);

    public async Task<AlpacaOrder> SubmitOrderAsync(OrderRequest order, CancellationToken ct = default)
    {
        using var response = await _http.PostAsJsonAsync("v2/orders", order, JsonOptions, ct).ConfigureAwait(false);
        return await ReadAsync<AlpacaOrder>(response, ct).ConfigureAwait(false);
    }

    /// <summary>Closes the whole position at market (sell for a long, buy to cover for a short).</summary>
    public async Task<AlpacaOrder> ClosePositionAsync(string symbol, CancellationToken ct = default)
    {
        using var response = await _http.DeleteAsync($"v2/positions/{Uri.EscapeDataString(symbol)}", ct).ConfigureAwait(false);
        return await ReadAsync<AlpacaOrder>(response, ct).ConfigureAwait(false);
    }

    public async Task CancelOrderAsync(string orderId, CancellationToken ct = default)
    {
        using var response = await _http.DeleteAsync($"v2/orders/{Uri.EscapeDataString(orderId)}", ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
    }

    private async Task<T> GetAsync<T>(string path, CancellationToken ct)
    {
        using var response = await _http.GetAsync(path, ct).ConfigureAwait(false);
        return await ReadAsync<T>(response, ct).ConfigureAwait(false);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        return (await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct).ConfigureAwait(false))!;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        throw new AlpacaApiException((int)response.StatusCode, ExtractMessage(body) ?? response.ReasonPhrase ?? "Request failed");
    }

    internal static string? ExtractMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() : null;
        }
        catch (JsonException)
        {
            return string.IsNullOrWhiteSpace(body) ? null : body;
        }
    }

    public void Dispose() => _http.Dispose();
}

public enum HistoryRange { Day, Week, Month, ThreeMonths, Year, All }

public sealed class AlpacaApiException(int statusCode, string message)
    : Exception($"Alpaca API error {statusCode}: {message}")
{
    public int StatusCode { get; } = statusCode;
}
