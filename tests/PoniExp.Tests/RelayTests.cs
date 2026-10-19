using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using PoniExp.Core.Crypto;
using PoniExp.Core.Protocol;
using PoniExp.Router;

namespace PoniExp.Tests;

public class RelayTests
{
    private static readonly MachineDescription Description = new("TestOS 1", "x64", "host");

    [Fact]
    public async Task AnUnknownMachineCanOnlyAskToJoin()
    {
        await using var router = await RouterFixture.StartAsync();
        var client = router.Client(MachineIdentity.Create());

        Assert.Equal(MembershipStatus.Unknown, await client.ConnectAsync());
        await Assert.ThrowsAsync<HubException>(() => client.RefreshMachinesAsync());
        Assert.Equal(MembershipStatus.Pending, await client.RequestJoinAsync("ise", Description));
        await Assert.ThrowsAsync<HubException>(() => client.RefreshMachinesAsync());

        var listed = Assert.Single(router.Admin.List());
        Assert.Equal(("ise", MembershipStatus.Pending, client.JoinCode), (listed.Name, listed.Status, listed.JoinCode));
    }

    [Fact]
    public async Task ApprovalReachesTheWaitingMachineAndUnlocksIt()
    {
        await using var router = await RouterFixture.StartAsync();
        var (client, identity) = await router.MemberAsync("ise");

        Assert.Equal(MembershipStatus.Approved, client.Status);
        var self = Assert.Single(client.Machines);
        Assert.Equal((identity.MachineId, "ise", true), (self.Id, self.Name, self.Online));
    }

    [Fact]
    public async Task MessagesAreRoutedAndVerified()
    {
        await using var router = await RouterFixture.StartAsync();
        var (ise, _) = await router.MemberAsync("ise");
        var (wojtek, _) = await router.MemberAsync("wojtek");
        var received = Wait.For<ReceivedMessageArgs>(h => wojtek.MessageReceived += m =>
        {
            h(new ReceivedMessageArgs(m.FromName, m.Envelope.Kind, m.Envelope.Body));
            return Task.CompletedTask;
        });

        var result = await ise.SendAsync("wojtek", "chat", "hello wojtek");

        Assert.Equal(DeliveryState.Delivered, result.State);
        Assert.Equal(new ReceivedMessageArgs("ise", "chat", "hello wojtek"), await received);
    }

    [Fact]
    public async Task OfflineMachinesGetTheirMessagesWhenTheyConnect()
    {
        await using var router = await RouterFixture.StartAsync();
        var (ise, _) = await router.MemberAsync("ise");
        var (wojtek, wojtekKey) = await router.MemberAsync("wojtek");
        await wojtek.DisposeAsync();
        await RouterFixture.Eventually(() => ise.Machines.Single(m => m.Name == "wojtek").Online == false);

        Assert.Equal(DeliveryState.Queued, (await ise.SendAsync("wojtek", "chat", "while you were out")).State);

        var back = router.Client(wojtekKey);
        var received = Wait.For<string>(h => back.MessageReceived += m =>
        {
            h(m.Envelope.Body);
            return Task.CompletedTask;
        });
        await back.ConnectAsync();
        Assert.Equal("while you were out", await received);
    }

    [Fact]
    public async Task UnackedMessagesSurviveARouterRestart()
    {
        var data = Path.Combine(Path.GetTempPath(), "poniexp-tests", Guid.NewGuid().ToString("N"));
        MachineIdentity wojtekKey;
        await using (var router = await RouterFixture.StartAsync(dataDirectory: data))
        {
            var (ise, _) = await router.MemberAsync("ise");
            var (wojtek, key) = await router.MemberAsync("wojtek");
            wojtekKey = key;
            await wojtek.DisposeAsync();
            await RouterFixture.Eventually(() => !ise.Machines.Single(m => m.Name == "wojtek").Online);
            await ise.SendAsync("wojtek", "chat", "persisted");
            Directory.CreateDirectory(data + "-keep");
            foreach (var file in Directory.GetFiles(data))
            {
                File.Copy(file, Path.Combine(data + "-keep", Path.GetFileName(file)));
            }
        }

        await using var restarted = await RouterFixture.StartAsync(dataDirectory: data + "-keep");
        var back = restarted.Client(wojtekKey);
        var received = Wait.For<string>(h => back.MessageReceived += m =>
        {
            h(m.Envelope.Body);
            return Task.CompletedTask;
        });
        Assert.Equal(MembershipStatus.Approved, await back.ConnectAsync());
        Assert.Equal("persisted", await received);
    }

