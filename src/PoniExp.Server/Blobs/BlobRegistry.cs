using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using PoniExp.Core.Files;
using PoniExp.Core.Protocol;
using PoniExp.Server.Hubs;
using PoniExp.Server.Machines;

namespace PoniExp.Server.Blobs;

/// <summary>
/// The router's blobs (see docs/plan.md, Phase 6): encrypted files uploaded once, readable only by their
/// sender and the machines named when the upload began. Records live in <c>router.db</c>, chunks under
/// <c>data/blobs/&lt;id&gt;/</c>. Space is reserved when an upload starts; a blob is deleted when every machine
/// it is shared with has released it, when it expires, or when its sender cancels it. A cancelled or expired
/// blob keeps a record (no data) for one more expiry term, so a late download is told why.
/// </summary>
public sealed class BlobRegistry
{
    private readonly Lock _lock = new();
    private readonly ConcurrentDictionary<string, Ticket> _tickets = new();
    private readonly RouterStore _store;
    private readonly IOptionsMonitor<BlobOptions> _options;
    private readonly MachineRegistry _machines;
    private readonly PresenceTracker _presence;
    private readonly TimeProvider _time;
    private readonly ILogger<BlobRegistry> _logger;
    private readonly Dictionary<Guid, BlobRecord> _blobs;

    public BlobRegistry(RouterStore store, IOptionsMonitor<RouterOptions> router, IOptionsMonitor<BlobOptions> options, MachineRegistry machines,
        PresenceTracker presence, TimeProvider time, ILogger<BlobRegistry> logger)
    {
        _store = store;
        _options = options;
        _machines = machines;
        _presence = presence;
        _time = time;
        _logger = logger;
        Root = Path.Combine(router.CurrentValue.DataDirectory, "blobs");
        Directory.CreateDirectory(Root);
        _blobs = store.LoadBlobs().ToDictionary(b => b.Id);
    }

    public string Root { get; }

    public IReadOnlyList<BlobRecord> List()
    {
        lock (_lock)
        {
            return [.. _blobs.Values.OrderBy(b => b.CreatedOn)];
        }
    }

    public BlobRecord? Get(Guid id)
    {
        lock (_lock)
        {
            return _blobs.GetValueOrDefault(id);
        }
    }

    /// <summary>Starts an upload, reserving its space against every limit. Refused (with why) if one would be passed.</summary>
    public BlobTicket Begin(string sender, BeginUploadRequest request)
    {
        var o = _options.CurrentValue;
        var shareWith = request.ShareWith?.Distinct().ToList() ?? [];
        if (shareWith.Count is 0 or > 64)
        {
            throw new RouterException("Name 1-64 machines to share the blob with.");
        }

        if (shareWith.FirstOrDefault(id => _machines.Get(id) is not { Status: MembershipStatus.Approved }) is { } unknown)
        {
            throw new RouterException($"{unknown[..Math.Min(8, unknown.Length)]} isn't an approved machine.");
        }

        var bytes = request.EncryptedBytes;
        if (request.ChunkCount < 1 || bytes < (long)request.ChunkCount * BlobCrypto.TagSize)
        {
            throw new RouterException("A blob has at least one chunk, and its size must fit its chunks.");
        }

        if (bytes > o.MaxFileBytes)
        {
            throw new RouterException($"Refused: the file is {Gb(bytes)}, over the {Gb(o.MaxFileBytes)} limit per file.");
        }

        lock (_lock)
        {
            var live = _blobs.Values.Where(b => b.IsLive).ToList();
            var senderName = _machines.Get(sender)?.Name ?? sender;
            var sent = live.Where(b => b.Sender == sender).Sum(b => b.ReservedBytes);
            if (sent + bytes > o.SenderLimitOf(senderName))
            {
                throw new RouterException($"Refused: this machine has {Gb(sent)} of {Gb(o.SenderLimitOf(senderName))} of files on the router; " +
                                          "cancel some ('poniexp-client blobs', 'cancel') or wait for them to be fetched.");
            }

            foreach (var recipient in shareWith)
            {
                var record = _machines.Get(recipient)!;
                var waiting = WaitingFor(live, recipient);
                if (waiting + bytes > o.RecipientLimitOf(record.Name))
                {
                    var seen = _presence.IsOnline(recipient) ? "online"
                        : record.LastSeenOn is { } on ? $"offline since {on:yyyy-MM-dd HH:mm} UTC" : "never seen online";
                    throw new RouterException($"Refused: {record.Name} has {Gb(waiting)} of {Gb(o.RecipientLimitOf(record.Name))} waiting, {seen}.");
                }
            }

            var total = live.Sum(b => b.ReservedBytes);
            if (total + bytes > o.RelayBytes)
            {
                throw new RouterException($"Refused: the router holds {Gb(total)} of {Gb(o.RelayBytes)} of files.");
            }

            if (FreeBytes() is { } free && free - bytes < o.FreeDiskFloorBytes)
            {
                throw new RouterException($"Refused: the router's disk has {Gb(free)} free, and keeps {Gb(o.FreeDiskFloorBytes)} spare.");
            }

            var now = _time.GetUtcNow();
            var blob = new BlobRecord(Guid.NewGuid(), sender, shareWith, BlobState.Uploading, bytes, request.ChunkCount, now, null, now + o.UnsealedExpiry, [], null);
            Directory.CreateDirectory(DirectoryOf(blob.Id));
            Save(blob);
            _logger.LogInformation("Upload {Id} from {Sender}: {Bytes}, {Chunks} chunks, shared with {Count} machines", blob.Id.ToString()[..8], senderName,
                Gb(bytes), request.ChunkCount, shareWith.Count);
            return Issue(blob.Id, sender, TicketDirection.Upload);
        }
    }

