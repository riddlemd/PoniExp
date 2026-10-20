using System.Security.Cryptography;
using System.Text.Json;
using PoniExp.Client;
using PoniExp.Core.Crypto;
using PoniExp.Core.Protocol;

namespace PoniExp.Tests;

/// <summary>Phase 10: owner admin from anywhere.</summary>
public class RemoteAdminTests
{
    [Fact]
    public async Task ASignedCommandRunsAndAnythingElseIsRefused()
    {
        await using var setup = await Setup.StartAsync();
        var (wojtek, owner, router) = (setup.Wojtek, setup.Owner, setup.RouterId);

        var list = await wojtek.Api.AdminAsync(owner.Admin(router, ["list"]));
        Assert.Equal(0, list.ExitCode);
        Assert.Contains("wojtek", list.Output);
        Assert.Contains("ise", list.Output);

        async Task<string> Refused(AdminCommand command) => (await Assert.ThrowsAsync<LocalApiException>(() => wojtek.Api.AdminAsync(command))).Message;
        Assert.Contains("isn't signed by the owner's key", await Refused(TestOwner.Create().Admin(router, ["list"])));
        var signed = owner.Admin(router, ["list"]);
        Assert.Contains("isn't signed by the owner's key", await Refused(signed with { Args = ["remove", "ise"] })); // altered after signing
        Assert.Contains("too old", await Refused(owner.Admin(router, ["list"], issued: DateTimeOffset.UtcNow.AddMinutes(-10))));
        Assert.Contains("another router", await Refused(owner.Admin(new string('a', 32), ["list"])));
        Assert.Contains("isn't an admin command", await Refused(owner.Admin(router, ["list", "--url", "http://example.com"])));
        Assert.Contains("uses '-'", await Refused(owner.Admin(router, ["backup", "/tmp/x.json"])));
        await wojtek.Api.AdminAsync(signed);
        Assert.Contains("already ran", await Refused(signed));

        var failed = await wojtek.Api.AdminAsync(owner.Admin(router, ["approve", "nobody"])); // runs, and fails as docker exec would
        Assert.Equal(1, failed.ExitCode);
        Assert.Contains("No machine matches", failed.Output);
    }

    [Fact]
    public async Task WithoutAnOwnerKeyNothingRunsRemotely()
    {
        await using var router = await RouterFixture.StartAsync(adminPort: RouterFixture.RestartablePort());
        await using var wojtek = await DaemonFixture.StartAsync(router, "wojtek");
        var routerId = (await wojtek.Api.OwnerAsync()).RouterId!;
        var refused = await Assert.ThrowsAsync<LocalApiException>(() => wojtek.Api.AdminAsync(TestOwner.Create().Admin(routerId, ["list"])));
        Assert.Contains("no owner key yet", refused.Message);
    }

    [Fact]
    public void ASealedBoxOpensOnlyForItsReaderAndPurpose()
    {
        using var reader = MachineKeys.Create();
        using var other = MachineKeys.Create();
        var box = SealedBox.Seal("secret"u8.ToArray(), "admin", reader.MachineId, reader.EncryptionPublicKey);
        Assert.Equal("secret"u8.ToArray(), box.Open("admin", reader.MachineId, reader.Encryption));
        Assert.ThrowsAny<CryptographicException>(() => box.Open("admin-result", reader.MachineId, reader.Encryption));
        Assert.ThrowsAny<CryptographicException>(() => box.Open("admin", other.MachineId, other.Encryption));
        Assert.ThrowsAny<CryptographicException>(() => (box with { Ciphertext = Convert.ToBase64String(new byte[40]) }).Open("admin", reader.MachineId, reader.Encryption));
    }

    [Fact]
    public async Task AMachineRefusesARouterWhoseKeyDiffersFromItsPin()
    {
        await using var router = await RouterFixture.StartAsync();
        await using var wojtek = await DaemonFixture.StartAsync(router, "wojtek");
        using var impostor = MachineKeys.Create();
        Assert.NotNull(wojtek.Store.GetSetting(RelayClient.RouterKeySetting)); // pinned on first connect
        await wojtek.StopAsync();
        new ClientStore(router.StorePath(wojtek.Keys)).Transact(DateTimeOffset.UtcNow, tx => tx.SetSetting(RelayClient.RouterKeySetting, impostor.PublicKey));
        await Assert.ThrowsAnyAsync<Exception>(wojtek.StartAsync); // never connects
    }

