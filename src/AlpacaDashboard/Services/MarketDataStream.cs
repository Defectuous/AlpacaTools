using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AlpacaDashboard.Models;

namespace AlpacaDashboard.Services;

/// <summary>Ordered best to worst, so the max of several streams is the one to surface.</summary>
public enum StreamState { Stopped, Live, Connecting, Reconnecting, Failed }

/// <summary>
/// Real-time price stream from one of Alpaca's market data websockets
/// (wss://stream.data.alpaca.markets/{path}: v2/iex, v2/sip or v1beta3/crypto/us).
/// Reconnects with backoff. Events are raised on a background thread.
/// </summary>
public sealed class MarketDataStream : IAsyncDisposable
{
    public const string IexPath = "v2/iex";
    public const string SipPath = "v2/sip";
    public const string CryptoPath = "v1beta3/crypto/us";

    private const string StreamBaseUrl = "wss://stream.data.alpaca.markets/";

    private readonly AccountProfile _profile;
    private readonly string _path;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly object _symbolsLock = new();
    private HashSet<string> _wanted = new(StringComparer.Ordinal);
    private HashSet<string> _subscribed = new(StringComparer.Ordinal);
    private ClientWebSocket? _socket;
    private bool _authenticated;
    private Task? _loop;

    public MarketDataStream(AccountProfile profile, string path)
    {
        _profile = profile;
        _path = path;
    }

    public string Path => _path;

    /// <summary>Short name for status text: IEX, SIP or CRYPTO.</summary>
    public string Label => _path switch
    {
        SipPath => "SIP",
        CryptoPath => "Crypto",
        _ => "IEX",
    };

    /// <summary>symbol, price, time. Prices come from trades, plus bid/ask midpoints on the crypto stream.</summary>
    public event Action<string, double, DateTimeOffset>? PriceReceived;

    // Crypto pairs on Alpaca can go minutes between trades, so also follow quotes there.
    private bool IncludeQuotes => _path == CryptoPath;
    public event Action<MarketDataStream, StreamState, string>? StatusChanged;
    /// <summary>Raised on error 409: the account's plan does not include this feed.</summary>
    public event Action? InsufficientSubscription;

    public void Start(IEnumerable<string> symbols)
    {
        lock (_symbolsLock) _wanted = new HashSet<string>(symbols, StringComparer.Ordinal);
        _loop ??= Task.Run(() => RunAsync(_cts.Token));
    }

    public async Task SetSymbolsAsync(IEnumerable<string> symbols)
    {
        lock (_symbolsLock) _wanted = new HashSet<string>(symbols, StringComparer.Ordinal);
        if (_authenticated) await SyncSubscriptionAsync(_cts.Token).ConfigureAwait(false);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(2);
        while (!ct.IsCancellationRequested)
        {
            StreamError? fatal = null;
            try
            {
                StatusChanged?.Invoke(this, StreamState.Connecting, $"{Label} connecting…");
                fatal = await ConnectAndReceiveAsync(ct).ConfigureAwait(false);
                delay = TimeSpan.FromSeconds(2);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is WebSocketException or IOException or JsonException)
            {
                StatusChanged?.Invoke(this, StreamState.Reconnecting, $"{Label} disconnected ({ex.Message}); retrying in {delay.TotalSeconds:0}s");
            }
            finally
            {
                _authenticated = false;
                _socket?.Dispose();
                _socket = null;
            }

            if (fatal is { } err)
            {
                switch (err.Code)
                {
                    case 402: // auth failed: retrying won't help
                        StatusChanged?.Invoke(this, StreamState.Failed, $"{Label} stream authentication failed");
                        return;
                    case 409: // feed not included in plan
                        StatusChanged?.Invoke(this, StreamState.Failed, $"Plan does not include the {Label} feed");
                        InsufficientSubscription?.Invoke();
                        return;
                    case 406: // another app (or another account under the same Alpaca user) holds the only connection
                        delay = TimeSpan.FromSeconds(60);
                        StatusChanged?.Invoke(this, StreamState.Reconnecting,
                            $"{Label}: connection limit reached (another stream is open for this Alpaca user); retrying in 60s");
                        break;
                    default:
                        StatusChanged?.Invoke(this, StreamState.Reconnecting, $"{Label} stream error {err.Code}: {err.Message}");
                        break;
                }
            }

            try { await Task.Delay(delay, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 60));
        }
        StatusChanged?.Invoke(this, StreamState.Stopped, "Stopped");
    }

