namespace Ansight.Cli.Tests.Commands;

public sealed class CommandHelpTests
{
    public static IEnumerable<object[]> GroupHelpCases()
    {
        yield return ["host", "ansight host run"];
        yield return ["version", "ansight version"];
        yield return ["update", "ansight update check"];
        yield return ["upgrade", "ansight update check"];
        yield return ["analytics", "ansight analytics status"];
        yield return ["config", "ansight config list"];
        yield return ["settings", "ansight config list"];
        yield return ["serve", "ansight serve"];
        yield return ["doctor", "ansight doctor"];
        yield return ["capabilities", "ansight doctor"];
        yield return ["device", "ansight device list"];
        yield return ["devices", "ansight device list"];
        yield return ["input", "ansight input tap"];
        yield return ["ui", "ansight ui snapshot"];
        yield return ["keyboard", "ansight keyboard is-open"];
        yield return ["app", "ansight app list"];
        yield return ["app-graph", "ansight app-graph list"];
        yield return ["pairing", "ansight pairing issue"];
        yield return ["enrollment", "ansight pairing issue"];
        yield return ["companion", "ansight companion access status"];
        yield return ["remote", "ansight companion access status"];
        yield return ["remote-simulator", "ansight companion access status"];
        yield return ["profile", "ansight profile dotnet --help"];
        yield return ["profiling", "ansight profile dotnet --help"];
        yield return ["repo", "ansight repo tasks"];
        yield return ["repository", "ansight repo tasks"];
        yield return ["runner", "ansight runner setup"];
        yield return ["runners", "ansight runner setup"];
        yield return ["task", "ansight task run"];
        yield return ["tasks", "ansight task run"];
        yield return ["replay", "ansight replay ansight"];
        yield return ["workspace", "ansight workspace init"];
        yield return ["workspaces", "ansight workspace init"];
        yield return ["auth", "ansight auth login"];
        yield return ["account", "ansight account open"];
        yield return ["secret", "ansight secret list"];
        yield return ["secrets", "ansight secret list"];
        yield return ["test", "ansight test list"];
        yield return ["tests", "ansight test list"];
        yield return ["trends", "ansight trends history"];
        yield return ["session", "ansight session list"];
        yield return ["sessions", "ansight session list"];
        yield return ["cloud", "ansight cloud session list"];
        yield return ["licenses", "ansight licenses list"];
        yield return ["licences", "ansight licenses list"];
        yield return ["notices", "ansight licenses list"];
        yield return ["attributions", "ansight licenses list"];
    }

