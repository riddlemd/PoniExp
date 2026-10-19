using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PoniExp.Client;
using PoniExp.Core.Crypto;
using PoniExp.Core.Protocol;

namespace PoniExp.Daemon;

/// <summary>What a daemon runs on: this machine's keys, its home directory, how to reach the router, and its database.</summary>
public sealed record DaemonContext(MachineKeys Keys, string Home, RelayClientOptions Relay, ClientStore Store, LocalAddress Address);

/// <summary>
/// The machine's daemon: its only connection to the router. It routes incoming messages to local
/// agents (holding them while an agent is offline, rejecting them for an unknown one), sends the
/// delivery receipts, keeps the agent registry and announces it to other machines, applies their
/// announcements, and runs the outbox. Everything a message causes (its event, its receipt) is written in
/// the transaction that stores it, so a crash never loses or doubles a receipt.
/// </summary>
public sealed partial class Daemon : BackgroundService, IIncomingHandler
{
    internal const string AnnouncementCounter = "agent-announcements";

    private readonly DaemonContext _context;
    private readonly RelayClient _relay;
    private readonly AgentSessions _sessions;
    private readonly IOptionsMonitor<DaemonOptions> _options;
    private readonly IOptionsMonitor<MessagingOptions> _messaging;
    private readonly IOptionsMonitor<RetentionOptions> _retention;
    private DateTimeOffset _lastRetention = DateTimeOffset.MinValue;
    private readonly TimeProvider _time;
    private readonly ILogger<Daemon> _logger;

    public Daemon(DaemonContext context, RelayClient relay, AgentSessions sessions, IOptionsMonitor<DaemonOptions> options,
        IOptionsMonitor<MessagingOptions> messaging, IOptionsMonitor<RetentionOptions> retention, TimeProvider time, ILogger<Daemon> logger, IHostApplicationLifetime lifetime)
    {
        // End the agents' streams first, so the server isn't left waiting for them to drain.
        lifetime.ApplicationStopping.Register(() =>
        {
            Stopping = true;
            foreach (var session in sessions.All)
            {
                session.End(leaving: false);
            }
        });
        _context = context;
        _relay = relay;
        _sessions = sessions;
        _options = options;
        _messaging = messaging;
        _retention = retention;
        _time = time;
        _logger = logger;
        relay.IncomingHandler = this;
        relay.MessageReceived += OnDeliveredAsync;
        relay.PresenceChanged += change =>
        {
            if (change.Online && change.MachineId != MachineId)
            {
                Background(() => AnnounceAsync([change.MachineId])); // it catches up on our agents
            }
        };
        relay.ConnectionChanged += (up, error) =>
        {
            if (up)
            {
                Background(() => AnnounceAsync(null));
            }
            else
            {
                Diagnose("warning", "connection", $"connection to the router lost: {error?.Message ?? "closed"}");
            }
        };
        relay.MessageRejected += (envelope, why) => Diagnose("warning", why.Contains("signature", StringComparison.Ordinal) ? "signature" : "delivery",
            $"refused a message: {why}", envelope?.FromMachine, envelope?.Id);
        relay.BackgroundError += e => Diagnose("error", "delivery", e.Message);
    }

    public string MachineId => _context.Keys.MachineId;

    public RelayClient Relay => _relay;

    public ClientStore Store => _context.Store;

    public AgentSessions Sessions => _sessions;

    public DaemonContext Context => _context;

    public DateTimeOffset Now => _time.GetUtcNow();

    /// <summary>Shutting down: states are still recorded, but nothing more is announced.</summary>
    public bool Stopping { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Agents that were online when the daemon last stopped aren't any more.
        Store.Transact(Now, tx =>
        {
            foreach (var e in tx.GetAgents().Where(e => e.State == AgentState.Online))
            {
                tx.SetAgentState(e.Name, AgentState.TimedOut);
            }
        });

        await ConnectAsync(stoppingToken);
        using var timer = new PeriodicTimer(_options.CurrentValue.SweepInterval, _time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            timer.Period = _options.CurrentValue.SweepInterval;
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Diagnose("error", "delivery", $"sweep failed: {e.Message}");
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var session in _sessions.All)
        {
            session.End(leaving: false);
        }

        await base.StopAsync(cancellationToken);
    }