    /// <summary>A new upload ticket for an upload its sender hasn't completed (to resume it).</summary>
    public BlobTicket Resume(string sender, Guid id)
    {
        var blob = Require(id, sender);
        return blob.State == BlobState.Uploading ? Issue(id, sender, TicketDirection.Upload)
            : throw new RouterException($"That blob is {WireProtocol.NameOf(blob.State)}, not being uploaded.", RouterErrorKind.Conflict);
    }

    /// <summary>Seals an upload once every chunk and the manifest are there. Its reservation shrinks to what it really takes.</summary>
    public BlobStatus Complete(string sender, Guid id)
    {
        lock (_lock)
        {
            var blob = Require(id, sender);
            if (blob.State != BlobState.Uploading)
            {
                throw new RouterException($"That blob is {WireProtocol.NameOf(blob.State)}, not being uploaded.", RouterErrorKind.Conflict);
            }

            var status = StatusOf(blob);
            if (!status.Manifest || status.Chunks.Count != blob.ChunkCount)
            {
                throw new RouterException($"The upload isn't complete: {status.Chunks.Count} of {blob.ChunkCount} chunks{(status.Manifest ? "" : ", no manifest")}.",
                    RouterErrorKind.Conflict);
            }

            var now = _time.GetUtcNow();
            Save(blob with { State = BlobState.Sealed, SealedOn = now, ExpiresOn = now + _options.CurrentValue.Expiry, ReservedBytes = status.Bytes });
            foreach (var t in _tickets.Where(t => t.Value.BlobId == id && t.Value.Direction == TicketDirection.Upload))
            {
                _tickets.TryRemove(t.Key, out _);
            }

            return StatusOf(_blobs[id]);
        }
    }

    /// <summary>
    /// A download ticket for a machine the blob is shared with (or its sender). Anyone else is told only that
    /// there's no such blob: never who it is shared with.
    /// </summary>
    public BlobTicket RequestDownload(string machine, Guid id)
    {
        var blob = Get(id);
        if (blob is null || (blob.Sender != machine && !blob.SharedWith.Contains(machine)))
        {
            throw new RouterException("No such blob for this machine (it may have expired).", RouterErrorKind.NotFound);
        }

        return blob.State switch
        {
            BlobState.Sealed => Issue(id, machine, TicketDirection.Download),
            BlobState.Uploading => throw new RouterException("That blob is still being uploaded.", RouterErrorKind.Conflict),
            BlobState.Cancelled => throw new RouterException($"That blob was {blob.EndReason ?? "cancelled by the sender"}.", RouterErrorKind.NotFound),
            _ => throw new RouterException("That blob has expired.", RouterErrorKind.NotFound),
        };
    }

    /// <summary>A machine is done with the blob (verified, or gave up). Deleted once every machine it is shared with has released it.</summary>
    public void Release(string machine, Guid id)
    {
        lock (_lock)
        {
            if (_blobs.GetValueOrDefault(id) is not { IsLive: true } blob || !blob.SharedWith.Contains(machine) || blob.ReleasedBy.Contains(machine))
            {
                return;
            }

            var released = blob with { ReleasedBy = [.. blob.ReleasedBy, machine] };
            if (released.SharedWith.All(released.ReleasedBy.Contains))
            {
                Delete(released, "released by every machine");
            }
            else
            {
                Save(released);
            }
        }
    }

