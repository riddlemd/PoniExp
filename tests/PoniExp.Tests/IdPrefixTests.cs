using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PoniExp.Client;
using PoniExp.Core.Protocol;
using PoniExp.Mcp;

namespace PoniExp.Tests;

/// <summary>
/// Message and conversation ids as people type them: the whole id, or a unique prefix of at least 4 characters
/// (the approve/deny rule), resolved once by the daemon for the CLI and the MCP tools alike.
/// </summary>
public class IdPrefixTests
{
    private const string Agent = "agent";
    private static readonly Guid First = Guid.Parse("abcd1234-0000-4000-8000-000000000001");
    private static readonly Guid Second = Guid.Parse("abcd5678-0000-4000-8000-000000000002");

    // ---- the shared resolver (rows seeded straight into the store, so ids can share a prefix) ----

    [Fact]
    public async Task Given_AReceivedMessage_When_ResolvingFourCharactersOfItsId_Then_ItsIdComesBack()
    {
        // Given
        await using var router = await RouterFixture.StartAsync();
        await using var ise = await DaemonFixture.StartAsync(router, "ise");
        SeedIncoming(ise, First);

        // When
        var id = ise.Daemon.ResolveMessageId(Agent, "abcd");

        // Then
        Assert.Equal(First, id);
    }

    [Fact]
    public async Task Given_TwoReceivedMessagesSharingAPrefix_When_ResolvingThatPrefix_Then_ItAsksForMoreOfTheId()
    {
        // Given
        await using var router = await RouterFixture.StartAsync();
        await using var ise = await DaemonFixture.StartAsync(router, "ise");
        SeedIncoming(ise, First);
        SeedIncoming(ise, Second);

        // When
        var error = Assert.Throws<ArgumentException>(() => ise.Daemon.ResolveMessageId(Agent, "abcd"));

        // Then
        Assert.Equal("'abcd' matches 2 messages; use more of its id", error.Message);
    }

    [Fact]
    public async Task Given_TwoReceivedMessagesSharingAPrefix_When_ResolvingOneCharacterMore_Then_TheOneItNamesComesBack()
    {
        // Given
        await using var router = await RouterFixture.StartAsync();
        await using var ise = await DaemonFixture.StartAsync(router, "ise");
        SeedIncoming(ise, First);
        SeedIncoming(ise, Second);

        // When
        var id = ise.Daemon.ResolveMessageId(Agent, "abcd5");

        // Then
        Assert.Equal(Second, id);
    }

    [Fact]
    public async Task Given_AReceivedMessage_When_ResolvingThreeCharactersOfItsId_Then_NothingMatches()
    {
        // Given
        await using var router = await RouterFixture.StartAsync();
        await using var ise = await DaemonFixture.StartAsync(router, "ise");
        SeedIncoming(ise, First);

        // When
        var error = Assert.Throws<KeyNotFoundException>(() => ise.Daemon.ResolveMessageId(Agent, "abc"));

        // Then
        Assert.Equal($"no message abc for agent '{Agent}'", error.Message);
    }

    [Fact]
    public async Task Given_AReceivedMessage_When_ResolvingAPrefixOfNoMessage_Then_NothingMatches()
    {
        // Given
        await using var router = await RouterFixture.StartAsync();
        await using var ise = await DaemonFixture.StartAsync(router, "ise");
        SeedIncoming(ise, First);

        // When
        var error = Assert.Throws<KeyNotFoundException>(() => ise.Daemon.ResolveMessageId(Agent, "abce"));

        // Then
        Assert.Equal($"no message abce for agent '{Agent}'", error.Message);
    }

    [Fact]
    public async Task Given_AReceivedMessage_When_ResolvingItsWholeIdInCapitals_Then_ItsIdComesBack()
    {
        // Given
        await using var router = await RouterFixture.StartAsync();
        await using var ise = await DaemonFixture.StartAsync(router, "ise");
        SeedIncoming(ise, First);

        // When
        var id = ise.Daemon.ResolveMessageId(Agent, First.ToString().ToUpperInvariant());

        // Then
        Assert.Equal(First, id);
    }