    /// <summary>One pass of the periodic work: timed-out agents, expired incoming messages, the outbox.</summary>
    internal async Task SweepAsync(CancellationToken cancellationToken = default)
    {
        _sessions.TimeOut(Now, _options.CurrentValue.AgentTimeout);
        var skew = _messaging.CurrentValue.ClockSkew;
        Store.Transact(Now, tx => tx.ExpireIncoming(skew));
        await _relay.FlushOutboxAsync(cancellationToken);
        var retention = _retention.CurrentValue;
        if (Now - _lastRetention >= retention.Interval)
        {
            _lastRetention = Now;
            ApplyRetention(retention);
        }

        await ReleaseOnRouterAsync();
    }

    internal RetentionResult ApplyRetention(RetentionOptions? retention = null)
    {
        retention ??= _retention.CurrentValue;
        DeleteOldCopies(Now - retention.Envelopes);
        var result = Store.ApplyRetention(retention, Now);
        if (result is not { EnvelopesDropped: 0, MessagesDeleted: 0, DiagnosticsDeleted: 0, OperationsDeleted: 0 })
        {
            _logger.LogInformation("Retention: dropped {Envelopes} envelopes, deleted {Messages} messages, {Diagnostics} diagnostics, {Operations} operations",
                result.EnvelopesDropped, result.MessagesDeleted, result.DiagnosticsDeleted, result.OperationsDeleted);
        }

        return result;
    }

    /// <summary>How long an operation record is kept: no retry can be accepted after that.</summary>
    private TimeSpan OperationWindow => _messaging.CurrentValue.MaxExpiry + _messaging.CurrentValue.ClockSkew;

    // ---- incoming ----

    /// <summary>Inside the transaction that stored <paramref name="message"/>: its first event, and the receipt it causes.</summary>
    public void OnStored(StoreTransaction tx, ReceivedMessage message)
    {
        var (envelope, content) = (message.Envelope, message.Content);
        switch (content.Type)
        {
            case MessageType.Message when RoomAddress.TryParse(envelope.ToMachine, out var roomId):
                OnRoomMessage(tx, envelope, content, roomId);
                break;

            case MessageType.Room: // between daemons: membership, applied if an admin sent it
                OnRoomSnapshot(tx, envelope, content);
                break;

            case MessageType.Message:
                var agent = content.ToAgent == Address.DaemonAgent ? null : tx.GetAgent(content.ToAgent);
                if (agent is { RetiredOn: null })
                {
                    if (content.ReplyTo is null && tx.GetOperation(envelope.FromMachine, content.FromAgent, content.OperationId) is { } operation)
                    {
                        AnswerRetry(tx, envelope, content, operation);
                        break;
                    }

                    tx.AddEvent(envelope.FromMachine, envelope.Id, MessageDirection.In, content.ToAgent, MessageEvents.Stored);
                    ComposeReceipt(tx, envelope, content, new ReceiptBody(ReceiptKind.Received));
                    if (content.ReplyTo is null)
                    {
                        tx.StartOperation(envelope.FromMachine, content.FromAgent, content.OperationId, content.ToAgent, envelope.Id, Now + OperationWindow);
                    }
                }
                else
                {
                    var reason = $"unknown agent '{content.ToAgent}'";
                    tx.AddEvent(envelope.FromMachine, envelope.Id, MessageDirection.In, content.ToAgent, MessageEvents.Rejected, reason);
                    ComposeReceipt(tx, envelope, content, new ReceiptBody(ReceiptKind.Rejected, reason));
                }

                break;

            case MessageType.Receipt: // automatic: applied, never answered
                if (Parse<ReceiptBody>(content.Body) is not { } receipt || content.ReplyTo is not { } about)
                {
                    tx.Diagnose("warning", "delivery", "a malformed receipt", envelope.FromMachine, envelope.Id);
                    break;
                }

                if (tx.FindOutgoing(MachineId, about) is not { } sent || sent.ToMachine != envelope.FromMachine)
                {
                    tx.Diagnose("warning", "delivery", "a receipt for a message this machine didn't send to its sender", envelope.FromMachine, envelope.Id);
                    break;
                }

                tx.AddEvent(MachineId, about, MessageDirection.Out, sent.FromAgent, receipt.Receipt switch
                {
                    ReceiptKind.Received => MessageEvents.Received,
                    ReceiptKind.Acknowledged => MessageEvents.Acknowledged,
                    _ => MessageEvents.Rejected,
                }, receipt.Reason);
                break;

            case MessageType.FileReceipt: // automatic: applied, never answered
                OnFileReceipt(tx, envelope, content);
                break;

            case MessageType.Agent: // automatic: applied, never answered
                if (Parse<AgentAnnouncement>(content.Body) is { } announcement)
                {
                    tx.ApplyAnnouncement(envelope.FromMachine, announcement);
                }
                else
                {
                    tx.Diagnose("warning", "delivery", "a malformed agent announcement", envelope.FromMachine, envelope.Id);
                }

                break;

            default:
                tx.Diagnose("info", "delivery", $"ignored a {WireProtocol.NameOf(content.Type)} message (not handled yet)", envelope.FromMachine, envelope.Id);
                break;
        }
    }

