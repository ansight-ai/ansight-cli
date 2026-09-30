namespace Ansight.Cli.Tests.Application;

public sealed class CliArgumentsTests
{
    [Fact]
    public void Parse_DiagnosticIsBooleanOption()
    {
        var arguments = CliArguments.Parse(["example", "command", "--diagnostic", "next-position"]);

        Assert.True(arguments.HasFlag("diagnostic"));
        Assert.Equal(["example", "command", "next-position"], arguments.Positionals);
    }

    [Fact]
    public void Parse_MaestroAiFlagDoesNotConsumeTheSessionId()
    {
        var arguments = CliArguments.Parse(
            ["task", "extract", "--ai", "session-1", "--format", "maestro"]);

        Assert.True(arguments.HasFlag("ai"));
        Assert.Equal(["task", "extract", "session-1"], arguments.Positionals);
        Assert.Equal("maestro", arguments.GetOption("format"));
    }

    [Fact]
    public void Parse_RecognizesVerboseAsABooleanOption()
    {
        var arguments = CliArguments.Parse(["test", "run", "workspace", "test-id", "--verbose"]);

        Assert.True(arguments.IsVerbose);
        Assert.Equal(4, arguments.Positionals.Count);
    }

    [Fact]
    public void Parse_RecognizesAuditAsABooleanOption()
    {
        var arguments = CliArguments.Parse(["test", "run", "workspace", "test-id", "--audit"]);

        Assert.True(arguments.HasFlag("audit"));
        Assert.Equal(4, arguments.Positionals.Count);
    }

    [Theory]
    [InlineData("executable-only")]
    [InlineData("include-unavailable")]
    public void Parse_RecognizesToolCatalogFlagsAsBooleanOptions(string option)
    {
        var arguments = CliArguments.Parse(
            ["app", "tools", "session-1", $"--{option}", "unexpected-position"]);

        Assert.True(arguments.HasFlag(option));
        Assert.Equal(["app", "tools", "session-1", "unexpected-position"], arguments.Positionals);
    }

    [Theory]
    [InlineData("upload-recordings")]
    [InlineData("no-upload-recordings")]
    [InlineData("upload-trends")]
    [InlineData("no-upload-trends")]
    [InlineData("upload-test-results")]
    [InlineData("no-upload-test-results")]
    [InlineData("no-headless")]
    public void Parse_RecognizesRunnerCompletionUploadFlagsAsBooleanOptions(string option)
    {
        var arguments = CliArguments.Parse(
            ["runner", "submit", $"--{option}", "unexpected-position"]);

        Assert.True(arguments.HasFlag(option));
        Assert.Equal(["runner", "submit", "unexpected-position"], arguments.Positionals);
    }

    [Theory]
    [InlineData("runner")]
    [InlineData("skip-build-preflight")]
    public void Parse_RecognizesHostRunnerFlagsAsBooleanOptions(string option)
    {
        var arguments = CliArguments.Parse(["host", "run", $"--{option}", "unexpected-position"]);

        Assert.True(arguments.HasFlag(option));
        Assert.Equal(["host", "run", "unexpected-position"], arguments.Positionals);
    }

    [Fact]
    public void Parse_RecognizesSilentAsABooleanOption()
    {
        var arguments = CliArguments.Parse(["test", "run", "workspace", "test-id", "--silent"]);

        Assert.True(arguments.IsSilent);
        Assert.Equal(4, arguments.Positionals.Count);
    }

    [Fact]
    public void Parse_TreatsDeviceAsAValueForWorkspaceTestRuns()
    {
        var arguments = CliArguments.Parse(["test", "run-all", ".", "--device", "DEVICE-1"]);

        Assert.Equal(["test", "run-all", "."], arguments.Positionals);
        Assert.Equal("DEVICE-1", arguments.GetOption("device"));
    }

    [Fact]
    public void Parse_TreatsDeviceAsAValueForInlineAgentRuns()
    {
        var arguments = CliArguments.Parse(
            ["test", "run-inline", "--session-id", "session-1", "--device", "DEVICE-1", "--instruction", "Open settings"]);

        Assert.Equal(["test", "run-inline"], arguments.Positionals);
        Assert.Equal("DEVICE-1", arguments.GetOption("device"));
        Assert.Equal("Open settings", arguments.GetOption("instruction"));
    }

    [Fact]
    public void Parse_TreatsDeviceAsAValueForAppExecutions()
    {
        var arguments = CliArguments.Parse(
            ["app", "execute", "session-1", "--device", "DEVICE-1", "--prompt", "Open settings"]);

        Assert.Equal(["app", "execute", "session-1"], arguments.Positionals);
        Assert.Equal("DEVICE-1", arguments.GetOption("device"));
        Assert.Equal("Open settings", arguments.GetOption("prompt"));
    }

