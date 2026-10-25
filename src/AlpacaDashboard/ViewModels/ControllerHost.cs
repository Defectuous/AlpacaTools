using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using AlpacaDashboard.Services;

namespace AlpacaDashboard.ViewModels;

/// <summary>
/// What a hardware controller can see and do: account totals and symbol prices for its keys, navigation,
/// and opening Buy/Sell windows. Nothing here places an order; the windows still need the user's confirmation.
/// </summary>
public sealed class ControllerHost : IDisposable
{
    // Symbols on keys that the account on screen doesn't hold: one snapshot request per asset type
    // every 15s, only while a key keeps asking for them.
    private static readonly TimeSpan WatchPollInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan WatchExpiry = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan WatchFirstFetchGap = TimeSpan.FromSeconds(3);

    private readonly MainViewModel _main;
    private readonly MainWindow _window;
    private readonly ControllerServer _server;
    private readonly DispatcherTimer _watchTimer;
    private readonly Dictionary<string, DateTime> _watchAsked = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (double Price, double? ChangePct)> _watchPrices = new(StringComparer.Ordinal);
    private DateTime _watchFetched = DateTime.MinValue;
    private bool _watchFetching;
    private WindowState _restoreState;

    public ControllerHost(MainViewModel main, MainWindow window)
    {
        _main = main;
        _window = window;
        _restoreState = window.WindowState;
        window.StateChanged += (_, _) => { if (window.WindowState != WindowState.Minimized) _restoreState = window.WindowState; };

        _watchTimer = new DispatcherTimer { Interval = WatchPollInterval };
        _watchTimer.Tick += async (_, _) => await PollWatchedAsync();
        _watchTimer.Start();

        _server = new ControllerServer(
            symbols => OnUi(() => GetState(symbols)),
            command => OnUi(() => RunAsync(command)),
            client => OnUi(() => ApprovePairing(client)));
        _server.Start();
    }

    public string? Error => _server.Error;

    private static Task<T> OnUi<T>(Func<T> work) => Application.Current.Dispatcher.InvokeAsync(work).Task;
    private static Task<T> OnUi<T>(Func<Task<T>> work) => Application.Current.Dispatcher.InvokeAsync(work).Task.Unwrap();

    // ----- State for the keys -----

    private object GetState(IReadOnlyList<string> symbols)
    {
        var selected = _main.SelectedAccount;
        var now = DateTime.Now;
        foreach (var symbol in symbols) _watchAsked[symbol] = now;
        if (symbols.Any(s => selected?.FindPosition(s) is null && !_watchPrices.ContainsKey(s)) &&
            now - _watchFetched >= WatchFirstFetchGap)
            _ = PollWatchedAsync(); // a key just appeared; don't make it wait for the timer

        return new
        {
            selectedAccount = selected?.Profile.Id,
            tab = _main.SelectedTab.ToString().ToLowerInvariant(),
            range = selected is null ? null : RangeLabel(selected.SelectedRange),
            accounts = _main.Accounts.Select((a, i) => new
            {
                id = a.Profile.Id,
                number = i + 1,
                name = a.Name,
                mode = a.ModeLabel,
                selected = a == selected,
                equity = a.LiveEquity,
                dayChange = a.LiveDayChange,
                dayChangePct = a.LiveDayChangePct,
                // Positions are loaded only for the account on screen.
                unrealizedPl = a == selected && a.HasAccount ? a.TotalUnrealizedPl : (decimal?)null,
                unrealizedPlPct = a == selected && a.HasAccount ? a.TotalUnrealizedPlPct : (decimal?)null,
                error = a.Error,
            }),
            symbols = symbols.ToDictionary(s => s, s => SymbolState(selected, s)),
        };
    }

    private object? SymbolState(AccountViewModel? account, string symbol)
    {
        if (account?.FindPosition(symbol) is { } held)
            return new
            {
                price = held.CurrentPrice,
                changePct = held.ChangeToday,
                held = true,
                qty = held.SignedQty,
                unrealizedPl = held.UnrealizedPl,
                unrealizedPlPct = held.UnrealizedPlPct,
            };
        return _watchPrices.TryGetValue(symbol, out var watched)
            ? new { price = (decimal)watched.Price, changePct = (decimal?)watched.ChangePct, held = false }
            : null;
    }

