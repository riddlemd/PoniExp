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
    TimeProvider time,
    IOptionsSnapshot<RouterOptions> options,
    ILogger<RelayHub> logger) : Hub
{
    internal const string MembersGroup = "members";
    private const string HelloItem = "hello";
    private const string ChallengeItem = "challenge";
    private const string ProvenItem = "proven";
    private const string MemberItem = "member";

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
        return new HelloResult(WireProtocol.Version);
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
            throw new HubException("The challenge signature doesn't verify.");
        }

        var id = KeyFingerprint.MachineIdOf(publicKey);
        await ProveKeyAsync(id);

        var record = registry.Get(id);
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

        foreach (var delivery in queue.For(id))
        {
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
            throw new HubException("The join request isn't signed by the key it carries.");
        }

        RequireFresh(request.RequestedOn);
        MachineRecord record;
        try
        {
            record = await registry.SubmitJoinAsync(request);
        }
        catch (RouterException e)
        {
            throw new HubException(e.Message);
        }

        await ProveKeyAsync(record.Id);
        logger.LogInformation("Join request: {Name} ({Id}) on {Os} {Arch}, join code {Code}: {Status}",
            request.Name, record.Id[..8], d.OperatingSystem, d.Architecture, record.JoinCode, record.Status);
        return new AuthResult(record.Status, record.Id, record.Name);
    }

    /// <summary>
    /// Checks what the router can see (sender, recipient, size, expiry, signature) and queues the envelope.
    /// The content's own rules are the recipient's to check: from phase 2 the router can't read it.
    /// </summary>
    [HubMethodName(HubMethods.Send)]
    public async Task<SendResult> SendAsync(Envelope envelope)
    {
        var me = RequireMember();
        if (envelope.FromMachine != me)
        {
            throw new HubException("An envelope's FromMachine must be your own machine id.");
        }

        if (envelope.Content is null || envelope.Size is < 0 or > Limits.MaxContentBytes)
        {
            throw new HubException($"The content must be at most {Limits.MaxContentBytes} bytes.");
        }

        if (!Signatures.VerifyEnvelope(envelope, registry.Get(me)!.PublicKey))
        {
            throw new HubException("The envelope signature doesn't verify.");
        }

        if (envelope.ExpiresOn <= time.GetUtcNow())
        {
            throw new HubException("The envelope has already expired; check this machine's clock.");
        }

        if (registry.Get(envelope.ToMachine) is not { Status: MembershipStatus.Approved })
        {
            throw new HubException("The recipient isn't an approved machine.");
        }

        var online = presence.IsOnline(envelope.ToMachine);
        if (await queue.EnqueueAsync(envelope) is { } delivery && online)
        {
            await Clients.Group(MachineGroup(envelope.ToMachine)).SendAsync(HubMethods.Receive, delivery);
        }

        return new SendResult(envelope.Id, online ? DeliveryState.Pushed : DeliveryState.Queued);
    }

    /// <summary>The recipient has stored the envelope: drop it from the queue.</summary>
    [HubMethodName(HubMethods.Confirm)]
    public Task ConfirmAsync(Guid id) => queue.ConfirmAsync(RequireMember(), id);

    [HubMethodName(HubMethods.GetMachines)]
    public IReadOnlyList<MachineInfo> GetMachines()
    {
        RequireMember();
        return registry.List()
            .Where(m => m.Status == MembershipStatus.Approved)
            .Select(m => new MachineInfo(m.Id, m.Name, m.Description, m.PublicKey, presence.IsOnline(m.Id), m.LastSeenOn))
            .ToList();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
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