    [Fact]
    public void Parse_TreatsDeviceAsAValueForReplayRuns()
    {
        var arguments = CliArguments.Parse(
            ["replay", "ansight", "capture-1", "--device", "DEVICE-1"]);

        Assert.Equal(["replay", "ansight", "capture-1"], arguments.Positionals);
        Assert.Equal("DEVICE-1", arguments.GetOption("device"));
    }

    [Fact]
    public void Parse_PreservesDeviceAsABooleanOptionForAuthentication()
    {
        var arguments = CliArguments.Parse(["auth", "login", "--device"]);

        Assert.Equal(["auth", "login"], arguments.Positionals);
        Assert.True(arguments.HasFlag("device"));
        Assert.Null(arguments.GetOption("device"));
    }

    [Theory]
    [InlineData("android")]
    [InlineData("ios")]
    [InlineData("offline")]
    [InlineData("all")]
    [InlineData("emulator")]
    [InlineData("simulator")]
    [InlineData("physical")]
    public void Parse_RecognizesDeviceListFiltersAsBooleanOptions(string option)
    {
        var arguments = CliArguments.Parse(["devices", "list", $"--{option}", "unexpected"]);

        Assert.True(arguments.HasFlag(option));
        Assert.Equal(["devices", "list", "unexpected"], arguments.Positionals);
    }

    [Fact]
    public void ParseSeparatesPositionalsFlagsAndValues()
    {
        var arguments = CliArguments.Parse(
        [
            "test",
            "run",
            "/workspace",
            "smoke",
            "--json",
            "--max-turns=12",
            "--test",
            "first",
            "--test",
            "second"
        ]);

        Assert.Equal(["test", "run", "/workspace", "smoke"], arguments.Positionals);
        Assert.True(arguments.IsJson);
        Assert.Equal("12", arguments.GetOption("max-turns"));
        Assert.Equal(["first", "second"], arguments.GetOptions("test"));
    }

