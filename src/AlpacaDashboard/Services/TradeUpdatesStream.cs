using System.IO;
using System.Net.WebSockets;
using System.Text.Json;
using AlpacaDashboard.Models;

namespace AlpacaDashboard.Services;

/// <summary>
/// Alpaca's account event stream (wss://{paper-}api.alpaca.markets/stream, "trade_updates"). It pushes
/// order events (new, fill, partial_fill, canceled, ...) so the dashboard can refresh positions and orders
/// when something actually changes instead of polling. Reconnects with backoff; events fire on a background thread.
/// </summary>
public sealed class TradeUpdatesStream : IAsyncDisposable
{
    private const string LiveUrl = "wss://api.alpaca.markets/stream";
    private const string PaperUrl = "wss://paper-api.alpaca.markets/stream";

    private readonly AccountProfile _profile;
    private readonly CancellationTokenSource _cts = new();
    private ClientWebSocket? _socket;
    private Task? _loop;

    public TradeUpdatesStream(AccountProfile profile) => _profile = profile;

    /// <summary>event name (e.g. "fill"), symbol.</summary>
    public event Action<string, string>? TradeUpdate;

    /// <summary>true once listening; false when disconnected.</summary>
    public event Action<bool>? ConnectionChanged;

    public void Start() => _loop ??= Task.Run(() => RunAsync(_cts.Token));

    private async Task RunAsync(CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(2);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ConnectAndReceiveAsync(ct).ConfigureAwait(false);
                delay = TimeSpan.FromSeconds(2);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is WebSocketException or IOException or JsonException or InvalidOperationException)
            {
                // fall through to reconnect
            }
            finally
            {
                _socket?.Dispose();
                _socket = null;
                ConnectionChanged?.Invoke(false);
            }

            try { await Task.Delay(delay, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 120));
        }
    }

    private async Task ConnectAndReceiveAsync(CancellationToken ct)
    {
        var url = Environment.GetEnvironmentVariable("ALPACA_TRADE_STREAM_URL_OVERRIDE") ?? (_profile.IsPaper ? PaperUrl : LiveUrl);
        _socket = new ClientWebSocket();
        await _socket.ConnectAsync(new Uri(url), ct).ConfigureAwait(false);
        await SendAsync(new { action = "auth", key = _profile.KeyId, secret = _profile.SecretKey }, ct).ConfigureAwait(false);

        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (_socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            message.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await _socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) return;
                message.Write(buffer, 0, result.Count); // paper sends binary frames, live sends text; both are JSON
            } while (!result.EndOfMessage);

            message.Position = 0;
            using var doc = await JsonDocument.ParseAsync(message, cancellationToken: ct).ConfigureAwait(false);
            var root = doc.RootElement;
            var data = root.TryGetProperty("data", out var d) ? d : default;
            switch (root.TryGetProperty("stream", out var s) ? s.GetString() : null)
            {
                case "authorization":
                    if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("status", out var status)
                        && status.GetString() == "authorized")
                        await SendAsync(new { action = "listen", data = new { streams = new[] { "trade_updates" } } }, ct).ConfigureAwait(false);
                    else
                        throw new InvalidOperationException("Trade updates stream: not authorized");
                    break;
                case "listening":
                    ConnectionChanged?.Invoke(true);
                    break;
                case "trade_updates" when data.ValueKind == JsonValueKind.Object:
                    var evt = data.TryGetProperty("event", out var e) ? e.GetString() ?? "" : "";
                    var symbol = data.TryGetProperty("order", out var o) && o.TryGetProperty("symbol", out var sym) ? sym.GetString() ?? "" : "";
                    TradeUpdate?.Invoke(evt, symbol);
                    break;
            }
        }
    }

    private async Task SendAsync(object payload, CancellationToken ct)
    {
        if (_socket is not { State: WebSocketState.Open } socket) return;
        await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(payload), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        _cts.Dispose();
    }
}
