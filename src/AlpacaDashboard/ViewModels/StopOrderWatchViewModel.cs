using System.Collections.ObjectModel;
using AlpacaDashboard.Models;
using AlpacaDashboard.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AlpacaDashboard.ViewModels;

/// <summary>
/// Automatic alerts for every open stop-limit sell order, in every account. One row per order steps through
/// near the stop (within 1%) → stop hit → below the limit (may not fill) → filled, notifying once per step and
/// skipping to the most serious one when the price jumps. Open orders are fetched once a minute per account, and at
/// once when Alpaca pushes an order event; prices come from the Alerts tab's checks.
/// </summary>
public sealed partial class StopOrderWatchViewModel : ObservableObject
{
    public const decimal NearStopFraction = 0.01m;
    private static readonly TimeSpan OrdersInterval = TimeSpan.FromSeconds(60);
    private static readonly HashSet<string> ClosedStatuses = ["canceled", "expired", "rejected", "replaced", "done_for_day"];

    private readonly Func<IReadOnlyCollection<AccountViewModel>> _accounts;
    private readonly AlertNotifier _notifier;
    private readonly Action _fired;
    private readonly Dictionary<Guid, DateTime> _ordersFetched = [];
    private readonly Dictionary<string, StopOrderState> _states;
    private bool _refreshing;

    /// <param name="fired">Called after each notification (the Alerts tab counts it as unseen).</param>
    public StopOrderWatchViewModel(Func<IReadOnlyCollection<AccountViewModel>> accounts, AlertNotifier notifier, Action fired)
    {
        _accounts = accounts;
        _notifier = notifier;
        _fired = fired;
        var settings = OrderWatchStore.Load();
        _isEnabled = settings.Enabled;
        _states = settings.Orders;
    }

    public ObservableCollection<StopOrderRowViewModel> Orders { get; } = [];

    /// <summary>Watch stop-limit sells automatically; off clears the list and stops the order requests.</summary>
    [ObservableProperty] private bool _isEnabled;

    partial void OnIsEnabledChanged(bool value)
    {
        if (!value) Orders.Clear();
        _ordersFetched.Clear();
        Save();
        if (value) _ = RefreshAsync();
    }

    public IEnumerable<(string Symbol, bool IsCrypto)> Symbols => Orders.Select(o => (o.Symbol, o.IsCrypto)).Distinct();

    /// <summary>Order event from an account's trade stream: a fill shows up now instead of within the minute.</summary>
    public void OnTradeEvent(AccountViewModel account, string evt, string symbol)
    {
        if (!IsEnabled || evt is not ("fill" or "canceled" or "expired" or "replaced" or "new")) return;
        _ordersFetched.Remove(account.Profile.Id);
        _ = RefreshAsync();
    }

    /// <summary>Picks up new stop-limit sells and settles ones no longer open (fill, cancel), at most once a minute per account.</summary>
    public async Task RefreshAsync()
    {
        if (!IsEnabled || _refreshing) return;
        _refreshing = true;
        try
        {
            var accounts = _accounts().ToList();
            foreach (var row in Orders.Where(o => accounts.All(a => a.Profile.Id != o.AccountId)).ToList())
                Orders.Remove(row); // account removed

            foreach (var account in accounts)
            {
                if (_ordersFetched.TryGetValue(account.Profile.Id, out var at) && DateTime.Now - at < OrdersInterval) continue;
                try
                {
                    await RefreshAccountAsync(account);
                    _ordersFetched[account.Profile.Id] = DateTime.Now;
                }
                catch (Exception)
                {
                    // Retried on the next check; rows keep their last known state.
                }
            }
            Save();
        }
        finally
        {
            _refreshing = false;
        }
    }

    private async Task RefreshAccountAsync(AccountViewModel account)
    {
        var open = await account.Trading.GetOpenOrdersAsync();
        var stops = open.Where(o => o is { Type: "stop_limit", Side: "sell", StopPrice: not null, LimitPrice: not null })
                        .ToDictionary(o => o.Id);

        List<AlpacaPosition>? positions = null;
        foreach (var order in stops.Values)
        {
            if (Orders.FirstOrDefault(r => r.OrderId == order.Id) is { } existing)
            {
                existing.Order = order;
                continue;
            }
            var state = _states.GetValueOrDefault(order.Id);
            if (state?.EntryPrice is null)
            {
                // Average entry of the position being protected, for the P/L when it fills.
                var entry = account.IsActive
                    ? account.FindPosition(order.Symbol)?.AvgEntryPrice
                    : (positions ??= await account.Trading.GetPositionsAsync())
                        .Select(p => new PositionRowViewModel(p, account.CryptoTier))
                        .FirstOrDefault(p => p.DataSymbol == order.Symbol)?.AvgEntryPrice;
                state = (state ?? new StopOrderState()) with { EntryPrice = entry };
                _states[order.Id] = state;
            }
            Orders.Add(new StopOrderRowViewModel(order, account.Profile.Id, account.Name, state.EntryPrice) { Notified = state.Notified });
        }

        // Rows whose order is no longer open: find out why.
        foreach (var row in Orders.Where(r => r.AccountId == account.Profile.Id && !stops.ContainsKey(r.OrderId) && r.Stage != StopOrderStage.Filled).ToList())
        {
            var final = await account.Trading.GetOrderAsync(row.OrderId);
            row.Order = final;
            if (final.Status == "filled")
            {
                row.Stage = StopOrderStage.Filled;
                Notify(row, StopOrderStage.Filled);
            }
            else if (ClosedStatuses.Contains(final.Status))
            {
                Orders.Remove(row);
                _states.Remove(row.OrderId);
            }
        }
    }

