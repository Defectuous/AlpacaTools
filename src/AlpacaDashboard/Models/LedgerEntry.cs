namespace AlpacaDashboard.Models;

// The app's own record of account history, independent of any broker's API shapes. Each broker's sync maps its
// activities to these; performance, lots and income are computed from the ledger rather than from broker DTOs.

public enum LedgerKind
{
    Trade,
    Dividend,        // includes withholding and fees charged against dividends (negative amounts)
    Interest,
    Fee,
    Deposit,
    Withdrawal,
    Transfer,        // cash or securities moved between accounts (journals, ACATS)
    CorporateAction, // splits, mergers, spin-offs, symbol changes; option assignment, exercise and expiry
    Other,
}

public sealed class LedgerEntry
{
    /// <summary>The dashboard's account profile this entry belongs to.</summary>
    public Guid AccountId { get; init; }

    /// <summary>Which broker it came from, e.g. "alpaca".</summary>
    public string Source { get; init; } = "";

    /// <summary>The broker's id for the activity; unique per account and source.</summary>
    public string ExternalId { get; init; } = "";

    public LedgerKind Kind { get; init; }

    /// <summary>When it happened (UTC). Date-only activities fall at midnight UTC of their date.</summary>
    public DateTimeOffset OccurredAt { get; init; }

    /// <summary>The US market date it belongs to (New York time for trades).</summary>
    public DateOnly Date { get; init; }

    public string? Symbol { get; init; }

    /// <summary>"buy" or "sell" for trades; null otherwise.</summary>
    public string? Side { get; init; }

    /// <summary>Shares or units. Positive for trades (see <see cref="Side"/>); as reported by the broker otherwise.</summary>
    public decimal? Quantity { get; init; }

    public decimal? Price { get; init; }

    /// <summary>Cash effect on the account: positive in, negative out.</summary>
    public decimal? Amount { get; init; }

    public string? OrderId { get; init; }
    public string? Description { get; init; }

    /// <summary>The broker's own activity type, e.g. "FILL" or "DIVNRA".</summary>
    public string RawType { get; init; } = "";

    /// <summary>The activity exactly as the broker sent it, so entries can be re-mapped later without refetching.</summary>
    public string RawJson { get; init; } = "";
}

/// <summary>Per account and source: how far the history download has got.</summary>
public sealed class LedgerSyncState
{
    public Guid AccountId { get; init; }
    public string Source { get; init; } = "";

    /// <summary>The broker's account id the entries came from; a different one means the keys now point elsewhere.</summary>
    public string? ExternalAccountId { get; set; }

    /// <summary>Where the backward walk through history resumes (the oldest activity id fetched so far).</summary>
    public string? BackfillCursor { get; set; }

    public bool BackfillDone { get; set; }
    public DateTimeOffset? LastSyncedAt { get; set; }

    /// <summary>True once anything has been stored, so newer activity can be found by walking back to a known id.</summary>
    public bool HasStarted => BackfillCursor is not null || BackfillDone;
}

public sealed record LedgerSummary(int Count, DateOnly? Oldest, DateOnly? Newest);
