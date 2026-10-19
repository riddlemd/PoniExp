using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PoniExp.Core.Protocol;

namespace PoniExp.Client;

/// <summary>
/// This machine's client database (~/.poniexp/client.db): every message sent and received (the
/// <c>messages</c> table, which is also the replay record), what happened to each (<c>message_events</c>,
/// with the latest in <c>message_state</c>), sequence counters per peer machine, metrics, the endpoint
/// registry, what other machines have announced, and diagnostics. Messages are kept as their signed,
/// encrypted envelopes; only routing and conversation metadata is in the clear, never a body.
/// Several processes may use it at once: writes are short IMMEDIATE transactions and readers wait on a
/// busy timeout. Work that must happen together (storing a message, its event and the receipt it causes)
/// goes through <see cref="Transact{T}"/>.
/// </summary>
public sealed class ClientStore
{
    private const int SchemaVersion = 4;

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
        CREATE INDEX IF NOT EXISTS messages_conversation ON messages (conversation_id);
        CREATE INDEX IF NOT EXISTS messages_reply_to ON messages (reply_to);
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
        CREATE TABLE IF NOT EXISTS message_events (
            from_machine TEXT NOT NULL,
            message_id   TEXT NOT NULL,
            direction    TEXT NOT NULL,
            endpoint     TEXT NOT NULL,
            event        TEXT NOT NULL,
            occurred_on  TEXT NOT NULL,
            detail       TEXT
        );
        CREATE INDEX IF NOT EXISTS message_events_message ON message_events (from_machine, message_id);
        CREATE TABLE IF NOT EXISTS message_state (
            from_machine TEXT NOT NULL,
            message_id   TEXT NOT NULL,
            direction    TEXT NOT NULL,
            endpoint     TEXT NOT NULL,
            event        TEXT NOT NULL,
            occurred_on  TEXT NOT NULL,
            detail       TEXT,
            PRIMARY KEY (from_machine, message_id, direction)
        ) WITHOUT ROWID;
        CREATE INDEX IF NOT EXISTS message_state_event ON message_state (direction, event);
        CREATE TABLE IF NOT EXISTS endpoints (
            name          TEXT PRIMARY KEY,
            state         TEXT NOT NULL,
            registered_on TEXT NOT NULL,
            last_seen_on  TEXT NOT NULL,
            changed_on    TEXT NOT NULL,
            retired_on    TEXT
        ) WITHOUT ROWID;
        CREATE TABLE IF NOT EXISTS peer_endpoints (
            machine    TEXT NOT NULL,
            endpoint   TEXT NOT NULL,
            state      TEXT NOT NULL,
            counter    INTEGER NOT NULL,
            changed_on TEXT NOT NULL,
            PRIMARY KEY (machine, endpoint)
        ) WITHOUT ROWID;
        CREATE TABLE IF NOT EXISTS peer_counters (
            machine TEXT PRIMARY KEY,
            counter INTEGER NOT NULL
        ) WITHOUT ROWID;
        CREATE TABLE IF NOT EXISTS daemon_state (
            name  TEXT PRIMARY KEY,
            value INTEGER NOT NULL
        ) WITHOUT ROWID;
        CREATE TABLE IF NOT EXISTS conversations (
            id               TEXT NOT NULL,
            local_endpoint   TEXT NOT NULL,
            peer_machine     TEXT NOT NULL,
            peer_endpoint    TEXT NOT NULL,
            subject          TEXT,
            started_by       TEXT NOT NULL,
            started_on       TEXT NOT NULL,
            last_activity_on TEXT NOT NULL,
            state            TEXT NOT NULL,
            PRIMARY KEY (id, local_endpoint)
        ) WITHOUT ROWID;
        CREATE TABLE IF NOT EXISTS operations (
            from_machine      TEXT NOT NULL,
            from_endpoint     TEXT NOT NULL,
            operation_id      TEXT NOT NULL,
            to_endpoint       TEXT NOT NULL,
            first_message_id  TEXT NOT NULL,
            state             TEXT NOT NULL,
            result_message_id TEXT,
            keep_until        TEXT NOT NULL,
            PRIMARY KEY (from_machine, from_endpoint, operation_id)
        ) WITHOUT ROWID;
        CREATE INDEX IF NOT EXISTS messages_operation ON messages (operation_id);
        CREATE INDEX IF NOT EXISTS messages_stored_on ON messages (stored_on);
        CREATE TABLE IF NOT EXISTS blobs (
            blob_id      TEXT PRIMARY KEY,
            from_machine TEXT NOT NULL,
            name         TEXT NOT NULL,
            size         INTEGER NOT NULL,
            state        TEXT NOT NULL,
            local_path   TEXT,
            detail       TEXT,
            fetched_on   TEXT,
            released_on  TEXT,
            deleted_on   TEXT
        ) WITHOUT ROWID;
        CREATE TABLE IF NOT EXISTS attachments (
            from_machine TEXT NOT NULL,
            message_id   TEXT NOT NULL,
            blob_id      TEXT NOT NULL,
            direction    TEXT NOT NULL,
            endpoint     TEXT NOT NULL,
            name         TEXT NOT NULL,
            size         INTEGER NOT NULL,
            content_type TEXT NOT NULL,
            receipt_sent INTEGER NOT NULL DEFAULT 0,
            released_on  TEXT,
            PRIMARY KEY (from_machine, message_id, blob_id)
        ) WITHOUT ROWID;
        CREATE INDEX IF NOT EXISTS attachments_blob ON attachments (blob_id);
        CREATE TABLE IF NOT EXISTS attachment_events (
            from_machine TEXT NOT NULL,
            message_id   TEXT NOT NULL,
            blob_id      TEXT NOT NULL,
            direction    TEXT NOT NULL,
            endpoint     TEXT NOT NULL,
            event        TEXT NOT NULL,
            occurred_on  TEXT NOT NULL,
            detail       TEXT
        );
        CREATE INDEX IF NOT EXISTS attachment_events_message ON attachment_events (from_machine, message_id);
        CREATE TABLE IF NOT EXISTS diagnostics (
            occurred_on  TEXT NOT NULL,
            level        TEXT NOT NULL,
            category     TEXT NOT NULL,
            detail       TEXT NOT NULL,
            machine      TEXT,
            message_id   TEXT
        );
        """;

    private const string MessageColumns = """
        m.from_machine, m.id, m.envelope, m.from_endpoint, m.to_machine, m.to_endpoint, m.type, m.created_on, m.relayed_on,
        m.stored_on, m.expires_on, s.event, s.occurred_on, s.detail
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

