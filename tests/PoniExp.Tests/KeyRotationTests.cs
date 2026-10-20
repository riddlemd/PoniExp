using PoniExp.Client;
using PoniExp.Core.Crypto;
using PoniExp.Core.Protocol;
using PoniExp.Server.Machines;

namespace PoniExp.Tests;

/// <summary>Phase 11: encryption key rotation, and rejoining with new keys.</summary>
public class KeyRotationTests
{
    [Fact]
    public async Task ARotatedKeyIsUsedBySendersAndOlderMessagesStillOpen()
    {
        await using var router = await RouterFixture.StartAsync();
        await using var ise = await DaemonFixture.StartAsync(router, "ise");
        await using var wojtek = await DaemonFixture.StartAsync(router, "wojtek");
        await using var agent = new AgentConnection(wojtek.Api, "default", _ => Task.CompletedTask);
        await agent.Registered.WaitAsync(TimeSpan.FromSeconds(10));
        await ise.Api.SendAsync(new LocalSendRequest("default", ["wojtek"], "before the rotation"));
        await RouterFixture.EventuallyAsync(async () => (await wojtek.Api.InboxAsync("default")).Count == 1);
        var oldKey = wojtek.Keys.EncryptionPublicKey;

        var keys = await wojtek.Api.RotateKeysAsync();
        Assert.Single(keys.Retired);
        Assert.NotEqual(oldKey, wojtek.Keys.EncryptionPublicKey);
        await RouterFixture.Eventually(() => ise.Daemon.Relay.Machine(wojtek.Keys.MachineId)?.EncryptionKey == wojtek.Keys.EncryptionPublicKey);

        var sent = await ise.Api.SendAsync(new LocalSendRequest("default", ["wojtek"], "after the rotation"));
        await RouterFixture.EventuallyAsync(async () => (await wojtek.Api.InboxAsync("default")).Count == 2);
        var bodies = (await wojtek.Api.InboxAsync("default")).Select(m => m.Body).Order().ToList();
        Assert.Equal(["after the rotation", "before the rotation"], bodies); // the first opens with the retired key
        var envelope = (await wojtek.Store.GetMessageAsync(ise.Keys.MachineId, sent.Single().Id))!.Envelope!;
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => MessageCrypto.Open(envelope, wojtek.Keys.MachineId, wojtek.Keys.RetiredEncryptionKeys[0].Key));
    }

    [Fact]
    public async Task AnOlderKeyHandedOutByTheRouterIsRefused()
    {
        await using var router = await RouterFixture.StartAsync();
        await using var ise = await DaemonFixture.StartAsync(router, "ise");
        await using var wojtek = await DaemonFixture.StartAsync(router, "wojtek");
        var (oldKey, oldSignature, oldIssued) = (wojtek.Keys.EncryptionPublicKey, wojtek.Keys.EncryptionKeySignature, wojtek.Keys.EncryptionKeyIssuedOn);
        await ise.Api.SendAsync(new LocalSendRequest("default", ["wojtek"], "first"));
        await wojtek.Api.RotateKeysAsync();
        await RouterFixture.Eventually(() => ise.Daemon.Relay.Machine(wojtek.Keys.MachineId)?.EncryptionKeyIssuedOn == wojtek.Keys.EncryptionKeyIssuedOn);
        await ise.Api.SendAsync(new LocalSendRequest("default", ["wojtek"], "second")); // ise has now seen the newer key

        await router.App.Services.GetRequiredService<MachineRegistry>().SubstituteEncryptionKeyAsync(wojtek.Keys.MachineId, oldKey, oldSignature, oldIssued);
        await ise.Daemon.Relay.RefreshMachinesAsync();
        var refused = await Assert.ThrowsAsync<LocalApiException>(() => ise.Api.SendAsync(new LocalSendRequest("default", ["wojtek"], "third")));
        Assert.Contains("older encryption key", refused.Message);
    }

    [Fact]
    public async Task TheDaemonRotatesOnItsOwnAndARotationMustBeNewer()
    {
        await using var router = await RouterFixture.StartAsync();
        await using var wojtek = await DaemonFixture.StartAsync(router, "wojtek", new Dictionary<string, string?> { ["Daemon:EncryptionKeyLifetime"] = "00:00:01" });
        var first = wojtek.Keys.EncryptionKeyIssuedOn;
        await RouterFixture.Eventually(() => wojtek.Keys.EncryptionKeyIssuedOn > first);
        Assert.NotEmpty(wojtek.Keys.RetiredEncryptionKeys);

        using var keys = MachineKeys.Create();
        var retired = keys.Encryption;
        keys.RotateEncryption(EncryptionKey.Create(), DateTimeOffset.UtcNow);
        Assert.Same(retired, keys.RetiredEncryptionKeys.Single().Key);
        Assert.Equal(1, keys.DropRetiredBefore(DateTimeOffset.UtcNow.AddSeconds(1)));
        Assert.Empty(keys.RetiredEncryptionKeys);
    }

    [Fact]
    public async Task ARevokedMachineRejoinsWithNewKeysUnderTheSameName()
    {
        await using var router = await RouterFixture.StartAsync(adminPort: RouterFixture.RestartablePort());
        await using var wojtek = await DaemonFixture.StartAsync(router, "wojtek");
        var (old, oldKeys) = await router.MemberAsync("blinky");
        await old.DisposeAsync();
        var owner = TestOwner.Create();
        router.Admin.SetOwner(owner.Key.ToString());
        await wojtek.Api.PinOwnerAsync(owner.Key.ToString());
        var routerId = (await wojtek.Api.OwnerAsync()).RouterId!;
        Assert.Equal(0, (await wojtek.Api.AdminAsync(owner.Admin(routerId, ["revoke", oldKeys.MachineId]))).ExitCode);
        var now = DateTimeOffset.UtcNow;
        await wojtek.Api.HereAsync(owner.Here(wojtek.Keys.MachineId, now, now.AddHours(1)));

        using var fresh = MachineKeys.Create(); // what keys reset makes
        var rejoining = router.Client(fresh);
        await rejoining.ConnectAsync();
        await rejoining.RequestJoinAsync("blinky", new MachineDescription("TestOS 1", "x64", "blinky-host"));
        var card = await RouterFixture.EventuallyValueAsync(async () => (await wojtek.Api.DeskAsync()).SingleOrDefault(a => a.Direction == "join"));
        Assert.Contains($"replacing the revoked {oldKeys.MachineId[..8]}", card.Summary);
        Assert.Equal(0, (await wojtek.Api.AdminAsync(owner.Admin(routerId, ["approve", fresh.MachineId]))).ExitCode);
        Assert.Equal(MembershipStatus.Approved, router.Admin.List().Single(m => m.Id == fresh.MachineId).Status);
    }
}