    private async Task PollWatchedAsync()
    {
        if (_watchFetching || _main.SelectedAccount is not { } account) return;
        var cutoff = DateTime.Now - WatchExpiry;
        foreach (var stale in _watchAsked.Where(w => w.Value < cutoff).Select(w => w.Key).ToList())
        {
            _watchAsked.Remove(stale);
            _watchPrices.Remove(stale);
        }
        var wanted = _watchAsked.Keys.Where(s => account.FindPosition(s) is null).ToList();
        if (wanted.Count == 0) return;

        _watchFetching = true;
        _watchFetched = DateTime.Now;
        try
        {
            var stocks = wanted.Where(s => !s.Contains('/')).ToList();
            var crypto = wanted.Where(s => s.Contains('/')).ToList();
            var snapshots = stocks.Count > 0
                ? await account.MarketData.GetSnapshotsAsync(stocks, account.DataFeed)
                : new Dictionary<string, Models.StockSnapshot>(StringComparer.Ordinal);
            if (crypto.Count > 0)
                foreach (var (pair, snap) in await account.MarketData.GetCryptoSnapshotsAsync(crypto))
                    snapshots[pair] = snap;

            foreach (var (symbol, snap) in snapshots)
            {
                var price = snap.LatestQuote is { BidPrice: > 0, AskPrice: > 0 } q && symbol.Contains('/')
                    ? (q.BidPrice + q.AskPrice) / 2
                    : snap.LatestTrade?.Price ?? snap.DailyBar?.Close ?? 0;
                if (price <= 0) continue;
                var prevClose = snap.PrevDailyBar?.Close;
                _watchPrices[symbol] = (price, prevClose is > 0 ? price / prevClose - 1 : null);
            }
        }
        catch (Exception)
        {
            // Keys keep their last price; the next poll retries.
        }
        finally
        {
            _watchFetching = false;
        }
    }

    // ----- Commands -----

    /// <summary>Runs a controller command; returns an error message for the key, or null.</summary>
    public async Task<string?> RunAsync(ControllerCommand command)
    {
        var arg = command.Arg?.Trim();
        var account = _main.SelectedAccount;
        switch (command.Action.Trim().ToLowerInvariant())
        {
            case "show":
                BringToFront();
                return null;
            case "toggle":
                if (_window.IsActive && _window.WindowState != WindowState.Minimized) _window.WindowState = WindowState.Minimized;
                else BringToFront();
                return null;

            case "account.next":
            case "account.prev":
                if (_main.Accounts.Count == 0) return "No accounts yet";
                var step = command.Action.EndsWith("next", StringComparison.OrdinalIgnoreCase) ? 1 : -1;
                var index = account is null ? 0 : _main.Accounts.IndexOf(account) + step;
                _main.SelectedAccount = _main.Accounts[(index % _main.Accounts.Count + _main.Accounts.Count) % _main.Accounts.Count];
                return null;
            case "account":
                if (FindAccount(arg) is not { } match) return $"No account \"{arg}\"";
                _main.SelectedAccount = match;
                return null;

            case "tab":
                if (!Enum.TryParse<MainTab>(arg, ignoreCase: true, out var tab)) return "Tab is portfolio or screener";
                _main.SetTabCommand.Execute(tab);
                return null;
            case "range":
                if (account is null) return "No account selected";
                if (ParseRange(arg) is not { } range) return "Range is 1D, 1W, 1M, 3M, 1Y or All";
                await account.SetRangeCommand.ExecuteAsync(range);
                return null;
            case "refresh":
                if (account is null) return "No account selected";
                await account.RefreshAllAsync();
                return account.Error;

            case "search":
                if (account is null) return "No account selected";
                BringToFront();
                _window.FocusSearch(arg);
                return null;
            case "chart":
                if (account is null) return "No account selected";
                if (string.IsNullOrEmpty(arg)) return "Which symbol?";
                BringToFront();
                _main.ShowSymbol(Normalize(arg), IsCrypto(arg));
                return null;
            case "buy":
                if (account is null) return "No account selected";
                if (string.IsNullOrEmpty(arg)) return "Which symbol?";
                var symbol = Normalize(arg);
                var knownPrice = account.FindPosition(symbol)?.CurrentPrice
                                 ?? (_watchPrices.TryGetValue(symbol, out var w) ? (decimal)w.Price : 0);
                BringToFront();
                account.OpenBuyWindow(symbol, IsCrypto(symbol), knownPrice, "from controller");
                return null;
            case "sell":
            case "close":
                if (account is null) return "No account selected";
                if (string.IsNullOrEmpty(arg)) return "Which symbol?";
                if (account.FindPosition(Normalize(arg)) is not { } position) return $"{Normalize(arg)} isn't held in {account.Name}";
                BringToFront();
                account.OpenTradeWindow(position, command.Action.Equals("close", StringComparison.OrdinalIgnoreCase)
                    ? TradeMode.Liquidate : TradeMode.SellQty);
                return null;

            default:
                return $"Unknown action \"{command.Action}\"";
        }
    }