    /// <summary>Runs <paramref name="work"/> in one IMMEDIATE transaction: all of it is stored, or none.</summary>
    public T Transact<T>(DateTimeOffset now, Func<StoreTransaction, T> work)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var result = work(new StoreTransaction(connection, transaction, now));
        transaction.Commit();
        return result;
    }

    public void Transact(DateTimeOffset now, Action<StoreTransaction> work) =>
        Transact(now, tx =>
        {
            work(tx);
            return 0;
        });

    /// <summary>Adds to a metric for a peer machine (e.g. an expired message refused).</summary>
    public Task CountAsync(string peerMachine, string metric, DateTimeOffset now, long amount = 1)
    {
        Transact(now, tx => tx.Count(peerMachine, metric, amount));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ClientMetric>> GetMetricsAsync() =>
        Task.FromResult<IReadOnlyList<ClientMetric>>(Query("SELECT peer_machine, name, count, last_on FROM metrics ORDER BY peer_machine, name;",
            r => new ClientMetric(r.GetString(0), r.GetString(1), r.GetInt64(2), Parse(r.GetString(3)))));

    public async Task<long> GetMetricAsync(string peerMachine, string metric) =>
        (await GetMetricsAsync()).Where(m => m.PeerMachine == peerMachine && m.Name == metric).Sum(m => m.Count);

    /// <summary>A stored message by its key, with its state (incoming if it was sent to this machine, else outgoing).</summary>
    public Task<StoredMessage?> GetMessageAsync(string fromMachine, Guid id) =>
        Task.FromResult(Query($"""
            SELECT {MessageColumns} FROM messages m
            LEFT JOIN message_state s ON s.from_machine = m.from_machine AND s.message_id = m.id
                AND s.direction = CASE WHEN m.from_machine = m.to_machine THEN 'in' ELSE s.direction END
            WHERE m.from_machine = $from AND m.id = $id;
            """, ReadMessage, ("$from", fromMachine), ("$id", Id(id))).FirstOrDefault());

    /// <summary>The message's latest event in one direction (null if none).</summary>
    public string? GetState(string fromMachine, Guid id, MessageDirection direction) =>
        Query("SELECT event FROM message_state WHERE from_machine = $from AND message_id = $id AND direction = $dir;",
            r => r.GetString(0), ("$from", fromMachine), ("$id", Id(id)), ("$dir", Name(direction))).FirstOrDefault();

    public IReadOnlyList<MessageEvent> GetEvents(string fromMachine, Guid id) =>
        Query("""
            SELECT direction, endpoint, event, occurred_on, detail FROM message_events
            WHERE from_machine = $from AND message_id = $id ORDER BY rowid;
            """, r => new MessageEvent(r.GetString(0) == "in" ? MessageDirection.In : MessageDirection.Out, r.GetString(1), r.GetString(2),
            Parse(r.GetString(3)), r.IsDBNull(4) ? null : r.GetString(4)), ("$from", fromMachine), ("$id", Id(id)));

    /// <summary>Outgoing messages (of every type) still waiting to be relayed, oldest first.</summary>
    public IReadOnlyList<StoredMessage> GetOutbox(string me) =>
        Query($"""
            SELECT {MessageColumns} FROM messages m
            JOIN message_state s ON s.from_machine = m.from_machine AND s.message_id = m.id AND s.direction = 'out'
            WHERE m.from_machine = $me AND s.event = '{MessageEvents.Queued}'
            ORDER BY m.stored_on, m.sequence;
            """, ReadMessage, ("$me", me));

    /// <summary>
    /// Messages for a local endpoint that no agent has acknowledged yet and that are still live (stored or
    /// fetched), oldest first. Only <see cref="MessageType.Message"/>: automatic messages are the daemon's.
    /// </summary>
    public IReadOnlyList<StoredMessage> GetInbox(string me, string endpoint) =>
        Query($"""
            SELECT {MessageColumns} FROM messages m
            JOIN message_state s ON s.from_machine = m.from_machine AND s.message_id = m.id AND s.direction = 'in'
            WHERE m.to_machine = $me AND m.to_endpoint = $endpoint AND m.type = 'message'
                AND s.event IN ('{MessageEvents.Stored}', '{MessageEvents.Fetched}')
            ORDER BY m.stored_on, m.sequence;
            """, ReadMessage, ("$me", me), ("$endpoint", endpoint));

    /// <summary>An incoming message for a local endpoint, found by id.</summary>
    public StoredMessage? FindIncoming(string me, string endpoint, Guid id) =>
        Query($"""
            SELECT {MessageColumns} FROM messages m
            LEFT JOIN message_state s ON s.from_machine = m.from_machine AND s.message_id = m.id AND s.direction = 'in'
            WHERE m.to_machine = $me AND m.to_endpoint = $endpoint AND m.id = $id AND m.type = 'message';
            """, ReadMessage, ("$me", me), ("$endpoint", endpoint), ("$id", Id(id))).FirstOrDefault();

    /// <summary>A message a local endpoint sent, found by id.</summary>
    public StoredMessage? FindOutgoing(string me, string endpoint, Guid id) =>
        Query($"""
            SELECT {MessageColumns} FROM messages m
            LEFT JOIN message_state s ON s.from_machine = m.from_machine AND s.message_id = m.id AND s.direction = 'out'
            WHERE m.from_machine = $me AND m.from_endpoint = $endpoint AND m.id = $id AND m.type = 'message';
            """, ReadMessage, ("$me", me), ("$endpoint", endpoint), ("$id", Id(id))).FirstOrDefault();

    /// <summary>A conversation as one local endpoint sees it: the messages it sent and received in it, oldest first.</summary>
    public IReadOnlyList<StoredMessage> GetConversation(string me, string endpoint, Guid conversationId) =>
        Query($"""
            SELECT {MessageColumns} FROM messages m
            LEFT JOIN message_state s ON s.from_machine = m.from_machine AND s.message_id = m.id
                AND s.direction = CASE WHEN m.to_machine = $me AND m.to_endpoint = $endpoint THEN 'in' ELSE 'out' END
            WHERE m.conversation_id = $conversation AND m.type = 'message'
                AND ((m.to_machine = $me AND m.to_endpoint = $endpoint) OR (m.from_machine = $me AND m.from_endpoint = $endpoint))
            ORDER BY m.created_on, m.stored_on;
            """, ReadMessage, ("$me", me), ("$endpoint", endpoint), ("$conversation", Id(conversationId)));

    public IReadOnlyList<LocalEndpointRecord> GetEndpoints() =>
        Query("SELECT name, state, registered_on, last_seen_on, changed_on, retired_on FROM endpoints ORDER BY name;", ReadEndpoint);

    public IReadOnlyList<PeerEndpoint> GetPeerEndpoints() =>
        Query("SELECT machine, endpoint, state, counter, changed_on FROM peer_endpoints ORDER BY machine, endpoint;",
            r => new PeerEndpoint(r.GetString(0), r.GetString(1), ParseState(r.GetString(2)), r.GetInt64(3), Parse(r.GetString(4))));

    public IReadOnlyList<Diagnostic> GetDiagnostics(int limit = 50) =>
        Query("""
            SELECT occurred_on, level, category, detail, machine, message_id FROM diagnostics ORDER BY rowid DESC LIMIT $limit;
            """, r => new Diagnostic(Parse(r.GetString(0)), r.GetString(1), r.GetString(2), r.GetString(3),
            r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : Guid.Parse(r.GetString(5))), ("$limit", limit));

    /// <summary>A message's attachments as this machine knows them: the file track's latest event, and the local copy.</summary>
    public IReadOnlyList<AttachmentRecord> GetAttachments(string fromMachine, Guid messageId) =>
        Query($"""
            SELECT {AttachmentColumns} FROM attachments a LEFT JOIN blobs b ON b.blob_id = a.blob_id
            WHERE a.from_machine = $from AND a.message_id = $id ORDER BY a.name;
            """, ReadAttachment, ("$from", fromMachine), ("$id", Id(messageId)));

    /// <summary>Every message (in one direction) that carries <paramref name="blobId"/>.</summary>
    public IReadOnlyList<AttachmentRecord> GetAttachmentsOf(Guid blobId) =>
        Query($"SELECT {AttachmentColumns} FROM attachments a LEFT JOIN blobs b ON b.blob_id = a.blob_id WHERE a.blob_id = $blob;",
            ReadAttachment, ("$blob", Id(blobId)));

    public IReadOnlyList<AttachmentEvent> GetAttachmentEvents(string fromMachine, Guid messageId) =>
        Query("""
            SELECT blob_id, direction, endpoint, event, occurred_on, detail FROM attachment_events
            WHERE from_machine = $from AND message_id = $id ORDER BY rowid;
            """, r => new AttachmentEvent(Guid.Parse(r.GetString(0)), r.GetString(1) == "in" ? MessageDirection.In : MessageDirection.Out, r.GetString(2),
            r.GetString(3), Parse(r.GetString(4)), r.IsDBNull(5) ? null : r.GetString(5)), ("$from", fromMachine), ("$id", Id(messageId)));

    public LocalBlob? GetLocalBlob(Guid blobId) =>
        Query($"SELECT {LocalBlobColumns} FROM blobs WHERE blob_id = $id;", ReadLocalBlob, ("$id", Id(blobId))).FirstOrDefault();

    /// <summary>Downloaded copies fetched before <paramref name="cutoff"/> and not yet deleted (retention).</summary>
    public IReadOnlyList<LocalBlob> GetLocalBlobsFetchedBefore(DateTimeOffset cutoff) =>
        Query($"SELECT {LocalBlobColumns} FROM blobs WHERE state = 'downloaded' AND fetched_on < $cutoff;", ReadLocalBlob, ("$cutoff", Stamp(cutoff)));

    /// <summary>Blobs this machine has finished with (verified or failed) but not yet told the router.</summary>
    public IReadOnlyList<LocalBlob> GetLocalBlobsToRelease() =>
        Query($"SELECT {LocalBlobColumns} FROM blobs WHERE state IN ('downloaded', 'failed', 'deleted') AND released_on IS NULL;", ReadLocalBlob);

    internal const string LocalBlobColumns = "blob_id, from_machine, name, size, state, local_path, detail, fetched_on, released_on, deleted_on";

    internal static LocalBlob ReadLocalBlob(SqliteDataReader r) =>
        new(Guid.Parse(r.GetString(0)), r.GetString(1), r.GetString(2), r.GetInt64(3), r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5),
            r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : Parse(r.GetString(7)), r.IsDBNull(8) ? null : Parse(r.GetString(8)),
            r.IsDBNull(9) ? null : Parse(r.GetString(9)));

    internal const string AttachmentColumns = """
        a.from_machine, a.message_id, a.blob_id, a.direction, a.endpoint, a.name, a.size, a.content_type, a.receipt_sent, a.released_on,
        (SELECT e.event FROM attachment_events e WHERE e.from_machine = a.from_machine AND e.message_id = a.message_id AND e.blob_id = a.blob_id
            ORDER BY e.rowid DESC LIMIT 1),
        (SELECT e.detail FROM attachment_events e WHERE e.from_machine = a.from_machine AND e.message_id = a.message_id AND e.blob_id = a.blob_id
            ORDER BY e.rowid DESC LIMIT 1),
        b.state, b.local_path
        """;

    internal static AttachmentRecord ReadAttachment(SqliteDataReader r) =>
        new(r.GetString(0), Guid.Parse(r.GetString(1)), Guid.Parse(r.GetString(2)), r.GetString(3) == "in" ? MessageDirection.In : MessageDirection.Out,
            r.GetString(4), r.GetString(5), r.GetInt64(6), r.GetString(7), r.GetInt64(8) == 1, r.IsDBNull(9) ? null : Parse(r.GetString(9)),
            r.IsDBNull(10) ? null : r.GetString(10), r.IsDBNull(11) ? null : r.GetString(11), r.IsDBNull(12) ? null : r.GetString(12),
            r.IsDBNull(13) ? null : r.GetString(13));

    /// <summary>The conversations local endpoints have had, most recent first.</summary>
    public IReadOnlyList<ConversationRecord> GetConversations(string? endpoint = null, int limit = 100) =>
        Query("""
            SELECT id, local_endpoint, peer_machine, peer_endpoint, subject, started_by, started_on, last_activity_on, state FROM conversations
            WHERE $endpoint IS NULL OR local_endpoint = $endpoint ORDER BY last_activity_on DESC LIMIT $limit;
            """, r => new ConversationRecord(Guid.Parse(r.GetString(0)), r.GetString(1), r.GetString(2), r.GetString(3),
            r.IsDBNull(4) ? null : r.GetString(4), r.GetString(5), Parse(r.GetString(6)), Parse(r.GetString(7)), r.GetString(8)),
            ("$endpoint", endpoint), ("$limit", limit));

    public IReadOnlyList<OperationRecord> GetOperations() =>
        Query($"SELECT {OperationColumns} FROM operations ORDER BY keep_until;", ReadOperation);

    /// <summary>
    /// The log: the most recent <see cref="LogQuery.Limit"/> stored messages matching every filter, returned
    /// oldest first, each with its state (incoming if sent to this machine, else outgoing). Automatic
    /// messages (receipts, announcements) only when <see cref="LogQuery.Automatic"/>.
    /// </summary>
    public IReadOnlyList<LogEntry> GetLog(string me, LogQuery q)
    {
        var rows = Query($"""
            SELECT {MessageColumns}, m.conversation_id, m.reply_to, m.operation_id, m.status, m.subject, m.content_type, m.sequence FROM messages m
            LEFT JOIN message_state s ON s.from_machine = m.from_machine AND s.message_id = m.id
                AND s.direction = CASE WHEN m.to_machine = $me AND m.from_machine <> $me THEN 'in' ELSE 'out' END
            WHERE ($automatic = 1 OR m.type = 'message')
                AND ($conversation IS NULL OR m.conversation_id = $conversation)
                AND ($operation IS NULL OR m.operation_id = $operation)
                AND ($peer IS NULL OR m.from_machine = $peer OR m.to_machine = $peer)
                AND ($endpoint IS NULL OR (m.to_machine = $me AND m.to_endpoint = $endpoint) OR (m.from_machine = $me AND m.from_endpoint = $endpoint))
                AND ($since IS NULL OR m.stored_on >= $since)
            ORDER BY m.stored_on DESC, m.sequence DESC LIMIT $limit;
            """, r => new LogEntry(ReadMessage(r), Guid.Parse(r.GetString(14)), r.IsDBNull(15) ? null : Guid.Parse(r.GetString(15)),
            Guid.Parse(r.GetString(16)), r.IsDBNull(17) ? null : r.GetString(17), r.IsDBNull(18) ? null : r.GetString(18), r.GetString(19),
            r.GetInt64(20)), ("$me", me), ("$automatic", q.Automatic ? 1 : 0), ("$conversation", q.Conversation is { } c ? Id(c) : null),
            ("$operation", q.Operation is { } o ? Id(o) : null), ("$peer", q.PeerMachine), ("$endpoint", q.Endpoint),
            ("$since", q.Since is { } since ? Stamp(since) : null), ("$limit", q.Limit));
        rows.Reverse();
        return rows;
    }

    /// <summary>
    /// Applies the retention rules (<see cref="RetentionOptions"/>): drops old envelopes (keeping the
    /// metadata), deletes old metadata with its events, old conversations, diagnostics and expired operation
    /// records, then drops the oldest envelopes until the database is under its size cap. A live message
    /// (in the outbox, or stored and not yet acknowledged, rejected or expired) is never touched.
    /// </summary>
    public RetentionResult ApplyRetention(RetentionOptions retention, DateTimeOffset now)
    {
        using var connection = Open();
        int envelopes, messages, diagnostics, operations;
        using (var transaction = connection.BeginTransaction(deferred: false))
        {
            envelopes = Execute(connection, transaction, $"""
                UPDATE messages SET envelope = NULL WHERE envelope IS NOT NULL AND stored_on < $cutoff AND NOT {Live};
                """, ("$cutoff", Stamp(now - retention.Envelopes)));
            var cutoff = ("$cutoff", (object?)Stamp(now - retention.Metadata));
            Execute(connection, transaction, $"""
                CREATE TEMP TABLE IF NOT EXISTS retired (from_machine TEXT NOT NULL, id TEXT NOT NULL);
                DELETE FROM retired;
                INSERT INTO retired SELECT from_machine, id FROM messages WHERE stored_on < $cutoff AND NOT {Live};
                DELETE FROM message_events WHERE (from_machine, message_id) IN (SELECT from_machine, id FROM retired);
                DELETE FROM message_state WHERE (from_machine, message_id) IN (SELECT from_machine, id FROM retired);
                """, cutoff);
            messages = Execute(connection, transaction, "DELETE FROM messages WHERE (from_machine, id) IN (SELECT from_machine, id FROM retired);");
            Execute(connection, transaction, "DELETE FROM retired; DELETE FROM conversations WHERE last_activity_on < $cutoff;", cutoff);
            diagnostics = Execute(connection, transaction, "DELETE FROM diagnostics WHERE occurred_on < $cutoff;",
                ("$cutoff", Stamp(now - retention.Diagnostics)));
            operations = Execute(connection, transaction, "DELETE FROM operations WHERE keep_until < $now;", ("$now", Stamp(now)));
            transaction.Commit();
        }

        // Over the cap: drop the oldest envelopes whose sizes add up to the excess (live ones stay whatever the size).
        for (var excess = UsedBytes(connection) - retention.MaxDatabaseBytes; excess > 0; excess = UsedBytes(connection) - retention.MaxDatabaseBytes)
        {
            var oldest = Query(connection, null, $"""
                SELECT from_machine, id, length(envelope) FROM messages WHERE envelope IS NOT NULL AND NOT {Live} ORDER BY stored_on LIMIT 1000;
                """, r => (From: r.GetString(0), Id: r.GetString(1), Bytes: r.GetInt64(2)));
            if (oldest.Count == 0)
            {
                break;
            }

            long freed = 0;
            var batch = oldest.TakeWhile(m => (freed += m.Bytes) - m.Bytes < excess).ToList();
            using var transaction = connection.BeginTransaction(deferred: false);
            foreach (var m in batch)
            {
                envelopes += Execute(connection, transaction, "UPDATE messages SET envelope = NULL WHERE from_machine = $from AND id = $id;",
                    ("$from", m.From), ("$id", m.Id));
            }

            transaction.Commit();
        }

        return new RetentionResult(envelopes, messages, diagnostics, operations);
    }

    /// <summary>Bytes the database's pages in use take (free pages don't count): what the size cap measures.</summary>
    public long UsedBytes()
    {
        using var connection = Open();
        return UsedBytes(connection);
    }

    private static long UsedBytes(SqliteConnection connection) =>
        ((long)Scalar(connection, null, "PRAGMA page_count;")! - (long)Scalar(connection, null, "PRAGMA freelist_count;")!)
        * (long)Scalar(connection, null, "PRAGMA page_size;")!;

    /// <summary>A message is live while it's in the outbox or waiting for an agent.</summary>
    private const string Live = """
        EXISTS (SELECT 1 FROM message_state ls WHERE ls.from_machine = messages.from_machine AND ls.message_id = messages.id
            AND ls.event IN ('queued', 'stored', 'fetched'))
        """;

    internal const string OperationColumns = "from_machine, from_endpoint, operation_id, to_endpoint, first_message_id, state, result_message_id, keep_until";

    internal static OperationRecord ReadOperation(SqliteDataReader r) =>
        new(r.GetString(0), r.GetString(1), Guid.Parse(r.GetString(2)), r.GetString(3), Guid.Parse(r.GetString(4)), r.GetString(5),
            r.IsDBNull(6) ? null : Guid.Parse(r.GetString(6)), Parse(r.GetString(7)));

    private static StoredMessage ReadMessage(SqliteDataReader r) =>
        new(r.GetString(0), Guid.Parse(r.GetString(1)),
            r.IsDBNull(2) ? null : JsonSerializer.Deserialize<Envelope>(r.GetString(2), WireProtocol.Json),
            r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6),
            Parse(r.GetString(7)), r.IsDBNull(8) ? null : Parse(r.GetString(8)), Parse(r.GetString(9)), Parse(r.GetString(10)),
            r.IsDBNull(11) ? null : r.GetString(11), r.IsDBNull(12) ? null : Parse(r.GetString(12)), r.IsDBNull(13) ? null : r.GetString(13));

    internal static LocalEndpointRecord ReadEndpoint(SqliteDataReader r) =>
        new(r.GetString(0), ParseState(r.GetString(1)), Parse(r.GetString(2)), Parse(r.GetString(3)), Parse(r.GetString(4)),
            r.IsDBNull(5) ? null : Parse(r.GetString(5)));

    private List<T> Query<T>(string sql, Func<SqliteDataReader, T> read, params (string Name, object? Value)[] parameters)
    {
        using var connection = Open();
        return Query(connection, null, sql, read, parameters);
    }

    internal static List<T> Query<T>(SqliteConnection connection, SqliteTransaction? transaction, string sql, Func<SqliteDataReader, T> read,
        params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, transaction, sql, parameters);
        using var reader = command.ExecuteReader();
        var rows = new List<T>();
        while (reader.Read())
        {
            rows.Add(read(reader));
        }

        return rows;
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    internal static int Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, transaction, sql, parameters);
        return command.ExecuteNonQuery();
    }

    internal static object? Scalar(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
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

    internal static string Id(Guid id) => id.ToString("D");

    internal static string Name(MessageDirection direction) => direction == MessageDirection.In ? "in" : "out";

    internal static string Stamp(DateTimeOffset time) => Signatures.Stamp(time);

    internal static DateTimeOffset Parse(string stamp) =>
        DateTimeOffset.Parse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    internal static EndpointState ParseState(string text) =>
        Enum.GetValues<EndpointState>().First(s => WireProtocol.NameOf(s) == text);
}