    /// <summary>
    /// A retry of an operation this machine already has: the agent never sees it. The daemon takes it on
    /// (an <c>acknowledged</c> receipt) and answers for the agent: "still running" while the first is, or
    /// a copy of the final reply once it's finished.
    /// </summary>
    private void AnswerRetry(StoreTransaction tx, Envelope envelope, MessageContent content, OperationRecord operation)
    {
        var detail = $"retry of operation {operation.OperationId} (first message {operation.FirstMessageId}); answered by the daemon";
        tx.AddEvent(envelope.FromMachine, envelope.Id, MessageDirection.In, content.ToAgent, MessageEvents.Acknowledged, detail);
        ComposeReceipt(tx, envelope, content, new ReceiptBody(ReceiptKind.Acknowledged));
        if (_relay.Machine(envelope.FromMachine) is not { } sender)
        {
            return;
        }

        OutgoingMessage answer;
        if (operation is { State: "done", ResultMessageId: { } result } && tx.GetEnvelope(MachineId, result) is { } final)
        {
            var copy = _relay.Open(final);
            answer = new OutgoingMessage(copy.Body)
            {
                ContentType = copy.ContentType,
                Metadata = copy.Metadata,
                Status = copy.Status,
                Subject = copy.Subject,
                Attachments = copy.Attachments, // the same blobs, already shared with this machine
            };
        }
        else if (operation.State == "done")
        {
            tx.Diagnose("warning", "delivery", $"a retry of operation {operation.OperationId}, whose final reply is no longer stored",
                envelope.FromMachine, envelope.Id);
            answer = new OutgoingMessage("This operation has finished, but its result is no longer stored.") { Status = ReplyStatus.Failed };
        }
        else
        {
            answer = new OutgoingMessage("still running") { Status = ReplyStatus.Progress };
        }

        tx.Diagnose("info", "delivery", detail, envelope.FromMachine, envelope.Id);
        _relay.Compose(tx, sender, content.FromAgent, RelayClient.ReplyTo(envelope.Id, content, answer with
        {
            FromAgent = content.ToAgent,
            OperationId = content.OperationId,
        }));
    }

    /// <summary>After the transaction: hand the message to its agent if it's online; tell a sender agent about its receipts.</summary>
    private Task OnDeliveredAsync(ReceivedMessage message)
    {
        var content = message.Content;
        if (content.Type == MessageType.Message && RoomAddress.TryParse(message.Envelope.ToMachine, out var roomId))
        {
            if (Store.GetState(message.Envelope.FromMachine, message.Envelope.Id, MessageDirection.In) == MessageEvents.Received
                && Store.GetRoom(roomId) is { } room)
            {
                PushToRoom(room.Snapshot, Store.GetRoomMessages(MachineId, roomId, null).Single(m => m.Id == message.Envelope.Id), MessageDirection.In);
            }
        }
        else if (content.Type == MessageType.Message)
        {
            if (_sessions.Get(content.ToAgent) is { } session
                && Store.GetState(message.Envelope.FromMachine, message.Envelope.Id, MessageDirection.In) == MessageEvents.Stored)
            {
                Deliver(session, Store.FindIncoming(MachineId, content.ToAgent, message.Envelope.Id)!, content);
            }
        }
        else if (content.Type == MessageType.FileReceipt && Parse<FileReceiptBody>(content.Body) is { } fileReceipt && content.ReplyTo is { } carried
                 && _sessions.Get(content.ToAgent) is { } owner)
        {
            owner.Push(new LocalEvent(LocalEventKinds.FileReceipt, FileReceipt: new LocalFileReceipt(carried, fileReceipt.BlobId,
                WireProtocol.NameOf(fileReceipt.Receipt), fileReceipt.Reason, $"{message.FromName}/{content.FromAgent}")));
        }
        else if (content.Type == MessageType.Receipt && Parse<ReceiptBody>(content.Body) is { } receipt && content.ReplyTo is { } about
                 && _sessions.Get(content.ToAgent) is { } sender)
        {
            sender.Push(new LocalEvent(LocalEventKinds.Receipt, Receipt: new LocalReceipt(about, WireProtocol.NameOf(receipt.Receipt), receipt.Reason,
                $"{message.FromName}/{content.FromAgent}")));
        }

        return Task.CompletedTask;
    }

