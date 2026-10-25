using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AlpacaDashboard.Services;

/// <summary>A button press from a controller: an action name and an optional argument ("buy", "SPY").</summary>
public sealed record ControllerCommand(string Action, string? Arg = null);

/// <summary>
/// Local API for hardware controllers (Stream Deck, Soomfon/StreamDock, anything that can run a program).
/// Listens on localhost only. Every request needs the token in %APPDATA%\AlpacaTools\controller.json: the
/// dashboard's own "--do" command line reads it from there, and a plugin gets it by pairing, which asks
/// the user first. The token also keeps web pages in a browser from driving the dashboard.
/// </summary>
public sealed class ControllerServer : IDisposable
{
    public const int DefaultPort = 47823;
    private const int MaxBodyBytes = 4096;

    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AlpacaTools", "controller.json");

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed record Config(int Port, string Token);

    private readonly HttpListener _listener = new();
    private readonly Config _config;
    private readonly Func<IReadOnlyList<string>, Task<object>> _getState;
    private readonly Func<ControllerCommand, Task<string?>> _run;
    private readonly Func<string, Task<bool>> _approvePairing;
    private readonly SemaphoreSlim _pairingLock = new(1, 1);

    /// <param name="getState">Accounts and the requested symbols' prices, as a JSON-serializable object.</param>
    /// <param name="run">Runs a command; returns an error message, or null on success.</param>
    /// <param name="approvePairing">Asks the user whether the named client may control the dashboard.</param>
    public ControllerServer(Func<IReadOnlyList<string>, Task<object>> getState,
        Func<ControllerCommand, Task<string?>> run, Func<string, Task<bool>> approvePairing)
    {
        _getState = getState;
        _run = run;
        _approvePairing = approvePairing;
        _config = LoadOrCreateConfig();
        // "localhost" prefixes need no admin rights, and http.sys rejects any other Host header,
        // which also stops DNS-rebinding pages from reaching the API.
        _listener.Prefixes.Add($"http://localhost:{_config.Port}/");
    }

    /// <summary>Why the server isn't listening (e.g. the port is taken), or null while it is.</summary>
    public string? Error { get; private set; }

    public void Start()
    {
        try
        {
            _listener.Start();
            _ = AcceptLoopAsync();
        }
        catch (HttpListenerException ex)
        {
            Error = $"Controller API could not listen on port {_config.Port}: {ex.Message}";
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync().ConfigureAwait(false); }
            catch (Exception) when (!_listener.IsListening) { return; }
            catch (HttpListenerException) { continue; }
            _ = HandleAsync(context);
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;
        try
        {
            // Plugins run as local web pages, so they need CORS (and Chromium's private-network opt-in).
            response.Headers["Access-Control-Allow-Origin"] = "*";
            response.Headers["Access-Control-Allow-Headers"] = "Authorization, Content-Type";
            response.Headers["Access-Control-Allow-Methods"] = "GET, POST";
            response.Headers["Access-Control-Allow-Private-Network"] = "true";
            if (request.HttpMethod == "OPTIONS")
            {
                response.StatusCode = 204;
                return;
            }

            var path = request.Url?.AbsolutePath.TrimEnd('/') ?? "";
            if (path == "/v1/pair" && request.HttpMethod == "POST")
            {
                await HandlePairingAsync(request, response).ConfigureAwait(false);
                return;
            }
            if (!IsAuthorized(request))
            {
                await WriteAsync(response, 401, new { error = "Not paired with Alpaca Dashboard" }).ConfigureAwait(false);
                return;
            }

            switch (path, request.HttpMethod)
            {
                case ("/v1/state", "GET"):
                    var symbols = (request.QueryString["symbols"] ?? "")
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(s => s.ToUpperInvariant()).Distinct().Take(50).ToList();
                    await WriteAsync(response, 200, await _getState(symbols).ConfigureAwait(false)).ConfigureAwait(false);
                    break;
                case ("/v1/command", "POST"):
                    var command = await ReadAsync<ControllerCommand>(request).ConfigureAwait(false);
                    if (command is not { Action.Length: > 0 })
                    {
                        await WriteAsync(response, 400, new { error = "Expected {\"action\": \"...\", \"arg\": \"...\"}" }).ConfigureAwait(false);
                        break;
                    }
                    var error = await _run(command).ConfigureAwait(false);
                    await WriteAsync(response, error is null ? 200 : 422, new { ok = error is null, error }).ConfigureAwait(false);
                    break;
                default:
                    await WriteAsync(response, 404, new { error = "Unknown endpoint" }).ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception ex)
        {
            try { await WriteAsync(response, 500, new { error = ex.Message }).ConfigureAwait(false); }
            catch (Exception) { /* client went away */ }
        }
        finally
        {
            try { response.Close(); }
            catch (Exception) { /* client went away */ }
        }
    }

    private bool IsAuthorized(HttpListenerRequest request)
    {
        var header = request.Headers["Authorization"];
        if (header is null || !header.StartsWith("Bearer ", StringComparison.Ordinal)) return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(header["Bearer ".Length..]), Encoding.UTF8.GetBytes(_config.Token));
    }

