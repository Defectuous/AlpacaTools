using System.Globalization;
using System.IO;
using AlpacaDashboard.Models;
using Microsoft.Data.Sqlite;

namespace AlpacaDashboard.Services;

/// <summary>
/// The local ledger: every account's activity history in %APPDATA%\AlpacaTools\ledger.db (SQLite).
/// Decimals are stored as invariant text so amounts and fractional quantities stay exact.
/// Calls are synchronous and short; callers off the UI thread wrap them in Task.Run.
/// </summary>
public sealed class LedgerStore
{
    private static readonly Lazy<LedgerStore> DefaultStore = new(() => new LedgerStore(
        Environment.GetEnvironmentVariable("ALPACA_LEDGER_PATH_OVERRIDE")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AlpacaTools", "ledger.db")));

    public static LedgerStore Default => DefaultStore.Value;

    private readonly string _connectionString;

    public LedgerStore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = true }.ToString();
        using var connection = Open();
        Execute(connection, """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS ledger_entries (
                account_id   TEXT NOT NULL,
                source       TEXT NOT NULL,
                external_id  TEXT NOT NULL,
                kind         TEXT NOT NULL,
                occurred_at  TEXT NOT NULL,
                date         TEXT NOT NULL,
                symbol       TEXT,
                side         TEXT,
                quantity     TEXT,
                price        TEXT,
                amount       TEXT,
                order_id     TEXT,
                description  TEXT,
                raw_type     TEXT NOT NULL,
                raw_json     TEXT NOT NULL,
                PRIMARY KEY (account_id, source, external_id)
            );
            CREATE INDEX IF NOT EXISTS ix_ledger_account_date ON ledger_entries (account_id, date);
            CREATE INDEX IF NOT EXISTS ix_ledger_account_symbol ON ledger_entries (account_id, symbol);
            CREATE TABLE IF NOT EXISTS ledger_sync_state (
                account_id          TEXT NOT NULL,
                source              TEXT NOT NULL,
                external_account_id TEXT,
                backfill_cursor     TEXT,
                backfill_done       INTEGER NOT NULL DEFAULT 0,
                last_synced_at      TEXT,
                PRIMARY KEY (account_id, source)
            );
            """);
    }

    public LedgerSyncState GetSyncState(Guid accountId, string source)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT external_account_id, backfill_cursor, backfill_done, last_synced_at
            FROM ledger_sync_state WHERE account_id = $account AND source = $source
            """;
        cmd.Parameters.AddWithValue("$account", accountId.ToString());
        cmd.Parameters.AddWithValue("$source", source);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return new LedgerSyncState { AccountId = accountId, Source = source };
        return new LedgerSyncState
        {
            AccountId = accountId,
            Source = source,
            ExternalAccountId = reader.IsDBNull(0) ? null : reader.GetString(0),
            BackfillCursor = reader.IsDBNull(1) ? null : reader.GetString(1),
            BackfillDone = reader.GetInt64(2) != 0,
            LastSyncedAt = reader.IsDBNull(3) ? null : DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture),
        };
    }

    /// <summary>Which of <paramref name="externalIds"/> are already stored.</summary>
    public HashSet<string> FindExisting(Guid accountId, string source, IReadOnlyCollection<string> externalIds)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        if (externalIds.Count == 0) return found;
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        var names = externalIds.Select((_, i) => $"$id{i}").ToList();
        cmd.CommandText = $"""
            SELECT external_id FROM ledger_entries
            WHERE account_id = $account AND source = $source AND external_id IN ({string.Join(",", names)})
            """;
        cmd.Parameters.AddWithValue("$account", accountId.ToString());
        cmd.Parameters.AddWithValue("$source", source);
        foreach (var (name, id) in names.Zip(externalIds)) cmd.Parameters.AddWithValue(name, id);
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) found.Add(reader.GetString(0));
        return found;
    }

    /// <summary>Adds entries (skipping ids already stored) and saves the sync state, in one transaction. Returns how many were new.</summary>
    public int Save(IReadOnlyCollection<LedgerEntry> entries, LedgerSyncState state)
    {
        using var connection = Open();
        using var tx = connection.BeginTransaction();
        var inserted = 0;
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT OR IGNORE INTO ledger_entries
                    (account_id, source, external_id, kind, occurred_at, date, symbol, side, quantity, price, amount,
                     order_id, description, raw_type, raw_json)
                VALUES ($account, $source, $id, $kind, $occurred, $date, $symbol, $side, $qty, $price, $amount,
                        $order, $description, $rawType, $rawJson)
                """;
            var p = new Dictionary<string, SqliteParameter>();
            foreach (var name in new[] { "$account", "$source", "$id", "$kind", "$occurred", "$date", "$symbol", "$side",
                                         "$qty", "$price", "$amount", "$order", "$description", "$rawType", "$rawJson" })
                p[name] = cmd.Parameters.Add(name, SqliteType.Text);

