using Ansight.Cli.Commands;
using Ansight.Host;
using Ansight.Host.Workspaces;

namespace Ansight.Cli.Tests.Commands.App;

public sealed class AppExecutionServiceTests
{
    [Fact]
    public void FormatProgress_ShowsTaskInventoryScoresAndExclusionReasons()
    {
        var formatted = SimulatorAgentProgressFormatter.Format(new SimulatorAgentProgress(
            SimulatorAgentProgressStage.TaskDiscovery,
            "Searched repository tasks: Open weather",
            1, 1, 0)
        {
            TaskDiscovery = new SimulatorAgentRepositoryTaskDiscoveryTrace("query", "Open weather",
            [
                new("load-weather", 90.5, 0.8, ["preload-candidate"]),
                new("open-guide", 70, 0.6, ["task-intent-mismatch"]),
                new("delete-account", null, null, ["no-match"])
            ])
            {
                AvailableTaskCount = 3,
                AvailableTaskIds = ["load-weather", "open-guide", "delete-account"]
            }
        });

        Assert.Contains("Searched repository tasks: Open weather", formatted);
        Assert.Contains("Available tasks (3): load-weather, open-guide, delete-account", formatted);
        Assert.Contains("load-weather: score=90.5, coverage=0.8; preload-candidate", formatted);
        Assert.Contains("open-guide: score=70, coverage=0.6; task-intent-mismatch", formatted);
        Assert.Contains("delete-account: score=unavailable, coverage=unavailable; no-match", formatted);
    }

    [Theory]
    [InlineData(1, 1, "[1/1] [Starting] Starting.")]
    [InlineData(2, 3, "[2/3] [ToolCompleted] Completed.")]
    public void FormatProgress_PreservesOneBasedInstructionIndex(
        int instructionIndex,
        int instructionCount,
        string expected)
    {
        var stage = instructionIndex == 1
            ? SimulatorAgentProgressStage.Starting
            : SimulatorAgentProgressStage.ToolCompleted;
        var message = instructionIndex == 1 ? "Starting." : "Completed.";

        var formatted = SimulatorAgentProgressFormatter.Format(new SimulatorAgentProgress(
            stage,
            message,
            instructionIndex,
            instructionCount,
            Turn: 0));

        Assert.Equal(expected, formatted);
    }

    [Fact]
    public async Task ReadPromptAsync_ReturnsTrimmedInlinePrompt()
    {
        var arguments = CliArguments.Parse(
            ["app", "execute", "session-1", "--prompt", "  Open settings and enable reminders.  "]);

        var prompt = await AppExecutionService.ReadPromptAsync(arguments, CancellationToken.None);

        Assert.Equal("Open settings and enable reminders.", prompt);
    }

    [Fact]
    public async Task ReadPromptAsync_ReadsTheCompletePromptFile()
    {
        using var directory = TestDirectory.Create();
        var promptPath = Path.Combine(directory.Path, "execution-prompt.txt");
        await File.WriteAllTextAsync(
            promptPath,
            "  Open settings.\nEnable reminders.\nVerify reminders are enabled.  ");
        var arguments = CliArguments.Parse(
            ["app", "execute", "session-1", "--prompt-file", promptPath]);

        var prompt = await AppExecutionService.ReadPromptAsync(arguments, CancellationToken.None);

        Assert.Equal(
            "Open settings.\nEnable reminders.\nVerify reminders are enabled.",
            prompt);
    }

    [Fact]
    public async Task ReadPromptAsync_RejectsInlineAndFilePromptsTogether()
    {
        var arguments = CliArguments.Parse(
        [
            "app",
            "execute",
            "session-1",
            "--prompt",
            "Open settings.",
            "--prompt-file",
            "prompt.txt"
        ]);

        var exception = await Assert.ThrowsAsync<CliUsageException>(() =>
            AppExecutionService.ReadPromptAsync(arguments, CancellationToken.None));

        Assert.Equal("Use only one of --prompt or --prompt-file.", exception.Message);
    }

