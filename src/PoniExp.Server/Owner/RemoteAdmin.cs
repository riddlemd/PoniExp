using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PoniExp.Core.Crypto;
using PoniExp.Core.Protocol;
using PoniExp.Server.Admin;
using PoniExp.Server.Machines;

namespace PoniExp.Server.Owner;

/// <summary>
/// Owner admin from anywhere (see docs/plan.md, Phase 10): a sealed, owner-signed command, run through the same admin
/// code <c>docker exec</c> uses, its output sealed back to the asking machine and signed by the router.
/// </summary>
public sealed class RemoteAdmin(RouterKeys keys, OwnerDesk desk, AdminApprovals approvals, AdminService admin, MachineRegistry machines, RouterStore store,
    IOptionsMonitor<RouterOptions> options, TimeProvider time, ILogger<RemoteAdmin> logger)
{
    /// <summary>
    /// A joining machine approves itself with the owner key (its SSH agent holds it): the first machine on a router that
    /// already has an owner key has no desk to be approved from.
    /// </summary>
    public async Task ApproveOwnJoinAsync(string machineId, AdminCommand command)
    {
        if (machines.Get(machineId) is not { Status: MembershipStatus.Pending })
        {
            throw new RouterException("Only a machine waiting to join can approve itself.");
        }

        if (command.Args is not ["approve", var who] || who != machineId || command.PendingId is not null || command.Input is not null)
        {
            throw new RouterException("That signature isn't for approving this machine.");
        }

        Check(command);
        logger.LogInformation("{Id} approved itself with the owner key ({Fingerprint})", machineId[..8], desk.Owner!.Fingerprint);
        await admin.ApproveAsync(machineId);
    }

    private readonly DateTimeOffset _started = time.GetUtcNow();
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _seen = new();

    /// <exception cref="RouterException">It can't be opened, or isn't a command the owner signed for now and here.</exception>
    public async Task<SignedAdminResult> RunAsync(string from, SealedBox sealedCommand)
    {
        var machine = machines.Get(from) ?? throw new RouterException("Unknown machine.");
        AdminCommand command;
        try
        {
            command = JsonSerializer.Deserialize<AdminCommand>(sealedCommand.Open(AdminCommand.SealPurpose, keys.Id, keys.Keys.Encryption), WireProtocol.Json)
                      ?? throw new RouterException("Empty admin command.");
        }
        catch (Exception e) when (e is CryptographicException or JsonException)
        {
            throw new RouterException("That admin command isn't sealed to this router.");
        }

        var input = Check(command);
        int exit;
        var output = new StringWriter();
        if (options.CurrentValue.AdminPort is not { } port)
        {
            output.WriteLine("error: the router's admin listener is off (Router:AdminPort)");
            exit = 1;
        }
        else
        {
            logger.LogInformation("Remote admin from {Name} ({Id}), signed by {Fingerprint}: {Command}", machine.Name, from[..8], desk.Owner!.Fingerprint,
                string.Join(" ", command.Args));
            try
            {
                exit = await AdminCli.RunAsync([.. command.Args, "--url", $"http://127.0.0.1:{port}"], output, input, approvals.Token);
            }
            catch (Exception e) when (e is IOException or InvalidOperationException or JsonException or FormatException)
            {
                output.WriteLine($"error: {e.Message}");
                exit = 1;
            }

            if (exit == 0)
            {
                await AfterAsync(command);
            }
        }

        var done = new AdminResult(command.Id, exit, output.ToString());
        if (command.PendingId is { } pendingId)
        {
            await approvals.CompleteAsync(pendingId, done); // the waiting docker exec prints it
        }

        var result = JsonSerializer.SerializeToUtf8Bytes(done, WireProtocol.Json);
        var box = SealedBox.Seal(result, AdminCommand.ResultPurpose, from, machine.EncryptionKey);
        return new SignedAdminResult(box, keys.Keys.Sign(SignedAdminResult.Payload(command.Id, from, box)));
    }

    /// <summary>Checks it; returns the input to run it with (a docker exec request's, if it approves one).</summary>
    private string? Check(AdminCommand command)
    {
        var owner = desk.Owner ?? throw new RouterException("The router has no owner key yet; set it with docker exec (poniexp admin owner set).");
        var now = time.GetUtcNow();
        var skew = options.CurrentValue.ClockSkew;
        if (command.Args is null || command.Args.Count == 0 || command.Args.Count > 300 || command.Args.Any(a => a is null || a == "--url")
            || command.Input?.Length > 64 * 1024 * 1024)
        {
            throw new RouterException("That isn't an admin command.");
        }

        if (command.Args is ["revoke", _] && command.RevokedMachine is null)
        {
            throw new RouterException("A signed revoke names the machine's full id.");
        }

        if (command.Args is ["backup" or "restore", var path, ..] && path != "-")
        {
            throw new RouterException("A remote backup or restore uses '-' (poniexp-client reads and writes the file on your machine).");
        }

        if (command.RouterId != keys.Id)
        {
            throw new RouterException("That admin command is for another router.");
        }

        if (!command.IsSignedBy(owner))
        {
            throw new RouterException("That admin command isn't signed by the owner's key.");
        }

        if ((now - command.IssuedOn).Duration() > skew || command.IssuedOn < _started)
        {
            throw new RouterException("That admin command is too old (or this machine's clock is off); sign it again.");
        }

        foreach (var old in _seen.Where(s => now - s.Value > skew * 2))
        {
            _seen.TryRemove(old.Key, out _);
        }

        var input = command.Input;
        if (command.PendingId is { } pendingId)
        {
            if (approvals.Get(pendingId) is not { State: "pending" } pending)
            {
                throw new RouterException("That docker exec change isn't waiting any more (answered, denied or timed out).");
            }

            if (!pending.Request.Args.SequenceEqual(command.Args) || pending.Request.InputSha256 != command.InputSha256 || command.Input is not null)
            {
                throw new RouterException("That signature is for a different command than the one waiting.");
            }

            input = pending.Input;
        }
        else if (command.InputSha256 is not null)
        {
            throw new RouterException("That isn't an admin command.");
        }

        if (!_seen.TryAdd(command.Id, now))
        {
            throw new RouterException("That admin command already ran.");
        }

        return input;
    }

    /// <summary>What the signature proves to daemons: an owner handover, or a signed group.</summary>
    private async Task AfterAsync(AdminCommand command)
    {
        if (command.NewOwnerKey is not null)
        {
            await desk.AddHandoverAsync(command);
        }
        else if (command.RevokedMachine is not null)
        {
            await desk.AddRevocationAsync(command);
        }
        else if (command.GroupSet is { } group)
        {
            store.SetState(AdminService.GroupSignatureState(group.Name), JsonSerializer.Serialize(command, WireProtocol.Json));
        }
    }
}