    [Fact]
    public async Task TrendsHelpUsesSpanTerminology()
    {
        var result = await RunAsync(["trends", "help"]);

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.Contains("--span-group <name>", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("--window-group", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuthHelp_ListsSigninAndSignoutAliases()
    {
        var result = await RunAsync(["auth", "help"]);

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.Contains("aliases: sign-in, signin", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("aliases: sign-out, signout", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AppHelp_ListsFocusedToolCatalogDiscovery()
    {
        var result = await RunAsync(["app", "help"]);

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.Contains("--query <text>", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("--policy <policy>", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("--category <category>", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("--tool-id <id>", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("--detail <level>", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("indexed tool metadata", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionHelp_DocumentsNativeVideoAsAnExplicitOptIn()
    {
        var result = await RunAsync(["session", "help"]);

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.Contains("--video", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("native platform encoder", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TestHelpDocumentsCorrelatedBatchRuns()
    {
        var result = await RunAsync(["test", "help"]);

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.Contains("--batch <run-id>", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("correlation ID", result.StandardOutput, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(GroupHelpCases))]
    public async Task HelpCommand_ReturnsDetailedGroupHelp(
        string command,
        string expectedUsage)
    {
        var result = await RunAsync([command, "help"]);

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.Contains("Usage:", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains(expectedUsage, result.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(string.Empty, result.StandardError);
    }

    [Fact]
    public async Task RootHelp_DescribesTheCli()
    {
        var result = await RunAsync(["help"]);

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.Contains("Ansight CLI — local app inspection", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Local host and targets:", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Apps, sessions, and evidence:", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Workspace:", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Cloud:", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("host run|status|logs|stop", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("All local developer features are free and require no account", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("mcp", result.StandardOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(string.Empty, result.StandardError);
    }

    [Fact]
    public async Task RemovedMcpCommandIsNotDispatchable()
    {
        var result = await RunAsync(["mcp", "help"]);

        Assert.Equal(CliExitCodes.Usage, result.ExitCode);
        Assert.Contains("Unknown command 'mcp'", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RootHelp_WithBetaFlagListsBetaFeatures()
    {
        var result = await RunAsync(["--beta", "help"]);

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.Contains("Beta feature commands:", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("app-graph list|show|plan", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("cloud app-graph", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("replay ansight|sentry|posthog", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains(".NET profiling:", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("profile dotnet tools|start|status", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Native mobile profiling:", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("profile ios tools|start|status", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("profile android tools|start|status", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Local process sampling:", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("profile sample tools|start|status", result.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(string.Empty, result.StandardError);
    }

    [Fact]
    public async Task CloudHelp_HidesAppGraphsUnlessBetaIsRequested()
    {
        var defaultResult = await RunAsync(["cloud", "help"]);
        var betaResult = await RunAsync(["cloud", "help", "--beta"]);

        Assert.Equal(CliExitCodes.Success, defaultResult.ExitCode);
        Assert.DoesNotContain("ansight cloud app-graph", defaultResult.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(CliExitCodes.Success, betaResult.ExitCode);
        Assert.Contains("Beta feature commands:", betaResult.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("ansight cloud app-graph list", betaResult.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AppGraphHelp_IdentifiesTheFeatureAsBeta()
    {
        var result = await RunAsync(["app-graph", "help"]);

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.Contains("App Graphs (BETA)", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("BETA FEATURE", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("--launch", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("--wait-seconds", result.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(string.Empty, result.StandardError);
    }

    [Fact]
    public async Task HelpDocumentsGraphStorageAndSyncWithoutExposingInternalCredentialCommands()
    {
        var results = await Task.WhenAll(
            RunAsync(["--beta", "help"]),
            RunAsync(["app", "help"]),
            RunAsync(["test", "help"]),
            RunAsync(["replay", "help"]),
            RunAsync(["app-graph", "help"]),
            RunAsync(["secret", "help"]));
        var help = string.Join(Environment.NewLine, results.Select(static result => result.StandardOutput));

        Assert.All(results, static result => Assert.Equal(CliExitCodes.Success, result.ExitCode));
        Assert.Contains("--graph-store local|hosted", help, StringComparison.Ordinal);
        Assert.Contains("default: local, no Ansight account required", help, StringComparison.Ordinal);
        Assert.Contains("app-graph sync", help, StringComparison.Ordinal);
        Assert.DoesNotContain("secret openai", help, StringComparison.Ordinal);
        Assert.DoesNotContain("direct-key", help, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("session", "logs", "Inspect, manage, replay")]
    [InlineData("device", "location", "physical devices")]
    [InlineData("repo", "automation", "repository tasks")]
    [InlineData("companion", "machines", "remote-control companion app")]
    [InlineData("profile", "dotnet", ".NET EventPipe traces")]
    [InlineData("profile", "ios", "Instruments profiles")]
    [InlineData("profile", "android", "Perfetto profiles")]
    public async Task NestedHelpFlag_ReturnsOwningGroupHelp(
        string command,
        string subcommand,
        string expectedDescription)
    {
        var result = await RunAsync([command, subcommand, "--help"]);

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.Contains(expectedDescription, result.StandardOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(string.Empty, result.StandardError);
    }

    [Fact]
    public async Task NativeProfilingHelp_ListsArtifactInspectionCommands()
    {
        var ios = await RunAsync(["profile", "ios", "help"]);
        var android = await RunAsync(["profile", "android", "help"]);

        Assert.Equal(CliExitCodes.Success, ios.ExitCode);
        Assert.Contains("profile ios artifact <capture-id>", ios.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("profile ios overview <capture-id>", ios.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("profile ios toc <capture-id>", ios.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(CliExitCodes.Success, android.ExitCode);
        Assert.Contains("profile android artifact <capture-id>", android.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("profile android overview <capture-id>", android.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("profile android query <capture-id>", android.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("ANSIGHT_TRACE_PROCESSOR_PATH", android.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Help_DoesNotCreateOrLockTheRequestedDataDirectory()
    {
        using var directory = TestDirectory.Create();
        var dataDirectory = Path.Combine(directory.Path, "help-must-not-touch-host-state");

        var result = await RunAsync(["session", "help", "--data-dir", dataDirectory]);

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.False(Directory.Exists(dataDirectory));
        Assert.Equal(string.Empty, result.StandardError);
    }

    [Fact]
    public async Task AppGraphExplore_InvalidUsageDoesNotCreateOrLockTheRequestedDataDirectory()
    {
        using var directory = TestDirectory.Create();
        var dataDirectory = Path.Combine(directory.Path, "invalid-app-graph-must-not-touch-host-state");

        var result = await RunAsync(["app-graph", "explore", "--data-dir", dataDirectory]);

        Assert.Equal(CliExitCodes.Usage, result.ExitCode);
        Assert.Contains(
            "ansight app-graph explore [<app-id>]",
            result.StandardError,
            StringComparison.Ordinal);
        Assert.DoesNotContain("already owned", result.StandardError, StringComparison.Ordinal);
        Assert.False(Directory.Exists(dataDirectory));
        Assert.Equal(string.Empty, result.StandardOutput);
    }

    [Fact]
    public async Task ProfileHelp_ListsEveryCaptureAndAnalysisCommand()
    {
        var result = await RunAsync(["profile", "dotnet", "help"]);

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.Contains(".NET EventPipe traces (BETA)", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("BETA FEATURE", result.StandardOutput, StringComparison.Ordinal);
        foreach (var command in new[]
                 {
                     "tools",
                     "start",
                     "status",
                     "cancel",
                     "list",
                     "manifest",
                     "import",
                     "speedscope",
                     "overview",
                     "startup",
                     "cpu",
                     "call-tree",
                     "threads",
                     "gc",
                     "jit",
                     "exceptions"
                 })
        {
            Assert.Contains(
                $"ansight profile dotnet {command}",
                result.StandardOutput,
                StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("ios", "launch,cpu,memory,leaks")]
    [InlineData("android", "system,launch,memory,native-heap,managed-heap")]
    public async Task NativeProfileHelp_ListsBetaCaptureCommandsAndPresets(
        string platform,
        string presets)
    {
        var result = await RunAsync(["profile", platform, "help"]);

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.Contains("BETA FEATURE", result.StandardOutput, StringComparison.Ordinal);
        foreach (var command in new[] { "tools", "start", "status", "cancel", "list", "manifest" })
        {
            Assert.Contains(
                $"ansight profile {platform} {command}",
                result.StandardOutput,
                StringComparison.Ordinal);
        }

        foreach (var preset in presets.Split(','))
        {
            Assert.Contains(preset, result.StandardOutput, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ProcessSampleHelp_ListsLifecycleAndArtifactCommands()
    {
        var result = await RunAsync(["profile", "sample", "help"]);

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.Contains("BETA FEATURE", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("/usr/bin/sample", result.StandardOutput, StringComparison.Ordinal);
        foreach (var command in new[]
                 {
                     "tools", "start", "status", "cancel", "list", "manifest", "artifact"
                 })
        {
            Assert.Contains(
                $"ansight profile sample {command}",
                result.StandardOutput,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ProfileCommand_RejectsUnscopedProfilingAction()
    {
        var result = await RunAsync(["profile", "list"]);

        Assert.Equal(CliExitCodes.Usage, result.ExitCode);
        Assert.Contains("Unknown profiling technology 'list'", result.StandardError, StringComparison.Ordinal);
        Assert.Contains(
            "Available technologies: ios, android, dotnet, sample",
            result.StandardError,
            StringComparison.Ordinal);
        Assert.Equal(string.Empty, result.StandardOutput);
    }

    [Fact]
    public async Task ProfileCommand_RejectsUnknownTechnologyHelpRequest()
    {
        var result = await RunAsync(["profile", "react", "--help"]);

        Assert.Equal(CliExitCodes.Usage, result.ExitCode);
        Assert.Contains("Unknown profiling technology 'react'", result.StandardError, StringComparison.Ordinal);
        Assert.Equal(string.Empty, result.StandardOutput);
    }

    private static async Task<CommandResult> RunAsync(string[] commandArguments)
    {
        var arguments = CliArguments.Parse(commandArguments);
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();
        var exitCode = await CliApplication.RunParsedAsync(
            arguments,
            new CliOutput(false, standardOutput, standardError),
            CancellationToken.None,
            accessAuthorizer: TestAccessAuthorizer.Allow,
            allowResidentHostForwarding: false);
        return new CommandResult(
            exitCode,
            standardOutput.ToString(),
            standardError.ToString());
    }
}