    [Fact]
    public async Task Given_AnotherAgentsMessage_When_ResolvingItsPrefix_Then_NothingMatches()
    {
        // Given
        await using var router = await RouterFixture.StartAsync();
        await using var ise = await DaemonFixture.StartAsync(router, "ise");
        SeedIncoming(ise, First, toAgent: "someone-else");

        // When
        var error = Record.Exception(() => ise.Daemon.ResolveMessageId(Agent, "abcd"));

        // Then
        Assert.IsType<KeyNotFoundException>(error);
    }

    [Fact]
    public async Task Given_AMessageTheAgentSent_When_ResolvingItsPrefixAmongReceivedOnes_Then_NothingMatches()
    {
        // Given
        await using var router = await RouterFixture.StartAsync();
        await using var ise = await DaemonFixture.StartAsync(router, "ise");
        SeedOutgoing(ise, First);

        // When
        var error = Record.Exception(() => ise.Daemon.ResolveMessageId(Agent, "abcd"));

        // Then
        Assert.IsType<KeyNotFoundException>(error);
    }

    [Fact]
    public async Task Given_AMessageTheAgentSent_When_ResolvingItsPrefixAmongSentOnesToo_Then_ItsIdComesBack()
    {
        // Given
        await using var router = await RouterFixture.StartAsync();
        await using var ise = await DaemonFixture.StartAsync(router, "ise");
        SeedOutgoing(ise, First);

        // When
        var id = ise.Daemon.ResolveMessageId(Agent, "abcd", sent: true);

        // Then
        Assert.Equal(First, id);
    }

    [Fact]
    public async Task Given_AConversationOfTwoMessages_When_ResolvingFourCharactersOfItsId_Then_ItsIdComesBack()
    {
        // Given
        await using var router = await RouterFixture.StartAsync();
        await using var ise = await DaemonFixture.StartAsync(router, "ise");
        SeedIncoming(ise, Guid.NewGuid(), conversation: First);
        SeedOutgoing(ise, Guid.NewGuid(), conversation: First);

        // When
        var id = ise.Daemon.ResolveConversationId(Agent, "abcd");

        // Then
        Assert.Equal(First, id);
    }

    [Fact]
    public async Task Given_TwoConversationsSharingAPrefix_When_ResolvingThatPrefix_Then_ItAsksForMoreOfTheId()
    {
        // Given
        await using var router = await RouterFixture.StartAsync();
        await using var ise = await DaemonFixture.StartAsync(router, "ise");
        SeedIncoming(ise, Guid.NewGuid(), conversation: First);
        SeedIncoming(ise, Guid.NewGuid(), conversation: Second);

        // When
        var error = Assert.Throws<ArgumentException>(() => ise.Daemon.ResolveConversationId(Agent, "abcd"));

        // Then
        Assert.Equal("'abcd' matches 2 conversations; use more of its id", error.Message);
    }

    [Fact]
    public async Task Given_AConversation_When_ResolvingThreeCharactersOfItsId_Then_NothingMatches()
    {
        // Given
        await using var router = await RouterFixture.StartAsync();
        await using var ise = await DaemonFixture.StartAsync(router, "ise");
        SeedIncoming(ise, Guid.NewGuid(), conversation: First);

        // When
        var error = Assert.Throws<KeyNotFoundException>(() => ise.Daemon.ResolveConversationId(Agent, "abc"));

        // Then
        Assert.Equal($"agent '{Agent}' has no conversation abc", error.Message);
    }

    [Fact]
    public async Task Given_AConversation_When_ResolvingAPrefixOfNoConversation_Then_NothingMatches()
    {
        // Given
        await using var router = await RouterFixture.StartAsync();
        await using var ise = await DaemonFixture.StartAsync(router, "ise");
        SeedIncoming(ise, Guid.NewGuid(), conversation: First);

        // When
        var error = Record.Exception(() => ise.Daemon.ResolveConversationId(Agent, "abce"));

        // Then
        Assert.IsType<KeyNotFoundException>(error);
    }

    // ---- end to end, through the local API ----