    [Fact]
    public void ResolveTarget_PreservesConnectedSessionExecution()
    {
        var arguments = CliArguments.Parse(
            ["app", "execute", "session-1", "--device-id", "DEVICE-1", "--prompt", "Open settings"]);

        var target = AppExecutionService.ResolveTarget(arguments);

        Assert.False(target.RequiresLaunch);
        Assert.Equal("session-1", target.SessionId);
        Assert.Null(target.ApplicationIdentifier);
        Assert.Null(target.LaunchRequest);
    }

    [Fact]
    public void ResolveTarget_AcceptsAnApplicationArtifact()
    {
        var arguments = CliArguments.Parse(
            ["app", "execute", "--app", "/build/Example.apk", "--prompt", "Open settings"]);

        var target = AppExecutionService.ResolveTarget(arguments);

        Assert.True(target.RequiresLaunch);
        Assert.Null(target.SessionId);
        Assert.Null(target.ApplicationIdentifier);
        Assert.Equal("/build/Example.apk", target.LaunchRequest?.ApplicationPath);
    }

    [Fact]
    public void ResolveTarget_AcceptsADeviceAndInstalledApplicationIdentifier()
    {
        var arguments = CliArguments.Parse(
        [
            "app",
            "execute",
            "--device-id",
            "DEVICE-1",
            "--app-id",
            "com.example.app",
            "--prompt",
            "Open settings"
        ]);

        var target = AppExecutionService.ResolveTarget(arguments);

        Assert.True(target.RequiresLaunch);
        Assert.Equal("com.example.app", target.ApplicationIdentifier);
        Assert.Equal("DEVICE-1", target.LaunchRequest?.DeviceIdentifier);
    }

    [Fact]
    public void ResolveTargets_ExpandsRepeatedDeviceIdentifiers()
    {
        var arguments = CliArguments.Parse(
        [
            "app",
            "execute",
            "--device-id",
            "DEVICE-1",
            "--device-id",
            "DEVICE-2",
            "--app-id",
            "com.example.app",
            "--platform",
            "ios",
            "--prompt",
            "Open settings"
        ]);

        var targets = AppExecutionService.ResolveTargets(arguments);

        Assert.Collection(
            targets,
            target =>
            {
                Assert.Equal("com.example.app", target.ApplicationIdentifier);
                Assert.Equal("DEVICE-1", target.LaunchRequest?.DeviceIdentifier);
                Assert.Equal("ios", target.LaunchRequest?.Platform);
            },
            target =>
            {
                Assert.Equal("com.example.app", target.ApplicationIdentifier);
                Assert.Equal("DEVICE-2", target.LaunchRequest?.DeviceIdentifier);
                Assert.Equal("ios", target.LaunchRequest?.Platform);
            });
    }

    [Fact]
    public async Task AppExecutionRunStartBarrier_ReleasesOnlyAfterEveryTargetArrives()
    {
        var barrier = new AppExecutionRunStartBarrier(2);
        using var firstParticipant = barrier.CreateParticipant();
        using var secondParticipant = barrier.CreateParticipant();

        var firstWait = firstParticipant.ArriveAndWaitAsync(CancellationToken.None);

        Assert.False(firstWait.IsCompleted);

        await secondParticipant.ArriveAndWaitAsync(CancellationToken.None);
        await firstWait;
    }