    public void Cancel(string sender, Guid id)
    {
        lock (_lock)
        {
            var blob = Require(id, sender);
            if (blob.IsLive)
            {
                End(blob, BlobState.Cancelled, "cancelled by the sender");
            }
        }
    }

    /// <summary>Releases everything waiting for a machine, freeing its share (<c>poniexp admin blobs clear --for</c>).</summary>
    public int ClearFor(string machine)
    {
        var waiting = List().Where(b => b.IsLive && b.SharedWith.Contains(machine) && !b.ReleasedBy.Contains(machine)).ToList();
        foreach (var blob in waiting)
        {
            Release(machine, blob.Id);
        }

        return waiting.Count;
    }

    /// <summary>A machine was removed: what waits for it is released, and its own uploads are cancelled.</summary>
    public void Forget(string machine)
    {
        ClearFor(machine);
        lock (_lock)
        {
            foreach (var blob in _blobs.Values.Where(b => b.Sender == machine && b.IsLive).ToList())
            {
                End(blob, BlobState.Cancelled, "cancelled: its sender was removed");
            }
        }
    }

    /// <summary>Expires unsealed uploads and old blobs, forgets old records and stale tickets.</summary>
    public void Sweep()
    {
        var now = _time.GetUtcNow();
        lock (_lock)
        {
            foreach (var blob in _blobs.Values.Where(b => b.ExpiresOn <= now).ToList())
            {
                if (blob is { State: BlobState.Sealed })
                {
                    End(blob, BlobState.Expired, "expired"); // its record says so for one more term
                }
                else if (blob is { State: BlobState.Uploading })
                {
                    DeleteData(blob.Id); // never completed: nobody was told of it
                    Forget(blob);
                }
                else
                {
                    Forget(blob);
                }
            }
        }

        foreach (var t in _tickets.Where(t => t.Value.ExpiresOn <= now))
        {
            _tickets.TryRemove(t.Key, out _);
        }
    }

    // ---- chunks (the HTTP side) ----

    /// <summary>The ticket, if it is valid for this blob (and direction, if given).</summary>
    public Ticket? Check(string? token, Guid id, TicketDirection? direction)
    {
        if (token is null || !_tickets.TryGetValue(token, out var ticket) || ticket.BlobId != id || ticket.ExpiresOn <= _time.GetUtcNow()
            || (direction is { } d && ticket.Direction != d))
        {
            return null;
        }

        return ticket;
    }

    public BlobStatus Status(Guid id) => Get(id) is { } blob ? StatusOf(blob) : throw new RouterException("No such blob.", RouterErrorKind.NotFound);

