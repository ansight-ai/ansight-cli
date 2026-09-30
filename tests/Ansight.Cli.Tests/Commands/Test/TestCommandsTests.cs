using Ansight.Host;
using Ansight.Host.Trends;
using Ansight.Host.Workspaces;

namespace Ansight.Cli.Tests.Commands.Test;

public sealed class TestCommandsTests
{
    [Fact]
    public void BatchRunIdIsTrimmedForSingleTestCorrelation()
    {
        var arguments = CliArguments.Parse(
            ["test", "run", "/workspace", "example", "--batch", " release_42 "]);

        Assert.Equal("release_42", TestCommands.ResolveBatchRunId(arguments));
    }

    [Fact]
    public void BatchRunIdRejectsMissingAndUnsafeValues()
    {
        var missing = CliArguments.Parse(["test", "run", "/workspace", "example", "--batch"]);
        var unsafeValue = CliArguments.Parse(
            ["test", "run", "/workspace", "example", "--batch", "release/42"]);

        Assert.Throws<CliUsageException>(() => TestCommands.ResolveBatchRunId(missing));
        Assert.Throws<CliUsageException>(() => TestCommands.ResolveBatchRunId(unsafeValue));
    }

    [Fact]
    public void RenderTrendsRebuildDescribesDryRunScopeAndSkippedApps()
    {
        var result = new WorkspaceTrendsHistoryRebuildResult(
            "/data/trends.sqlite",
            DryRun: true,
            AppId: null,
            AppVersion: null,
            MetricKey: null,
            RemovedHistoryResultCount: 12,
            RebuiltHistoryResultCount: 10,
            Apps:
            [
                new WorkspaceTrendsHistoryRebuildAppResult(
                    "com.example.app",
                    "/workspace",
                    MetricCount: 20,
                    HistoryDefinitionCount: 2,
                    HistoryResultCount: 10)
            ],
            SkippedApps:
            [
                new WorkspaceTrendsHistoryRebuildSkippedApp(
                    "com.example.missing",
                    "No registered codebase.")
            ]);

        var rendered = TrendsCommands.RenderRebuild(result);

        Assert.Contains("Would rebuild 10 trends comparison(s)", rendered, StringComparison.Ordinal);
        Assert.Contains("would replace 12 stored result(s)", rendered, StringComparison.Ordinal);
        Assert.Contains("APP\tcom.example.app", rendered, StringComparison.Ordinal);
        Assert.Contains("SKIPPED\tcom.example.missing", rendered, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SimulatorAgentProgressStage.Thinking)]
    [InlineData(SimulatorAgentProgressStage.ModelCompleted)]
    public void DefaultProgressSuppressesModelTelemetry(SimulatorAgentProgressStage stage)
    {
        var progress = CreateProgress(stage);

        Assert.False(TestCommands.ShouldWriteProgress(progress, isVerbose: false));
        Assert.True(TestCommands.ShouldWriteProgress(progress, isVerbose: true));
    }

    [Theory]
    [InlineData(SimulatorAgentProgressStage.Starting)]
    [InlineData(SimulatorAgentProgressStage.CallingTool)]
    [InlineData(SimulatorAgentProgressStage.ToolCompleted)]
    [InlineData(SimulatorAgentProgressStage.InstructionCompleted)]
    [InlineData(SimulatorAgentProgressStage.Completed)]
    public void DefaultProgressKeepsActionsAndOutcomes(SimulatorAgentProgressStage stage)
    {
        Assert.True(TestCommands.ShouldWriteProgress(CreateProgress(stage), isVerbose: false));
    }

    [Fact]
    public void DefaultProgressKeepsHostAndTargetLifecycleEvents()
    {
        var progress = new WorkspaceTestRunProgress("app.launch", "Launching the app.");

        Assert.True(TestCommands.ShouldWriteProgress(progress, isVerbose: false));
    }