    [Fact]
    public async Task BackupAndRestoreRoundTripRemotely()
    {
        await using var setup = await Setup.StartAsync();
        var (wojtek, owner, router) = (setup.Wojtek, setup.Owner, setup.RouterId);
        var backup = await wojtek.Api.AdminAsync(owner.Admin(router, ["backup", "-"]));
        Assert.Equal(0, backup.ExitCode);
        var parsed = JsonDocument.Parse(backup.Output).RootElement;
        Assert.Equal("poniexp-router-backup", parsed.GetProperty("format").GetString());
        Assert.True(parsed.TryGetProperty("routerKeys", out _));

        var restored = await wojtek.Api.AdminAsync(owner.Admin(router, ["restore", "-"], input: backup.Output));
        Assert.Equal(0, restored.ExitCode);
        Assert.Contains("restored 2 approved machine(s)", restored.Output);
        Assert.DoesNotContain("router's keys differ", restored.Output);
    }

    [Fact]
    public async Task ARemoteOwnerSetMovesEveryPinAndADockerExecOneDoesnt()
    {
        await using var setup = await Setup.StartAsync();
        var (ise, wojtek, owner, router) = (setup.Ise, setup.Wojtek, setup.Owner, setup.RouterId);
        await ise.StopAsync(); // offline: it catches up when it reconnects
        var next = TestOwner.Create();
        var set = await wojtek.Api.AdminAsync(owner.Admin(router, ["owner", "set", .. next.Key.ToString().Split(' ')]));
        Assert.Equal(0, set.ExitCode);
        await RouterFixture.EventuallyAsync(async () => (await wojtek.Api.OwnerAsync()).PinnedFingerprint == next.Key.Fingerprint);
        await ise.StartAsync();
        await RouterFixture.EventuallyAsync(async () => (await ise.Api.OwnerAsync()).PinnedFingerprint == next.Key.Fingerprint);

        Assert.Contains("isn't signed by the owner's key", (await Assert.ThrowsAsync<LocalApiException>(() => wojtek.Api.AdminAsync(owner.Admin(router, ["list"])))).Message);
        Assert.Equal(0, (await wojtek.Api.AdminAsync(next.Admin(router, ["list"]))).ExitCode);

        setup.Router.Admin.SetOwner(TestOwner.Create().Key.ToString()); // docker exec, over an existing key: not followed
        await Task.Delay(300);
        var seen = await wojtek.Api.OwnerAsync();
        Assert.Equal(next.Key.Fingerprint, seen.PinnedFingerprint);
        Assert.NotEqual(seen.RouterFingerprint, seen.PinnedFingerprint);
    }

    [Fact]
    public async Task OnlyAnOwnerSignedGroupIsUsedWhereAnOwnerKeyIsPinned()
    {
        await using var setup = await Setup.StartAsync();
        var (ise, wojtek, owner, router) = (setup.Ise, setup.Wojtek, setup.Owner, setup.RouterId);
        setup.Router.Admin.SetGroup("oncall", ["wojtek/agent"]); // docker exec: unsigned
        var unsigned = await Assert.ThrowsAsync<LocalApiException>(() => ise.Api.SendAsync(new LocalSendRequest("default", ["group:oncall"], "page")));
        Assert.Contains("isn't signed by the owner", unsigned.Message);

        Assert.Equal(0, (await wojtek.Api.AdminAsync(owner.Admin(router, ["group", "set", "oncall", "wojtek/agent", "ise/claude"]))).ExitCode);
        var sent = await ise.Api.SendAsync(new LocalSendRequest("default", ["group:oncall"], "page"));
        Assert.Equal(2, sent.Count);
        Assert.NotNull(setup.Router.Admin.Groups().Single().Signed);

        setup.Router.Admin.SetGroup("oncall", ["wojtek/agent", "ise/eve"]); // the router (or docker exec) changes it: no longer signed
        Assert.Contains("isn't signed by the owner", (await Assert.ThrowsAsync<LocalApiException>(() =>
            ise.Api.SendAsync(new LocalSendRequest("default", ["group:oncall"], "page")))).Message);
    }

