using Ansight.Cli.Commands.App.Learning;

namespace Ansight.Cli.Tests.Commands.App.Learning;

public sealed class AppLearnPipelineTests
{
    [Fact]
    public async Task SavesOnlyAfterPreparationAndNamedAssertionsPass()
    {
        var author = new FakeAuthor();
        var result = await RunAsync(author);
        Assert.Equal("saved", result.Status);
        Assert.Equal(new[] { "author", "prepare", "test", "save" }, author.Calls);
        Assert.NotNull(result.Extraction!.CommittedPath);
    }

    [Fact]
    public async Task DraftOnlyNeverPreparesRunsOrSaves()
    {
        var author = new FakeAuthor();
        var result = await RunAsync(author, draftOnly: true);
        Assert.Equal("draft", result.Status);
        Assert.Equal(new[] { "author" }, author.Calls);
        Assert.Null(result.Extraction!.TestResult);
    }

    [Theory]
    [InlineData("needsReview")]
    [InlineData("failed")]
    public async Task InvalidDraftNeverRuns(string status)
    {
        var author = new FakeAuthor { AuthorStatus = status };
        var result = await RunAsync(author);
        Assert.Equal("needs-review", result.Status);
        Assert.Equal(new[] { "author" }, author.Calls);
    }

    [Fact]
    public async Task StaticWarningsBlockVerificationEvenWhenReady()
    {
        var author = new FakeAuthor { Warnings = ["Unobserved selector"] };
        var result = await RunAsync(author);
        Assert.Equal("needs-review", result.Status);
        Assert.Equal(new[] { "author" }, author.Calls);
    }

    [Fact]
    public async Task PreparationFailurePreservesDraftWithoutExecutingIt()
    {
        var author = new FakeAuthor();
        var result = await RunAsync(author, prepare: false);
        Assert.Equal("blocked", result.Status);
        Assert.NotNull(result.Extraction!.Draft);
        Assert.Equal(new[] { "author", "prepare" }, author.Calls);
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("inconclusive")]
    [InlineData("cancelled")]
    [InlineData("timedout")]
    public async Task UnsuccessfulVerificationNeverSaves(string status)
    {
        var author = new FakeAuthor { TestResult = AppLearnTestData.PassedResult() with { Status = status } };
        Assert.Equal("failed", (await RunAsync(author)).Status);
        Assert.DoesNotContain("save", author.Calls);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("generic")]
    [InlineData("false")]
    public async Task PassingStatusWithoutRequiredProofNeverSaves(string kind)
    {
        var assertions = kind switch
        {
            "none" => Array.Empty<RepositoryTaskAssertion>(),
            "generic" => [new RepositoryTaskAssertion("ui-stable", true, "Stable", null, null)],
            _ => AppLearnTestData.PassedResult().Assertions.Select(assertion => assertion with { Passed = false }).ToArray()
        };
        var author = new FakeAuthor { TestResult = AppLearnTestData.PassedResult() with { Assertions = assertions } };
        Assert.Equal("failed", (await RunAsync(author)).Status);
        Assert.DoesNotContain("save", author.Calls);
    }

    [Fact]
    public async Task SaveCollisionRetainsVerifiedDraftAndReportsFailure()
    {
        var author = new FakeAuthor { SaveFails = true };
        var result = await RunAsync(author);
        Assert.Equal("failed", result.Status);
        Assert.Equal("passed", result.Extraction!.TestResult!.Status);
        Assert.Contains("already exists", result.Message);
    }

    [Fact]
    public async Task VerificationFromAnotherSessionIsNotAccepted()
    {
        var author = new FakeAuthor { TestResult = AppLearnTestData.PassedResult() with { SessionId = "other-session" } };
        Assert.Equal("failed", (await RunAsync(author)).Status);
        Assert.DoesNotContain("save", author.Calls);
    }

    [Fact]
    public async Task CancellationAfterAuthoringNeverRunsAndCheckpointsDraft()
    {
        using var cancellation = new CancellationTokenSource();
        var author = new FakeAuthor();
        LocalTaskExtractionSnapshot? checkpoint = null;
        var candidate = AppLearnTestData.Candidate();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AppLearnPipeline.RunCandidateAsync(candidate,
            Request(), false, author, (_, _, token) =>
            {
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
                return Task.FromResult(true);
            }, cancellation.Token, snapshot => checkpoint = snapshot));
        Assert.NotNull(checkpoint!.Draft);
        Assert.Equal(new[] { "author" }, author.Calls);
    }

    [Fact]
    public async Task AuthorFailureAfterProducingSourceRetainsItsCheckpoint()
    {
        var author = new FakeAuthor { AuthorFailsAfterCheckpoint = true };
        var result = await RunAsync(author);
        Assert.Equal("failed", result.Status);
        Assert.NotNull(result.Extraction!.Draft);
        Assert.Equal(new[] { "author" }, author.Calls);
    }

    private static Task<AppLearnTaskResult> RunAsync(FakeAuthor author, bool draftOnly = false, bool prepare = true)
        => AppLearnPipeline.RunCandidateAsync(AppLearnTestData.Candidate(), Request(), draftOnly, author, (_, _, _) =>
        {
            author.Calls.Add("prepare");
            return Task.FromResult(prepare);
        }, CancellationToken.None);

    private static LocalTaskExtractionStartRequest Request() => new("session-1", DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow, "Learn");

    private sealed class FakeAuthor : IAppLearnAuthor
    {
        private LocalTaskExtractionSnapshot snapshot = AppLearnTestData.Snapshot(AppLearnTestData.Candidate());
        public List<string> Calls { get; } = [];
        public string AuthorStatus { get; init; } = "ready";
        public IReadOnlyList<string> Warnings { get; init; } = [];
        public LocalTaskExtractionTestResult TestResult { get; init; } = AppLearnTestData.PassedResult();
        public bool SaveFails { get; init; }
        public bool AuthorFailsAfterCheckpoint { get; init; }

        public Task<LocalTaskExtractionSnapshot> AuthorAsync(LocalTaskExtractionStartRequest request, CancellationToken cancellationToken,
            Action<LocalTaskExtractionSnapshot> checkpoint)
        {
            Calls.Add("author");
            snapshot = snapshot with { Status = AuthorStatus, Draft = snapshot.Draft! with { ValidationWarnings = Warnings } };
            checkpoint(snapshot);
            if (AuthorFailsAfterCheckpoint) throw new IOException("Authoring failed after source was retained.");
            return Task.FromResult(snapshot);
        }

        public Task<LocalTaskExtractionSnapshot> TestAsync(string extractionId, string sessionId, CancellationToken cancellationToken)
        {
            Calls.Add("test");
            snapshot = snapshot with { TestStatus = TestResult.Status, TestResult = TestResult };
            return Task.FromResult(snapshot);
        }

        public LocalTaskExtractionSnapshot Save(string extractionId)
        {
            Calls.Add("save");
            if (SaveFails) throw new IOException("Task already exists.");
            return snapshot with { Status = "committed", CommittedPath = "/workspace/ansight/tasks/task.ts" };
        }
    }
}