    /// <summary>Stores one sealed chunk (or the manifest, index null), within the blob's reservation.</summary>
    public async Task PutAsync(Guid id, int? index, Stream body, CancellationToken cancellationToken)
    {
        var blob = Get(id) ?? throw new RouterException("No such blob.", RouterErrorKind.NotFound);
        if (blob.State != BlobState.Uploading)
        {
            throw new RouterException("That blob is no longer being uploaded.", RouterErrorKind.Conflict);
        }

        if (index is { } n && (n < 0 || n >= blob.ChunkCount))
        {
            throw new RouterException($"Chunk {n} is outside 0-{blob.ChunkCount - 1}.");
        }

        var max = index is null ? BlobCrypto.MaxSealedManifest : BlobCrypto.MaxSealedChunk;
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > max)
            {
                throw new RouterException($"A {(index is null ? "manifest" : "chunk")} is at most {max} bytes.");
            }
        }

        var path = PathOf(id, index);
        lock (_lock)
        {
            var stored = StatusOf(blob).Bytes - (File.Exists(path) ? new FileInfo(path).Length : 0);
            if (stored + buffer.Length > blob.ReservedBytes)
            {
                throw new RouterException($"The upload is over the {blob.ReservedBytes} bytes it reserved.");
            }
        }

        var temp = path + ".part";
        await using (var file = OperatingSystem.IsWindows() ? new FileStream(temp, FileMode.Create, FileAccess.Write)
                         : new FileStream(temp, new FileStreamOptions
                         {
                             Mode = FileMode.Create,
                             Access = FileAccess.Write,
                             UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite, // like router.db
                         }))
        {
            buffer.Position = 0;
            await buffer.CopyToAsync(file, cancellationToken);
        }

        File.Move(temp, path, overwrite: true);
    }

    /// <summary>A sealed blob's chunk (or manifest) file, if it's there.</summary>
    public string? FileOf(Guid id, int? index)
    {
        var path = PathOf(id, index);
        return Get(id) is { State: BlobState.Sealed } && File.Exists(path) ? path : null;
    }

    /// <summary>Bytes waiting for <paramref name="machine"/>: live blobs shared with it that it hasn't released.</summary>
    public long WaitingFor(string machine)
    {
        lock (_lock)
        {
            return WaitingFor(_blobs.Values.Where(b => b.IsLive), machine);
        }
    }

    internal static string Gb(long bytes) =>
        bytes >= 1_000_000_000 ? $"{bytes / 1e9:0.#} GB" : bytes >= 1_000_000 ? $"{bytes / 1e6:0.#} MB" : $"{bytes / 1e3:0.#} KB";

    // ---- helpers ----

    private static long WaitingFor(IEnumerable<BlobRecord> live, string machine) =>
        live.Where(b => b.SharedWith.Contains(machine) && !b.ReleasedBy.Contains(machine)).Sum(b => b.ReservedBytes);

    private BlobRecord Require(Guid id, string sender) =>
        Get(id) is { } blob && blob.Sender == sender ? blob : throw new RouterException("No such blob from this machine.", RouterErrorKind.NotFound);

    private BlobTicket Issue(Guid id, string machine, TicketDirection direction)
    {
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var expires = _time.GetUtcNow() + _options.CurrentValue.TicketLifetime;
        _tickets[token] = new Ticket(id, machine, direction, expires);
        return new BlobTicket(id, token, expires);
    }

    private BlobStatus StatusOf(BlobRecord blob)
    {
        var chunks = new List<int>();
        long bytes = 0;
        if (Directory.Exists(DirectoryOf(blob.Id)))
        {
            for (var i = 0; i < blob.ChunkCount; i++)
            {
                if (new FileInfo(PathOf(blob.Id, i)) is { Exists: true } f)
                {
                    chunks.Add(i);
                    bytes += f.Length;
                }
            }

            if (new FileInfo(PathOf(blob.Id, null)) is { Exists: true } m)
            {
                bytes += m.Length;
            }
        }

        return new BlobStatus(blob.Id, blob.State, blob.ChunkCount, chunks, File.Exists(PathOf(blob.Id, null)), bytes);
    }

    /// <summary>Ends a live blob: its data goes; a record stays for one more expiry term to tell late downloaders why.</summary>
    private void End(BlobRecord blob, BlobState state, string why)
    {
        DeleteData(blob.Id);
        Save(blob with { State = state, EndReason = why, ReservedBytes = 0, ExpiresOn = _time.GetUtcNow() + _options.CurrentValue.Expiry });
        _logger.LogInformation("Blob {Id}: {Why}", blob.Id.ToString()[..8], why);
    }

    private void Delete(BlobRecord blob, string why)
    {
        DeleteData(blob.Id);
        Forget(blob);
        _logger.LogInformation("Blob {Id} deleted: {Why}", blob.Id.ToString()[..8], why);
    }

    private void Forget(BlobRecord blob)
    {
        _store.DeleteBlob(blob.Id);
        _blobs.Remove(blob.Id);
    }

    private void DeleteData(Guid id)
    {
        try
        {
            if (Directory.Exists(DirectoryOf(id)))
            {
                Directory.Delete(DirectoryOf(id), recursive: true);
            }
        }
        catch (IOException e)
        {
            _logger.LogWarning(e, "Couldn't delete blob {Id}'s data", id);
        }
    }

    private void Save(BlobRecord blob)
    {
        _store.SaveBlob(blob);
        _blobs[blob.Id] = blob;
    }

    private long? FreeBytes()
    {
        try
        {
            return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(Root))!).AvailableFreeSpace;
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private string DirectoryOf(Guid id) => Path.Combine(Root, id.ToString("N"));

    private string PathOf(Guid id, int? index) =>
        Path.Combine(DirectoryOf(id), index is { } n ? n.ToString("D6", CultureInfo.InvariantCulture) : "manifest");
}

public enum TicketDirection
{
    Upload,
    Download,
}

public sealed record Ticket(Guid BlobId, string Machine, TicketDirection Direction, DateTimeOffset ExpiresOn);

/// <summary>A blob's record. Machines are ids. Live while uploading or sealed; <see cref="ReservedBytes"/> counts against the limits.</summary>
public sealed record BlobRecord(Guid Id, string Sender, IReadOnlyList<string> SharedWith, BlobState State, long ReservedBytes, int ChunkCount,
    DateTimeOffset CreatedOn, DateTimeOffset? SealedOn, DateTimeOffset ExpiresOn, IReadOnlyList<string> ReleasedBy, string? EndReason)
{
    public bool IsLive => State is BlobState.Uploading or BlobState.Sealed;
}
