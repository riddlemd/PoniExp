using System.Security.Cryptography;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using PoniExp.Core.Crypto;
using PoniExp.Core.Protocol;
using PoniExp.Server.Machines;

namespace PoniExp.Server.Hubs;

/// <summary>
/// The relay. A connection first says which protocol version it speaks, then proves which key it holds by
/// signing a one-time challenge; only connections holding an approved key may send, receive, or list
/// machines. Everything else is join-only.
/// </summary>
public sealed class RelayHub(
    MachineRegistry registry,
    MessageQueue queue,
    PresenceTracker presence,
    ConnectionGate gate,
    TrafficLog traffic,
    RouterStore store,
    PoniExp.Server.Blobs.BlobRegistry blobs,
    PoniExp.Server.Rooms.RoomRegistry rooms,
    PoniExp.Server.Jobs.JobBoard jobs,
    TimeProvider time,
    IOptionsSnapshot<RouterOptions> options,
    ILogger<RelayHub> logger) : Hub
{
    internal const string MembersGroup = "members";
    private const string HelloItem = "hello";
    private const string ChallengeItem = "challenge";
    private const string ProvenItem = "proven";
    private const string MemberItem = "member";

    /// <summary>Every connection starts unauthenticated: admitted only within the limits, and with a deadline to prove a key.</summary>
    public override async Task OnConnectedAsync()
    {
        if (gate.Open(Context, Context.GetHttpContext()?.Connection.RemoteIpAddress) is { } refused)
        {
            logger.LogWarning("Refused a connection: {Reason}", refused);
            traffic.Refused($"connection: {refused}", Address);
            Context.Abort();
            return;
        }

        await base.OnConnectedAsync();
    }

    /// <summary>The first call on every connection. A client speaking another protocol version is refused.</summary>
    [HubMethodName(HubMethods.Hello)]
    public HelloResult Hello(int protocolVersion)
    {
        if (protocolVersion != WireProtocol.Version)
        {
            throw new HubException(protocolVersion < WireProtocol.Version
                ? $"This relay speaks protocol {WireProtocol.Version} and this client speaks {protocolVersion}: upgrade poniexp-client."
                : $"This relay speaks protocol {WireProtocol.Version} and this client speaks {protocolVersion}: upgrade the relay.");
        }

        Context.Items[HelloItem] = protocolVersion;
        return new HelloResult(WireProtocol.Version, time.GetUtcNow(), options.Value.ClockSkew);
    }

    [HubMethodName(HubMethods.GetChallenge)]
    public string GetChallenge()
    {
        RequireHello();
        var challenge = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        Context.Items[ChallengeItem] = challenge;
        return challenge;
    }

    [HubMethodName(HubMethods.Authenticate)]
    public async Task<AuthResult> AuthenticateAsync(string publicKey, string signature)
    {
        if (!TryItem(ChallengeItem, out var challenge))
        {
            throw new HubException("Ask for a challenge first.");
        }

        Context.Items.Remove(ChallengeItem); // one answer per challenge
        if (!PublicKeys.Verify(publicKey, Signatures.AuthPayload(challenge, publicKey), signature))
        {
            throw Refuse("The challenge signature doesn't verify.");
        }

        var id = KeyFingerprint.MachineIdOf(publicKey);
        await ProveKeyAsync(id);

        var record = registry.Get(id);
        if (record is { Status: MembershipStatus.Approved or MembershipStatus.Pending } && gate.Admit(Context.ConnectionId, id) is { } refused)
        {
            throw Refuse($"Refused: {refused}.", id);
        }

        if (record is not { Status: MembershipStatus.Approved })
        {
            return new AuthResult(record?.Status ?? MembershipStatus.Unknown, id, record?.Name);
        }

        if (!TryItem(MemberItem, out _))
        {
            Context.Items[MemberItem] = id;
            await Groups.AddToGroupAsync(Context.ConnectionId, MachineGroup(id));
            await Groups.AddToGroupAsync(Context.ConnectionId, MembersGroup);
            var now = time.GetUtcNow();
            var cameOnline = presence.Add(id, Context);
            await registry.TouchAsync(id, now);
            if (cameOnline)
            {
                logger.LogInformation("{Name} ({Id}) is online", record.Name, id[..8]);
                await Clients.Group(MembersGroup).SendAsync(HubMethods.PresenceChanged, new PresenceChange(id, record.Name, true, now));
            }
        }

        // Room state first: a queued room message is judged against the members as they are when it was sent.
        foreach (var room in rooms.Mine(id, []))
        {
            await Clients.Caller.SendAsync(HubMethods.RoomChanged, room);
        }

        foreach (var delivery in queue.For(id))
        {
            traffic.Delivered(delivery.Envelope);
            await Clients.Caller.SendAsync(HubMethods.Receive, delivery);
        }

        return new AuthResult(MembershipStatus.Approved, id, record.Name);
    }

    [HubMethodName(HubMethods.RequestJoin)]
    public async Task<AuthResult> RequestJoinAsync(JoinRequest request)
    {
        RequireHello();
        if (!Limits.IsValidName(request.Name))
        {
            throw new HubException("A name is 2-32 lowercase letters, digits or dashes, starting with a letter.");
        }

        var d = request.Description;
        if (d is null || new[] { d.OperatingSystem, d.Architecture, d.HostName, d.Notes ?? string.Empty }
                .Any(f => f is null || f.Length > Limits.MaxDescriptionField))
        {
            throw new HubException($"Description fields are required and at most {Limits.MaxDescriptionField} characters.");
        }

        if (!PublicKeys.IsValid(request.PublicKey) || !Signatures.VerifyJoin(request))
        {
            throw Refuse("The join request isn't signed by the key it carries, or lacks an encryption key signed by it.");
        }

        RequireFresh(request.RequestedOn);
        var newRequest = registry.IsNewRequest(KeyFingerprint.MachineIdOf(request.PublicKey));
        if (newRequest && gate.TakeJoin(Address) is { } limited)
        {
            throw Refuse($"Refused: {limited}.", KeyFingerprint.MachineIdOf(request.PublicKey));
        }

        MachineRecord record;
        try
        {
            record = await registry.SubmitJoinAsync(request);
        }
        catch (RouterException e)
        {
            throw Refuse(e.Message, KeyFingerprint.MachineIdOf(request.PublicKey));
        }

        await ProveKeyAsync(record.Id);
        if (record.Status is MembershipStatus.Pending or MembershipStatus.Approved && gate.Admit(Context.ConnectionId, record.Id) is { } refused)
        {
            throw Refuse($"Refused: {refused}.", record.Id);
        }

        if (newRequest && record.Status == MembershipStatus.Pending)
        {
            traffic.JoinRequested(record.Id, record.Name, Address);
        }

        logger.LogInformation("Join request: {Name} ({Id}) on {Os} {Arch}, join code {Code}: {Status}",
            request.Name, record.Id[..8], d.OperatingSystem, d.Architecture, record.JoinCode, record.Status);
        return new AuthResult(record.Status, record.Id, record.Name);
    }

    /// <summary>
    /// Checks what the router can see (sender, recipient, size, expiry, who the content key is wrapped for,
    /// the signature) and queues the envelope. The content is encrypted: its rules are the recipient's to check.
    /// </summary>
    [HubMethodName(HubMethods.Send)]
    public async Task<SendResult> SendAsync(Envelope envelope)
    {
        var me = RequireMember();
        if (envelope.FromMachine != me)
        {
            throw Refuse("An envelope's FromMachine must be your own machine id.", me, envelope);
        }

        if (envelope.Ciphertext is null || envelope.Size is < 0 or > Limits.MaxContentBytes)
        {
            throw Refuse($"The encrypted content must be at most {Limits.MaxContentBytes} bytes.", me, envelope);
        }

        // A room envelope goes to the machines of the room's members, and must be wrapped for exactly those; a message to its one recipient.
        var isRoom = RoomAddress.TryParse(envelope.ToMachine, out var roomId);
        List<string> recipients;
        if (isRoom)
        {
            try
            {
                recipients = [.. rooms.CheckMessage(me, envelope, roomId)];
            }
            catch (RouterException e)
            {
                throw Refuse(e.Message, me, envelope);
            }
        }
        else
        {
            recipients = [envelope.ToMachine];
            if (envelope.Wraps is null || envelope.Wraps.Count is 0 or > 2 || envelope.Wraps.All(w => w.Machine != envelope.ToMachine)
                || envelope.Wraps.Any(w => w.Machine != envelope.ToMachine && w.Machine != me))
            {
                throw Refuse("The content key must be wrapped for the recipient (and at most the sender too).", me, envelope);
            }
        }

        if (!Signatures.VerifyEnvelope(envelope, registry.Get(me)!.PublicKey))
        {
            throw Refuse("The envelope signature doesn't verify.", me, envelope);
        }

        if (envelope.ExpiresOn <= time.GetUtcNow())
        {
            throw Refuse("The envelope has already expired; check this machine's clock.", me, envelope);
        }

        foreach (var recipient in recipients)
        {
            if (registry.Get(recipient) is not { Status: MembershipStatus.Approved })
            {
                throw Refuse(isRoom ? $"{recipient[..Math.Min(8, recipient.Length)]} isn't an approved machine." : "The recipient isn't an approved machine.",
                    me, envelope);
            }

            var queued = queue.QueuedBytesFor(recipient);
            if (queued + envelope.Size > options.Value.MaxQueuedBytesPerRecipient && queue.ExpiryOf(envelope.Id) is null)
            {
                throw Refuse($"Refused: {registry.Get(recipient)!.Name} has {PoniExp.Server.Blobs.BlobRegistry.Gb(queued)} of messages waiting, " +
                             $"the most the router holds for one machine; it must come online and fetch them first.", me, envelope);
            }
        }

        var online = recipients.Where(presence.IsOnline).ToList();
        if (isRoom && queue.ExpiryOf(envelope.Id) is null)
        {
            rooms.Record(roomId, envelope);
        }

        if (recipients.Count > 0 && await queue.EnqueueAsync(envelope, isRoom ? recipients : null) is { } delivery)
        {
            foreach (var recipient in online)
            {
                traffic.Delivered(envelope); // before the push: the recipient may confirm before SendAsync returns
                await Clients.Group(MachineGroup(recipient)).SendAsync(HubMethods.Receive, delivery);
            }
        }

        return new SendResult(envelope.Id, online.Count == recipients.Count ? DeliveryState.Pushed : DeliveryState.Queued);
    }

    /// <summary>The recipient has stored the envelope: drop it from the queue.</summary>
    [HubMethodName(HubMethods.Confirm)]
    public Task ConfirmAsync(Guid id) => queue.ConfirmAsync(RequireMember(), id);

    // ---- files ----

    [HubMethodName(HubMethods.BeginUpload)]
    public BlobTicket BeginUpload(BeginUploadRequest request)
    {
        var me = RequireMember();
        return Blob(() => blobs.Begin(me, request), me);
    }

    [HubMethodName(HubMethods.ResumeUpload)]
    public BlobTicket ResumeUpload(Guid blobId)
    {
        var me = RequireMember();
        return Blob(() => blobs.Resume(me, blobId), me);
    }

    [HubMethodName(HubMethods.CompleteUpload)]
    public BlobStatus CompleteUpload(Guid blobId)
    {
        var me = RequireMember();
        return Blob(() => blobs.Complete(me, blobId), me);
    }

    [HubMethodName(HubMethods.RequestDownload)]
    public BlobTicket RequestDownload(Guid blobId)
    {
        var me = RequireMember();
        return Blob(() => blobs.RequestDownload(me, blobId), me);
    }

    [HubMethodName(HubMethods.ReleaseBlob)]
    public void ReleaseBlob(Guid blobId) => blobs.Release(RequireMember(), blobId);

    [HubMethodName(HubMethods.CancelBlob)]
    public void CancelBlob(Guid blobId)
    {
        var me = RequireMember();
        Blob(() =>
        {
            blobs.Cancel(me, blobId);
            return 0;
        }, me);
    }

    /// <summary>The caller's own uploads, with who they're shared with and who has released them.</summary>
    [HubMethodName(HubMethods.GetBlobs)]
    public IReadOnlyList<SenderBlob> GetBlobs()
    {
        var me = RequireMember();
        string Name(string id) => registry.Get(id)?.Name ?? id[..Math.Min(8, id.Length)];
        return [.. blobs.List().Where(b => b.Sender == me).Select(b => new SenderBlob(b.Id, b.State, b.ReservedBytes,
            [.. b.SharedWith.Select(Name)], [.. b.ReleasedBy.Select(Name)], b.CreatedOn, b.ExpiresOn))];
    }

    private T Blob<T>(Func<T> work, string me)
    {
        try
        {
            return work();
        }
        catch (RouterException e)
        {
            throw Refuse(e.Message, me);
        }
    }

    // ---- rooms ----

    [HubMethodName(HubMethods.CreateRoom)]
    public Task<RoomState> CreateRoomAsync(RoomEvent create)
    {
        var me = RequireMember();
        return Room(() => rooms.CreateAsync(me, create), me);
    }

    [HubMethodName(HubMethods.SubmitRoomEvent)]
    public Task<RoomState> SubmitRoomEventAsync(RoomEvent e)
    {
        var me = RequireMember();
        return Room(() => rooms.SubmitAsync(me, e), me);
    }

    [HubMethodName(HubMethods.GetRoom)]
    public Task<RoomState> GetRoomAsync(Guid roomId)
    {
        var me = RequireMember();
        return Room(() => Task.FromResult(rooms.Get(me, roomId)), me);
    }

    /// <summary>Live rooms this machine is involved in, and what became of the ones it knows that aren't.</summary>
    [HubMethodName(HubMethods.GetMyRooms)]
    public IReadOnlyList<RoomState> GetMyRooms(IReadOnlyList<Guid> known) => rooms.Mine(RequireMember(), known ?? []);

    // ---- the job board ----

    [HubMethodName(HubMethods.SubmitJobAction)]
    public Task<Job> SubmitJobActionAsync(JobAction action)
    {
        var me = RequireMember();
        return Room(() => jobs.SubmitAsync(me, action), me);
    }

    [HubMethodName(HubMethods.GetJobs)]
    public IReadOnlyList<Job> GetJobs(bool includeFinished)
    {
        RequireMember();
        return jobs.List(includeFinished);
    }

    [HubMethodName(HubMethods.GetJob)]
    public Job GetJob(Guid id)
    {
        RequireMember();
        return jobs.Get(id) ?? throw new HubException("No such job.");
    }

    /// <summary>The claimant's daemon keeps its leases alive; returns the jobs it still holds.</summary>
    [HubMethodName(HubMethods.RenewJobLeases)]
    public IReadOnlyList<Guid> RenewJobLeases(IReadOnlyList<Guid> ids) => jobs.Renew(RequireMember(), ids ?? []);

    private async Task<T> Room<T>(Func<Task<T>> work, string me)
    {
        try
        {
            return await work();
        }
        catch (RouterException e)
        {
            throw Refuse(e.Message, me);
        }
    }

    /// <summary>The owner's groups. Senders' daemons resolve them; the router never fans a message out to a group.</summary>
    [HubMethodName(HubMethods.GetGroups)]
    public IReadOnlyList<GroupInfo> GetGroups()
    {
        RequireMember();
        return store.LoadGroups();
    }

    [HubMethodName(HubMethods.GetMachines)]
    public IReadOnlyList<MachineInfo> GetMachines()
    {
        RequireMember();
        return registry.List()
            .Where(m => m.Status == MembershipStatus.Approved)
            .Select(m => new MachineInfo(m.Id, m.Name, m.Description, m.PublicKey, m.EncryptionKey, m.EncryptionKeySignature,
                presence.IsOnline(m.Id), m.LastSeenOn))
            .ToList();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        gate.Close(Context.ConnectionId);
        if (presence.Remove(Context.ConnectionId, out var id) && id is not null)
        {
            var now = time.GetUtcNow();
            await registry.TouchAsync(id, now);
            var name = registry.Get(id)?.Name ?? id;
            logger.LogInformation("{Name} ({Id}) is offline", name, id[..8]);
            await Clients.Group(MembersGroup).SendAsync(HubMethods.PresenceChanged, new PresenceChange(id, name, false, now));
        }

        await base.OnDisconnectedAsync(exception);
    }

    internal static string MachineGroup(string id) => "machine:" + id;

    private System.Net.IPAddress? Address => Context.GetHttpContext()?.Connection.RemoteIpAddress;

    /// <summary>Records a refusal in the traffic log (if it's on) and returns the exception that tells the caller.</summary>
    private HubException Refuse(string reason, string? machine = null, Envelope? envelope = null)
    {
        traffic.Refused(reason, Address, machine, envelope);
        return new HubException(reason);
    }

    /// <summary>Connections that proved a key, approved or not: where membership changes are announced.</summary>
    internal static string KeyGroup(string id) => "key:" + id;

    private async Task ProveKeyAsync(string id)
    {
        if (TryItem(ProvenItem, out var proven))
        {
            if (proven != id)
            {
                throw new HubException("This connection already proved a different key.");
            }

            return;
        }

        Context.Items[ProvenItem] = id;
        await Groups.AddToGroupAsync(Context.ConnectionId, KeyGroup(id));
    }

    private void RequireHello()
    {
        if (!Context.Items.ContainsKey(HelloItem))
        {
            throw new HubException($"Say hello with the protocol version first (this relay speaks {WireProtocol.Version}).");
        }
    }

    private string RequireMember()
    {
        if (TryItem(MemberItem, out var id) && registry.Get(id) is { Status: MembershipStatus.Approved })
        {
            return id;
        }

        throw new HubException("This connection isn't an approved machine; authenticate first.");
    }

    private void RequireFresh(DateTimeOffset stamp)
    {
        if ((time.GetUtcNow() - stamp).Duration() > options.Value.ClockSkew)
        {
            throw new HubException("The signed timestamp is too far from the router's clock; check this machine's time.");
        }
    }

    private bool TryItem(string key, out string value)
    {
        if (Context.Items.TryGetValue(key, out var item) && item is string s)
        {
            value = s;
            return true;
        }

        value = string.Empty;
        return false;
    }
}