/// <summary>One transaction on the client database (see <see cref="ClientStore.Transact{T}"/>).</summary>
public sealed class StoreTransaction
{
    private readonly SqliteConnection _connection;
    private readonly SqliteTransaction _transaction;

    internal StoreTransaction(SqliteConnection connection, SqliteTransaction transaction, DateTimeOffset now)
    {
        _connection = connection;
        _transaction = transaction;
        Now = now;
    }

    public DateTimeOffset Now { get; }

    /// <summary>
    /// Takes the next sequence number for <paramref name="toMachine"/>, seals the envelope with it and stores
    /// it as <c>queued</c> in the outbox: a number is never handed out twice, even across processes or a crash.
    /// </summary>
    public Envelope ReserveOutgoing(string toMachine, MessageContent content, Func<long, Envelope> seal)
    {
        Execute("INSERT INTO sequences (peer_machine) VALUES ($peer) ON CONFLICT DO NOTHING;", ("$peer", toMachine));
        var sequence = (long)Scalar("SELECT next_outgoing FROM sequences WHERE peer_machine = $peer;", ("$peer", toMachine))!;
        Execute("UPDATE sequences SET next_outgoing = next_outgoing + 1 WHERE peer_machine = $peer;", ("$peer", toMachine));
        var envelope = seal(sequence);
        Insert(envelope, content, relayedOn: null, expiresOn: envelope.ExpiresOn);
        Converse(content, content.FromEndpoint, envelope.ToMachine, content.ToEndpoint, startedBy: "local");
        AddAttachments(envelope, content, MessageDirection.Out, content.FromEndpoint, AttachmentEvents.Uploaded);
        AddEvent(envelope.FromMachine, envelope.Id, MessageDirection.Out, content.FromEndpoint, MessageEvents.Queued);
        return envelope;
    }