    [Fact]
    public async Task PresenceIsAnnounced()
    {
        await using var router = await RouterFixture.StartAsync();
        var (ise, _) = await router.MemberAsync("ise");
        var online = Wait.For<PresenceChange>(h => ise.PresenceChanged += p =>
        {
            if (p.Name == "wojtek" && p.Online)
            {
                h(p);
            }
        });
        var (wojtek, _) = await router.MemberAsync("wojtek");
        await online;

        var offline = Wait.For<PresenceChange>(h => ise.PresenceChanged += p =>
        {
            if (p.Name == "wojtek" && !p.Online)
            {
                h(p);
            }
        });
        await wojtek.DisposeAsync();
        await offline;
    }

    [Fact]
    public async Task AMemberCantSendAsSomeoneElseOrTamper()
    {
        await using var router = await RouterFixture.StartAsync();
        var (_, iseKey) = await router.MemberAsync("ise");
        var (_, wojtekKey) = await router.MemberAsync("wojtek");
        var connection = await RawMemberConnectionAsync(router, iseKey);

        var forged = Signatures.SignEnvelope(wojtekKey, iseKey.MachineId, "chat", "I am wojtek", DateTimeOffset.UtcNow);
        var spoofed = await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync<SendResult>(HubMethods.Send, forged));
        Assert.Contains("own machine id", spoofed.Message);

        var genuine = Signatures.SignEnvelope(iseKey, wojtekKey.MachineId, "chat", "hi", DateTimeOffset.UtcNow);
        var tampered = await Assert.ThrowsAsync<HubException>(() =>
            connection.InvokeAsync<SendResult>(HubMethods.Send, genuine with { Body = "rm -rf" }));
        Assert.Contains("signature", tampered.Message);