    [Fact]
    public async Task Given_AReceivedMessage_When_AckingItByAPrefix_Then_ItsSenderSeesItAcknowledged()
    {
        // Given
        await using var pair = await Pair.StartAsync();
        var received = await pair.ReceiveAsync("ack me");

        // When
        await pair.Wojtek.Api.AckAsync(Agent, Prefix(received.Id));

        // Then
        await pair.Ise.WaitForOutgoingAsync(received.Id, MessageEvents.Acknowledged);
    }

    [Fact]
    public async Task Given_AReceivedMessage_When_ReplyingToItByAPrefix_Then_TheReplyReachesItsSender()
    {
        // Given
        await using var pair = await Pair.StartAsync();
        var received = await pair.ReceiveAsync("how many?");

        // When
        await pair.Wojtek.Api.ReplyAsync(Agent, new LocalReplyRequest(Prefix(received.Id), "three", "done"));

        // Then
        var reply = await pair.ReplyToAsync(received.Id);
        Assert.Equal("three", reply.Body);
    }

    [Fact]
    public async Task Given_TwoReceivedMessagesSharingAPrefix_When_ReplyingByThatPrefix_Then_TheApiSaysUseMoreOfItsId()
    {
        // Given
        await using var pair = await Pair.StartAsync();
        var received = await pair.ReceiveAsync("one of two");
        var prefix = received.Id.ToString()[..8];
        SeedIncoming(pair.Wojtek, Guid.Parse(prefix + "-0000-4000-8000-000000000000"));

        // When
        var error = await Assert.ThrowsAsync<LocalApiException>(() => pair.Wojtek.Api.ReplyAsync(Agent, new LocalReplyRequest(prefix, "which?")));

        // Then
        Assert.Equal((HttpStatusCode.BadRequest, $"'{prefix}' matches 2 messages; use more of its id"), (error.Status, error.Message));
    }

    [Fact]
    public async Task Given_AMessageTheAgentSentSharingAPrefixWithOneItReceived_When_ReplyingByThatPrefix_Then_TheReceivedOneIsAnswered()
    {
        // Given
        await using var pair = await Pair.StartAsync();
        var received = await pair.ReceiveAsync("answer this one");
        var prefix = received.Id.ToString()[..8];
        SeedOutgoing(pair.Wojtek, Guid.Parse(prefix + "-0000-4000-8000-000000000000"));

        // When
        await pair.Wojtek.Api.ReplyAsync(Agent, new LocalReplyRequest(prefix, "answered"));

        // Then
        var reply = await pair.ReplyToAsync(received.Id);
        Assert.Equal("answered", reply.Body);
    }

    [Fact]
    public async Task Given_AMessageTheAgentSent_When_AskingForItByAPrefix_Then_ItAndItsHistoryComeBack()
    {
        // Given
        await using var pair = await Pair.StartAsync();
        var received = await pair.ReceiveAsync("where is it?");

        // When
        var detail = await pair.Ise.Api.MessageAsync("ops", Prefix(received.Id));

        // Then
        Assert.Equal((received.Id, MessageEvents.Queued), (detail.Message.Id, detail.Events[0].Event));
    }

    [Fact]
    public async Task Given_AConversationOfAQuestionAndAReply_When_ReadingItByAPrefix_Then_BothMessagesComeBack()
    {
        // Given
        await using var pair = await Pair.StartAsync();
        var received = await pair.ReceiveAsync("how many?");
        await pair.Wojtek.Api.ReplyAsync(Agent, new LocalReplyRequest(received.Id.ToString(), "three", "done"));

        // When
        var conversation = await pair.Wojtek.Api.ConversationAsync(Agent, Prefix(received.ConversationId));

        // Then
        Assert.Equal(["how many?", "three"], conversation.Select(m => m.Body));
    }

    [Fact]
    public async Task Given_AReceivedMessageWithAFile_When_FetchingByAPrefix_Then_TheFileIsHere()
    {
        // Given
        await using var pair = await Pair.StartAsync();
        var (path, bytes) = NewFile();
        var received = await pair.ReceiveAsync("a file", path);

        // When
        var fetched = Assert.Single(await pair.Wojtek.Api.FetchAsync(Agent, new LocalFileRequest(Prefix(received.Id))));

        // Then
        Assert.Equal(bytes, await File.ReadAllBytesAsync(fetched.Path!));
    }