    /// <summary>Pushes a stored message to its agent's stream and records that it was fetched.</summary>
    internal void Deliver(AgentSession session, StoredMessage stored, MessageContent? content = null)
    {
        content ??= _relay.Open(stored.Envelope!);
        if (session.Push(new LocalEvent(LocalEventKinds.Message, Message: ToLocal(stored, MessageDirection.In, content) with { State = MessageEvents.Fetched })))
        {
            MarkFetched([stored]);
        }
    }

    internal void MarkFetched(IEnumerable<StoredMessage> messages) =>
        Store.Transact(Now, tx =>
        {
            foreach (var m in messages.Where(m => tx.GetState(m.FromMachine, m.Id, MessageDirection.In) == MessageEvents.Stored))
            {
                tx.AddEvent(m.FromMachine, m.Id, MessageDirection.In, m.ToAgent, MessageEvents.Fetched);
            }
        });

    // ---- acknowledging and replying ----

    /// <summary>
    /// An agent takes the message on: records <c>acknowledged</c> and sends the receipt, once. Acknowledging
    /// again is harmless; a rejected or expired message can't be acknowledged.
    /// </summary>
    internal async Task<StoredMessage> AcknowledgeAsync(string agent, Guid id, CancellationToken cancellationToken)
    {
        var stored = Store.FindIncoming(MachineId, agent, id) ?? throw new KeyNotFoundException($"no message {id} for agent '{agent}'");
        await _relay.ResolveAsync(stored.FromMachine, cancellationToken);
        Store.Transact(Now, tx => Acknowledge(tx, stored));
        await _relay.FlushOutboxAsync(cancellationToken);
        return Store.FindIncoming(MachineId, agent, id)!;
    }

    private void Acknowledge(StoreTransaction tx, StoredMessage stored)
    {
        var state = tx.GetState(stored.FromMachine, stored.Id, MessageDirection.In);
        if (state == MessageEvents.Acknowledged)
        {
            return;
        }

        if (MessageEvents.IsFinal(state))
        {
            throw new InvalidOperationException($"message {stored.Id} is {state}; it can't be acknowledged");
        }

        tx.AddEvent(stored.FromMachine, stored.Id, MessageDirection.In, stored.ToAgent, MessageEvents.Acknowledged);
        var content = _relay.Open(stored.Envelope!);
        ComposeReceipt(tx, stored.Envelope!, content, new ReceiptBody(ReceiptKind.Acknowledged));
    }

    /// <summary>Replies to a message for <paramref name="agent"/>; replying acknowledges it too (once).</summary>
    internal async Task<SentMessage> ReplyAsync(string agent, Guid id, OutgoingMessage reply, CancellationToken cancellationToken,
        IReadOnlyList<string>? files = null)
    {
        var stored = Store.FindIncoming(MachineId, agent, id) ?? throw new KeyNotFoundException($"no message {id} for agent '{agent}'");
        var content = _relay.Open(stored.Envelope!);
        var target = await _relay.ResolveAsync(stored.FromMachine, cancellationToken);
        if (files is { Count: > 0 })
        {
            reply = reply with { Attachments = await UploadAsync(files, [target.Id], cancellationToken) };
        }

        var envelope = Store.Transact(Now, tx =>
        {
            var sent = _relay.Compose(tx, target, content.FromAgent, RelayClient.ReplyTo(id, content, reply with { FromAgent = agent }));
            if (content.ReplyTo is null && reply.Status is ReplyStatus.Done or ReplyStatus.Failed)
            {
                tx.FinishOperation(stored.FromMachine, content.FromAgent, content.OperationId, sent.Id, Now + OperationWindow);
            }
            if (!MessageEvents.IsFinal(tx.GetState(stored.FromMachine, stored.Id, MessageDirection.In)))
            {
                Acknowledge(tx, stored);
            }

            return sent;
        });
        return (await _relay.RelayComposedAsync([envelope], cancellationToken))[0];
    }

