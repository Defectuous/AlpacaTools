using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;

namespace AlpacaDashboard.Services;

/// <summary>
/// Request budget for one API key on one Alpaca API (trading or market data). Alpaca's limits are per
/// key and shared with anything else using that key (scripts, bots), so the dashboard:
/// <list type="bullet">
/// <item>caps itself at <see cref="MaxPerMinute"/> requests per rolling minute,</item>
/// <item>waits for the reset when Alpaca reports less than <see cref="ReserveFraction"/> of the key's budget
/// left, leaving the rest for other tools, and</item>
/// <item>backs off until the reset after a 429.</item>
/// </list>
/// </summary>
public sealed class ApiBudget
{

    private static readonly ConcurrentDictionary<string, ApiBudget> Budgets = new();

    private readonly object _lock = new();
    private readonly Queue<DateTime> _recent = new();
    private DateTime _blockedUntil = DateTime.MinValue;

    private readonly int _normalMax;
    private readonly int _yieldingMax;

    private ApiBudget(string name, int normalMax, int yieldingMax, int planLimit)
    {
        Name = name;
        _normalMax = normalMax;
        _yieldingMax = yieldingMax;
        MaxPerMinute = normalMax;
        PlanLimit = planLimit;
    }

    /// <summary>Shared budget for this key on the trading API (Alpaca limit: 200/min per key).</summary>
    public static ApiBudget Trading(string keyId) => Budgets.GetOrAdd($"trading:{keyId}", _ => new ApiBudget("Trading API", 40, 10, 200));

    /// <summary>Shared budget for this key on the market data API (200/min on Basic, 10,000/min on Algo Trader Plus).</summary>
    public static ApiBudget MarketData(string keyId) => Budgets.GetOrAdd($"data:{keyId}", _ => new ApiBudget("Market data API", 60, 20, 200));

    public string Name { get; }
    public int MaxPerMinute { get; private set; }

    /// <summary>
    /// Alpaca's per-minute limit for this key on the current plan. Used until Alpaca reports its own
    /// figure in the headers; the market data one follows the detected plan.
    /// </summary>
    public int PlanLimit { get; set; }

    /// <summary>Share of the key's budget kept free for other tools; below it the dashboard waits for the reset.</summary>
    public double ReserveFraction { get; private set; } = 0.25;

    /// <summary>When the key is shared with a bot that comes first: a lower self-cap and a 50% reserve.</summary>
    public void SetYieldToOtherApps(bool yield)
    {
        lock (_lock)
        {
            MaxPerMinute = yield ? _yieldingMax : _normalMax;
            ReserveFraction = yield ? 0.5 : 0.25;
        }
    }

    /// <summary>Last values Alpaca reported in X-RateLimit-* headers (these include other tools' usage).</summary>
    public int? KeyLimit { get; private set; }
    public int? KeyRemaining { get; private set; }

    /// <summary>"left/limit" for this key this minute, or null before Alpaca has reported it.</summary>
    public string? KeyBudgetText => KeyRemaining is { } left ? $"{left}/{KeyLimit ?? PlanLimit}" : null;

    public int CallsLastMinute
    {
        get { lock (_lock) { Prune(DateTime.UtcNow); return _recent.Count; } }
    }

    public bool IsThrottled
    {
        get { lock (_lock) return DateTime.UtcNow < _blockedUntil; }
    }

    /// <summary>How long a request made now would wait for the budget (zero when it would go straight out).</summary>
    public TimeSpan CurrentWait
    {
        get
        {
            lock (_lock)
            {
                var now = DateTime.UtcNow;
                Prune(now);
                if (now < _blockedUntil) return _blockedUntil - now;
                return _recent.Count < MaxPerMinute ? TimeSpan.Zero : _recent.Peek().AddMinutes(1) - now;
            }
        }
    }

    /// <summary>Waits until a request may be sent, then records it.</summary>
    public async Task AcquireAsync(CancellationToken ct)
    {
        while (true)
        {
            TimeSpan wait;
            lock (_lock)
            {
                var now = DateTime.UtcNow;
                Prune(now);
                if (now < _blockedUntil)
                {
                    wait = _blockedUntil - now;
                }
                else if (_recent.Count < MaxPerMinute)
                {
                    _recent.Enqueue(now);
                    return;
                }
                else
                {
                    wait = _recent.Peek().AddMinutes(1) - now;
                }
            }
            await Task.Delay(wait < TimeSpan.FromMilliseconds(50) ? TimeSpan.FromMilliseconds(50) : wait, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Reads Alpaca's rate-limit headers; on 429 or a nearly spent key budget, pauses until the reset.</summary>
    public void Observe(HttpResponseMessage response)
    {
        var limit = HeaderInt(response, "X-RateLimit-Limit");
        var remaining = HeaderInt(response, "X-RateLimit-Remaining");
        var reset = HeaderInt(response, "X-RateLimit-Reset"); // unix seconds

        lock (_lock)
        {
            KeyLimit = limit ?? KeyLimit;
            KeyRemaining = remaining ?? KeyRemaining;

            var resetAt = reset is { } r
                ? DateTimeOffset.FromUnixTimeSeconds(r).UtcDateTime
                : DateTime.UtcNow.AddSeconds(response.Headers.RetryAfter?.Delta?.TotalSeconds ?? 60);
            // Never pause longer than a minute in case the reset header is off.
            resetAt = Min(resetAt, DateTime.UtcNow.AddMinutes(1));

            var nearlySpent = limit is > 0 && remaining is { } left && left < limit.Value * ReserveFraction;
            if (response.StatusCode == HttpStatusCode.TooManyRequests || nearlySpent)
                _blockedUntil = Max(_blockedUntil, resetAt);
        }
    }

    private void Prune(DateTime now)
    {
        while (_recent.Count > 0 && now - _recent.Peek() > TimeSpan.FromMinutes(1)) _recent.Dequeue();
    }

    private static int? HeaderInt(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) && int.TryParse(values.FirstOrDefault(), out var v) ? v : null;

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
}

/// <summary>
/// HTTP handler that routes every request through an <see cref="ApiBudget"/> and retries once after a 429.
/// Owns the network timeout: waiting for the budget can take up to a minute and must not count against it,
/// so clients using this handler set <c>HttpClient.Timeout</c> to infinite.
/// </summary>
public sealed class RateLimitingHandler(ApiBudget budget) : DelegatingHandler(new HttpClientHandler())
{
    public static readonly TimeSpan NetworkTimeout = TimeSpan.FromSeconds(20);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        await budget.AcquireAsync(ct).ConfigureAwait(false);
        var response = await SendTimedAsync(request, ct).ConfigureAwait(false);
        budget.Observe(response);
        if (response.StatusCode != HttpStatusCode.TooManyRequests) return response;

        // A 429 means Alpaca didn't process the request, so a single retry after the reset is safe.
        response.Dispose();
        await budget.AcquireAsync(ct).ConfigureAwait(false);
        response = await SendTimedAsync(request, ct).ConfigureAwait(false);
        budget.Observe(response);
        return response;
    }

    /// <summary>One network round trip, body included, limited to <see cref="NetworkTimeout"/>.</summary>
    private async Task<HttpResponseMessage> SendTimedAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(NetworkTimeout);
        HttpResponseMessage? response = null;
        try
        {
            response = await base.SendAsync(request, timeout.Token).ConfigureAwait(false);
            await response.Content.LoadIntoBufferAsync(timeout.Token).ConfigureAwait(false);
            return response;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            response?.Dispose();
            throw new TimeoutException($"Alpaca didn't respond within {NetworkTimeout.TotalSeconds:0} seconds.");
        }
    }
}