    /// <summary>A price for a symbol: moves each of its orders' rows to the stage that price puts them in.</summary>
    public void OnPrice(string symbol, decimal price)
    {
        foreach (var row in Orders)
        {
            if (row.Symbol != symbol || row.Stage == StopOrderStage.Filled) continue;
            row.CurrentPrice = price;
            row.Stage = StageFor(price, row.Stop, row.Limit);
            if (row.Stage > row.Notified)
            {
                Notify(row, row.Stage);
                Save();
            }
        }
    }

    /// <summary>Where a price puts an open stop-limit sell (the stop sits above the limit).</summary>
    public static StopOrderStage StageFor(decimal price, decimal stop, decimal limit) =>
        price < limit ? StopOrderStage.BelowLimit
        : price <= stop ? StopOrderStage.StopHit
        : price <= stop * (1 + NearStopFraction) ? StopOrderStage.NearStop
        : StopOrderStage.Watching;

    private void Notify(StopOrderRowViewModel row, StopOrderStage stage)
    {
        if (stage <= row.Notified) return;
        row.Notified = stage;
        _states[row.OrderId] = (_states.GetValueOrDefault(row.OrderId) ?? new StopOrderState()) with { Notified = stage };

        var price = row.CurrentPrice is { } p ? AlertRowViewModel.FormatPrice(p) : "—";
        var stop = AlertRowViewModel.FormatPrice(row.Stop);
        var limit = AlertRowViewModel.FormatPrice(row.Limit);
        var order = $"Stop-limit sell {row.QtyText} in {row.AccountName}";
        var (title, text) = stage switch
        {
            StopOrderStage.NearStop => ($"{row.Symbol} is near its stop",
                $"Now {price}, {row.ToStopText} above the {stop} stop.\n{order}, limit {limit}."),
            StopOrderStage.StopHit => ($"{row.Symbol} hit its stop",
                $"Now {price}. The order is now a limit sell at {limit} or better.\n{order}."),
            StopOrderStage.BelowLimit => ($"{row.Symbol} fell below its limit",
                $"Now {price}, under the {limit} limit, so the order may not fill and you may still hold it.\n{order}."),
            _ => ($"{row.Symbol} stop-limit filled",
                $"Sold {row.FilledQtyText} at {row.FillPriceText}" + (row.RealizedPlText is { } pl ? $" · P/L {pl} before fees" : "") +
                $"\n{row.AccountName}"),
        };
        _notifier.Show(title, text);
        _fired();
    }

    private void Save() => OrderWatchStore.Save(new OrderWatchSettings
    {
        Enabled = IsEnabled,
        // Keep state only for orders still listed, so the file doesn't grow forever.
        Orders = _states.Where(s => Orders.Any(o => o.OrderId == s.Key)).ToDictionary(),
    });
}

/// <summary>One watched stop-limit sell order.</summary>
public sealed partial class StopOrderRowViewModel(AlpacaOrder order, Guid accountId, string accountName, decimal? entryPrice) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QtyText), nameof(FilledQtyText), nameof(FillPriceText), nameof(RealizedPlText), nameof(StatusText))]
    private AlpacaOrder _order = order;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(PriceText), nameof(ToStopText), nameof(StatusText))]
    private decimal? _currentPrice;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(StatusText))] private StopOrderStage _stage;

    /// <summary>The furthest stage already notified.</summary>
    public StopOrderStage Notified { get; set; }

    public string OrderId => Order.Id;
    public Guid AccountId { get; } = accountId;
    public string AccountName { get; } = accountName;
    public decimal? EntryPrice { get; } = entryPrice;
    public string Symbol => Order.Symbol;
    public bool IsCrypto => Order.Symbol.Contains('/');
    public decimal Stop => Order.StopPrice ?? 0;
    public decimal Limit => Order.LimitPrice ?? 0;

    public string QtyText => (Order.Qty ?? 0).ToString("0.########");
    public string StopText => AlertRowViewModel.FormatPrice(Stop);
    public string LimitText => AlertRowViewModel.FormatPrice(Limit);
    public string PriceText => CurrentPrice is { } p ? AlertRowViewModel.FormatPrice(p) : "—";

    /// <summary>How far the price is above the stop; empty once at or below it.</summary>
    public string ToStopText => CurrentPrice is > 0 and { } p && Stop > 0 && p > Stop ? $"{(p - Stop) / p:P2}" : "";

    public string FilledQtyText => Order.FilledQty.ToString("0.########");
    public string FillPriceText => Order.FilledAvgPrice is { } f ? AlertRowViewModel.FormatPrice(f) : "—";

    public string? RealizedPlText => Order.FilledAvgPrice is { } fill && EntryPrice is > 0 and { } entry
        ? $"{AlertRowViewModel.SignedMoney((fill - entry) * Order.FilledQty)} ({(fill >= entry ? "+" : "−")}{Math.Abs(fill / entry - 1):P2})"
        : null;

    public string StatusText => Stage switch
    {
        StopOrderStage.NearStop => $"Within {StopOrderWatchViewModel.NearStopFraction:P0} of the stop",
        StopOrderStage.StopHit => "Stop hit · selling at the limit or better",
        StopOrderStage.BelowLimit => "Below the limit · may not fill",
        StopOrderStage.Filled => $"Filled at {FillPriceText}" + (RealizedPlText is { } pl ? $" · P/L {pl}" : ""),
        _ => "Watching",
    };
}