    /// <summary>Runs one connection until it closes. Returns a server error that ended it, if any.</summary>
    private async Task<StreamError?> ConnectAndReceiveAsync(CancellationToken ct)
    {
        var baseUrl = Environment.GetEnvironmentVariable("ALPACA_STREAM_URL_OVERRIDE") ?? StreamBaseUrl;
        _socket = new ClientWebSocket();
        await _socket.ConnectAsync(new Uri(baseUrl + _path), ct).ConfigureAwait(false);
        lock (_symbolsLock) _subscribed = new HashSet<string>(StringComparer.Ordinal);

        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        while (_socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            message.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await _socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) return null;
                message.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            message.Position = 0;
            using var doc = await JsonDocument.ParseAsync(message, cancellationToken: ct).ConfigureAwait(false);
            if (await HandleMessagesAsync(doc.RootElement, ct).ConfigureAwait(false) is { } error)
                return error;
        }
        return null;
    }

    private async Task<StreamError?> HandleMessagesAsync(JsonElement root, CancellationToken ct)
    {
        // Every frame is a JSON array; control messages arrive alone, data may be batched.
        foreach (var msg in root.EnumerateArray())
        {
            switch (msg.GetProperty("T").GetString())
            {
                case "t":
                    PriceReceived?.Invoke(msg.GetProperty("S").GetString()!, msg.GetProperty("p").GetDouble(),
                        msg.GetProperty("t").GetDateTimeOffset());
                    break;
                case "q" when msg.GetProperty("bp").GetDouble() is > 0 and var bid
                              && msg.GetProperty("ap").GetDouble() is > 0 and var ask:
                    PriceReceived?.Invoke(msg.GetProperty("S").GetString()!, (bid + ask) / 2,
                        msg.GetProperty("t").GetDateTimeOffset());
                    break;
                case "success" when msg.GetProperty("msg").GetString() == "connected":
                    await SendAsync(new { action = "auth", key = _profile.KeyId, secret = _profile.SecretKey }, ct).ConfigureAwait(false);
                    break;
                case "success" when msg.GetProperty("msg").GetString() == "authenticated":
                    _authenticated = true;
                    await SyncSubscriptionAsync(ct).ConfigureAwait(false);
                    break;
                case "subscription":
                    var count = msg.TryGetProperty("trades", out var t) ? t.GetArrayLength() : 0;
                    StatusChanged?.Invoke(this, StreamState.Live, $"{Label} live · {count} symbols");
                    break;
                case "error":
                    return new StreamError(msg.GetProperty("code").GetInt32(), msg.GetProperty("msg").GetString() ?? "");
            }
        }
        return null;
    }

    private async Task SyncSubscriptionAsync(CancellationToken ct)
    {
        string[] add, remove;
        lock (_symbolsLock)
        {
            add = _wanted.Except(_subscribed).ToArray();
            remove = _subscribed.Except(_wanted).ToArray();
            _subscribed = new HashSet<string>(_wanted, StringComparer.Ordinal);
        }
        if (remove.Length > 0) await SendAsync(SubscriptionMessage("unsubscribe", remove), ct).ConfigureAwait(false);
        if (add.Length > 0) await SendAsync(SubscriptionMessage("subscribe", add), ct).ConfigureAwait(false);
        if (add.Length == 0 && remove.Length == 0)
            StatusChanged?.Invoke(this, StreamState.Live, $"{Label} live · {_subscribed.Count} symbols");
    }

    private object SubscriptionMessage(string action, string[] symbols) => IncludeQuotes
        ? new { action, trades = symbols, quotes = symbols }
        : new { action, trades = symbols };

    private async Task SendAsync(object payload, CancellationToken ct)
    {
        if (_socket is not { State: WebSocketState.Open } socket) return;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try { await socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false); }
        finally { _sendLock.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_socket is { State: WebSocketState.Open } socket)
        {
            try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException) { }
        }
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        _cts.Dispose();
    }

    private sealed record StreamError(int Code, string Message);
}