            foreach (var e in entries)
            {
                p["$account"].Value = e.AccountId.ToString();
                p["$source"].Value = e.Source;
                p["$id"].Value = e.ExternalId;
                p["$kind"].Value = e.Kind.ToString();
                p["$occurred"].Value = e.OccurredAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
                p["$date"].Value = e.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                p["$symbol"].Value = (object?)e.Symbol ?? DBNull.Value;
                p["$side"].Value = (object?)e.Side ?? DBNull.Value;
                p["$qty"].Value = Dec(e.Quantity);
                p["$price"].Value = Dec(e.Price);
                p["$amount"].Value = Dec(e.Amount);
                p["$order"].Value = (object?)e.OrderId ?? DBNull.Value;
                p["$description"].Value = (object?)e.Description ?? DBNull.Value;
                p["$rawType"].Value = e.RawType;
                p["$rawJson"].Value = e.RawJson;
                inserted += cmd.ExecuteNonQuery();
            }
        }
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO ledger_sync_state (account_id, source, external_account_id, backfill_cursor, backfill_done, last_synced_at)
                VALUES ($account, $source, $external, $cursor, $done, $synced)
                ON CONFLICT (account_id, source) DO UPDATE SET
                    external_account_id = excluded.external_account_id,
                    backfill_cursor = excluded.backfill_cursor,
                    backfill_done = excluded.backfill_done,
                    last_synced_at = excluded.last_synced_at
                """;
            cmd.Parameters.AddWithValue("$account", state.AccountId.ToString());
            cmd.Parameters.AddWithValue("$source", state.Source);
            cmd.Parameters.AddWithValue("$external", (object?)state.ExternalAccountId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$cursor", (object?)state.BackfillCursor ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$done", state.BackfillDone ? 1 : 0);
            cmd.Parameters.AddWithValue("$synced", state.LastSyncedAt is { } t
                ? t.UtcDateTime.ToString("O", CultureInfo.InvariantCulture) : DBNull.Value);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return inserted;
    }

    /// <summary>Removes an account's entries and sync state (account removed, or its keys now point at another account).</summary>
    public void DeleteAccount(Guid accountId, string? source = null)
    {
        using var connection = Open();
        using var tx = connection.BeginTransaction();
        foreach (var table in new[] { "ledger_entries", "ledger_sync_state" })
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"DELETE FROM {table} WHERE account_id = $account" + (source is null ? "" : " AND source = $source");
            cmd.Parameters.AddWithValue("$account", accountId.ToString());
            if (source is not null) cmd.Parameters.AddWithValue("$source", source);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public LedgerSummary GetSummary(Guid accountId)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*), MIN(date), MAX(date) FROM ledger_entries WHERE account_id = $account";
        cmd.Parameters.AddWithValue("$account", accountId.ToString());
        using var reader = cmd.ExecuteReader();
        reader.Read();
        return new LedgerSummary(
            reader.GetInt32(0),
            reader.IsDBNull(1) ? null : DateOnly.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
            reader.IsDBNull(2) ? null : DateOnly.Parse(reader.GetString(2), CultureInfo.InvariantCulture));
    }

    /// <summary>All of an account's entries, oldest first.</summary>
    public List<LedgerEntry> GetEntries(Guid accountId)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT source, external_id, kind, occurred_at, date, symbol, side, quantity, price, amount,
                   order_id, description, raw_type, raw_json
            FROM ledger_entries WHERE account_id = $account
            ORDER BY occurred_at, external_id
            """;
        cmd.Parameters.AddWithValue("$account", accountId.ToString());
        using var reader = cmd.ExecuteReader();
        var entries = new List<LedgerEntry>();
        while (reader.Read())
        {
            entries.Add(new LedgerEntry
            {
                AccountId = accountId,
                Source = reader.GetString(0),
                ExternalId = reader.GetString(1),
                Kind = Enum.TryParse<LedgerKind>(reader.GetString(2), out var kind) ? kind : LedgerKind.Other,
                OccurredAt = DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture),
                Date = DateOnly.Parse(reader.GetString(4), CultureInfo.InvariantCulture),
                Symbol = Str(reader, 5),
                Side = Str(reader, 6),
                Quantity = ParseDec(Str(reader, 7)),
                Price = ParseDec(Str(reader, 8)),
                Amount = ParseDec(Str(reader, 9)),
                OrderId = Str(reader, 10),
                Description = Str(reader, 11),
                RawType = reader.GetString(12),
                RawJson = reader.GetString(13),
            });
        }
        return entries;
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static object Dec(decimal? value) => value is { } v ? v.ToString(CultureInfo.InvariantCulture) : DBNull.Value;
    private static decimal? ParseDec(string? text) => text is null ? null : decimal.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
    private static string? Str(SqliteDataReader reader, int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
}