    [Fact]
    public void RenderRunIncludesSelectedSessionId()
    {
        var result = CreateRunResult("com-example-app-042");

        var rendered = TestCommands.RenderRun(result, "/tmp/result.json", includeAudit: false);

        Assert.Contains("Session: com-example-app-042", rendered, StringComparison.Ordinal);
        Assert.StartsWith("🔴 FAILED: example — Example test", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderRunIncludesTheRequestedTestIdWhenTheTestWasNotFound()
    {
        var result = WorkspaceTestRunResult.Failure("The test was not found.");

        var rendered = TestCommands.RenderRun(
            result,
            "/tmp/result.json",
            includeAudit: false,
            requestedTestId: "open-3d-guide");

        Assert.StartsWith("🔴 FAILED: open-3d-guide", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderRunDistinguishesAnUnsupportedTaskTargetAsSkipped()
    {
        var test = CreateRunResult("session").Test!;
        var result = WorkspaceTestRunResult.Skipped("Unsupported framework.", test);

        var rendered = TestCommands.RenderRun(result, "/tmp/result.json", includeAudit: false);

        Assert.StartsWith("🟡 SKIPPED: example — Example test", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderBatchIncludesEachSelectedSessionId()
    {
        var result = new WorkspaceTestBatchResult(
            [CreateRunResult("com-example-app-042")],
            PassedCount: 0,
            FailedCount: 1,
            SkippedCount: 0,
            WasCancelled: false);

        var rendered = TestCommands.RenderBatch(result, "/tmp/result.json", includeAudit: false);

        Assert.Contains("Session: example = com-example-app-042", rendered, StringComparison.Ordinal);
        Assert.Contains("FAILED: example: Example failure.", rendered, StringComparison.Ordinal);
        Assert.EndsWith("🔴", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyBatchIsAConfigurationFailureWithRedStatus()
    {
        var result = new WorkspaceTestBatchResult(
            [],
            PassedCount: 0,
            FailedCount: 0,
            SkippedCount: 0,
            WasCancelled: false);

        var rendered = TestCommands.RenderBatch(result, "/tmp/result.json", includeAudit: false);

        Assert.Equal(CliExitCodes.Configuration, TestCommands.ResolveBatchExitCode(result));
        Assert.Contains("FAILED: No workspace tests were executed.", rendered, StringComparison.Ordinal);
        Assert.EndsWith("🔴", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void RunAllConfigurationRejectsAnEmptyCatalog()
    {
        var catalog = new WorkspaceTestCatalogResult("/workspace", [], []);

        var error = TestCommands.GetRunAllConfigurationError(catalog, []);

        Assert.Contains("No runnable workspace tests", error, StringComparison.Ordinal);
    }

    [Fact]
    public void RunAllConfigurationRejectsUnknownRequestedTests()
    {
        var catalog = new WorkspaceTestCatalogResult(
            "/workspace",
            [CreateRunResult("session").Test!],
            []);

        var error = TestCommands.GetRunAllConfigurationError(catalog, ["missing"]);

        Assert.Contains("missing", error, StringComparison.Ordinal);
    }

    [Fact]
    public void RunAllConfigurationRejectsExplicitlyRequestedDisabledTests()
    {
        var disabled = CreateRunResult("session").Test! with { Enabled = false };
        var catalog = new WorkspaceTestCatalogResult(
            "/workspace",
            [disabled, CreateRunResult("other-session").Test! with { TestId = "other" }],
            []);

        var error = TestCommands.GetRunAllConfigurationError(catalog, ["example"]);

        Assert.Contains("disabled", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("example", error, StringComparison.Ordinal);
    }

    [Fact]
    public void RunAllConfigurationRejectsACatalogContainingOnlyDisabledTests()
    {
        var disabled = CreateRunResult("session").Test! with { Enabled = false };
        var catalog = new WorkspaceTestCatalogResult("/workspace", [disabled], []);

        var error = TestCommands.GetRunAllConfigurationError(catalog, []);

        Assert.Contains("No runnable workspace tests", error, StringComparison.Ordinal);
        Assert.Contains("All discovered tests are disabled", error, StringComparison.Ordinal);
    }

    [Fact]
    public void SessionLinksIncludeAnsightAndAppiumSessions()
    {
        var result = CreateRunResult("com-example-app-042") with
        {
            AppiumSessionId = "appium-017"
        };

        var links = TestSessionLink.From(result, "example");

        Assert.Collection(
            links,
            link =>
            {
                Assert.Equal("ansight", link.Kind);
                Assert.Equal("com-example-app-042", link.SessionId);
            },
            link =>
            {
                Assert.Equal("appium", link.Kind);
                Assert.Equal("appium-017", link.SessionId);
            });
    }

    private static WorkspaceTestRunResult CreateRunResult(string sessionId)
    {
        var test = new WorkspaceTestDefinition(
            "example",
            "Example test",
            "com.example.app",
            "Run the example.",
            new WorkspaceTestValidation(string.Empty, []),
            [],
            "/tmp/example.json");
        return WorkspaceTestRunResult.Failure("Example failure.", test, sessionId);
    }

    private static WorkspaceTestRunProgress CreateProgress(SimulatorAgentProgressStage stage)
        => new(
            stage.ToString(),
            "Progress message.",
            new SimulatorAgentProgress(stage, "Progress message.", 1, 1, 1));
}