        var stale = Signatures.SignEnvelope(iseKey, wojtekKey.MachineId, "chat", "old", DateTimeOffset.UtcNow.AddHours(-1));
        Assert.Contains("clock", (await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync<SendResult>(HubMethods.Send, stale))).Message);
    }

    [Fact]
    public async Task AChallengeAnswerCantBeReplayedOrSignedWithAnotherKey()
    {
        await using var router = await RouterFixture.StartAsync();
        var (_, iseKey) = await router.MemberAsync("ise");
        using var stranger = MachineIdentity.Create();
        var connection = await RawConnectionAsync(router);

        var challenge = await connection.InvokeAsync<string>(HubMethods.GetChallenge);
        var wrongKey = stranger.Sign(Signatures.AuthPayload(challenge, iseKey.PublicKey));
        await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync<AuthResult>(HubMethods.Authenticate, iseKey.PublicKey, wrongKey));

        var other = await RawConnectionAsync(router);
        var otherChallenge = await other.InvokeAsync<string>(HubMethods.GetChallenge);
        var answer = iseKey.Sign(Signatures.AuthPayload(otherChallenge, iseKey.PublicKey));
        Assert.Equal(MembershipStatus.Approved, (await other.InvokeAsync<AuthResult>(HubMethods.Authenticate, iseKey.PublicKey, answer)).Status);
        await Assert.ThrowsAsync<HubException>(() => other.InvokeAsync<AuthResult>(HubMethods.Authenticate, iseKey.PublicKey, answer));
    }

    [Fact]
    public async Task AJoinRequestMustBeSignedByItsOwnKey()
    {
        await using var router = await RouterFixture.StartAsync();
        using var victim = MachineIdentity.Create();
        using var attacker = MachineIdentity.Create();
        var connection = await RawConnectionAsync(router);

        var request = Signatures.SignJoin(attacker, "ise", Description, DateTimeOffset.UtcNow) with { PublicKey = victim.PublicKey };
        await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync<AuthResult>(HubMethods.RequestJoin, request));
        var badName = Signatures.SignJoin(attacker, "Bad Name!", Description, DateTimeOffset.UtcNow);
        await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync<AuthResult>(HubMethods.RequestJoin, badName));
        Assert.Empty(router.Admin.List());
    }

    [Fact]
    public async Task RejectingAMachineCutsItOffAndItStaysRejected()
    {
        await using var router = await RouterFixture.StartAsync();
        var (ise, _) = await router.MemberAsync("ise");
        var (wojtek, wojtekKey) = await router.MemberAsync("wojtek");
        var lost = Wait.For<bool>(h => wojtek.ConnectionChanged += (up, _) =>
        {
            if (!up)
            {
                h(up);
            }
        });

        await router.Admin.RejectAsync("wojtek");
        await lost;
        await Assert.ThrowsAsync<HubException>(() => ise.SendAsync(wojtekKey.MachineId, "chat", "hello?"));

        var again = router.Client(wojtekKey);
        Assert.Equal(MembershipStatus.Rejected, await again.ConnectAsync());
        Assert.Equal(MembershipStatus.Rejected, await again.RequestJoinAsync("wojtek", Description));
    }

    [Fact]
    public async Task ApprovingNeedsAnUnambiguousSelectorAndAFreeName()
    {
        await using var router = await RouterFixture.StartAsync();
        await router.MemberAsync("ise");
        var impostor = router.Client(MachineIdentity.Create());
        await impostor.ConnectAsync();
        await impostor.RequestJoinAsync("ise", Description);

        var ambiguous = await Assert.ThrowsAsync<RouterException>(() => router.Admin.ApproveAsync("ise"));
        Assert.Equal(RouterErrorKind.Conflict, ambiguous.Kind);
        var clash = await Assert.ThrowsAsync<RouterException>(() => router.Admin.ApproveAsync(impostor.MachineId));
        Assert.Contains("already named", clash.Message);
        await Assert.ThrowsAsync<RouterException>(() => router.Admin.ApproveAsync("nobody"));
    }

    [Fact]
    public async Task TlsPinningAcceptsOnlyTheRoutersCertificate()
    {
        await using var router = await RouterFixture.StartAsync(useHttps: true);
        var pin = router.App.Services.GetRequiredService<PoniExp.Router.Security.RouterCertificateInfo>().Sha256!;
        Assert.StartsWith("https://", router.Url.ToString());

        var good = router.Client(MachineIdentity.Create(), pin);
        Assert.Equal(MembershipStatus.Unknown, await good.ConnectAsync());

        var bad = router.Client(MachineIdentity.Create(), new string('0', 64));
        await Assert.ThrowsAnyAsync<Exception>(() => bad.ConnectAsync());

        var unpinned = router.Client(MachineIdentity.Create());
        await Assert.ThrowsAnyAsync<Exception>(() => unpinned.ConnectAsync()); // self-signed: normal validation fails
    }

    [Fact]
    public async Task TheRouterKeepsItsCertificateAcrossRestarts()
    {
        var data = Path.Combine(Path.GetTempPath(), "poniexp-tests", Guid.NewGuid().ToString("N"));
        string first;
        await using (var router = await RouterFixture.StartAsync(useHttps: true, dataDirectory: data + "-a"))
        {
            first = router.App.Services.GetRequiredService<PoniExp.Router.Security.RouterCertificateInfo>().Sha256!;
            File.Copy(Path.Combine(data + "-a", "router.pfx"), Path.Combine(Directory.CreateDirectory(data + "-b").FullName, "router.pfx"));
        }

        await using var again = await RouterFixture.StartAsync(useHttps: true, dataDirectory: data + "-b");
        Assert.Equal(first, again.App.Services.GetRequiredService<PoniExp.Router.Security.RouterCertificateInfo>().Sha256);
    }

    private static async Task<HubConnection> RawConnectionAsync(RouterFixture router)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(router.Url, HubMethods.Path))
            .Build();
        await connection.StartAsync();
        return connection;
    }

    private static async Task<HubConnection> RawMemberConnectionAsync(RouterFixture router, MachineIdentity identity)
    {
        var connection = await RawConnectionAsync(router);
        var challenge = await connection.InvokeAsync<string>(HubMethods.GetChallenge);
        var result = await connection.InvokeAsync<AuthResult>(HubMethods.Authenticate, identity.PublicKey,
            identity.Sign(Signatures.AuthPayload(challenge, identity.PublicKey)));
        Assert.Equal(MembershipStatus.Approved, result.Status);
        return connection;
    }

    private sealed record ReceivedMessageArgs(string From, string Kind, string Body);
}
