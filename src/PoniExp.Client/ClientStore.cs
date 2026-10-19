using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PoniExp.Core.Protocol;

namespace PoniExp.Client;

/// <summary>
/// This machine's client database (~/.poniexp/client.db). Phase 1 holds what must survive a restart:
/// every message sent and received (the <c>messages</c> table, which is also the replay record), sequence
/// counters per peer machine, and metrics. Several processes may use it at once: writes are short
/// IMMEDIATE transactions and readers wait on a busy timeout.
/// </summary>
public sealed class ClientStore
{
    private const int SchemaVersion = 1;

    private const string Schema = """
        PRAGMA journal_mode = WAL;
        CREATE TABLE IF NOT EXISTS messages (
            from_machine    TEXT NOT NULL,
            id              TEXT NOT NULL,
            from_endpoint   TEXT NOT NULL,
            to_machine      TEXT NOT NULL,
            to_endpoint     TEXT NOT NULL,
            conversation_id TEXT NOT NULL,
            reply_to        TEXT,
            operation_id    TEXT NOT NULL,
            type            TEXT NOT NULL,
            status          TEXT,
            content_type    TEXT NOT NULL,
            subject         TEXT,
            sequence        INTEGER NOT NULL,
            created_on      TEXT NOT NULL,
            relayed_on      TEXT,
            stored_on       TEXT NOT NULL,
            expires_on      TEXT NOT NULL,
            envelope        TEXT,
            PRIMARY KEY (from_machine, id)
        ) WITHOUT ROWID;
        CREATE INDEX IF NOT EXISTS messages_to_machine ON messages (to_machine);
        CREATE TABLE IF NOT EXISTS sequences (
            peer_machine  TEXT PRIMARY KEY,
            next_outgoing INTEGER NOT NULL DEFAULT 1,
            last_incoming INTEGER NOT NULL DEFAULT 0
        ) WITHOUT ROWID;
        CREATE TABLE IF NOT EXISTS metrics (
            peer_machine TEXT NOT NULL,
            name         TEXT NOT NULL,
            count        INTEGER NOT NULL,
            last_on      TEXT NOT NULL,
            PRIMARY KEY (peer_machine, name)
        ) WITHOUT ROWID;
        """;

    private readonly string _connectionString;

    public ClientStore(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        if (!File.Exists(Path) && !OperatingSystem.IsWindows())
        {
            // Owner-only from the start; SQLite gives its -wal and -shm files the same mode.
            using var _ = new FileStream(Path, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            });
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            DefaultTimeout = 30, // also the busy timeout when another process holds the write lock
        }.ToString();

