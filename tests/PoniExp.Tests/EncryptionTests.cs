using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Data.Sqlite;
using PoniExp.Client;
using PoniExp.Core.Crypto;
using PoniExp.Core.Protocol;
using PoniExp.Secrets;
using PoniExp.Server.Machines;

namespace PoniExp.Tests;

/// <summary>Phase 2: end-to-end encryption, key distribution and key storage.</summary>
public class EncryptionTests
{
    // ---- sealing and opening ----

    [Fact]
    public void OnlyTheRecipientAndTheSenderCanOpenAMessage()
    {
        using var ise = MachineKeys.Create();
        using var wojtek = MachineKeys.Create();
        using var blinky = MachineKeys.Create();
        var envelope = Envelopes.Sign(ise, wojtek, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1), "for wojtek");

        Assert.Equal("for wojtek", MessageCrypto.Open(envelope, wojtek.MachineId, wojtek.Encryption).Body);
        Assert.Equal("for wojtek", MessageCrypto.Open(envelope, ise.MachineId, ise.Encryption).Body); // the sender's own copy
        Assert.ThrowsAny<CryptographicException>(() => MessageCrypto.Open(envelope, blinky.MachineId, blinky.Encryption));
        // Claiming to be wojtek isn't enough without wojtek's key.
        Assert.ThrowsAny<CryptographicException>(() => MessageCrypto.Open(envelope, wojtek.MachineId, blinky.Encryption));
    }

    [Fact]
    public void ATamperedCiphertextOrHeaderFailsToDecrypt()
    {
        using var ise = MachineKeys.Create();
        using var wojtek = MachineKeys.Create();
        var envelope = Envelopes.Sign(ise, wojtek, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));
        var wraps = envelope.Wraps;

        Envelope[] tampered =
        [
            envelope with { Ciphertext = RelayTests.Flip(envelope.Ciphertext) },
            envelope with { Sequence = envelope.Sequence + 1 }, // the header is the associated data
            envelope with { ExpiresOn = envelope.ExpiresOn.AddDays(1) },
            envelope with { Id = Guid.NewGuid() },
            envelope with { Wraps = [.. wraps.Select(w => w with { WrappedKey = RelayTests.Flip(w.WrappedKey) })] },
        ];
        Assert.All(tampered, t => Assert.ThrowsAny<CryptographicException>(() => MessageCrypto.Open(t, wojtek.MachineId, wojtek.Encryption)));
    }

    [Fact]
    public void ACiphertextResignedByAnotherMachineFailsToDecrypt()
    {
        using var ise = MachineKeys.Create();
        using var wojtek = MachineKeys.Create();
        using var mallory = MachineKeys.Create();
        var genuine = Envelopes.Sign(ise, wojtek, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1), "from ise");

        var claimed = Envelopes.Resign(genuine, mallory);

        Assert.True(Signatures.VerifyEnvelope(claimed, mallory.PublicKey)); // the forgery's signature is fine...
        Assert.ThrowsAny<CryptographicException>(() => MessageCrypto.Open(claimed, wojtek.MachineId, wojtek.Encryption)); // ...but it won't open
    }

    [Fact]
    public void EveryMessageUsesFreshWrappingKeys()
    {
        using var ise = MachineKeys.Create();
        using var wojtek = MachineKeys.Create();
        var now = DateTimeOffset.UtcNow;
        var content = Envelopes.Content(now, "same words");

        var first = Signatures.SealEnvelope(ise, Guid.NewGuid(), wojtek.MachineId, wojtek.EncryptionPublicKey, 1, now.AddHours(1), content);
        var second = Signatures.SealEnvelope(ise, Guid.NewGuid(), wojtek.MachineId, wojtek.EncryptionPublicKey, 2, now.AddHours(1), content);

        var ephemeral = first.Wraps.Concat(second.Wraps).Select(w => w.EphemeralKey).ToList();
        Assert.Equal(ephemeral.Count, ephemeral.Distinct().Count());
        Assert.NotEqual(first.Ciphertext, second.Ciphertext);
    }

    [Fact]
    public void AnEncryptionKeyCountsOnlyIfItsMachineSignedItAndIsSeparateFromTheIdentityKey()
    {
        using var ise = MachineKeys.Create();
        using var mallory = MachineKeys.Create();

        Assert.True(PublicKeys.VerifyEncryptionKey(ise.PublicKey, ise.EncryptionPublicKey, ise.EncryptionKeySignature));
        Assert.False(PublicKeys.VerifyEncryptionKey(ise.PublicKey, mallory.EncryptionPublicKey, mallory.EncryptionKeySignature));
        Assert.False(PublicKeys.VerifyEncryptionKey(ise.PublicKey, mallory.EncryptionPublicKey, ise.EncryptionKeySignature));
        var selfSigned = ise.Sign(Signatures.EncryptionKeyPayload(ise.PublicKey, ise.PublicKey));
        Assert.False(PublicKeys.VerifyEncryptionKey(ise.PublicKey, ise.PublicKey, selfSigned)); // the identity key can't double as one
    }

    // ---- through the router ----

    [Fact]
    public async Task ARouterSubstitutedEncryptionKeyIsRefused()
    {
        await using var router = await RouterFixture.StartAsync();
        var (ise, _) = await router.MemberAsync("ise");
        var (_, wojtekKey) = await router.MemberAsync("wojtek");
        await ise.SendAsync("wojtek", new OutgoingMessage("before"));
        using var mallory = MachineKeys.Create();

        await router.App.Services.GetRequiredService<MachineRegistry>()
            .SubstituteEncryptionKeyAsync(wojtekKey.MachineId, mallory.EncryptionPublicKey, mallory.EncryptionKeySignature);
        await ise.RefreshMachinesAsync();

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => ise.SendAsync("wojtek", new OutgoingMessage("after")));
        Assert.Contains("isn't signed", refused.Message);
    }

    [Fact]
    public async Task MessagesOnTheRoutersDiskAreUnreadable()
    {
        await using var router = await RouterFixture.StartAsync();
        var (ise, _) = await router.MemberAsync("ise");
        var (wojtek, _) = await router.MemberAsync("wojtek");
        await wojtek.DisposeAsync(); // offline, so the message waits in the router's database

        await ise.SendAsync("wojtek/vault", new OutgoingMessage("the eagle lands at 0400") { Subject = "landing", FromAgent = "ops" });
        await RouterFixture.Eventually(() => router.Queue.Count == 1);

        // Re-read until the envelope is visible: SQLite may checkpoint the -wal between reading it and the main file.
        var onDisk = "";
        await RouterFixture.Eventually(() =>
        {
            onDisk = string.Concat(Directory.GetFiles(router.DataDirectory, "router.db*").Select(ReadShared));
            return onDisk.Contains("ciphertext", StringComparison.Ordinal);
        });
        Assert.DoesNotContain("eagle", onDisk);
        Assert.DoesNotContain("landing", onDisk);
        Assert.DoesNotContain("vault", onDisk); // agents too
        Assert.Contains("ciphertext", onDisk);
    }

    [Fact]
    public async Task AMessageResignedByAnotherMemberIsRefusedByTheRecipient()
    {
        await using var router = await RouterFixture.StartAsync();
        var (ise, iseKey) = await router.MemberAsync("ise");
        var (wojtek, wojtekKey) = await router.MemberAsync("wojtek");
        var (_, malloryKey) = await router.MemberAsync("mallory");
        var genuine = Envelopes.Sign(iseKey, wojtekKey, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1), "from ise");
        var handled = 0;
        wojtek.MessageReceived += _ => { Interlocked.Increment(ref handled); return Task.CompletedTask; };

        // Mallory takes ise's ciphertext and sends it as its own (keeping only wojtek's wrap, as the router requires).
        var claimed = Envelopes.Resign(genuine with { Wraps = [genuine.Wraps.Single(w => w.Machine == wojtekKey.MachineId)] }, malloryKey);
        var connection = await RelayTests.RawMemberConnectionAsync(router, malloryKey);
        await connection.InvokeAsync<SendResult>(HubMethods.Send, claimed);

        await RouterFixture.EventuallyAsync(async () => await wojtek.Store.GetMetricAsync(malloryKey.MachineId, ClientMetrics.Undecryptable) == 1);
        await RouterFixture.Eventually(() => router.Queue.Count == 0);
        Assert.Equal(0, handled);
    }

    [Fact]
    public async Task TheSenderCanReadWhatItSentAndBodiesArentStoredInTheClear()
    {
        await using var router = await RouterFixture.StartAsync();
        var (ise, iseKey) = await router.MemberAsync("ise");
        var (wojtek, wojtekKey) = await router.MemberAsync("wojtek");
        var received = Wait.For<ReceivedMessage>(h => wojtek.MessageReceived += m => { h(m); return Task.CompletedTask; });

        var sent = await ise.SendAsync("wojtek", new OutgoingMessage("correct horse battery staple"));
        var message = await received;

        Assert.Equal("correct horse battery staple", (await ise.ReadAsync(iseKey.MachineId, sent.Id))!.Body);
        Assert.Equal("correct horse battery staple", (await wojtek.ReadAsync(iseKey.MachineId, message.Envelope.Id))!.Body);
        SqliteConnection.ClearAllPools(); // flush, so everything written is in the files
        foreach (var keys in new[] { iseKey, wojtekKey })
        {
            foreach (var file in Directory.GetFiles(Path.GetDirectoryName(router.StorePath(keys))!, Path.GetFileName(router.StorePath(keys)) + "*"))
            {
                Assert.DoesNotContain("battery staple", Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file)));
            }
        }
    }

    // ---- key storage ----

    [Fact]
    public void KeysAreCreatedOnceAndKeptInTheSecretStore()
    {
        var home = TempHome();
        using var first = KeyRing.LoadOrCreate(home);
        using var again = KeyRing.LoadOrCreate(home);

        Assert.Equal((first.MachineId, first.EncryptionPublicKey), (again.MachineId, again.EncryptionPublicKey));
        Assert.NotEqual(first.PublicKey, first.EncryptionPublicKey);
        foreach (var file in Directory.GetFiles(home))
        {
            if (OperatingSystem.IsWindows())
            {
                Assert.DoesNotContain("PRIVATE KEY", File.ReadAllText(file), StringComparison.Ordinal); // DPAPI blobs, never a bare PEM
            }
            else
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file)); // Linux: owner-only files
            }
        }
    }

    [Fact]
    public void AnOldMachineKeyFileIsMovedIntoTheStore()
    {
        var home = TempHome();
        using var legacy = MachineIdentity.Create();
        File.WriteAllText(Path.Combine(home, KeyRing.LegacyKeyFile), legacy.ExportPrivateKeyPem());

        using var keys = KeyRing.LoadOrCreate(home);

        Assert.Equal(legacy.MachineId, keys.MachineId);
        Assert.False(File.Exists(Path.Combine(home, KeyRing.LegacyKeyFile)));
    }

    [Fact]
    public void TheWindowsStoreIsDpapiAndTiedToItsName()
    {
        if (OperatingSystem.IsWindows())
        {
            DpapiRoundTrip();
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void DpapiRoundTrip()
    {
        var home = TempHome();
        var store = new DpapiSecretStore(home);
        store.Save("a", "top secret"u8.ToArray());

        Assert.Equal("top secret", Encoding.UTF8.GetString(store.Load("a")!));
        Assert.DoesNotContain("top secret", File.ReadAllText(Path.Combine(home, "a.dpapi")));
        File.Copy(Path.Combine(home, "a.dpapi"), Path.Combine(home, "b.dpapi"));
        Assert.ThrowsAny<CryptographicException>(() => store.Load("b")); // one secret's blob can't pass for another's
        Assert.True(store.Delete("a"));
        Assert.Null(store.Load("a"));
    }

    [Fact]
    public void TheFileStoreIsOwnerOnly()
    {
        var home = TempHome();
        var store = new FileSecretStore(home);
        store.Save("a", [1, 2, 3]);

        Assert.Equal([1, 2, 3], store.Load("a"));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(home, "a.secret")));
        }
    }

    internal static string ReadShared(string path)
    {
        if (!File.Exists(path))
        {
            return string.Empty; // a -wal or -shm file checkpointed away since the listing
        }

        try
        {
            return ReadOpen(path);
        }
        catch (FileNotFoundException)
        {
            return string.Empty;
        }
    }

    private static string ReadOpen(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static string TempHome() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "poniexp-tests", "home-" + Guid.NewGuid().ToString("N"))).FullName;
}