    /// <summary>
    /// Stores a verified incoming message, updating the sender's sequence and the metrics. False if it was
    /// already stored (a redelivery or a replay): the caller confirms it to the router again and drops it.
    /// </summary>
    public bool InsertIncoming(Envelope envelope, MessageContent content, DateTimeOffset relayedOn, DateTimeOffset expiresOn, string me)
    {
        var stored = Insert(envelope, content, relayedOn, expiresOn, ignoreExisting: true) == 1;
        if (!stored && envelope.FromMachine == me && envelope.ToMachine == me)
        {
            // Sent to this machine by this machine: the row exists as our outgoing copy; this is its arrival.
            stored = Execute("UPDATE messages SET relayed_on = $relayed WHERE from_machine = $from AND id = $id AND relayed_on IS NULL;",
                ("$relayed", ClientStore.Stamp(relayedOn)), ("$from", me), ("$id", ClientStore.Id(envelope.Id))) == 1;
        }

        if (!stored)
        {
            Count(envelope.FromMachine, ClientMetrics.Duplicate, 1);
            return false;
        }

        Execute("INSERT INTO sequences (peer_machine) VALUES ($peer) ON CONFLICT DO NOTHING;", ("$peer", envelope.FromMachine));
        var last = (long)Scalar("SELECT last_incoming FROM sequences WHERE peer_machine = $peer;", ("$peer", envelope.FromMachine))!;
        if (envelope.Sequence > last + 1)
        {
            Count(envelope.FromMachine, ClientMetrics.SequenceGap, envelope.Sequence - last - 1);
        }
        else if (envelope.Sequence <= last)
        {
            Count(envelope.FromMachine, ClientMetrics.OutOfOrder, 1);
        }

        Execute("UPDATE sequences SET last_incoming = max(last_incoming, $seq) WHERE peer_machine = $peer;",
            ("$seq", envelope.Sequence), ("$peer", envelope.FromMachine));
        if (envelope.FromMachine != me)
        {
            Converse(content, content.ToEndpoint, envelope.FromMachine, content.FromEndpoint, startedBy: "peer");
        }

        if (envelope.ToMachine == me)
        {
            AddAttachments(envelope, content, MessageDirection.In, content.ToEndpoint, AttachmentEvents.Waiting);
        }

        return true;
    }

