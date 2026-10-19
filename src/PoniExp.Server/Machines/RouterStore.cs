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
    private const int SchemaVersion = 1;

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

    public void SaveQueue(IReadOnlyDictionary<Guid, MessageQueue.QueuedEnvelope> before, IReadOnlyDictionary<Guid, MessageQueue.QueuedEnvelope> after) =>
        Save(before, after, "DELETE FROM queue WHERE id = $id;", (command, q) =>
        {
            command.CommandText = """
                INSERT INTO queue (id, to_machine, queued_on, expires_on, entry) VALUES ($id, $to, $queued, $expires, $entry)
                ON CONFLICT (id) DO UPDATE SET entry = $entry, expires_on = $expires;
                """;
            command.Parameters.AddWithValue("$id", q.Envelope.Id.ToString("D"));
            command.Parameters.AddWithValue("$to", q.Envelope.ToMachine);
            command.Parameters.AddWithValue("$queued", Signatures.Stamp(q.QueuedOn));
            command.Parameters.AddWithValue("$expires", Signatures.Stamp(q.ExpiresOn));
            command.Parameters.AddWithValue("$entry", JsonSerializer.Serialize(q, Json));
        });

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
            command.Parameters.AddWithValue("$id", key is Guid g ? g.ToString("D") : key.ToString());
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
            SaveQueue(new Dictionary<Guid, MessageQueue.QueuedEnvelope>(),
                queue.Where(q => q.Envelope?.Ciphertext is not null).ToDictionary(q => q.Envelope.Id)); // older formats can't be delivered
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
