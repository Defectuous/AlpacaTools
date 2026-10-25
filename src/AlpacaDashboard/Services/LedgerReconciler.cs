using AlpacaDashboard.Models;

namespace AlpacaDashboard.Services;

/// <summary>
/// Checks the ledger against the broker: for each stock, shares bought minus shares sold should equal the position the
/// broker reports. Symbols whose share count also moved outside trades (splits, mergers, share transfers) are skipped,
/// as is crypto, where fees are taken in the coin.
/// </summary>
public static class LedgerReconciler
{
    /// <param name="stockPositions">Broker-reported stock positions: symbol to signed quantity (shorts negative).</param>
    public static ReconcileResult Check(IEnumerable<LedgerEntry> entries, IReadOnlyDictionary<string, decimal> stockPositions)
    {
        var net = new Dictionary<string, decimal>(StringComparer.Ordinal);
        var skipped = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in entries)
        {
            if (e.Symbol is not { Length: > 0 } symbol) continue;
            if (symbol.Contains('/')) { skipped.Add(symbol); continue; } // crypto
            if (e.Kind == LedgerKind.Trade && e.Quantity is { } qty)
                net[symbol] = net.GetValueOrDefault(symbol) + (e.Side == "sell" ? -qty : qty);
            else if (e.Kind is LedgerKind.CorporateAction or LedgerKind.Transfer && e.Quantity is not (null or 0))
                skipped.Add(symbol);
        }

        var mismatches = new List<string>();
        var checkedCount = 0;
        foreach (var symbol in net.Keys.Union(stockPositions.Keys).Where(s => !skipped.Contains(s)).Order(StringComparer.Ordinal))
        {
            var ledgerQty = net.GetValueOrDefault(symbol);
            var brokerQty = stockPositions.GetValueOrDefault(symbol);
            if (ledgerQty == 0 && brokerQty == 0) continue; // opened and closed again: nothing to compare
            checkedCount++;
            if (ledgerQty != brokerQty) mismatches.Add($"{symbol}: ledger {ledgerQty:0.#########}, broker {brokerQty:0.#########}");
        }
        return new ReconcileResult(checkedCount, mismatches, skipped.Count(s => !s.Contains('/')));
    }
}

/// <param name="Checked">Stocks with a non-zero position on either side that were compared.</param>
/// <param name="Mismatches">One line per stock whose ledger quantity differs from the broker's.</param>
/// <param name="SkippedStocks">Stocks not compared because shares also moved through corporate actions or transfers.</param>
public sealed record ReconcileResult(int Checked, IReadOnlyList<string> Mismatches, int SkippedStocks);
