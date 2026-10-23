using PoniExp.Client.Completion;

namespace PoniExp.Tests;

public class CompletionTests
{
    private const string Bq = "bq79ag00-0000-0000-0000-000000000001";
    private const string B0 = "b059a000-0000-0000-0000-000000000002";

    /// <summary>Answers every kind with its own fixed list and remembers what was asked.</summary>
    private sealed class FakeSource : ICompletionSource
    {
        public Dictionary<CompletionKind, string[]> Lists { get; } = new()
        {
            [CompletionKind.PendingApproval] = [Bq, B0],
            [CompletionKind.InboxMessage] = ["inbox-1"],
            [CompletionKind.LoggedMessage] = ["logged-1"],
            [CompletionKind.Conversation] = ["conv-1"],
            [CompletionKind.Rule] = ["rule-1"],
            [CompletionKind.Job] = ["job-1"],
            [CompletionKind.Room] = ["standup"],
            [CompletionKind.LocalAgent] = ["hermes-default", "claude"],
            [CompletionKind.WakeAgent] = ["hermes-default"],
            [CompletionKind.Machine] = ["ise", "blinky"],
            [CompletionKind.Address] = ["ise", "ise/hermes-default", "blinky/hermes-default", "room:standup"],
            [CompletionKind.Blob] = ["blob-1"],
        };

        public List<(CompletionKind Kind, string? Agent)> Asked { get; } = [];

        public Task<IReadOnlyList<string>> ListAsync(CompletionKind kind, string? agent, CancellationToken ct)
        {
            Asked.Add((kind, agent));
            return Task.FromResult<IReadOnlyList<string>>(Lists[kind]);
        }
    }

    private static Task<IReadOnlyList<string>> CompleteAsync(FakeSource source, params string[] words) => CommandCompletion.CompleteAsync(words, source);

    [Fact]
    public async Task Given_NothingTyped_When_Completing_Then_EveryCommandIsOfferedButNotTheHiddenHelper()
    {
        // Given
        var source = new FakeSource();

        // When
        var offered = await CompleteAsync(source, "");

        // Then
        Assert.Equal(CommandCompletion.Commands, offered);
        Assert.DoesNotContain("complete", offered);
    }

    [Fact]
    public async Task Given_TwoPendingApprovals_When_CompletingApproveWithTwoLetters_Then_OnlyTheMatchingFullIdIsOffered()
    {
        // Given
        var source = new FakeSource();

        // When
        var bq = await CompleteAsync(source, "approve", "bq");
        var b0 = await CompleteAsync(source, "approve", "b0");

        // Then
        Assert.Equal([Bq], bq);
        Assert.Equal([B0], b0);
    }

    [Fact]
    public async Task Given_TwoPendingApprovals_When_CompletingASharedFirstLetter_Then_BothAreOffered()
    {
        // Given
        var source = new FakeSource();

        // When
        var offered = await CompleteAsync(source, "deny", "b");

        // Then
        Assert.Equal([Bq, B0], offered);
    }

    [Fact]
    public async Task Given_AnUppercasePrefix_When_Completing_Then_ItMatchesIgnoringCase()
    {
        // Given
        var source = new FakeSource();

        // When
        var offered = await CompleteAsync(source, "approve", "BQ");

        // Then
        Assert.Equal([Bq], offered);
    }

    [Fact]
    public async Task Given_TheIdIsAlreadyTyped_When_CompletingTheNextWord_Then_NothingIsOffered()
    {
        // Given
        var source = new FakeSource();

        // When
        var offered = await CompleteAsync(source, "approve", Bq, "");

        // Then
        Assert.Empty(offered);
        Assert.Empty(source.Asked);
    }

    [Fact]
    public async Task Given_AnAgentOption_When_CompletingAnAckId_Then_ThatAgentsInboxIsAsked()
    {
        // Given
        var source = new FakeSource();

        // When
        var offered = await CompleteAsync(source, "ack", "--agent", "claude", "");

        // Then
        Assert.Equal(["inbox-1"], offered);
        Assert.Equal([(CompletionKind.InboxMessage, "claude")], source.Asked);
    }

    [Fact]
    public async Task Given_NoAgentOption_When_CompletingAReplyId_Then_EveryAgentsInboxIsAsked()
    {
        // Given
        var source = new FakeSource();

        // When
        await CompleteAsync(source, "reply", "");

        // Then
        Assert.Equal([(CompletionKind.InboxMessage, null)], source.Asked);
    }

    [Theory]
    [InlineData("message", CompletionKind.LoggedMessage)]
    [InlineData("fetch", CompletionKind.LoggedMessage)]
    [InlineData("release", CompletionKind.LoggedMessage)]
    [InlineData("conversation", CompletionKind.Conversation)]
    [InlineData("cancel", CompletionKind.Blob)]
    public async Task Given_ACommandTakingAnId_When_CompletingItsFirstArgument_Then_ThatKindIsAsked(string command, CompletionKind kind)
    {
        // Given
        var source = new FakeSource();

        // When
        var offered = await CompleteAsync(source, command, "");

        // Then
        Assert.Equal(source.Lists[kind], offered);
        Assert.Equal(kind, Assert.Single(source.Asked).Kind);
    }