    /// <summary>
    /// Appends an event, and makes it the message's state in that direction if it moves the message forward
    /// (a late <c>received</c> doesn't undo <c>acknowledged</c>; a final state is never left).
    /// </summary>
    public void AddEvent(string fromMachine, Guid id, MessageDirection direction, string endpoint, string @event, string? detail = null)
    {
        var dir = ClientStore.Name(direction);
        var stamp = ClientStore.Stamp(Now);
        Execute("""
            INSERT INTO message_events (from_machine, message_id, direction, endpoint, event, occurred_on, detail)
            VALUES ($from, $id, $dir, $endpoint, $event, $on, $detail);
            """, ("$from", fromMachine), ("$id", ClientStore.Id(id)), ("$dir", dir), ("$endpoint", endpoint), ("$event", @event),
            ("$on", stamp), ("$detail", detail));
        var current = GetState(fromMachine, id, direction);
        if (current is null || MessageEvents.Rank(@event) > MessageEvents.Rank(current))
        {
            Execute("""
                INSERT INTO message_state (from_machine, message_id, direction, endpoint, event, occurred_on, detail)
                VALUES ($from, $id, $dir, $endpoint, $event, $on, $detail)
                ON CONFLICT (from_machine, message_id, direction) DO UPDATE SET event = $event, occurred_on = $on, detail = $detail;
                """, ("$from", fromMachine), ("$id", ClientStore.Id(id)), ("$dir", dir), ("$endpoint", endpoint), ("$event", @event),
                ("$on", stamp), ("$detail", detail));
        }
    }

    public string? GetState(string fromMachine, Guid id, MessageDirection direction) =>
        (string?)Scalar("SELECT event FROM message_state WHERE from_machine = $from AND message_id = $id AND direction = $dir;",
            ("$from", fromMachine), ("$id", ClientStore.Id(id)), ("$dir", ClientStore.Name(direction)));

    /// <summary>An outgoing message's recipient and sending endpoint (null if this machine never sent it).</summary>
    public (string ToMachine, string FromEndpoint)? FindOutgoing(string me, Guid id) =>
        ClientStore.Query(_connection, _transaction, "SELECT to_machine, from_endpoint FROM messages WHERE from_machine = $me AND id = $id;",
            r => ((string, string)?)(r.GetString(0), r.GetString(1)), ("$me", me), ("$id", ClientStore.Id(id))).FirstOrDefault();

    /// <summary>Marks live incoming messages past their expiry (plus skew) <c>expired</c>. Nothing is sent back.</summary>
    public int ExpireIncoming(TimeSpan skew)
    {
        var due = ClientStore.Query(_connection, _transaction, $"""
            SELECT s.from_machine, s.message_id, s.endpoint, m.expires_on FROM message_state s
            JOIN messages m ON m.from_machine = s.from_machine AND m.id = s.message_id
            WHERE s.direction = 'in' AND s.event IN ('{MessageEvents.Stored}', '{MessageEvents.Fetched}');
            """, r => (From: r.GetString(0), Id: Guid.Parse(r.GetString(1)), Endpoint: r.GetString(2), ExpiresOn: ClientStore.Parse(r.GetString(3))));
        var expired = due.Where(d => Now > d.ExpiresOn + skew).ToList();
        foreach (var d in expired)
        {
            AddEvent(d.From, d.Id, MessageDirection.In, d.Endpoint, MessageEvents.Expired);
        }

        return expired.Count;
    }