    [Fact]
    public void ForwardedTestRunResolvesCallerRelativePaths()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "ansight-caller-workspace");
        var arguments = CliArguments.Parse(
        [
            "test",
            "run-all",
            "--verbose",
            ".",
            "--app",
            "products/example.apk",
            "--result-file=results/batch.json"
        ]);

        var forwarded = ControlClient.CreateForwardedArguments(arguments, workingDirectory);

        Assert.Equal(Path.GetFullPath(workingDirectory), forwarded[3]);
        Assert.Equal(
            Path.Combine(workingDirectory, "products", "example.apk"),
            forwarded[5]);
        Assert.Equal(
            $"--result-file={Path.Combine(workingDirectory, "results", "batch.json")}",
            forwarded[6]);
    }

    [Fact]
    public void ForwardedWorkspaceInitResolvesCallerRelativePath()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "ansight-workspace-init-caller");
        var arguments = CliArguments.Parse(
        [
            "workspace",
            "init",
            ".",
            "--app-id",
            "com.example.app"
        ]);

        var forwarded = ControlClient.CreateForwardedArguments(arguments, workingDirectory);

        Assert.Equal(Path.GetFullPath(workingDirectory), forwarded[2]);
        Assert.Equal("com.example.app", forwarded[4]);
    }

    [Fact]
    public void ForwardedLocationReplayResolvesCallerRelativeRoutePath()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "ansight-route-caller");
        var arguments = CliArguments.Parse(
            ["device", "location", "play", "ios", "DEVICE-1", "routes/walk.gpx"]);

        var forwarded = ControlClient.CreateForwardedArguments(arguments, workingDirectory);

        Assert.Equal(
            Path.Combine(workingDirectory, "routes", "walk.gpx"),
            forwarded[5]);
    }

    [Fact]
    public void ForwardedCloudAttachmentUploadResolvesCallerRelativeFilePath()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "ansight-cloud-caller");
        var arguments = CliArguments.Parse(
            ["cloud", "attachment", "upload", Guid.NewGuid().ToString("D"), "evidence/report.txt"]);

        var forwarded = ControlClient.CreateForwardedArguments(arguments, workingDirectory);

        Assert.Equal(
            Path.Combine(workingDirectory, "evidence", "report.txt"),
            forwarded[4]);
    }

    [Fact]
    public void ForwardedCloudTestImportResolvesCallerRelativeResultPath()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "ansight-cloud-test-caller");
        var arguments = CliArguments.Parse(
            ["cloud", "test", "import", "reports/appium.xml", "--format", "junit"]);

        var forwarded = ControlClient.CreateForwardedArguments(arguments, workingDirectory);

        Assert.Equal(
            Path.Combine(workingDirectory, "reports", "appium.xml"),
            forwarded[3]);
    }

    [Fact]
    public void ForwardedAppExecutionResolvesCallerRelativeArtifactAndPromptPaths()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "ansight-app-execution-caller");
        var arguments = CliArguments.Parse(
        [
            "app",
            "execute",
            "--app",
            "build/Example.app",
            "--prompt-file",
            "prompts/enable-reminders.txt"
        ]);

        var forwarded = ControlClient.CreateForwardedArguments(arguments, workingDirectory);

        Assert.Equal(
            Path.Combine(workingDirectory, "build", "Example.app"),
            forwarded[3]);
        Assert.Equal(
            Path.Combine(workingDirectory, "prompts", "enable-reminders.txt"),
            forwarded[5]);
    }

    [Fact]
    public void ForwardedAppToolPolicyPreservesCatalogPolicyValue()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "ansight-app-tools-caller");
        var arguments = CliArguments.Parse(
            ["app", "tools", "session-1", "--query", "mapbox", "--policy", "read"]);

        var forwarded = ControlClient.CreateForwardedArguments(arguments, workingDirectory);

        Assert.Equal("read", forwarded[6]);
    }

    [Fact]
    public void ForwardedSessionShareResolvesCallerRelativePolicyPath()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "ansight-session-share-caller");
        var arguments = CliArguments.Parse(
            ["session", "share", "session-1", "--policy", "sanitizers/policy.json"]);

        var forwarded = ControlClient.CreateForwardedArguments(arguments, workingDirectory);

        Assert.Equal(
            Path.Combine(workingDirectory, "sanitizers", "policy.json"),
            forwarded[4]);
    }

    [Fact]
    public void ForwardedUiPreservesAppIdentifier()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "ansight-ui-caller");
        var arguments = CliArguments.Parse(
            ["ui", "tap", "--app", "com.example.app", "--automation-id", "SaveButton"]);

        var forwarded = ControlClient.CreateForwardedArguments(arguments, workingDirectory);

        Assert.Equal("com.example.app", forwarded[3]);
    }

    [Fact]
    public void ForwardedKeyboardPreservesAppIdentifier()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "ansight-keyboard-caller");
        var arguments = CliArguments.Parse(
            ["keyboard", "is-open", "--app", "com.example.app"]);

        var forwarded = ControlClient.CreateForwardedArguments(arguments, workingDirectory);

        Assert.Equal("com.example.app", forwarded[3]);
    }

    [Fact]
    public void ForwardedExternalReplayResolvesCallerRelativeExportPath()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "ansight-replay-caller");
        var arguments = CliArguments.Parse(
            ["replay", "posthog", "captures/recording.json", "--plan-only"]);

        var forwarded = ControlClient.CreateForwardedArguments(arguments, workingDirectory);

        Assert.Equal(
            Path.Combine(workingDirectory, "captures", "recording.json"),
            forwarded[2]);
    }

    [Fact]
    public void ForwardedTestTraceExportResolvesCallerRelativeOutputPath()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "ansight-trace-export-caller");
        var arguments = CliArguments.Parse(
            ["test", "export", "run-123", "reports/run-123.zip"]);

        var forwarded = ControlClient.CreateForwardedArguments(arguments, workingDirectory);

        Assert.Equal(
            Path.Combine(workingDirectory, "reports", "run-123.zip"),
            forwarded[3]);
    }

    [Theory]
    [InlineData("cloud", "session", "list")]
    [InlineData("device", "location", "play")]
    [InlineData("device", "location", "status")]
    [InlineData("device", "location", "stop")]
    [InlineData("test", "run-inline", null)]
    [InlineData("test", "inspect", null)]
    [InlineData("test", "export", null)]
    [InlineData("app", "execute", null)]
    [InlineData("app-graph", "explore", null)]
    [InlineData("app-graphs", "run", null)]
    [InlineData("ui", "tap", null)]
    [InlineData("keyboard", "is-open", null)]
    [InlineData("trends", "rebuild", null)]
    [InlineData("replay", "ansight", null)]
    [InlineData("account", "grants", null)]
    [InlineData("account", "grant", null)]
    public void ShouldForward_StatefulParityCommands(
        string command,
        string action,
        string? nestedAction)
    {
        var values = nestedAction is null
            ? new[] { command, action }
            : new[] { command, action, nestedAction };

        Assert.True(ControlClient.ShouldForward(CliArguments.Parse(values)));
    }

    [Fact]
    public void ResolveOptionsEnablesRepositoryAutomationsByDefault()
    {
        var arguments = CliArguments.Parse(["host", "run", "--data-dir", "/tmp/ansight-test"]);

        var options = CliRuntime.ResolveOptions(arguments);

        Assert.True(options.EnableRepositoryAutomations);
    }

    [Fact]
    public void ResolveOptionsAllowsRepositoryAutomationsToBeDisabled()
    {
        var arguments = CliArguments.Parse(
            ["host", "run", "--data-dir", "/tmp/ansight-test", "--disable-repository-automations"]);

        var options = CliRuntime.ResolveOptions(arguments);

        Assert.False(options.EnableRepositoryAutomations);
    }

    [Fact]
    public void ResolveOptionsRejectsRemovedMcpHostingOptions()
    {
        var arguments = CliArguments.Parse(
            ["host", "run", "--data-dir", "/tmp/ansight-test", "--trust-mcp-certificate"]);

        var exception = Assert.Throws<CliUsageException>(() => CliRuntime.ResolveOptions(arguments));

        Assert.Contains("MCP hosting has been removed", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnsurePositionalCountRejectsCommandLineSecretValues()
    {
        var arguments = CliArguments.Parse(["secret", "set", "com.example.app", "TOKEN", "secret-value"]);

        var exception = Assert.Throws<CliUsageException>(() =>
            arguments.EnsurePositionalCount(
                4,
                "ansight secret set <app-id> <alias> [--stdin|--from-env <NAME>]"));

        Assert.Contains("Unexpected positional argument", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("app identifier", "ansight app list")]
    [InlineData("device identifier", "ansight device list")]
    [InlineData("session identifier", "ansight session list")]
    [InlineData("tool identifier", "ansight app tools <session-id>")]
    [InlineData("test identifier", "ansight test list <workspace-path>")]
    [InlineData("secret alias", "ansight secret list <app-id>")]
    [InlineData("invite identifier", "ansight pairing list")]
    [InlineData("capture identifier", "ansight profile dotnet list")]
    public void RequirePositionalSuggestsHowToFindRequiredIdentifiers(
        string label,
        string expectedCommand)
    {
        var arguments = CliArguments.Parse(["command"]);

        var exception = Assert.Throws<CliUsageException>(() =>
            arguments.RequirePositional(2, label));

        Assert.Contains(expectedCommand, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("host subcommand", "ansight host help")]
    [InlineData("device subcommand", "ansight device help")]
    [InlineData("location action", "ansight device help")]
    [InlineData("app action", "ansight app help")]
    [InlineData("auth action", "ansight auth help")]
    [InlineData("input action", "ansight input help")]
    [InlineData("ui action", "ansight ui help")]
    [InlineData("pairing action", "ansight pairing help")]
    [InlineData("profiling action", "ansight profile help")]
    [InlineData("profiling technology", "ansight profile help")]
    [InlineData(".NET profiling action", "ansight profile dotnet help")]
    [InlineData("repository command", "ansight repo help")]
    [InlineData("automation action", "ansight repo help")]
    [InlineData("secret action", "ansight secret help")]
    [InlineData("session action", "ansight session help")]
    [InlineData("test subcommand", "ansight test help")]
    [InlineData("workspace action", "ansight workspace help")]
    [InlineData("definition kind", "ansight workspace help")]
    public void RequirePositionalSuggestsOwningHelpCommand(
        string label,
        string expectedCommand)
    {
        var arguments = CliArguments.Parse(["command"]);

        var exception = Assert.Throws<CliUsageException>(() =>
            arguments.RequirePositional(2, label));

        Assert.Contains(expectedCommand, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RequireAppIdOptionSuggestsKnownAppInventory()
    {
        var arguments = CliArguments.Parse(["workspace", "add", "test"]);

        var exception = Assert.Throws<CliUsageException>(() => arguments.RequireOption("app-id"));

        Assert.Contains("ansight app list", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingDeviceLaunchArgumentsSuggestTargetAndInstalledAppInventories()
    {
        var arguments = CliArguments.Parse(["device", "launch"]);

        var exception = Assert.Throws<CliUsageException>(() =>
            arguments.EnsurePositionalCount(
                5,
                "ansight device launch <ios|android> <device-id> <app-id>"));

        Assert.Contains("ansight device list", exception.Message, StringComparison.Ordinal);
        Assert.Contains("ansight device apps <ios|android> <device-id>", exception.Message, StringComparison.Ordinal);
    }
}
