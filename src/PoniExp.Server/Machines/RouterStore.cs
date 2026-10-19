using System.Text.Json;
using Microsoft.Data.Sqlite;
using PoniExp.Core.Crypto;
using PoniExp.Core.Protocol;

namespace PoniExp.Server.Machines;

/// <summary>
/// The router's database (<c>router.db</c> in the data directory): the machines and the message queue.
/// Each row keeps its record as JSON, with a few columns for inspection. The services that own the data
/// (<see cref="MachineRegistry"/>, <see cref="MessageQueue"/>) keep it in memory and write only what
/// changed, in one transaction per change, before publishing it.
/// </summary>
public sealed class RouterStore
{
    private const int SchemaVersion = 4;

    private static readonly JsonSerializerOptions Json = WireProtocol.Configure(new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private readonly string _connectionString;

    public RouterStore(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        Path = System.IO.Path.Combine(dataDirectory, "router.db");
        if (!File.Exists(Path) && !OperatingSystem.IsWindows())
        {
            // Owner-only from the start (it says who may use the relay); SQLite gives -wal and -shm the same mode.
            using var _ = new FileStream(Path, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            });
        }

        _connectionString = new SqliteConnectionStringBuilder { DataSource = Path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = true }.ToString();
        using var connection = Open();
        Execute(connection, null, """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS machines (
                id     TEXT PRIMARY KEY,
                name   TEXT NOT NULL,
                status TEXT NOT NULL,
                record TEXT NOT NULL
            ) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS queue (
                id         TEXT PRIMARY KEY,
                to_machine TEXT NOT NULL,
                queued_on  TEXT NOT NULL,
                expires_on TEXT NOT NULL,
                entry      TEXT NOT NULL
            ) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS blobs (
                id         TEXT PRIMARY KEY,
                sender     TEXT NOT NULL,
                state      TEXT NOT NULL,
                expires_on TEXT NOT NULL,
                record     TEXT NOT NULL
            ) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS groups (
                name    TEXT PRIMARY KEY,
                members TEXT NOT NULL
            ) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS router_state (
                name  TEXT PRIMARY KEY,
                value TEXT NOT NULL
            ) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS traffic_log (
                occurred_on  TEXT NOT NULL,
                event        TEXT NOT NULL,
                from_machine TEXT,
                to_machine   TEXT,
                message_id   TEXT,
                size         INTEGER,
                sequence     INTEGER,
                address      TEXT,
                detail       TEXT
            );
            CREATE INDEX IF NOT EXISTS traffic_log_occurred_on ON traffic_log (occurred_on);
            """);
        Execute(connection, null, $"PRAGMA user_version = {SchemaVersion};");
        ImportJson(dataDirectory);
    }

    public string Path { get; }

    public IReadOnlyList<MachineRecord> LoadMachines() => Load<MachineRecord>("SELECT record FROM machines;");

    public IReadOnlyList<MessageQueue.QueuedEnvelope> LoadQueue() => Load<MessageQueue.QueuedEnvelope>("SELECT entry FROM queue ORDER BY queued_on;");

    /// <summary>Writes the machines that changed between two snapshots (added, changed or removed), in one transaction.</summary>
    public void SaveMachines(IReadOnlyDictionary<string, MachineRecord> before, IReadOnlyDictionary<string, MachineRecord> after) =>
        Save(before, after, "DELETE FROM machines WHERE id = $id;", (command, m) =>
        {
            command.CommandText = """
                INSERT INTO machines (id, name, status, record) VALUES ($id, $name, $status, $record)
                ON CONFLICT (id) DO UPDATE SET name = $name, status = $status, record = $record;
                """;
            command.Parameters.AddWithValue("$id", m.Id);
            command.Parameters.AddWithValue("$name", m.Name);
            command.Parameters.AddWithValue("$status", WireProtocol.NameOf(m.Status));
            command.Parameters.AddWithValue("$record", JsonSerializer.Serialize(m, Json));
        });