    /// <summary>
    /// Records a message in its conversation, as one local endpoint sees it: the first message creates it, a
    /// <c>done</c> or <c>failed</c> reply finishes it, and anything after reopens it. Automatic messages don't count.
    /// </summary>
    private void Converse(MessageContent c, string localEndpoint, string peerMachine, string peerEndpoint, string startedBy)
    {
        if (c.Type != MessageType.Message)
        {
            return;
        }

        var state = c.ReplyTo is not null && c.Status is ReplyStatus.Done or ReplyStatus.Failed ? "finished" : "open";
        Execute("""
            INSERT INTO conversations (id, local_endpoint, peer_machine, peer_endpoint, subject, started_by, started_on, last_activity_on, state)
            VALUES ($id, $local, $peerMachine, $peerEndpoint, $subject, $startedBy, $now, $now, $state)
            ON CONFLICT (id, local_endpoint) DO UPDATE SET subject = coalesce(subject, $subject), last_activity_on = $now, state = $state;
            """, ("$id", ClientStore.Id(c.ConversationId)), ("$local", localEndpoint), ("$peerMachine", peerMachine),
            ("$peerEndpoint", peerEndpoint), ("$subject", c.Subject), ("$startedBy", startedBy), ("$now", ClientStore.Stamp(Now)), ("$state", state));
    }

    private void AddAttachments(Envelope envelope, MessageContent content, MessageDirection direction, string endpoint, string firstEvent)
    {
        foreach (var a in content.Attachments ?? [])
        {
            Execute("""
                INSERT INTO attachments (from_machine, message_id, blob_id, direction, endpoint, name, size, content_type)
                VALUES ($from, $id, $blob, $dir, $endpoint, $name, $size, $type) ON CONFLICT DO NOTHING;
                """, ("$from", envelope.FromMachine), ("$id", ClientStore.Id(envelope.Id)), ("$blob", ClientStore.Id(a.BlobId)),
                ("$dir", ClientStore.Name(direction)), ("$endpoint", endpoint), ("$name", a.Name), ("$size", a.Size), ("$type", a.ContentType));
            AddAttachmentEvent(envelope.FromMachine, envelope.Id, a.BlobId, direction, endpoint, firstEvent);
        }
    }

    /// <summary>Appends to the file track (separate from the message's delivery events).</summary>
    public void AddAttachmentEvent(string fromMachine, Guid messageId, Guid blobId, MessageDirection direction, string endpoint, string @event,
        string? detail = null) =>
        Execute("""
            INSERT INTO attachment_events (from_machine, message_id, blob_id, direction, endpoint, event, occurred_on, detail)
            VALUES ($from, $id, $blob, $dir, $endpoint, $event, $on, $detail);
            """, ("$from", fromMachine), ("$id", ClientStore.Id(messageId)), ("$blob", ClientStore.Id(blobId)), ("$dir", ClientStore.Name(direction)),
            ("$endpoint", endpoint), ("$event", @event), ("$on", ClientStore.Stamp(Now)), ("$detail", detail));

    public IReadOnlyList<AttachmentRecord> GetAttachmentsOf(Guid blobId, MessageDirection direction) =>
        ClientStore.Query(_connection, _transaction, $"""
            SELECT {ClientStore.AttachmentColumns} FROM attachments a LEFT JOIN blobs b ON b.blob_id = a.blob_id
            WHERE a.blob_id = $blob AND a.direction = $dir;
            """, ClientStore.ReadAttachment, ("$blob", ClientStore.Id(blobId)), ("$dir", ClientStore.Name(direction)));

    public AttachmentRecord? GetAttachment(string fromMachine, Guid messageId, Guid blobId) =>
        ClientStore.Query(_connection, _transaction, $"""
            SELECT {ClientStore.AttachmentColumns} FROM attachments a LEFT JOIN blobs b ON b.blob_id = a.blob_id
            WHERE a.from_machine = $from AND a.message_id = $id AND a.blob_id = $blob;
            """, ClientStore.ReadAttachment, ("$from", fromMachine), ("$id", ClientStore.Id(messageId)), ("$blob", ClientStore.Id(blobId))).FirstOrDefault();

    public void MarkFileReceiptSent(string fromMachine, Guid messageId, Guid blobId) =>
        Execute("UPDATE attachments SET receipt_sent = 1 WHERE from_machine = $from AND message_id = $id AND blob_id = $blob;",
            ("$from", fromMachine), ("$id", ClientStore.Id(messageId)), ("$blob", ClientStore.Id(blobId)));

    /// <summary>An endpoint is done with a file it received. True if this release was new.</summary>
    public bool ReleaseAttachment(string fromMachine, Guid messageId, Guid blobId) =>
        Execute("UPDATE attachments SET released_on = $now WHERE from_machine = $from AND message_id = $id AND blob_id = $blob AND released_on IS NULL;",
            ("$from", fromMachine), ("$id", ClientStore.Id(messageId)), ("$blob", ClientStore.Id(blobId)), ("$now", ClientStore.Stamp(Now))) == 1;

    public LocalBlob? GetLocalBlob(Guid blobId) =>
        ClientStore.Query(_connection, _transaction, $"SELECT {ClientStore.LocalBlobColumns} FROM blobs WHERE blob_id = $id;", ClientStore.ReadLocalBlob,
            ("$id", ClientStore.Id(blobId))).FirstOrDefault();

    /// <summary>Records the local copy's state: downloaded (with its path), failed (with why) or deleted.</summary>
    public void SetLocalBlob(Guid blobId, string fromMachine, string name, long size, string state, string? path = null, string? detail = null)
    {
        var now = ClientStore.Stamp(Now);
        Execute("""
            INSERT INTO blobs (blob_id, from_machine, name, size, state, local_path, detail, fetched_on, deleted_on)
            VALUES ($id, $from, $name, $size, $state, $path, $detail, CASE WHEN $state = 'downloaded' THEN $now END, CASE WHEN $state = 'deleted' THEN $now END)
            ON CONFLICT (blob_id) DO UPDATE SET state = $state, local_path = coalesce($path, local_path), detail = $detail,
                fetched_on = CASE WHEN $state = 'downloaded' THEN $now ELSE fetched_on END,
                deleted_on = CASE WHEN $state = 'deleted' THEN $now ELSE deleted_on END;
            """, ("$id", ClientStore.Id(blobId)), ("$from", fromMachine), ("$name", name), ("$size", size), ("$state", state), ("$path", path),
            ("$detail", detail), ("$now", now));
    }

    public void MarkBlobReleasedOnRouter(Guid blobId) =>
        Execute("UPDATE blobs SET released_on = $now WHERE blob_id = $id AND released_on IS NULL;", ("$id", ClientStore.Id(blobId)), ("$now", ClientStore.Stamp(Now)));

    /// <summary>The record of an operation a sender endpoint asked of this machine, unless it has expired.</summary>
    public OperationRecord? GetOperation(string fromMachine, string fromEndpoint, Guid operationId) =>
        ClientStore.Query(_connection, _transaction, $"""
            SELECT {ClientStore.OperationColumns} FROM operations
            WHERE from_machine = $from AND from_endpoint = $endpoint AND operation_id = $op AND keep_until >= $now;
            """, ClientStore.ReadOperation, ("$from", fromMachine), ("$endpoint", fromEndpoint), ("$op", ClientStore.Id(operationId)),
            ("$now", ClientStore.Stamp(Now))).FirstOrDefault();