    [Fact]
    public async Task Given_AFetchedFile_When_ReleasingByAPrefix_Then_ItIsReleased()
    {
        // Given
        await using var pair = await Pair.StartAsync();
        var (path, _) = NewFile();
        var received = await pair.ReceiveAsync("a file", path);
        await pair.Wojtek.Api.FetchAsync(Agent, new LocalFileRequest(received.Id.ToString()));

        // When
        var released = Assert.Single(await pair.Wojtek.Api.ReleaseAsync(Agent, new LocalFileRequest(Prefix(received.Id))));

        // Then
        Assert.Equal(AttachmentEvents.Released, released.State);
    }

    [Fact]
    public async Task Given_TwoReceivedMessagesSharingAPrefix_When_AckingByThatPrefix_Then_TheApiSaysUseMoreOfItsId()
    {
        // Given
        await using var pair = await Pair.StartAsync();
        var received = await pair.ReceiveAsync("one of two");
        var twin = Guid.Parse(received.Id.ToString()[..8] + "-0000-4000-8000-000000000000");
        SeedIncoming(pair.Wojtek, twin);

        // When
        var error = await Assert.ThrowsAsync<LocalApiException>(() => pair.Wojtek.Api.AckAsync(Agent, received.Id.ToString()[..8]));

        // Then
        Assert.Equal((HttpStatusCode.BadRequest, $"'{received.Id.ToString()[..8]}' matches 2 messages; use more of its id"), (error.Status, error.Message));
    }

    [Fact]
    public async Task Given_AReceivedMessage_When_AckingByThreeCharacters_Then_TheApiFindsNoSuchMessage()
    {
        // Given
        await using var pair = await Pair.StartAsync();
        var received = await pair.ReceiveAsync("too short");

        // When
        var error = await Assert.ThrowsAsync<LocalApiException>(() => pair.Wojtek.Api.AckAsync(Agent, received.Id.ToString()[..3]));

        // Then
        Assert.Equal(HttpStatusCode.NotFound, error.Status);
    }

    [Fact]
    public async Task Given_AReceivedMessage_When_AckingItByItsWholeId_Then_ItsSenderSeesItAcknowledged()
    {
        // Given
        await using var pair = await Pair.StartAsync();
        var received = await pair.ReceiveAsync("the whole id");

        // When
        await pair.Wojtek.Api.AckAsync(Agent, received.Id.ToString());

        // Then
        await pair.Ise.WaitForOutgoingAsync(received.Id, MessageEvents.Acknowledged);
    }

    [Fact]
    public async Task Given_AReceivedMessage_When_TheMcpAckToolGetsAPrefix_Then_ItsSenderSeesItAcknowledged()
    {
        // Given
        await using var pair = await Pair.StartAsync();
        var received = await pair.ReceiveAsync("ack me through MCP");
        var tools = new PoniExpTools(new McpAgent(pair.Wojtek.Api, Agent));

        // When
        await tools.AckAsync(Prefix(received.Id));

        // Then
        await pair.Ise.WaitForOutgoingAsync(received.Id, MessageEvents.Acknowledged);
    }

    [Fact]
    public async Task Given_AReceivedMessage_When_TheMcpReplyToolGetsAPrefix_Then_TheReplyReachesItsSender()
    {
        // Given
        await using var pair = await Pair.StartAsync();
        var received = await pair.ReceiveAsync("answer me through MCP");
        var tools = new PoniExpTools(new McpAgent(pair.Wojtek.Api, Agent));

        // When
        await tools.ReplyAsync(Prefix(received.Id), "answered", "done");

        // Then
        var reply = await pair.ReplyToAsync(received.Id);
        Assert.Equal("answered", reply.Body);
    }

    [Fact]
    public async Task Given_AConversation_When_TheMcpConversationToolGetsAPrefix_Then_ItsMessagesComeBack()
    {
        // Given
        await using var pair = await Pair.StartAsync();
        var received = await pair.ReceiveAsync("read me through MCP");
        var tools = new PoniExpTools(new McpAgent(pair.Wojtek.Api, Agent));

        // When
        var json = await tools.ConversationAsync(Prefix(received.ConversationId));

        // Then
        Assert.Equal(["read me through MCP"], JsonSerializer.Deserialize<List<LocalMessage>>(json, WireProtocol.Json)!.Select(m => m.Body));
    }