    [Fact]
    public void ResolveTarget_RequiresAnArtifactOrApplicationIdentifierWithoutASession()
    {
        var arguments = CliArguments.Parse(
            ["app", "execute", "--device-id", "DEVICE-1", "--prompt", "Open settings"]);

        var exception = Assert.Throws<CliUsageException>(() =>
            AppExecutionService.ResolveTarget(arguments));

        Assert.Contains("--app <path-to-.app-or-.apk>", exception.Message, StringComparison.Ordinal);
        Assert.Contains("--app-id <id>", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveTarget_RejectsLaunchOptionsWithASession()
    {
        var arguments = CliArguments.Parse(
        [
            "app",
            "execute",
            "session-1",
            "--app",
            "/build/Example.app",
            "--prompt",
            "Open settings"
        ]);

        var exception = Assert.Throws<CliUsageException>(() =>
            AppExecutionService.ResolveTarget(arguments));

        Assert.Contains("cannot be combined with a session ID", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ArtifactIdentifierResolver_ReadsAnAppleBundleIdentifier()
    {
        using var directory = TestDirectory.Create();
        var appPath = Path.Combine(directory.Path, "Example.app");
        Directory.CreateDirectory(appPath);
        await File.WriteAllTextAsync(
            Path.Combine(appPath, "Info.plist"),
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <plist version="1.0">
              <dict>
                <key>CFBundleIdentifier</key>
                <string>com.example.apple</string>
              </dict>
            </plist>
            """);
        var resolver = new AppExecutionArtifactIdentifierResolver();

        var result = await resolver.ResolveAsync(appPath, configuredAdbPath: null);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal("com.example.apple", result.ApplicationIdentifier);
    }

    [Fact]
    public async Task ArtifactIdentifierResolver_ReadsAnAndroidPackageIdentifier()
    {
        using var directory = TestDirectory.Create();
        var apkPath = Path.Combine(directory.Path, "Example.apk");
        await File.WriteAllBytesAsync(apkPath, []);
        var runner = new RecordingArtifactToolRunner(
            new AppExecutionArtifactToolResult(
                0,
                "package: name='com.example.android' versionCode='1' versionName='1.0'",
                string.Empty));
        var resolver = new AppExecutionArtifactIdentifierResolver(runner, "/fake/aapt");

        var result = await resolver.ResolveAsync(apkPath, configuredAdbPath: null);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal("com.example.android", result.ApplicationIdentifier);
        Assert.Equal("/fake/aapt", runner.ExecutablePath);
        Assert.Equal(["dump", "badging", apkPath], runner.Arguments);
    }

    [Fact]
    public async Task AppExecuteWithoutPrompt_ReturnsUsageBeforeStartingTheHost()
    {
        var arguments = CliArguments.Parse(["app", "execute", "session-1"]);
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();

        var exitCode = await CliApplication.RunParsedAsync(
            arguments,
            new CliOutput(false, standardOutput, standardError),
            CancellationToken.None,
            accessAuthorizer: TestAccessAuthorizer.Allow,
            allowResidentHostForwarding: false);

        Assert.Equal(CliExitCodes.Usage, exitCode);
        Assert.Equal(string.Empty, standardOutput.ToString());
        Assert.Contains("--prompt", standardError.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AppHelp_DescribesPromptDrivenExecution()
    {
        var arguments = CliArguments.Parse(["app", "help"]);
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();

        var exitCode = await CliApplication.RunParsedAsync(
            arguments,
            new CliOutput(false, standardOutput, standardError),
            CancellationToken.None,
            accessAuthorizer: TestAccessAuthorizer.Allow,
            allowResidentHostForwarding: false);

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Contains(
            "ansight app execute <session-id> --prompt <text>",
            standardOutput.ToString(),
            StringComparison.Ordinal);
        Assert.Contains(
            "ansight app execute --app <path-to-.app-or-.apk>",
            standardOutput.ToString(),
            StringComparison.Ordinal);
        Assert.Contains(
            "ansight app execute --device-id <id> --app-id <id>",
            standardOutput.ToString(),
            StringComparison.Ordinal);
        Assert.Contains("Interpret a prompt", standardOutput.ToString(), StringComparison.Ordinal);
        Assert.Contains("--team-id <uuid>", standardOutput.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, standardError.ToString());
    }

    [Fact]
    public async Task PrepareAppExecutionRunAsyncRejectsMissingCloudGateway()
    {
        var result = await AppExecutionService.PrepareAppExecutionRunAsync(
            gateway: null,
            "com.example.app",
            "gpt-5.6-terra",
            "/workspace/app-executions",
            teamId: null,
            instructionCount: 1,
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("cloud gateway", result.Message);
        Assert.False(result.UsesExternalTransport);
    }

    private static SimulatorAgentRunResult CreateRunResult()
    {
        var startedUtc = DateTimeOffset.Parse("2026-09-02T07:23:38Z");
        var tokens = new SimulatorAgentTokenUsage(
            InputTokens: 8031,
            OutputTokens: 175,
            TotalTokens: 8206,
            CachedInputTokens: 7710,
            CacheWriteInputTokens: 318,
            ReasoningOutputTokens: 97);
        var instruction = new SimulatorAgentInstructionResult(
            1,
            "Open account.",
            SimulatorAgentInstructionStatus.Succeeded,
            "Account is open.",
            Turns: 1,
            ToolCalls: 0);
        var modelPass = new SimulatorAgentModelPassAudit(
            Sequence: 1,
            InstructionIndex: 1,
            InstructionTurn: 1,
            StartedUtc: startedUtc,
            DurationMilliseconds: 125,
            Succeeded: true,
            ResponseId: "resp_luna_1",
            ResponseModel: "gpt-5.6-luna",
            AssistantText: "",
            FunctionCallCount: 0,
            Tokens: tokens,
            ErrorMessage: null)
        {
            ResponseServiceTier = "default"
        };
        var audit = new SimulatorAgentRunAudit(
            SchemaVersion: 14,
            RunId: Guid.NewGuid().ToString("N"),
            SessionId: "session-1",
            Model: "gpt-5.6-luna",
            Status: SimulatorAgentRunStatus.Succeeded,
            Message: "Completed 1 instruction.",
            StartedUtc: startedUtc,
            CompletedUtc: startedUtc.AddSeconds(1),
            DurationMilliseconds: 1000,
            MaximumTurnsPerInstruction: 64,
            MaximumToolCalls: 512,
            InstructionCount: 1,
            PassedInstructionCount: 1,
            FailedInstructionCount: 0,
            CancelledInstructionCount: 0,
            ModelPassCount: 1,
            FailedModelPassCount: 0,
            FunctionCallCount: 0,
            AnsightToolCallCount: 0,
            SuccessfulAnsightToolCallCount: 0,
            FailedAnsightToolCallCount: 0,
            Tokens: tokens,
            RequestedInstructions: [instruction.Instruction],
            Instructions: [instruction],
            ModelPasses: [modelPass],
            ToolCalls: []);
        return new SimulatorAgentRunResult(
            audit.Status,
            audit.Message,
            audit.Instructions,
            TotalTurns: 1,
            TotalToolCalls: 0,
            InputTokens: tokens.InputTokens,
            OutputTokens: tokens.OutputTokens,
            Duration: TimeSpan.FromSeconds(1),
            Audit: audit,
            AuditFilePath: "/history/original-audit.json",
            AuditPersistenceError: null);
    }

    private sealed class RecordingArtifactToolRunner : IAppExecutionArtifactToolRunner
    {
        private readonly AppExecutionArtifactToolResult result;

        public RecordingArtifactToolRunner(AppExecutionArtifactToolResult result)
        {
            this.result = result;
        }

        public string? ExecutablePath { get; private set; }

        public IReadOnlyList<string> Arguments { get; private set; } = [];

        public Task<AppExecutionArtifactToolResult> RunAsync(
            string executablePath,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExecutablePath = executablePath;
            Arguments = arguments.ToArray();
            return Task.FromResult(result);
        }
    }

    private sealed class RecordingWorkspaceTestRunGateway : IWorkspaceTestRunGateway
    {
        private readonly WorkspaceTestRunPreparation preparation;
        private readonly WorkspaceTestRunMeterResult completionResult;

        public RecordingWorkspaceTestRunGateway(
            WorkspaceTestRunPreparation preparation,
            WorkspaceTestRunMeterResult? completionResult = null)
        {
            this.preparation = preparation;
            this.completionResult = completionResult ?? WorkspaceTestRunMeterResult.Success();
        }

        public WorkspaceTestRunPreparationRequest? PreparationRequest { get; private set; }

        public WorkspaceTestRunMeterCompletion? Completion { get; private set; }

        public Task<WorkspaceTestRunPreparation> PrepareAsync(
            WorkspaceTestRunPreparationRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PreparationRequest = request;
            return Task.FromResult(preparation);
        }

        public Task<WorkspaceTestRunMeterResult> CompleteAsync(
            WorkspaceTestRunMeterCompletion completion,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Completion = completion;
            return Task.FromResult(completionResult);
        }
    }
}