    /// <summary>Starts an operation (or starts it over, past an expired record): <c>running</c> until a done or failed reply.</summary>
    public void StartOperation(string fromMachine, string fromEndpoint, Guid operationId, string toEndpoint, Guid firstMessageId, DateTimeOffset keepUntil) =>
        Execute("""
            INSERT INTO operations (from_machine, from_endpoint, operation_id, to_endpoint, first_message_id, state, keep_until)
            VALUES ($from, $endpoint, $op, $to, $first, 'running', $keep)
            ON CONFLICT (from_machine, from_endpoint, operation_id) DO UPDATE SET to_endpoint = $to, first_message_id = $first,
                state = 'running', result_message_id = NULL, keep_until = $keep;
            """, ("$from", fromMachine), ("$endpoint", fromEndpoint), ("$op", ClientStore.Id(operationId)), ("$to", toEndpoint),
            ("$first", ClientStore.Id(firstMessageId)), ("$keep", ClientStore.Stamp(keepUntil)));

    /// <summary>Records an operation's final reply (retries get a copy of it from now on) and keeps the record a full window longer.</summary>
    public bool FinishOperation(string fromMachine, string fromEndpoint, Guid operationId, Guid resultMessageId, DateTimeOffset keepUntil) =>
        Execute("""
            UPDATE operations SET state = 'done', result_message_id = $result, keep_until = max(keep_until, $keep)
            WHERE from_machine = $from AND from_endpoint = $endpoint AND operation_id = $op AND state = 'running';
            """, ("$from", fromMachine), ("$endpoint", fromEndpoint), ("$op", ClientStore.Id(operationId)), ("$result", ClientStore.Id(resultMessageId)),
            ("$keep", ClientStore.Stamp(keepUntil))) == 1;

    /// <summary>A stored message's envelope (null if retention dropped it, or there's no such message).</summary>
    public Envelope? GetEnvelope(string fromMachine, Guid id) =>
        (string?)Scalar("SELECT envelope FROM messages WHERE from_machine = $from AND id = $id;", ("$from", fromMachine), ("$id", ClientStore.Id(id)))
            is { } json ? JsonSerializer.Deserialize<Envelope>(json, WireProtocol.Json) : null;

    public void Count(string peer, string metric, long amount = 1) =>
        Execute("""
            INSERT INTO metrics (peer_machine, name, count, last_on) VALUES ($peer, $name, $amount, $now)
            ON CONFLICT (peer_machine, name) DO UPDATE SET count = count + $amount, last_on = $now;
            """, ("$peer", peer), ("$name", metric), ("$amount", amount), ("$now", ClientStore.Stamp(Now)));

    public LocalEndpointRecord? GetEndpoint(string name) =>
        ClientStore.Query(_connection, _transaction,
            "SELECT name, state, registered_on, last_seen_on, changed_on, retired_on FROM endpoints WHERE name = $name;",
            ClientStore.ReadEndpoint, ("$name", name)).FirstOrDefault();

    public IReadOnlyList<LocalEndpointRecord> GetEndpoints() =>
        ClientStore.Query(_connection, _transaction,
            "SELECT name, state, registered_on, last_seen_on, changed_on, retired_on FROM endpoints ORDER BY name;", ClientStore.ReadEndpoint);

    /// <summary>Records an endpoint's state; registering (going online) brings a retired endpoint back.</summary>
    public void SetEndpointState(string name, EndpointState state)
    {
        var stamp = ClientStore.Stamp(Now);
        Execute("""
            INSERT INTO endpoints (name, state, registered_on, last_seen_on, changed_on) VALUES ($name, $state, $now, $now, $now)
            ON CONFLICT (name) DO UPDATE SET state = $state, last_seen_on = $now, changed_on = $now,
                retired_on = CASE WHEN $state = 'online' THEN NULL ELSE retired_on END;
            """, ("$name", name), ("$state", WireProtocol.NameOf(state)), ("$now", stamp));
    }

    /// <summary>Retires an endpoint: from now on its messages get a <c>rejected</c> receipt. False if it isn't registered.</summary>
    public bool RetireEndpoint(string name) =>
        Execute("UPDATE endpoints SET retired_on = $now WHERE name = $name AND retired_on IS NULL;",
            ("$name", name), ("$now", ClientStore.Stamp(Now))) == 1;

    /// <summary>
    /// Applies another machine's endpoint list if its counter is newer than any seen from that machine; a
    /// replayed or late list is ignored. True if applied.
    /// </summary>
    public bool ApplyAnnouncement(string machine, EndpointAnnouncement announcement)
    {
        var known = (long?)Scalar("SELECT counter FROM peer_counters WHERE machine = $machine;", ("$machine", machine));
        if (known is { } k && announcement.Counter <= k)
        {
            return false;
        }

        Execute("""
            INSERT INTO peer_counters (machine, counter) VALUES ($machine, $counter)
            ON CONFLICT (machine) DO UPDATE SET counter = $counter;
            """, ("$machine", machine), ("$counter", announcement.Counter));
        Execute("DELETE FROM peer_endpoints WHERE machine = $machine;", ("$machine", machine));
        foreach (var e in announcement.Endpoints)
        {
            Execute("""
                INSERT INTO peer_endpoints (machine, endpoint, state, counter, changed_on) VALUES ($machine, $endpoint, $state, $counter, $changed)
                ON CONFLICT (machine, endpoint) DO NOTHING;
                """, ("$machine", machine), ("$endpoint", e.Name), ("$state", WireProtocol.NameOf(e.State)), ("$counter", announcement.Counter),
                ("$changed", ClientStore.Stamp(e.ChangedOn)));
        }

        return true;
    }

    /// <summary>The next value of a counter that only ever increases (e.g. this daemon's announcement counter).</summary>
    public long NextCounter(string name)
    {
        Execute("INSERT INTO daemon_state (name, value) VALUES ($name, 1) ON CONFLICT (name) DO UPDATE SET value = value + 1;", ("$name", name));
        return (long)Scalar("SELECT value FROM daemon_state WHERE name = $name;", ("$name", name))!;
    }

    public void Diagnose(string level, string category, string detail, string? machine = null, Guid? messageId = null) =>
        Execute("""
            INSERT INTO diagnostics (occurred_on, level, category, detail, machine, message_id) VALUES ($on, $level, $category, $detail, $machine, $id);
            DELETE FROM diagnostics WHERE rowid <= (SELECT max(rowid) - 1000 FROM diagnostics);
            """, ("$on", ClientStore.Stamp(Now)), ("$level", level), ("$category", category), ("$detail", detail), ("$machine", machine),
            ("$id", messageId is { } id ? ClientStore.Id(id) : null));