    [Theory]
    [InlineData(new[] { "approvals", "" }, new[] { "rules" })]
    [InlineData(new[] { "approvals", "rules", "" }, new[] { "remove" })]
    [InlineData(new[] { "approvals", "rules", "remove", "" }, new[] { "rule-1" })]
    [InlineData(new[] { "job", "claim", "" }, new[] { "job-1" })]
    [InlineData(new[] { "job", "move", "job-1", "" }, new[] { "backlog", "ready", "running", "done", "failed" })]
    [InlineData(new[] { "room", "join", "" }, new[] { "standup" })]
    [InlineData(new[] { "room", "invite", "standup", "blinky" }, new[] { "blinky/hermes-default" })]
    [InlineData(new[] { "wake", "test", "" }, new[] { "hermes-default" })]
    [InlineData(new[] { "agent", "retire", "cl" }, new[] { "claude" })]
    [InlineData(new[] { "completion", "" }, new[] { "bash", "powershell", "zsh" })]
    [InlineData(new[] { "keys", "r" }, new[] { "rotate", "reset" })]
    public async Task Given_ASubcommandPath_When_Completing_Then_ItsNextWordIsOffered(string[] words, string[] expected)
    {
        // Given
        var source = new FakeSource();

        // When
        var offered = await CompleteAsync(source, words);

        // Then
        Assert.Equal(expected, offered);
    }

    [Theory]
    [InlineData(new[] { "send", "--to", "ise/" }, new[] { "ise/hermes-default" })]
    [InlineData(new[] { "send", "--to", "room:" }, new[] { "room:standup" })]
    [InlineData(new[] { "reply", "x", "--status", "" }, new[] { "progress", "done", "failed" })]
    [InlineData(new[] { "log", "--peer", "b" }, new[] { "blinky" })]
    [InlineData(new[] { "job", "list", "--column", "r" }, new[] { "ready", "running" })]
    [InlineData(new[] { "send", "--subject", "" }, new string[0])]
    public async Task Given_AnOptionThatTakesAValue_When_CompletingTheValue_Then_ItsValuesAreOffered(string[] words, string[] expected)
    {
        // Given
        var source = new FakeSource();

        // When
        var offered = await CompleteAsync(source, words);

        // Then
        Assert.Equal(expected, offered);
    }

    [Fact]
    public async Task Given_ADash_When_Completing_Then_TheCommandsOptionsAreOffered()
    {
        // Given
        var source = new FakeSource();

        // When
        var offered = await CompleteAsync(source, "approve", "--a");

        // Then
        Assert.Equal(["--always"], offered);
    }

    [Fact]
    public async Task Given_AFlagBeforeTheId_When_Completing_Then_TheIdIsStillTheFirstArgument()
    {
        // Given
        var source = new FakeSource();

        // When
        var offered = await CompleteAsync(source, "approve", "--always", "b0");

        // Then
        Assert.Equal([B0], offered);
    }

    [Fact]
    public async Task Given_AHomeOption_When_Completing_Then_ItIsSkipped()
    {
        // Given
        var source = new FakeSource();

        // When
        var offered = await CompleteAsync(source, "--home", "/tmp/x", "appr");

        // Then
        Assert.Equal(["approvals", "approve"], offered);
    }

    [Fact]
    public void Given_ALineEndingInASpace_When_Splitting_Then_TheWordUnderTheCursorIsEmpty()
    {
        // Given
        const string line = "poniexp-client approve ";

        // When
        var words = CommandCompletion.SplitLine(line);

        // Then
        Assert.Equal(["approve", ""], words);
    }

    [Fact]
    public void Given_AQuotedWord_When_Splitting_Then_ItStaysOneWordWithoutQuotes()
    {
        // Given
        const string line = "C:\\bin\\poniexp-client.exe deny b0 --reason 'not now' --a";

        // When
        var words = CommandCompletion.SplitLine(line);

        // Then
        Assert.Equal(["deny", "b0", "--reason", "not now", "--a"], words);
    }

    [Fact]
    public void Given_OnlyTheProgramName_When_Splitting_Then_OneEmptyWordIsLeft()
    {
        // Given
        const string line = "poniexp-client";

        // When
        var words = CommandCompletion.SplitLine(line);

        // Then
        Assert.Equal([""], words);
    }

    [Fact]
    public void Given_AWordWithAColon_When_ShapingForBash_Then_TheCandidatesStartAfterTheColon()
    {
        // Given
        string[] candidates = ["room:standup", "room:retro"];

        // When
        var shaped = CommandCompletion.ForBash("room:st", candidates);

        // Then
        Assert.Equal(["standup", "retro"], shaped);
    }

    [Fact]
    public void Given_AWordWithoutABreak_When_ShapingForBash_Then_TheCandidatesAreUnchanged()
    {
        // Given
        string[] candidates = [Bq];

        // When
        var shaped = CommandCompletion.ForBash("bq", candidates);

        // Then
        Assert.Equal(candidates, shaped);
    }

    [Theory]
    [InlineData("bash", "complete --bash --line")]
    [InlineData("zsh", "complete --")]
    [InlineData("powershell", "complete --line")]
    public void Given_ASupportedShell_When_AskingForItsScript_Then_TheScriptCallsTheHelper(string shell, string call)
    {
        // Given / When
        var script = CompletionScripts.For(shell);

        // Then
        Assert.Contains("poniexp-client " + call, script, StringComparison.Ordinal);
    }

    [Fact]
    public void Given_AnUnknownShell_When_AskingForItsScript_Then_ItIsRefused()
    {
        // Given
        const string shell = "fish";

        // When
        var refused = Assert.Throws<ArgumentException>(() => CompletionScripts.For(shell));

        // Then
        Assert.Contains("bash, zsh or powershell", refused.Message, StringComparison.Ordinal);
    }
}
