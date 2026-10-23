using PoniExp.Core.Crypto;
using System.Security.Cryptography;
using Microsoft.AspNetCore.SignalR;
using PoniExp.Client;
using PoniExp.Core.Files;
using PoniExp.Core.Protocol;
using PoniExp.Server.Blobs;

namespace PoniExp.Tests;

/// <summary>Phase 6: encrypted files, uploaded once, verified on download, with file receipts, release and limits.</summary>
public class FileTests
{
    // ---- the crypto ----

    [Fact]
    public void AChunkOpensOnlyInItsOwnPlaceOfItsOwnBlob()
    {
        var key = BlobCrypto.NewKey();
        var blob = Guid.NewGuid();
        var plain = RandomNumberGenerator.GetBytes(1000);
        var sealedChunk = BlobCrypto.SealChunk(key, blob, 1, last: false, plain);

        Assert.Equal(plain, BlobCrypto.OpenChunk(key, blob, 1, last: false, sealedChunk));
        Assert.ThrowsAny<CryptographicException>(() => BlobCrypto.OpenChunk(key, blob, 2, last: false, sealedChunk)); // moved
        Assert.ThrowsAny<CryptographicException>(() => BlobCrypto.OpenChunk(key, blob, 1, last: true, sealedChunk)); // passed off as the end
        Assert.ThrowsAny<CryptographicException>(() => BlobCrypto.OpenChunk(key, Guid.NewGuid(), 1, last: false, sealedChunk)); // another blob's
        Assert.ThrowsAny<CryptographicException>(() => BlobCrypto.OpenChunk(BlobCrypto.NewKey(), blob, 1, last: false, sealedChunk));
        var manifest = BlobCrypto.SealManifest(key, blob, new BlobManifest("a.txt", "text/plain", 1000, 1, ["x"]));
        Assert.Equal("a.txt", BlobCrypto.OpenManifest(key, blob, manifest).Name);
        Assert.ThrowsAny<CryptographicException>(() => BlobCrypto.OpenManifest(key, Guid.NewGuid(), manifest));
        Assert.Equal(1, BlobCrypto.ChunkCountOf(0));
        Assert.Equal(2, BlobCrypto.ChunkCountOf(BlobCrypto.ChunkSize + 1));
        Assert.False(BlobCrypto.IsValidFileName("../etc/passwd"));
        Assert.False(BlobCrypto.IsValidFileName("a\\b"));
        Assert.True(BlobCrypto.IsValidFileName("report v2.pdf"));
    }

    // ---- transfers ----

    [Fact]
    public async Task AVerifiedFetchSendsDownloadedAndTheRouterDeletesTheBlob()
    {
        await using var router = await RouterFixture.StartAsync();
        await using var ise = await DaemonFixture.StartAsync(router, "ise");
        await using var wojtek = await DaemonFixture.StartAsync(router, "wojtek");
        await RegisterAndLeaveAsync(wojtek, "agent");
        var (path, bytes) = NewFile(BlobCrypto.ChunkSize * 2 + 12345, "data.bin"); // three chunks

        var sent = (await ise.Api.SendAsync(new LocalSendRequest("ops", ["wojtek/agent"], "here's the data", Attachments: [path]))).Single();
        var message = await InboxOneAsync(wojtek, "agent");
        var offered = Assert.Single(message.Attachments!);
        Assert.Equal(("data.bin", bytes.Length, AttachmentEvents.Waiting), (offered.Name, offered.Size, offered.State));
        Assert.Contains(router.Blobs.List(), b => b.Id == offered.BlobId && b.State == BlobState.Sealed);

        var fetched = Assert.Single(await wojtek.Api.FetchAsync("agent", new LocalFileRequest(message.Id.ToString())));
        Assert.Equal(AttachmentEvents.Downloaded, fetched.State);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(fetched.Path!));