    private int Insert(Envelope envelope, MessageContent c, DateTimeOffset? relayedOn, DateTimeOffset expiresOn, bool ignoreExisting = false) =>
        Execute($"""
            INSERT INTO messages (from_machine, id, from_endpoint, to_machine, to_endpoint, conversation_id, reply_to, operation_id,
                type, status, content_type, subject, sequence, created_on, relayed_on, stored_on, expires_on, envelope)
            VALUES ($from, $id, $fromEndpoint, $to, $toEndpoint, $conversation, $replyTo, $operation,
                $type, $status, $contentType, $subject, $sequence, $created, $relayed, $stored, $expires, $envelope)
            {(ignoreExisting ? "ON CONFLICT (from_machine, id) DO NOTHING" : string.Empty)};
            """,
            ("$from", envelope.FromMachine), ("$id", ClientStore.Id(envelope.Id)), ("$fromEndpoint", c.FromEndpoint),
            ("$to", envelope.ToMachine), ("$toEndpoint", c.ToEndpoint), ("$conversation", ClientStore.Id(c.ConversationId)),
            ("$replyTo", c.ReplyTo is { } re ? ClientStore.Id(re) : null), ("$operation", ClientStore.Id(c.OperationId)),
            ("$type", WireProtocol.NameOf(c.Type)), ("$status", c.Status is { } s ? WireProtocol.NameOf(s) : null), ("$contentType", c.ContentType),
            ("$subject", c.Subject), ("$sequence", envelope.Sequence), ("$created", ClientStore.Stamp(c.CreatedOn)),
            ("$relayed", relayedOn is { } r ? ClientStore.Stamp(r) : null), ("$stored", ClientStore.Stamp(Now)),
            ("$expires", ClientStore.Stamp(expiresOn)), ("$envelope", JsonSerializer.Serialize(envelope, WireProtocol.Json)));

    private int Execute(string sql, params (string Name, object? Value)[] parameters) => ClientStore.Execute(_connection, _transaction, sql, parameters);

    private object? Scalar(string sql, params (string Name, object? Value)[] parameters) => ClientStore.Scalar(_connection, _transaction, sql, parameters);
}

public enum MessageDirection
{
    In,
    Out,
}

/// <summary>
/// Delivery events in <c>message_events</c>. Incoming: stored → fetched → acknowledged, or rejected /
/// expired. Outgoing: queued → relayed → received → acknowledged, or rejected (the recipient's reason) /
/// failed (the router refused it) / expired (never relayed in time). relayed is the router's word;
/// received, acknowledged and rejected are the recipient's signed receipts.
/// </summary>
public static class MessageEvents
{
    public const string Queued = "queued";
    public const string Relayed = "relayed";
    public const string Received = "received";
    public const string Stored = "stored";
    public const string Fetched = "fetched";
    public const string Acknowledged = "acknowledged";
    public const string Rejected = "rejected";
    public const string Failed = "failed";
    public const string Expired = "expired";

    /// <summary>How far along an event is; final events outrank everything, so they're never left.</summary>
    public static int Rank(string @event) => @event switch
    {
        Queued or Stored => 0,
        Relayed or Fetched => 1,
        Received => 2,
        Acknowledged => 3,
        _ => 9,
    };

    public static bool IsFinal(string? @event) => @event is Rejected or Failed or Expired;
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
    /// <summary>Signed by its sender but not decryptable here (not sealed for this machine's current key, or tampered before signing).</summary>
    public const string Undecryptable = "undecryptable";
}

public sealed record ClientMetric(string PeerMachine, string Name, long Count, DateTimeOffset LastOn);

/// <summary>
/// A stored message: its envelope (still encrypted; open it with <c>RelayClient.Open</c>), its routing
/// metadata, when things happened, and its latest delivery event (<see cref="State"/>).
/// </summary>
public sealed record StoredMessage(
    string FromMachine,
    Guid Id,
    Envelope? Envelope,
    string FromEndpoint,
    string ToMachine,
    string ToEndpoint,
    string Type,
    DateTimeOffset CreatedOn,
    DateTimeOffset? RelayedOn,
    DateTimeOffset StoredOn,
    DateTimeOffset ExpiresOn,
    string? State,
    DateTimeOffset? StateOn,
    string? StateDetail);

public sealed record MessageEvent(MessageDirection Direction, string Endpoint, string Event, DateTimeOffset OccurredOn, string? Detail);

/// <summary>An endpoint registered with this machine's daemon (the endpoint registry).</summary>
public sealed record LocalEndpointRecord(string Name, EndpointState State, DateTimeOffset RegisteredOn, DateTimeOffset LastSeenOn,
    DateTimeOffset ChangedOn, DateTimeOffset? RetiredOn);

/// <summary>An endpoint another machine has announced.</summary>
public sealed record PeerEndpoint(string Machine, string Endpoint, EndpointState State, long Counter, DateTimeOffset ChangedOn);

/// <summary>
/// One message's attachment. <see cref="LastEvent"/> is the file track's latest event (see <see cref="AttachmentEvents"/>);
/// <see cref="LocalState"/> and <see cref="LocalPath"/> describe this machine's copy of the blob, shared by every message carrying it.
/// </summary>
public sealed record AttachmentRecord(string FromMachine, Guid MessageId, Guid BlobId, MessageDirection Direction, string Endpoint, string Name, long Size,
    string ContentType, bool ReceiptSent, DateTimeOffset? ReleasedOn, string? LastEvent, string? LastDetail, string? LocalState, string? LocalPath);

public sealed record AttachmentEvent(Guid BlobId, MessageDirection Direction, string Endpoint, string Event, DateTimeOffset OccurredOn, string? Detail);

/// <summary>This machine's copy of a blob it received: <see cref="State"/> is downloaded, failed or deleted.</summary>
public sealed record LocalBlob(Guid BlobId, string FromMachine, string Name, long Size, string State, string? LocalPath, string? Detail,
    DateTimeOffset? FetchedOn, DateTimeOffset? ReleasedOn, DateTimeOffset? DeletedOn);

/// <summary>
/// The file track, in <c>attachment_events</c>. Incoming: waiting → verified (a <c>downloaded</c> receipt went back) →
/// released, or download-failed. Outgoing: uploaded → downloaded or download-failed (the recipient's signed file
/// receipts) or cancelled.
/// </summary>
public static class AttachmentEvents
{
    public const string Waiting = "waiting";
    public const string Verified = "verified";
    public const string Released = "released";
    public const string Uploaded = "uploaded";
    public const string Downloaded = "downloaded";
    public const string DownloadFailed = "download-failed";
    public const string Cancelled = "cancelled";
}

/// <summary>A conversation as one local endpoint sees it. <see cref="State"/> is open or finished.</summary>
public sealed record ConversationRecord(Guid Id, string LocalEndpoint, string PeerMachine, string PeerEndpoint, string? Subject, string StartedBy,
    DateTimeOffset StartedOn, DateTimeOffset LastActivityOn, string State);

/// <summary>An operation a sender endpoint asked of this machine. <see cref="State"/> is running or done.</summary>
public sealed record OperationRecord(string FromMachine, string FromEndpoint, Guid OperationId, string ToEndpoint, Guid FirstMessageId, string State,
    Guid? ResultMessageId, DateTimeOffset KeepUntil);

/// <summary>Filters for <see cref="ClientStore.GetLog"/>; null means any.</summary>
public sealed record LogQuery(Guid? Conversation = null, Guid? Operation = null, string? PeerMachine = null, string? Endpoint = null,
    DateTimeOffset? Since = null, bool Automatic = false, int Limit = 1000);

/// <summary>A log row: the message with the metadata this machine keeps in the clear (the body is only in the envelope).</summary>
public sealed record LogEntry(StoredMessage Message, Guid ConversationId, Guid? ReplyTo, Guid OperationId, string? Status, string? Subject,
    string ContentType, long Sequence);

public sealed record RetentionResult(int EnvelopesDropped, int MessagesDeleted, int DiagnosticsDeleted, int OperationsDeleted);

public sealed record Diagnostic(DateTimeOffset OccurredOn, string Level, string Category, string Detail, string? Machine, Guid? MessageId);