    /// <summary>Sends from a local agent. An agent that sends is entered in the registry, so replies to it are held, not rejected.</summary>
    internal async Task<IReadOnlyList<SentMessage>> SendAsync(IReadOnlyCollection<string> to, OutgoingMessage message, CancellationToken cancellationToken,
        IReadOnlyList<string>? files = null)
    {
        var from = message.FromAgent ?? Address.DefaultAgent;
        to = await ResolveAddressesAsync(to, from, cancellationToken);
        if (files is { Count: > 0 })
        {
            // Uploaded once, shared with exactly the machines these messages go to.
            var machines = new HashSet<string>();
            foreach (var address in to.Select(Address.Parse))
            {
                machines.Add((await _relay.ResolveAsync(address.Machine, cancellationToken)).Id);
            }

            message = message with { Attachments = await UploadAsync(files, machines, cancellationToken) };
        }

        if (message is { OperationId: { } operation, ConversationId: null }
            && Store.GetLog(MachineId, new LogQuery(Operation: operation, Agent: from, Limit: 1)).FirstOrDefault() is { } earlier)
        {
            message = message with { ConversationId = earlier.ConversationId }; // a retry stays in the first attempt's conversation
        }

        Store.Transact(Now, tx =>
        {
            if (tx.GetAgent(from) is null)
            {
                tx.SetAgentState(from, AgentState.Left);
            }
        });
        return await _relay.SendAsync(to, message with { FromAgent = from }, cancellationToken);
    }

    // ---- the agent registry ----

    /// <summary>Records a local agent's new state and announces the agent list to every online machine.</summary>
    internal async Task SetAgentStateAsync(string name, AgentState state, IReadOnlyList<string>? roles = null)
    {
        Store.Transact(Now, tx =>
        {
            tx.SetAgentState(name, state, roles);
            if (!Stopping)
            {
                ComposeAnnouncements(tx, null);
            }
        });
        if (!Stopping)
        {
            await FlushQuietlyAsync();
        }
    }

    /// <summary>Retires an agent: its messages get a <c>rejected</c> receipt from now on, and it drops out of the announcements.</summary>
    internal async Task<bool> RetireAsync(string name)
    {
        var retired = Store.Transact(Now, tx =>
        {
            if (!tx.RetireAgent(name))
            {
                return false;
            }

            ComposeAnnouncements(tx, null);
            return true;
        });
        await FlushQuietlyAsync();
        return retired;
    }

    /// <summary>Sends this daemon's agent list to <paramref name="machines"/> (null: every online machine).</summary>
    internal async Task AnnounceAsync(IReadOnlyCollection<string>? machines)
    {
        if (!_relay.IsConnected)
        {
            return;
        }

        Store.Transact(Now, tx => ComposeAnnouncements(tx, machines));
        await FlushQuietlyAsync();
    }

    private void ComposeAnnouncements(StoreTransaction tx, IReadOnlyCollection<string>? machines)
    {
        var targets = _relay.Machines.Where(m => m.Id != MachineId && (machines?.Contains(m.Id) ?? m.Online)).ToList();
        if (targets.Count == 0)
        {
            return;
        }

        var announcement = new AgentAnnouncement(tx.NextCounter(AnnouncementCounter), [.. tx.GetAgents()
            .Where(e => e.RetiredOn is null)
            .Select(e => new AnnouncedAgent(e.Name, _sessions.IsOnline(e.Name) ? AgentState.Online : e.State == AgentState.Online
                ? AgentState.TimedOut : e.State, e.ChangedOn, e.Roles is { Count: > 0 } r ? r : null))]);
        var body = JsonSerializer.Serialize(announcement, WireProtocol.Json);
        foreach (var machine in targets)
        {
            try
            {
                _relay.Compose(tx, machine, Address.DaemonAgent, Automatic(body, MessageType.Agent) with { FromAgent = Address.DaemonAgent });
            }
            catch (InvalidOperationException e)
            {
                tx.Diagnose("warning", "signature", e.Message, machine.Id);
            }
        }
    }