    // ---- helpers ----

    private static string Prefix(Guid id) => id.ToString()[..4];

    /// <summary>ise/ops sends to wojtek/agent; the message as wojtek's agent sees it.</summary>
    private sealed class Pair : IAsyncDisposable
    {
        public required RouterFixture Router { get; init; }
        public required DaemonFixture Ise { get; init; }
        public required DaemonFixture Wojtek { get; init; }

        public static async Task<Pair> StartAsync()
        {
            var router = await RouterFixture.StartAsync();
            var pair = new Pair
            {
                Router = router,
                Ise = await DaemonFixture.StartAsync(router, "ise"),
                Wojtek = await DaemonFixture.StartAsync(router, "wojtek"),
            };
            await using var connection = new AgentConnection(pair.Wojtek.Api, Agent, _ => Task.CompletedTask);
            await connection.Registered.WaitAsync(TimeSpan.FromSeconds(10));
            return pair;
        }

        public async Task<LocalMessage> ReceiveAsync(string body, string? file = null)
        {
            var sent = (await Ise.Api.SendAsync(new LocalSendRequest("ops", [$"wojtek/{Agent}"], body, Attachments: file is null ? null : [file]))).Single();
            return await RouterFixture.EventuallyValueAsync(async () => (await Wojtek.Api.InboxAsync(Agent)).SingleOrDefault(m => m.Id == sent.Id));
        }

        /// <summary>wojtek's reply to <paramref name="id"/>, once it is in ise/ops's inbox.</summary>
        public Task<LocalMessage> ReplyToAsync(Guid id) =>
            RouterFixture.EventuallyValueAsync(async () => (await Ise.Api.InboxAsync("ops")).SingleOrDefault(m => m.ReplyTo == id));

        public async ValueTask DisposeAsync()
        {
            await Wojtek.DisposeAsync();
            await Ise.DisposeAsync();
            await Router.DisposeAsync();
        }
    }

    private static (string Path, byte[] Bytes) NewFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "poniexp-tests", "files-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(directory);
        var bytes = RandomNumberGenerator.GetBytes(3000);
        var path = Path.Combine(directory, "notes.txt");
        File.WriteAllBytes(path, bytes);
        return (path, bytes);
    }

    private static void SeedIncoming(DaemonFixture daemon, Guid id, Guid? conversation = null, string toAgent = Agent) =>
        Seed(daemon, id, "peer-machine", "peer", daemon.Keys.MachineId, toAgent, conversation ?? Guid.NewGuid());

    private static void SeedOutgoing(DaemonFixture daemon, Guid id, Guid? conversation = null) =>
        Seed(daemon, id, daemon.Keys.MachineId, Agent, "peer-machine", "peer", conversation ?? Guid.NewGuid());

    /// <summary>A bare <c>messages</c> row (no envelope): enough for the resolver, which only reads ids and addresses.</summary>
    private static void Seed(DaemonFixture daemon, Guid id, string fromMachine, string fromAgent, string toMachine, string toAgent, Guid conversation)
    {
        using var connection = new SqliteConnection($"Data Source={daemon.Store.Path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO messages (from_machine, id, from_agent, to_machine, to_agent, conversation_id, operation_id, type, content_type,
                sequence, created_on, stored_on, expires_on)
            VALUES ($fromMachine, $id, $fromAgent, $toMachine, $toAgent, $conversation, $operation, 'message', 'text/plain', 1, $now, $now, $now);
            """;
        command.Parameters.AddWithValue("$fromMachine", fromMachine);
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.Parameters.AddWithValue("$fromAgent", fromAgent);
        command.Parameters.AddWithValue("$toMachine", toMachine);
        command.Parameters.AddWithValue("$toAgent", toAgent);
        command.Parameters.AddWithValue("$conversation", conversation.ToString("D"));
        command.Parameters.AddWithValue("$operation", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }
}