    /// <summary>By name (case-insensitive) or by its 1-based place in the sidebar.</summary>
    private AccountViewModel? FindAccount(string? nameOrNumber)
    {
        if (string.IsNullOrEmpty(nameOrNumber)) return null;
        if (int.TryParse(nameOrNumber, out var n) && n >= 1 && n <= _main.Accounts.Count) return _main.Accounts[n - 1];
        return _main.Accounts.FirstOrDefault(a => string.Equals(a.Name, nameOrNumber, StringComparison.OrdinalIgnoreCase))
               ?? (Guid.TryParse(nameOrNumber, out var id) ? _main.Accounts.FirstOrDefault(a => a.Profile.Id == id) : null);
    }

    private static string Normalize(string symbol) => symbol.Trim().ToUpperInvariant();
    private static bool IsCrypto(string symbol) => symbol.Contains('/');

    private static HistoryRange? ParseRange(string? text) => text?.ToUpperInvariant() switch
    {
        "1D" => HistoryRange.Day,
        "1W" => HistoryRange.Week,
        "1M" => HistoryRange.Month,
        "3M" => HistoryRange.ThreeMonths,
        "1Y" => HistoryRange.Year,
        "ALL" => HistoryRange.All,
        _ => null,
    };

    private static string RangeLabel(HistoryRange range) => range switch
    {
        HistoryRange.Day => "1D",
        HistoryRange.Week => "1W",
        HistoryRange.Month => "1M",
        HistoryRange.ThreeMonths => "3M",
        HistoryRange.Year => "1Y",
        _ => "All",
    };

    private bool ApprovePairing(string client)
    {
        BringToFront();
        return MessageBox.Show(_window,
            $"\"{client}\" wants to connect to Alpaca Dashboard.\n\n" +
            "It will be able to show your account totals and prices on its keys, switch accounts and views, " +
            "and open Buy and Sell windows. It can't place an order: you still confirm every order here.\n\nAllow it?",
            "Connect a controller", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    }

    // ----- Window -----

    /// <summary>
    /// Restores and focuses the dashboard. Windows only lets the foreground app hand out focus, and a key press
    /// goes to the controller's software, not us; a synthetic Alt tap is the usual way to be allowed.
    /// </summary>
    public void BringToFront()
    {
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = _restoreState;
        _window.Show();
        _window.Activate();
        var hwnd = new WindowInteropHelper(_window).Handle;
        if (GetForegroundWindow() == hwnd) return;
        keybd_event(VkMenu, 0, 0, 0);
        keybd_event(VkMenu, 0, KeyEventfKeyUp, 0);
        SetForegroundWindow(hwnd);
    }

    private const byte VkMenu = 0x12;
    private const uint KeyEventfKeyUp = 0x0002;

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, nint dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hWnd);

    public void Dispose()
    {
        _watchTimer.Stop();
        _server.Dispose();
    }
}