    public void SaveQueue(IReadOnlyDictionary<string, MessageQueue.QueuedEnvelope> before, IReadOnlyDictionary<string, MessageQueue.QueuedEnvelope> after) =>
        Save(before, after, "DELETE FROM queue WHERE id = $id;", (command, q) =>
        {
            command.CommandText = """
                INSERT INTO queue (id, to_machine, queued_on, expires_on, entry) VALUES ($id, $to, $queued, $expires, $entry)
                ON CONFLICT (id) DO UPDATE SET entry = $entry, expires_on = $expires;
                """;
            command.Parameters.AddWithValue("$id", q.Key);
            command.Parameters.AddWithValue("$to", q.RecipientMachine);
            command.Parameters.AddWithValue("$queued", Signatures.Stamp(q.QueuedOn));
            command.Parameters.AddWithValue("$expires", Signatures.Stamp(q.ExpiresOn));
            command.Parameters.AddWithValue("$entry", JsonSerializer.Serialize(q, Json));
        });

    public IReadOnlyList<Blobs.BlobRecord> LoadBlobs() => Load<Blobs.BlobRecord>("SELECT record FROM blobs;");

    public void SaveBlob(Blobs.BlobRecord blob)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO blobs (id, sender, state, expires_on, record) VALUES ($id, $sender, $state, $expires, $record)
            ON CONFLICT (id) DO UPDATE SET state = $state, expires_on = $expires, record = $record;
            """;
        command.Parameters.AddWithValue("$id", blob.Id.ToString("D"));
        command.Parameters.AddWithValue("$sender", blob.Sender);
        command.Parameters.AddWithValue("$state", WireProtocol.NameOf(blob.State));
        command.Parameters.AddWithValue("$expires", Signatures.Stamp(blob.ExpiresOn));
        command.Parameters.AddWithValue("$record", JsonSerializer.Serialize(blob, Json));
        command.ExecuteNonQuery();
    }

    public void DeleteBlob(Guid id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM blobs WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<GroupInfo> LoadGroups()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name, members FROM groups ORDER BY name;";
        using var r = command.ExecuteReader();
        var groups = new List<GroupInfo>();
        while (r.Read())
        {
            groups.Add(new GroupInfo(r.GetString(0), JsonSerializer.Deserialize<List<string>>(r.GetString(1), Json) ?? []));
        }

        return groups;
    }

    /// <summary>Sets a group's members; none deletes it.</summary>
    public void SaveGroup(string name, IReadOnlyList<string> members)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = members.Count == 0 ? "DELETE FROM groups WHERE name = $name;"
            : "INSERT INTO groups (name, members) VALUES ($name, $members) ON CONFLICT (name) DO UPDATE SET members = $members;";
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$members", JsonSerializer.Serialize(members, Json));
        command.ExecuteNonQuery();
    }

    public string? GetState(string name)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM router_state WHERE name = $name;";
        command.Parameters.AddWithValue("$name", name);
        return command.ExecuteScalar() as string;
    }

    public void SetState(string name, string value)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO router_state (name, value) VALUES ($name, $value) ON CONFLICT (name) DO UPDATE SET value = $value;";
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    public void AppendTraffic(TrafficEntry e)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO traffic_log (occurred_on, event, from_machine, to_machine, message_id, size, sequence, address, detail)
            VALUES ($on, $event, $from, $to, $id, $size, $sequence, $address, $detail);
            """;
        command.Parameters.AddWithValue("$on", Signatures.Stamp(e.OccurredOn));
        command.Parameters.AddWithValue("$event", e.Event);
        command.Parameters.AddWithValue("$from", (object?)e.FromMachine ?? DBNull.Value);
        command.Parameters.AddWithValue("$to", (object?)e.ToMachine ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", e.MessageId is { } id ? id.ToString("D") : DBNull.Value);
        command.Parameters.AddWithValue("$size", (object?)e.Size ?? DBNull.Value);
        command.Parameters.AddWithValue("$sequence", (object?)e.Sequence ?? DBNull.Value);
        command.Parameters.AddWithValue("$address", (object?)e.Address ?? DBNull.Value);
        command.Parameters.AddWithValue("$detail", (object?)e.Detail ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    /// <summary>The most recent <paramref name="limit"/> entries since <paramref name="since"/> (involving <paramref name="machine"/> if given), oldest first.</summary>
    public IReadOnlyList<TrafficEntry> ReadTraffic(DateTimeOffset? since, string? machine, int limit)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT occurred_on, event, from_machine, to_machine, message_id, size, sequence, address, detail FROM traffic_log
            WHERE ($since IS NULL OR occurred_on >= $since) AND ($machine IS NULL OR from_machine = $machine OR to_machine = $machine)
            ORDER BY rowid DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$since", since is { } s ? Signatures.Stamp(s) : DBNull.Value);
        command.Parameters.AddWithValue("$machine", (object?)machine ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", limit);
        using var r = command.ExecuteReader();
        var rows = new List<TrafficEntry>();
        while (r.Read())
        {
            rows.Add(new TrafficEntry(DateTimeOffset.Parse(r.GetString(0), System.Globalization.CultureInfo.InvariantCulture), r.GetString(1),
                r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : Guid.Parse(r.GetString(4)),
                r.IsDBNull(5) ? null : r.GetInt32(5), r.IsDBNull(6) ? null : r.GetInt64(6), r.IsDBNull(7) ? null : r.GetString(7),
                r.IsDBNull(8) ? null : r.GetString(8)));
        }

        rows.Reverse();
        return rows;
    }

    public int PruneTraffic(DateTimeOffset before)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM traffic_log WHERE occurred_on < $before;";
        command.Parameters.AddWithValue("$before", Signatures.Stamp(before));
        return command.ExecuteNonQuery();
    }

    /// <summary>True if the database can be read (for <c>/health</c>).</summary>
    public bool IsHealthy()
    {
        try
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM machines;";
            command.ExecuteScalar();
            return true;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    private void Save<TKey, T>(IReadOnlyDictionary<TKey, T> before, IReadOnlyDictionary<TKey, T> after, string delete,
        Action<SqliteCommand, T> upsert) where TKey : notnull where T : class
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        foreach (var (key, value) in after)
        {
            if (!before.TryGetValue(key, out var old) || !ReferenceEquals(old, value))
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                upsert(command, value);
                command.ExecuteNonQuery();
            }
        }

        foreach (var key in before.Keys.Where(k => !after.ContainsKey(k)))
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = delete;
            command.Parameters.AddWithValue("$id", key.ToString());
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>The JSON files earlier versions kept, imported once and renamed to <c>*.imported</c>.</summary>
    private void ImportJson(string dataDirectory)
    {
        var machinesFile = System.IO.Path.Combine(dataDirectory, "machines.json");
        if (File.Exists(machinesFile))
        {
            var machines = JsonSerializer.Deserialize<List<MachineRecord>>(File.ReadAllText(machinesFile), Json) ?? [];
            SaveMachines(new Dictionary<string, MachineRecord>(), machines.ToDictionary(m => m.Id));
            File.Move(machinesFile, machinesFile + ".imported", overwrite: true);
        }

        var queueFile = System.IO.Path.Combine(dataDirectory, "queue.json");
        if (File.Exists(queueFile))
        {
            var queue = JsonSerializer.Deserialize<List<MessageQueue.QueuedEnvelope>>(File.ReadAllText(queueFile), Json) ?? [];
            SaveQueue(new Dictionary<string, MessageQueue.QueuedEnvelope>(),
                queue.Where(q => q.Envelope?.Ciphertext is not null).ToDictionary(q => q.Key)); // older formats can't be delivered
            File.Move(queueFile, queueFile + ".imported", overwrite: true);
        }
    }

    private List<T> Load<T>(string sql)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<T>();
        while (reader.Read())
        {
            if (JsonSerializer.Deserialize<T>(reader.GetString(0), Json) is { } row)
            {
                rows.Add(row);
            }
        }

        return rows;
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
