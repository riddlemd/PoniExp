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
    private const int SchemaVersion = 2;

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

public sealed record Diagnostic(DateTimeOffset OccurredOn, string Level, string Category, string Detail, string? Machine, Guid? MessageId);
