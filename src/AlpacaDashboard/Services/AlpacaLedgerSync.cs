using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using AlpacaDashboard.Models;

namespace AlpacaDashboard.Services;

/// <summary>
/// Copies one Alpaca account's activity history into the ledger and keeps it current.
/// <list type="bullet">
/// <item><b>Backfill</b>: walks back from the newest activity one page at a time with a pause between pages, saving
/// the cursor with each page, so a long history downloads in the background and resumes after a restart.</item>
/// <item><b>Head</b>: once anything is stored, newer activity is found by walking back from the newest until a page
/// holds an id already stored. Usually that is the page the dashboard just fetched for its activity list, so keeping
/// current costs no extra requests.</item>
/// </list>
/// Activity ids start with their timestamp, and Alpaca lists newest first by id, so everything past a known id is known.
/// </summary>
public sealed class AlpacaLedgerSync(LedgerStore store, Guid accountId)
{
    public const string Source = "alpaca";
    public const int PageSize = 100; // Alpaca's maximum

    private static readonly JsonSerializerOptions RawJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");

    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>
    /// Brings the ledger up to date, finishing any backfill with <paramref name="backfillGap"/> between pages.
    /// <paramref name="newestPage"/> is a page the caller already fetched with <c>GetActivitiesPageAsync(PageSize, null)</c>,
    /// or null to fetch it here. <paramref name="progress"/> hears after each backfill page.
    /// Returns null if a sync was already running.
    /// </summary>
    public async Task<LedgerSyncResult?> SyncAsync(AlpacaClient client, string alpacaAccountId,
        IReadOnlyList<AlpacaActivity>? newestPage, TimeSpan backfillGap,
        IProgress<LedgerSyncResult>? progress = null, CancellationToken ct = default)
    {
        if (!await _lock.WaitAsync(0, ct).ConfigureAwait(false)) return null;
        try
        {
            var state = await Task.Run(() => store.GetSyncState(accountId, Source), ct).ConfigureAwait(false);
            if (state.ExternalAccountId is { } previous && previous != alpacaAccountId)
            {
                // The keys now reach a different Alpaca account (new keys, or a paper account reset): start over.
                await Task.Run(() => store.DeleteAccount(accountId, Source), ct).ConfigureAwait(false);
                state = new LedgerSyncState { AccountId = accountId, Source = Source };
            }
            state.ExternalAccountId = alpacaAccountId;

            var added = 0;
            if (state.HasStarted) added += await SyncHeadAsync(client, state, newestPage, ct).ConfigureAwait(false);

            while (!state.BackfillDone)
            {
                var activities = await client.GetActivitiesPageAsync(PageSize, state.BackfillCursor, ct).ConfigureAwait(false);
                if (activities.Count > 0) state.BackfillCursor = activities[^1].Id;
                if (activities.Count < PageSize) state.BackfillDone = true;
                state.LastSyncedAt = DateTimeOffset.UtcNow;
                var entries = activities.Select(Map).ToList();
                added += await Task.Run(() => store.Save(entries, state), ct).ConfigureAwait(false);
                progress?.Report(new LedgerSyncResult(added, state.BackfillDone));
                if (!state.BackfillDone) await Task.Delay(backfillGap, ct).ConfigureAwait(false);
            }

            state.LastSyncedAt = DateTimeOffset.UtcNow;
            await Task.Run(() => store.Save([], state), ct).ConfigureAwait(false);
            return new LedgerSyncResult(added, state.BackfillDone);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Newest activity down to the first stored id, saved in one transaction so an interrupted walk leaves no gap.</summary>
    private async Task<int> SyncHeadAsync(AlpacaClient client, LedgerSyncState state,
        IReadOnlyList<AlpacaActivity>? newestPage, CancellationToken ct)
    {
        var fresh = new List<AlpacaActivity>();
        var page = newestPage ?? await client.GetActivitiesPageAsync(PageSize, null, ct).ConfigureAwait(false);
        while (true)
        {
            var ids = page.Select(a => a.Id).ToList();
            var known = await Task.Run(() => store.FindExisting(accountId, Source, ids), ct).ConfigureAwait(false);
            fresh.AddRange(page.Where(a => !known.Contains(a.Id)));
            if (known.Count > 0 || page.Count < PageSize) break;
            page = await client.GetActivitiesPageAsync(PageSize, page[^1].Id, ct).ConfigureAwait(false);
        }
        if (fresh.Count == 0) return 0;
        var entries = fresh.Select(Map).ToList();
        return await Task.Run(() => store.Save(entries, state), ct).ConfigureAwait(false);
    }

    private LedgerEntry Map(AlpacaActivity a)
    {
        var isFill = a.ActivityType == "FILL";
        var occurred = a.TransactionTime
                       ?? (DateOnly.TryParse(a.Date, CultureInfo.InvariantCulture, out var d)
                           ? new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
                           : DateTimeOffset.UnixEpoch);
        var date = a.TransactionTime is { } t
            ? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(t, NewYork).DateTime)
            : DateOnly.FromDateTime(occurred.UtcDateTime);

        return new LedgerEntry
        {
            AccountId = accountId,
            Source = Source,
            ExternalId = a.Id,
            Kind = KindOf(a),
            OccurredAt = occurred,
            Date = date,
            Symbol = a.Symbol,
            Side = isFill ? a.Side : null,
            Quantity = isFill && a.Qty is { } q ? Math.Abs(q) : a.Qty,
            Price = a.Price,
            Amount = isFill
                ? a.Qty is { } qty && a.Price is { } price ? Math.Abs(qty) * price * (a.Side == "buy" ? -1 : 1) : null
                : a.NetAmount,
            OrderId = a.OrderId,
            Description = a.Description,
            RawType = a.ActivityType,
            RawJson = JsonSerializer.Serialize(a, RawJsonOptions),
        };
    }

    /// <summary>Alpaca activity types: https://docs.alpaca.markets/docs/account-activities</summary>
    private static LedgerKind KindOf(AlpacaActivity a) => a.ActivityType switch
    {
        "FILL" => LedgerKind.Trade,
        var t when t.StartsWith("DIV", StringComparison.Ordinal) => LedgerKind.Dividend,
        var t when t.StartsWith("INT", StringComparison.Ordinal) => LedgerKind.Interest,
        "FEE" or "CFEE" or "PTC" => LedgerKind.Fee,
        "CSD" => LedgerKind.Deposit,
        "CSW" => LedgerKind.Withdrawal,
        "TRANS" => a.NetAmount >= 0 ? LedgerKind.Deposit : LedgerKind.Withdrawal,
        "JNL" or "JNLC" or "JNLS" or "ACATC" or "ACATS" => LedgerKind.Transfer,
        "SPLIT" or "MA" or "NC" or "REORG" or "SC" or "SSO" or "SSP" or "OPASN" or "OPEXP" or "OPXRC" => LedgerKind.CorporateAction,
        _ => LedgerKind.Other,
    };
}

public sealed record LedgerSyncResult(int Added, bool HistoryComplete);