        using var connection = Open();
        Execute(connection, null, Schema);
        Execute(connection, null, $"PRAGMA user_version = {SchemaVersion};");
    }

    public string Path { get; }

    /// <summary>
    /// Takes the next sequence number for <paramref name="toMachine"/>, signs the envelope with it and stores
    /// it, in one transaction: a number is never handed out twice, even across processes or a crash.
    /// </summary>
    public Task<Envelope> ReserveOutgoingAsync(string toMachine, Func<long, Envelope> sign, DateTimeOffset now)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        Execute(connection, transaction, "INSERT INTO sequences (peer_machine) VALUES ($peer) ON CONFLICT DO NOTHING;", ("$peer", toMachine));
        var sequence = (long)Scalar(connection, transaction, "SELECT next_outgoing FROM sequences WHERE peer_machine = $peer;", ("$peer", toMachine))!;
        Execute(connection, transaction, "UPDATE sequences SET next_outgoing = next_outgoing + 1 WHERE peer_machine = $peer;", ("$peer", toMachine));
        var envelope = sign(sequence);
        Insert(connection, transaction, envelope, relayedOn: null, storedOn: now, expiresOn: envelope.ExpiresOn);
        transaction.Commit();
        return Task.FromResult(envelope);
    }

    /// <summary>Drops an outgoing message the router refused. Its sequence number stays used (the recipient sees a gap).</summary>
    public Task ForgetOutgoingAsync(string fromMachine, Guid id)
    {
        using var connection = Open();
        Execute(connection, null, "DELETE FROM messages WHERE from_machine = $from AND id = $id AND relayed_on IS NULL;",
            ("$from", fromMachine), ("$id", id.ToString("D")));
        return Task.CompletedTask;
    }

    /// <summary>
    /// Stores a verified incoming message, updating the sender's sequence and the metrics in the same
    /// transaction. False if it was already stored (a redelivery or a replay): the caller confirms it to the
    /// router again and drops it.
    /// </summary>
    public Task<bool> StoreIncomingAsync(Envelope envelope, DateTimeOffset relayedOn, DateTimeOffset expiresOn, string me, DateTimeOffset now)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var stored = Insert(connection, transaction, envelope, relayedOn, now, expiresOn, ignoreExisting: true) == 1;
        if (!stored && envelope.FromMachine == me && envelope.ToMachine == me)
        {
            // Sent to this machine by this machine: the row exists as our outgoing copy; this is its arrival.
            stored = Execute(connection, transaction,
                "UPDATE messages SET relayed_on = $relayed WHERE from_machine = $from AND id = $id AND relayed_on IS NULL;",
                ("$relayed", Stamp(relayedOn)), ("$from", me), ("$id", envelope.Id.ToString("D"))) == 1;
        }

        if (!stored)
        {
            Count(connection, transaction, envelope.FromMachine, ClientMetrics.Duplicate, 1, now);
        }
        else
        {
            Execute(connection, transaction, "INSERT INTO sequences (peer_machine) VALUES ($peer) ON CONFLICT DO NOTHING;",
                ("$peer", envelope.FromMachine));
            var last = (long)Scalar(connection, transaction, "SELECT last_incoming FROM sequences WHERE peer_machine = $peer;",
                ("$peer", envelope.FromMachine))!;
            if (envelope.Sequence > last + 1)
            {
                Count(connection, transaction, envelope.FromMachine, ClientMetrics.SequenceGap, envelope.Sequence - last - 1, now);
            }
            else if (envelope.Sequence <= last)
            {
                Count(connection, transaction, envelope.FromMachine, ClientMetrics.OutOfOrder, 1, now);
            }

            Execute(connection, transaction, "UPDATE sequences SET last_incoming = max(last_incoming, $seq) WHERE peer_machine = $peer;",
                ("$seq", envelope.Sequence), ("$peer", envelope.FromMachine));
        }

        transaction.Commit();
        return Task.FromResult(stored);
    }

    /// <summary>Adds to a metric for a peer machine (e.g. an expired message refused).</summary>
    public Task CountAsync(string peerMachine, string metric, DateTimeOffset now, long amount = 1)
    {
        using var connection = Open();
        Count(connection, null, peerMachine, metric, amount, now);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ClientMetric>> GetMetricsAsync()
    {
        using var connection = Open();
        using var command = Command(connection, null, "SELECT peer_machine, name, count, last_on FROM metrics ORDER BY peer_machine, name;");
        using var reader = command.ExecuteReader();
        var metrics = new List<ClientMetric>();
        while (reader.Read())
        {
            metrics.Add(new ClientMetric(reader.GetString(0), reader.GetString(1), reader.GetInt64(2), Parse(reader.GetString(3))));
        }

        return Task.FromResult<IReadOnlyList<ClientMetric>>(metrics);
    }

    public async Task<long> GetMetricAsync(string peerMachine, string metric) =>
        (await GetMetricsAsync()).Where(m => m.PeerMachine == peerMachine && m.Name == metric).Sum(m => m.Count);

    public Task<StoredMessage?> GetMessageAsync(string fromMachine, Guid id)
    {
        using var connection = Open();
        using var command = Command(connection, null,
            "SELECT envelope, relayed_on, stored_on, expires_on FROM messages WHERE from_machine = $from AND id = $id;",
            ("$from", fromMachine), ("$id", id.ToString("D")));
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return Task.FromResult<StoredMessage?>(null);
        }

        var envelope = reader.IsDBNull(0) ? null : JsonSerializer.Deserialize<Envelope>(reader.GetString(0), WireProtocol.Json);
        return Task.FromResult<StoredMessage?>(new StoredMessage(envelope, reader.IsDBNull(1) ? null : Parse(reader.GetString(1)),
            Parse(reader.GetString(2)), Parse(reader.GetString(3))));
    }

    private int Insert(SqliteConnection connection, SqliteTransaction transaction, Envelope envelope, DateTimeOffset? relayedOn,
        DateTimeOffset storedOn, DateTimeOffset expiresOn, bool ignoreExisting = false)
    {
        var c = envelope.Content;
        return Execute(connection, transaction, $"""
            INSERT INTO messages (from_machine, id, from_endpoint, to_machine, to_endpoint, conversation_id, reply_to, operation_id,
                type, status, content_type, subject, sequence, created_on, relayed_on, stored_on, expires_on, envelope)
            VALUES ($from, $id, $fromEndpoint, $to, $toEndpoint, $conversation, $replyTo, $operation,
                $type, $status, $contentType, $subject, $sequence, $created, $relayed, $stored, $expires, $envelope)
            {(ignoreExisting ? "ON CONFLICT (from_machine, id) DO NOTHING" : string.Empty)};
            """,
            ("$from", envelope.FromMachine), ("$id", envelope.Id.ToString("D")), ("$fromEndpoint", c.FromEndpoint),
            ("$to", envelope.ToMachine), ("$toEndpoint", c.ToEndpoint), ("$conversation", c.ConversationId.ToString("D")),
            ("$replyTo", c.ReplyTo?.ToString("D")), ("$operation", c.OperationId.ToString("D")), ("$type", WireProtocol.NameOf(c.Type)),
            ("$status", c.Status is { } s ? WireProtocol.NameOf(s) : null), ("$contentType", c.ContentType), ("$subject", c.Subject),
            ("$sequence", envelope.Sequence), ("$created", Stamp(c.CreatedOn)), ("$relayed", relayedOn is { } r ? Stamp(r) : null),
            ("$stored", Stamp(storedOn)), ("$expires", Stamp(expiresOn)),
            ("$envelope", JsonSerializer.Serialize(envelope, WireProtocol.Json)));
    }

    private static void Count(SqliteConnection connection, SqliteTransaction? transaction, string peer, string metric, long amount,
        DateTimeOffset now) =>
        Execute(connection, transaction, """
            INSERT INTO metrics (peer_machine, name, count, last_on) VALUES ($peer, $name, $amount, $now)
            ON CONFLICT (peer_machine, name) DO UPDATE SET count = count + $amount, last_on = $now;
            """, ("$peer", peer), ("$name", metric), ("$amount", amount), ("$now", Stamp(now)));

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static int Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, transaction, sql, parameters);
        return command.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, transaction, sql, parameters);
        return command.ExecuteScalar();
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql,
        params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }

    private static string Stamp(DateTimeOffset time) => Signatures.Stamp(time);

    private static DateTimeOffset Parse(string stamp) =>
        DateTimeOffset.Parse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}

/// <summary>Counter names in the <c>metrics</c> table.</summary>
public static class ClientMetrics
{
    /// <summary>Sequence numbers from a sender that never arrived (expired and dropped, or dropped on purpose: the two look the same).</summary>
    public const string SequenceGap = "sequence-gap";
    public const string OutOfOrder = "out-of-order";
    /// <summary>A message already stored arrived again (a redelivery after a crash, or a replay). Confirmed and dropped.</summary>
    public const string Duplicate = "duplicate";
    public const string Expired = "expired";
    /// <summary>A message created further in the future than the clock skew allows.</summary>
    public const string FromTheFuture = "from-the-future";
    public const string Invalid = "invalid";
}

public sealed record ClientMetric(string PeerMachine, string Name, long Count, DateTimeOffset LastOn);

public sealed record StoredMessage(Envelope? Envelope, DateTimeOffset? RelayedOn, DateTimeOffset StoredOn, DateTimeOffset ExpiresOn);