    [Fact]
    public async Task AJoinRequestReachesTheDeskAndIsApprovedFromThere()
    {
        await using var setup = await Setup.StartAsync();
        var (wojtek, owner, router) = (setup.Wojtek, setup.Owner, setup.RouterId);
        var now = DateTimeOffset.UtcNow;
        await wojtek.Api.HereAsync(owner.Here(wojtek.Keys.MachineId, now, now.AddHours(1)));

        using var blinkyKeys = MachineKeys.Create();
        var blinky = setup.Router.Client(blinkyKeys);
        await blinky.ConnectAsync();
        var approved = Wait.For<MembershipStatus>(h => blinky.MembershipChanged += h);
        await blinky.RequestJoinAsync("blinky", new MachineDescription("TestOS 1", "x64", "blinky-host", "the new build box"));
        var card = await RouterFixture.EventuallyValueAsync(async () => (await wojtek.Api.DeskAsync()).SingleOrDefault(a => a.Direction == "join"));
        Assert.Equal(("blinky (new machine)", blinkyKeys.MachineId), (card.From, card.Id.ToString("N")));
        Assert.Contains("the new build box", card.Details);
        Assert.Contains("answered with the owner key", (await Assert.ThrowsAsync<LocalApiException>(() => wojtek.Api.AnswerAsync(card.Id.ToString(), true))).Message);

        Assert.Equal(0, (await wojtek.Api.AdminAsync(owner.Admin(router, ["approve", card.Id.ToString("N")]))).ExitCode); // what `approve <id>` signs
        Assert.Equal(MembershipStatus.Approved, await approved);
        await RouterFixture.EventuallyAsync(async () => (await wojtek.Api.DeskAsync()).Count == 0);
        Assert.Equal((ApprovalRecord.Approved, "admin"), wojtek.Store.GetApprovals(ApprovalRecord.Join).Select(a => (a.State, a.AnsweredBy)).Single());

        using var dazzleKeys = MachineKeys.Create(); // approved elsewhere: it leaves the desk
        var dazzle = setup.Router.Client(dazzleKeys);
        await dazzle.ConnectAsync();
        await dazzle.RequestJoinAsync("dazzle", new MachineDescription("TestOS 1", "x64", "dazzle-host"));
        await RouterFixture.EventuallyAsync(async () => (await wojtek.Api.DeskAsync()).Count == 1);
        await setup.Router.Admin.RemoveAsync("dazzle");
        await RouterFixture.EventuallyAsync(async () => (await wojtek.Api.DeskAsync()).Count == 0);
    }

    /// <summary>A router with its admin listener on, two machines, and an owner key set and pinned.</summary>
    private sealed class Setup : IAsyncDisposable
    {
        public required RouterFixture Router { get; init; }
        public required DaemonFixture Ise { get; init; }
        public required DaemonFixture Wojtek { get; init; }
        public required string RouterId { get; init; }
        public TestOwner Owner { get; } = TestOwner.Create();

        public static async Task<Setup> StartAsync()
        {
            var router = await RouterFixture.StartAsync(adminPort: RouterFixture.RestartablePort());
            var ise = await DaemonFixture.StartAsync(router, "ise");
            var wojtek = await DaemonFixture.StartAsync(router, "wojtek");
            var setup = new Setup { Router = router, Ise = ise, Wojtek = wojtek, RouterId = (await wojtek.Api.OwnerAsync()).RouterId! };
            router.Admin.SetOwner(setup.Owner.Key.ToString());
            await ise.Api.PinOwnerAsync(setup.Owner.Key.ToString());
            await wojtek.Api.PinOwnerAsync(setup.Owner.Key.ToString());
            return setup;
        }

        public async ValueTask DisposeAsync()
        {
            await Wojtek.DisposeAsync();
            await Ise.DisposeAsync();
            await Router.DisposeAsync();
        }
    }
}