    /// <summary>A plugin asks for the token; the user decides. One request at a time, so a page can't stack dialogs.</summary>
    private async Task HandlePairingAsync(HttpListenerRequest request, HttpListenerResponse response)
    {
        if (!await _pairingLock.WaitAsync(0).ConfigureAwait(false))
        {
            await WriteAsync(response, 429, new { error = "Another pairing request is waiting for an answer" }).ConfigureAwait(false);
            return;
        }
        try
        {
            var body = await ReadAsync<PairRequest>(request).ConfigureAwait(false);
            var client = body?.Client?.Trim() is { Length: > 0 } name ? name[..Math.Min(name.Length, 40)] : "A controller";
            if (await _approvePairing(client).ConfigureAwait(false))
                await WriteAsync(response, 200, new { token = _config.Token }).ConfigureAwait(false);
            else
                await WriteAsync(response, 403, new { error = "Pairing was declined in Alpaca Dashboard" }).ConfigureAwait(false);
        }
        finally
        {
            _pairingLock.Release();
        }
    }

    private sealed record PairRequest(string? Client);

    private static async Task<T?> ReadAsync<T>(HttpListenerRequest request)
    {
        if (request.ContentLength64 > MaxBodyBytes) return default;
        using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
        var text = await reader.ReadToEndAsync().ConfigureAwait(false);
        if (text.Length > MaxBodyBytes || text.Length == 0) return default;
        try { return JsonSerializer.Deserialize<T>(text, Json); }
        catch (JsonException) { return default; }
    }

    private static async Task WriteAsync(HttpListenerResponse response, int status, object body)
    {
        response.StatusCode = status;
        response.ContentType = "application/json; charset=utf-8";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body, Json);
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
    }

    // ----- Config (port + token), shared with the "--do" command line -----

    private static Config LoadOrCreateConfig()
    {
        if (TryLoadConfig() is { } existing) return existing;
        var config = new Config(DefaultPort, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(config, Json));
        return config;
    }

    private static Config? TryLoadConfig()
    {
        try
        {
            return File.Exists(ConfigPath) && JsonSerializer.Deserialize<Config>(File.ReadAllText(ConfigPath), Json) is
                { Port: > 0, Token.Length: >= 32 } config ? config : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Sends a command to the dashboard that is already running. Returns null if nothing is listening,
    /// otherwise the result: an empty string on success or the dashboard's error message.
    /// </summary>
    public static async Task<string?> SendToRunningAsync(ControllerCommand command)
    {
        if (TryLoadConfig() is not { } config) return null;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", config.Token);
        HttpResponseMessage response;
        try
        {
            response = await http.PostAsync($"http://localhost:{config.Port}/v1/command",
                new StringContent(JsonSerializer.Serialize(command, Json), Encoding.UTF8, "application/json")).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return null; // not running
        }
        catch (TaskCanceledException)
        {
            return "Alpaca Dashboard didn't respond";
        }
        if (response.IsSuccessStatusCode) return "";
        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            return doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() ?? "Command failed" : "Command failed";
        }
        catch (JsonException)
        {
            return $"Command failed ({(int)response.StatusCode})";
        }
    }

    public void Dispose()
    {
        try { _listener.Close(); }
        catch (ObjectDisposedException) { }
    }
}