        await RouterFixture.EventuallyAsync(async () =>
            (await ise.Api.MessageAsync("ops", sent.Id)).Message.Attachments!.Single().State == AttachmentEvents.Downloaded);
        await RouterFixture.Eventually(() => router.Blobs.Get(offered.BlobId) is null); // released by its only recipient
        var fileTrack = ise.Store.GetAttachmentEvents(ise.Keys.MachineId, sent.Id).Select(e => e.Event);
        Assert.Equal([AttachmentEvents.Uploaded, AttachmentEvents.Downloaded], fileTrack);
        Assert.Equal(MessageEvents.Received, ise.Store.GetState(ise.Keys.MachineId, sent.Id, MessageDirection.Out)); // the tracks are separate
    }

    [Fact]
    public async Task ATamperedChunkGetsDownloadFailedWithTheReason()
    {
        await using var router = await RouterFixture.StartAsync();
        await using var ise = await DaemonFixture.StartAsync(router, "ise");
        await using var wojtek = await DaemonFixture.StartAsync(router, "wojtek");
        await RegisterAndLeaveAsync(wojtek, "agent");
        var (path, _) = NewFile(BlobCrypto.ChunkSize + 100, "plan.txt");
        var sent = (await ise.Api.SendAsync(new LocalSendRequest("ops", ["wojtek/agent"], "the plan", Attachments: [path]))).Single();
        var message = await InboxOneAsync(wojtek, "agent");

        var chunk = Path.Combine(router.Blobs.Root, message.Attachments!.Single().BlobId.ToString("N"), "000001");
        var onDisk = await File.ReadAllBytesAsync(chunk);
        onDisk[10] ^= 0xff; // the router (or its disk) changes a byte
        await File.WriteAllBytesAsync(chunk, onDisk);

        var error = await Assert.ThrowsAsync<LocalApiException>(() => wojtek.Api.FetchAsync("agent", new LocalFileRequest(message.Id.ToString())));
        Assert.Contains("chunk 1 doesn't match its hash", error.Message);
        Assert.False(Directory.Exists(Path.Combine(wojtek.Home, "files", message.Attachments!.Single().BlobId.ToString("N"), "plan.txt")));
        await RouterFixture.EventuallyAsync(async () =>
            (await ise.Api.MessageAsync("ops", sent.Id)).Message.Attachments!.Single() is { State: AttachmentEvents.DownloadFailed } a
            && a.Detail!.Contains("chunk 1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ACancelledBlobGetsDownloadFailedAndTheSenderSeesItCancelled()
    {
        await using var router = await RouterFixture.StartAsync();
        await using var ise = await DaemonFixture.StartAsync(router, "ise");
        await using var wojtek = await DaemonFixture.StartAsync(router, "wojtek");
        await RegisterAndLeaveAsync(wojtek, "agent");
        var (path, _) = NewFile(1000, "oops.txt");
        var sent = (await ise.Api.SendAsync(new LocalSendRequest("ops", ["wojtek/agent"], "wrong file", Attachments: [path]))).Single();
        var message = await InboxOneAsync(wojtek, "agent");
        var blob = message.Attachments!.Single().BlobId;
        Assert.Equal(blob, Assert.Single(await ise.Api.BlobsAsync()).BlobId);

        await ise.Api.CancelBlobAsync(blob);

        var error = await Assert.ThrowsAsync<LocalApiException>(() => wojtek.Api.FetchAsync("agent", new LocalFileRequest(message.Id.ToString())));
        Assert.Contains("cancelled by the sender", error.Message);
        await RouterFixture.EventuallyAsync(async () =>
            ise.Store.GetAttachmentEvents(ise.Keys.MachineId, sent.Id).Select(e => e.Event).SequenceEqual(
                [AttachmentEvents.Uploaded, AttachmentEvents.Cancelled, AttachmentEvents.DownloadFailed]));
        Assert.Equal(AttachmentEvents.DownloadFailed, (await wojtek.Api.InboxAsync("agent")).Single().Attachments!.Single().State);
    }

    [Fact]
    public async Task SeveralAgentsShareOneFetchAndTheCopyGoesWhenAllHaveReleased()
    {
        await using var router = await RouterFixture.StartAsync();
        await using var ise = await DaemonFixture.StartAsync(router, "ise");
        await using var wojtek = await DaemonFixture.StartAsync(router, "wojtek");
        await RegisterAndLeaveAsync(wojtek, "alpha");
        await RegisterAndLeaveAsync(wojtek, "beta");
        var (path, bytes) = NewFile(5000, "shared.txt");

        var sent = await ise.Api.SendAsync(new LocalSendRequest("ops", ["wojtek/alpha", "wojtek/beta"], "for both of you", Attachments: [path]));
        Assert.Single(router.Blobs.List()); // uploaded once
        var toAlpha = await InboxOneAsync(wojtek, "alpha");
        var toBeta = await InboxOneAsync(wojtek, "beta");
        Assert.Equal(toAlpha.Attachments!.Single().BlobId, toBeta.Attachments!.Single().BlobId);

        var first = Assert.Single(await wojtek.Api.FetchAsync("alpha", new LocalFileRequest(toAlpha.Id.ToString())));
        await RouterFixture.Eventually(() => router.Blobs.List().Count == 0); // this machine has it: the router can let go
        var second = Assert.Single(await wojtek.Api.FetchAsync("beta", new LocalFileRequest(toBeta.Id.ToString()))); // from the local copy
        Assert.Equal(first.Path, second.Path);

        foreach (var s in sent) // a downloaded receipt for each message that carried it
        {
            await RouterFixture.EventuallyAsync(async () =>
                (await ise.Api.MessageAsync("ops", s.Id)).Message.Attachments!.Single().State == AttachmentEvents.Downloaded);
        }

        await wojtek.Api.AckAsync("alpha", toAlpha.Id);
        await wojtek.Api.AckAsync("beta", toBeta.Id);
        Assert.True(File.Exists(first.Path)); // acknowledging doesn't release

        Assert.Equal(AttachmentEvents.Released, Assert.Single(await wojtek.Api.ReleaseAsync("alpha", new LocalFileRequest(toAlpha.Id.ToString()))).State);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(first.Path!)); // beta still has it
        await wojtek.Api.ReleaseAsync("beta", new LocalFileRequest(toBeta.Id.ToString()));
        Assert.False(File.Exists(first.Path));
        Assert.Equal("deleted", wojtek.Store.GetLocalBlob(first.BlobId)!.State);
    }

    [Fact]
    public async Task AReplyCanCarryAFileBack()
    {
        await using var router = await RouterFixture.StartAsync();
        await using var ise = await DaemonFixture.StartAsync(router, "ise");
        await using var wojtek = await DaemonFixture.StartAsync(router, "wojtek");
        await RegisterAndLeaveAsync(wojtek, "agent");
        var request = (await ise.Api.SendAsync(new LocalSendRequest("ops", ["wojtek/agent"], "send me the log"))).Single();
        await InboxOneAsync(wojtek, "agent");
        var (path, bytes) = NewFile(2048, "build.log");

        await wojtek.Api.ReplyAsync("agent", new LocalReplyRequest(request.Id.ToString(), "attached", "done", Attachments: [path]));

        var reply = await InboxOneAsync(ise, "ops");
        var file = Assert.Single(await ise.Api.FetchAsync("ops", new LocalFileRequest(reply.Id.ToString())));
        Assert.Equal(("build.log", "text/plain"), (file.Name, file.ContentType));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(file.Path!));
    }

    // ---- who may read, and the limits ----

    [Fact]
    public async Task OnlyTheMachinesItIsSharedWithMayDownloadAndOnlyWithATicket()
    {
        await using var router = await RouterFixture.StartAsync();
        var (ise, iseKeys) = await router.MemberAsync("ise");
        var (wojtek, wojtekKeys) = await router.MemberAsync("wojtek");
        var (blinky, _) = await router.MemberAsync("blinky");
        var (path, _) = NewFile(100, "secret.txt");
        await ise.RefreshMachinesAsync();
        var attachment = await ise.UploadAsync(path, [wojtekKeys.MachineId]);

        var refused = await Assert.ThrowsAsync<BlobTransferException>(() => blinky.DownloadAsync(attachment, Path.Combine(Path.GetTempPath(), "x")));
        Assert.Contains("No such blob for this machine", refused.Message); // nothing about who it is shared with
        using var http = new HttpClient { BaseAddress = router.Url };
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, (await http.GetAsync($"/blobs/{attachment.BlobId}/chunks/0")).StatusCode);
        var target = Path.Combine(Path.GetTempPath(), "poniexp-tests", Guid.NewGuid().ToString("N"), "secret.txt");
        await wojtek.DownloadAsync(attachment, target);
        Assert.True(File.Exists(target));
        await ise.DisposeAsync();
        await wojtek.DisposeAsync();
        await blinky.DisposeAsync();
    }

    [Fact]
    public async Task LimitsRefuseWithWhy()
    {
        await using var router = await RouterFixture.StartAsync(settings: new Dictionary<string, string?>
        {
            ["PoniExp:Blobs:MaxFileBytes"] = "200000",
            ["PoniExp:Blobs:PerRecipientBytes"] = "150000",
            ["PoniExp:Blobs:PerSenderBytes"] = "1000000",
            ["PoniExp:Blobs:FreeDiskFloorBytes"] = "0",
        });
        var (ise, _) = await router.MemberAsync("ise");
        var (wojtek, wojtekKeys) = await router.MemberAsync("wojtek");
        await ise.RefreshMachinesAsync();

        var tooBig = await Assert.ThrowsAsync<HubException>(() => ise.UploadAsync(NewFile(300_000, "big.bin").Path, [wojtekKeys.MachineId]));
        Assert.Contains("limit per file", tooBig.Message);

        await ise.UploadAsync(NewFile(100_000, "one.bin").Path, [wojtekKeys.MachineId]);
        await wojtek.DisposeAsync();
        await RouterFixture.Eventually(() => !router.Presence.IsOnline(wojtekKeys.MachineId));
        var full = await Assert.ThrowsAsync<HubException>(() => ise.UploadAsync(NewFile(100_000, "two.bin").Path, [wojtekKeys.MachineId]));
        Assert.True(full.Message.Contains("wojtek has 100.2 KB of 150 KB waiting, offline since", StringComparison.Ordinal), full.Message);

        router.SetSetting("PoniExp:Blobs:Overrides:wojtek:PerRecipientBytes", "1000000"); // live, per machine
        await ise.UploadAsync(NewFile(100_000, "two.bin").Path, [wojtekKeys.MachineId]);
        Assert.Equal(2, router.Admin.ClearBlobsFor("wojtek"));
        Assert.Empty(router.Blobs.List());
        await ise.DisposeAsync();
    }

    [Fact]
    public async Task AnOfflineMachinesMessageQueueHasACap()
    {
        await using var router = await RouterFixture.StartAsync(settings: new Dictionary<string, string?>
        {
            ["PoniExp:MaxQueuedBytesPerRecipient"] = (4 * 1024 * 1024).ToString(System.Globalization.CultureInfo.InvariantCulture),
        });
        var (ise, _) = await router.MemberAsync("ise");
        var (wojtek, _) = await router.MemberAsync("wojtek");
        await wojtek.DisposeAsync();
        var body = new string('x', Limits.MaxBodyBytes - 1024);
        var refused = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            for (var i = 0; i < 40; i++)
            {
                await ise.SendAsync("wojtek", new OutgoingMessage(body));
            }
        });
        Assert.Contains("of messages waiting", refused.Message);
        Assert.InRange(router.Queue.Count, 8, 16);
        await ise.DisposeAsync();
    }

    [Fact]
    public async Task ABlobSurvivesARouterRestartAndThenExpires()
    {
        var data = Path.Combine(Path.GetTempPath(), "poniexp-tests", Guid.NewGuid().ToString("N"));
        Attachment attachment;
        string wojtekId;
        MachineKeys wojtekKeys;
        await using (var first = await RouterFixture.StartAsync(dataDirectory: data))
        {
            var (ise, _) = await first.MemberAsync("ise");
            var (wojtek, keys) = await first.MemberAsync("wojtek");
            (wojtekId, wojtekKeys) = (keys.MachineId, keys);
            await ise.RefreshMachinesAsync();
            attachment = await ise.UploadAsync(NewFile(1000, "kept.txt").Path, [wojtekId]);
            await ise.DisposeAsync();
            await wojtek.DisposeAsync();
            first.KeepData = true;
        }

        await using var router = await RouterFixture.StartAsync(dataDirectory: data, settings: new Dictionary<string, string?>
        {
            ["PoniExp:Blobs:Expiry"] = "00:00:02",
            ["PoniExp:MaintenanceInterval"] = "00:00:00.200",
        });
        Assert.Equal(BlobState.Sealed, router.Blobs.Get(attachment.BlobId)!.State); // sealed before this term's shorter expiry
        await using var client = router.Client(wojtekKeys);
        await client.ConnectAsync();
        var target = Path.Combine(Path.GetTempPath(), "poniexp-tests", Guid.NewGuid().ToString("N"), "kept.txt");
        await client.DownloadAsync(attachment, target);
        Assert.True(File.Exists(target));

        // A blob sealed under this router's 2 s expiry: expired, its data deleted, and a late download told why.
        var (sender, _) = await router.MemberAsync("blinky");
        await sender.RefreshMachinesAsync();
        var shortLived = await sender.UploadAsync(NewFile(1000, "brief.txt").Path, [wojtekId]);
        await RouterFixture.Eventually(() => router.Blobs.Get(shortLived.BlobId)?.State == BlobState.Expired);
        Assert.False(Directory.Exists(Path.Combine(router.Blobs.Root, shortLived.BlobId.ToString("N"))));
        var late = await Assert.ThrowsAsync<BlobTransferException>(() => client.DownloadAsync(shortLived, target + ".late"));
        Assert.Contains("expired", late.Message);
        await sender.DisposeAsync();
    }

    // ---- helpers ----

    private static (string Path, byte[] Bytes) NewFile(int size, string name)
    {
        var directory = Path.Combine(Path.GetTempPath(), "poniexp-tests", "files-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(directory);
        var bytes = RandomNumberGenerator.GetBytes(size);
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, bytes);
        return (path, bytes);
    }

    private static async Task<LocalMessage> InboxOneAsync(DaemonFixture daemon, string agent)
    {
        await RouterFixture.EventuallyAsync(async () => (await daemon.Api.InboxAsync(agent)).Count >= 1);
        return (await daemon.Api.InboxAsync(agent)).First();
    }

    private static async Task RegisterAndLeaveAsync(DaemonFixture daemon, string agent)
    {
        await using var connection = new AgentConnection(daemon.Api, agent, _ => Task.CompletedTask);
        await connection.Registered.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