    // ---- automatic messages ----

    private void ComposeReceipt(StoreTransaction tx, Envelope envelope, MessageContent content, ReceiptBody receipt) =>
        ComposeAutomatic(tx, envelope, content, JsonSerializer.Serialize(receipt, WireProtocol.Json), MessageType.Receipt);

    private void ComposeAutomatic(StoreTransaction tx, Envelope envelope, MessageContent content, string body, MessageType type)
    {
        if (_relay.Machine(envelope.FromMachine) is not { } sender)
        {
            tx.Diagnose("warning", "delivery", "can't send a receipt to an unknown machine", envelope.FromMachine, envelope.Id);
            return;
        }

        _relay.Compose(tx, sender, content.FromAgent, Automatic(body, type) with
        {
            FromAgent = content.ToAgent,
            ConversationId = content.ConversationId,
            ReplyTo = envelope.Id,
        });
    }

    private OutgoingMessage Automatic(string body, MessageType type) => new(body)
    {
        Type = type,
        ContentType = "application/json",
        ExpiresIn = _options.CurrentValue.AutomaticExpiry,
    };

    // ---- helpers ----

    /// <summary>A stored message as an agent sees it.</summary>
    internal LocalMessage ToLocal(StoredMessage stored, MessageDirection direction, MessageContent? content = null)
    {
        content ??= _relay.Open(stored.Envelope!);
        var incoming = direction == MessageDirection.In;
        var to = RoomAddress.TryParse(stored.ToMachine, out var roomId) ? $"room:{Store.GetRoom(roomId)?.Snapshot.Name ?? roomId.ToString()}"
            : $"{NameOf(stored.ToMachine)}/{content.ToAgent}";
        return new LocalMessage(stored.Id, stored.FromMachine, $"{NameOf(stored.FromMachine)}/{content.FromAgent}", to, incoming ? "in" : "out", content.ConversationId, content.ReplyTo, content.OperationId,
            content.Status is { } s ? WireProtocol.NameOf(s) : null, content.Subject, WireProtocol.NameOf(content.Type), content.ContentType,
            content.Metadata, content.Body, content.CreatedOn, stored.ExpiresOn, stored.State, stored.StateDetail,
            content.Attachments is { Count: > 0 } ? AttachmentsOf(stored, direction) : null);
    }

    internal string NameOf(string machineId) => _relay.Machine(machineId)?.Name ?? machineId[..Math.Min(8, machineId.Length)];

    internal LocalSent ToLocal(SentMessage sent) =>
        new(sent.Id, $"{NameOf(sent.ToMachine)}/{sent.ToAgent}", sent.ConversationId, sent.Sequence, sent.ExpiresOn,
            sent.State is { } state ? WireProtocol.NameOf(state) : "outbox");

    internal void Diagnose(string level, string category, string detail, string? machine = null, Guid? messageId = null)
    {
        _logger.LogInformation("{Category}: {Detail}", category, detail);
        try
        {
            Store.Transact(Now, tx => tx.Diagnose(level, category, detail, machine, messageId));
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "couldn't record a diagnostic");
        }
    }

    private async Task ConnectAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var status = await _relay.ConnectAsync(stoppingToken);
                if (status != MembershipStatus.Approved)
                {
                    Diagnose("warning", "connection", $"this machine is {WireProtocol.NameOf(status)} at the router; run 'poniexp-client join <name>'");
                }

                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                Diagnose("warning", e.Message.Contains("protocol", StringComparison.OrdinalIgnoreCase) ? "version" : "connection",
                    $"can't reach the router: {e.Message}");
                await Task.Delay(_options.CurrentValue.ConnectRetry, _time, stoppingToken);
            }
        }
    }

    private async Task FlushQuietlyAsync()
    {
        try
        {
            await _relay.FlushOutboxAsync();
        }
        catch (Exception e)
        {
            Diagnose("warning", "delivery", $"outbox: {e.Message}");
        }
    }

    private void Background(Func<Task> work) =>
        _ = Task.Run(async () =>
        {
            try
            {
                await work();
            }
            catch (Exception e)
            {
                Diagnose("warning", "delivery", e.Message);
            }
        });

    private static T? Parse<T>(string body) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(body, WireProtocol.Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
