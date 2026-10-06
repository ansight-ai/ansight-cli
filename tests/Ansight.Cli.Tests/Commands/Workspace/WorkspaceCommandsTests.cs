using System.Text.Json.Nodes;
using Ansight.Host;
using Ansight.Infrastructure.Security;

namespace Ansight.Cli.Tests.Commands.Workspace;

public sealed class WorkspaceCommandsTests
{
    [Theory]
    [InlineData("--help")]
    [InlineData("help")]
    public async Task Help_DescribesInitializedComponents(string helpArgument)
    {
        var arguments = CliArguments.Parse(["workspace", helpArgument]);
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();

        var exitCode = await CliApplication.RunParsedAsync(
            arguments,
            new CliOutput(false, standardOutput, standardError),
            CancellationToken.None,
            accessAuthorizer: TestAccessAuthorizer.Allow,
            allowResidentHostForwarding: false);

        Assert.Equal(CliExitCodes.Success, exitCode);
        var help = standardOutput.ToString();
        Assert.Contains("What init creates:", help, StringComparison.Ordinal);
        Assert.Contains("ansight workspace list", help, StringComparison.Ordinal);
        Assert.Contains("ansight/tasks/", help, StringComparison.Ordinal);
        Assert.Contains("ansight/tests/", help, StringComparison.Ordinal);
        Assert.Contains("ansight/triggers/", help, StringComparison.Ordinal);
        Assert.Contains("ansight/sanitizers/", help, StringComparison.Ordinal);
        Assert.Contains("ansight/schema/", help, StringComparison.Ordinal);
        Assert.Contains("prompt for an App ID", help, StringComparison.Ordinal);
        Assert.Contains("--no-register", help, StringComparison.Ordinal);
        Assert.Contains("--required-secret", help, StringComparison.Ordinal);
        Assert.Equal(string.Empty, standardError.ToString());
    }

    [Fact]
    public async Task List_ShowsOnlyAppsLinkedToWorkspaces()
    {
        using var directory = TestDirectory.Create();
        var workspacePath = Path.Combine(directory.Path, "linked-workspace");
        var dataDirectory = Path.Combine(directory.Path, "host-data");
        Directory.CreateDirectory(workspacePath);
        await using var runtime = CreateRuntime(dataDirectory);
        Assert.True(runtime.Apps.Register(new AppRegistrationRequest(
            "com.example.linked",
            "Linked App",
            workspacePath)).IsSuccess);
        Assert.True(runtime.Apps.Register(new AppRegistrationRequest(
            "com.example.unlinked",
            "Unlinked App")).IsSuccess);
        using var context = CliCommandContext.Push(runtime, dataDirectory, secretValue: null);

        using var jsonOutput = new StringWriter();
        var jsonExitCode = await CliApplication.RunParsedAsync(
            CliArguments.Parse(
            [
                "workspace",
                "list",
                "--data-dir",
                dataDirectory,
                "--json"
            ]),
            new CliOutput(true, jsonOutput, TextWriter.Null),
            CancellationToken.None,
            accessAuthorizer: TestAccessAuthorizer.Allow,
            allowResidentHostForwarding: false);

        Assert.Equal(CliExitCodes.Success, jsonExitCode);
        var payload = JsonNode.Parse(jsonOutput.ToString())!.AsObject();
        Assert.Equal("ansight.workspaces/v1", payload["schema"]!.GetValue<string>());
        var workspaces = payload["workspaces"]!.AsArray();
        var workspace = Assert.Single(workspaces)!.AsObject();
        Assert.Equal("com.example.linked", workspace["appId"]!.GetValue<string>());
        Assert.Equal("Linked App", workspace["appName"]!.GetValue<string>());
        Assert.Equal(Path.GetFullPath(workspacePath), workspace["codebasePath"]!.GetValue<string>());

        using var humanOutput = new StringWriter();
        var humanExitCode = await CliApplication.RunParsedAsync(
            CliArguments.Parse(
            [
                "workspaces",
                "list",
                "--data-dir",
                dataDirectory
            ]),
            new CliOutput(false, humanOutput, TextWriter.Null),
            CancellationToken.None,
            accessAuthorizer: TestAccessAuthorizer.Allow,
            allowResidentHostForwarding: false);

        Assert.Equal(CliExitCodes.Success, humanExitCode);
        Assert.Contains("com.example.linked", humanOutput.ToString(), StringComparison.Ordinal);
        Assert.Contains("Linked App", humanOutput.ToString(), StringComparison.Ordinal);
        Assert.Contains(Path.GetFullPath(workspacePath), humanOutput.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("com.example.unlinked", humanOutput.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_WithNoLinkedWorkspaces_PrintsHelpfulMessage()
    {
        using var directory = TestDirectory.Create();
        var dataDirectory = Path.Combine(directory.Path, "host-data");
        await using var runtime = CreateRuntime(dataDirectory);
        using var context = CliCommandContext.Push(runtime, dataDirectory, secretValue: null);
        using var standardOutput = new StringWriter();

        var exitCode = await CliApplication.RunParsedAsync(
            CliArguments.Parse(
            [
                "workspace",
                "list",
                "--data-dir",
                dataDirectory
            ]),
            new CliOutput(false, standardOutput, TextWriter.Null),
            CancellationToken.None,
            accessAuthorizer: TestAccessAuthorizer.Allow,
            allowResidentHostForwarding: false);

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Equal($"No linked workspaces.{Environment.NewLine}", standardOutput.ToString());
    }

    private static RuntimeCoordinator CreateRuntime(string dataDirectory)
    {
        var storagePath = Path.Combine(dataDirectory, "secure-storage.json");
        var keyPath = FileEncryptionKeyProvider.ResolveDefaultKeyFilePath(storagePath);
        FileEncryptionKeyProvider.CreateKeyFile(keyPath);
        return new RuntimeCoordinator(new RuntimeOptions
        {
            BaseFolderPath = dataDirectory,
            SecureStorageFilePath = storagePath,
            SecureStorageKeyFilePath = keyPath
        });
    }

    [Fact]
    public async Task Initialize_InteractivePromptRegistersAppForAutomaticAnalysis()
    {
        using var directory = TestDirectory.Create();
        var workspacePath = Path.Combine(directory.Path, "workspace");
        var dataDirectory = Path.Combine(directory.Path, "host-data");
        await using var runtime = CreateRuntime(dataDirectory);
        using var context = CliCommandContext.Push(runtime, dataDirectory, null);
        var arguments = CliArguments.Parse(
        [
            "workspace",
            "init",
            workspacePath,
            "--data-dir",
            dataDirectory
        ]);
        using var standardInput = new StringReader("com.example.app\n");
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();

        var exitCode = await CliApplication.RunParsedAsync(
            arguments,
            new CliOutput(false, standardOutput, standardError),
            CancellationToken.None,
            accessAuthorizer: TestAccessAuthorizer.Allow,
            allowResidentHostForwarding: false,
            standardInput: standardInput,
            standardInputIsInteractive: true);

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Contains("App ID to register", standardError.ToString(), StringComparison.Ordinal);
        Assert.Contains("Registered app 'com.example.app'", standardOutput.ToString(), StringComparison.Ordinal);
        Assert.Contains("Automatic session analysis is enabled", standardOutput.ToString(), StringComparison.Ordinal);

        using var appOutput = new StringWriter();
        var getExitCode = await CliApplication.RunParsedAsync(
            CliArguments.Parse(
            [
                "app",
                "get",
                "com.example.app",
                "--data-dir",
                dataDirectory,
                "--json"
            ]),
            new CliOutput(true, appOutput, TextWriter.Null),
            CancellationToken.None,
            accessAuthorizer: TestAccessAuthorizer.Allow,
            allowResidentHostForwarding: false);

        Assert.Equal(CliExitCodes.Success, getExitCode);
        var appPayload = JsonNode.Parse(appOutput.ToString())!.AsObject();
        Assert.Equal(
            Path.GetFullPath(workspacePath),
            appPayload["app"]!["codebasePath"]!.GetValue<string>());
        Assert.True(appPayload["app"]!["automaticTrendsMonitoringEnabled"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Initialize_JsonRegistersExplicitAppIdWithoutPrompting()
    {
        using var directory = TestDirectory.Create();
        var dataDirectory = Path.Combine(directory.Path, "host-data");
        await using var runtime = CreateRuntime(dataDirectory);
        using var context = CliCommandContext.Push(runtime, dataDirectory, null);
        var workspacePath = Path.Combine(directory.Path, "workspace");
        var arguments = CliArguments.Parse(
        [
            "workspace",
            "init",
            workspacePath,
            "--app-id",
            "com.example.ci",
            "--name",
            "Example CI",
            "--data-dir",
            Path.Combine(directory.Path, "host-data"),
            "--json"
        ]);
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();

        var exitCode = await CliApplication.RunParsedAsync(
            arguments,
            new CliOutput(true, standardOutput, standardError),
            CancellationToken.None,
            accessAuthorizer: TestAccessAuthorizer.Allow,
            allowResidentHostForwarding: false,
            standardInput: new StringReader("ignored\n"),
            standardInputIsInteractive: true);

        Assert.Equal(CliExitCodes.Success, exitCode);
        var payload = JsonNode.Parse(standardOutput.ToString())!.AsObject();
        Assert.Equal("com.example.ci", payload["appRegistration"]!["app"]!["appId"]!.GetValue<string>());
        Assert.Equal("Example CI", payload["appRegistration"]!["app"]!["name"]!.GetValue<string>());
        Assert.Equal(string.Empty, standardError.ToString());
    }

    [Fact]
    public async Task Initialize_NoRegisterSkipsPromptAndPrintsNextStep()
    {
        using var directory = TestDirectory.Create();
        var arguments = CliArguments.Parse(
            ["workspace", "init", directory.Path, "--no-register"]);
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();

        var exitCode = await CliApplication.RunParsedAsync(
            arguments,
            new CliOutput(false, standardOutput, standardError),
            CancellationToken.None,
            accessAuthorizer: TestAccessAuthorizer.Allow,
            allowResidentHostForwarding: false,
            standardInput: new StringReader("com.example.ignored\n"),
            standardInputIsInteractive: true);

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Contains("App registration skipped", standardOutput.ToString(), StringComparison.Ordinal);
        Assert.Contains("ansight app register <app-id>", standardOutput.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, standardError.ToString());
    }

    [Fact]
    public async Task NestedHelp_DescribesWorkspaceInsteadOfRequiringArguments()
    {
        var arguments = CliArguments.Parse(["workspace", "add", "test", "--help"]);
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();

        var exitCode = await CliApplication.RunParsedAsync(
            arguments,
            new CliOutput(false, standardOutput, standardError),
            CancellationToken.None,
            accessAuthorizer: TestAccessAuthorizer.Allow,
            allowResidentHostForwarding: false);

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Contains("Test options:", standardOutput.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, standardError.ToString());
    }

    [Fact]
    public async Task AddTest_CreatesDefinitionAndReturnsVersionedJson()
    {
        using var directory = TestDirectory.Create();
        var arguments = CliArguments.Parse(
        [
            "workspace",
            "add",
            "test",
            directory.Path,
            "onboarding-smoke",
            "--app-id",
            "com.example.app",
            "--assertion",
            "The home screen is visible",
            "--json"
        ]);
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();

        var exitCode = await CliApplication.RunParsedAsync(
            arguments,
            new CliOutput(true, standardOutput, standardError),
            CancellationToken.None,
            accessAuthorizer: TestAccessAuthorizer.Allow,
            allowResidentHostForwarding: false);

        Assert.Equal(CliExitCodes.Success, exitCode);
        var payload = JsonNode.Parse(standardOutput.ToString())!.AsObject();
        Assert.Equal("ansight.workspace-authoring/v1", payload["schema"]!.GetValue<string>());
        Assert.True(payload["result"]!["isSuccess"]!.GetValue<bool>());
        Assert.True(File.Exists(Path.Combine(
            directory.Path,
            "ansight",
            "tests",
            "onboarding-smoke.yaml")));
        Assert.Equal(string.Empty, standardError.ToString());
    }

    [Fact]
    public async Task AddTest_RequiresAppId()
    {
        using var directory = TestDirectory.Create();
        var arguments = CliArguments.Parse(
            ["workspace", "add", "test", directory.Path, "onboarding-smoke", "--json"]);
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();

        var exitCode = await CliApplication.RunParsedAsync(
            arguments,
            new CliOutput(true, standardOutput, standardError),
            CancellationToken.None,
            accessAuthorizer: TestAccessAuthorizer.Allow,
            allowResidentHostForwarding: false);

        Assert.Equal(CliExitCodes.Usage, exitCode);
        Assert.Contains("--app-id", standardOutput.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, standardError.ToString());
    }

    [Fact]
    public async Task AddSanitizer_CreatesTypeScriptModule()
    {
        using var directory = TestDirectory.Create();
        var arguments = CliArguments.Parse(
            ["workspace", "add", "sanitizer", directory.Path, "team-safe"]);
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();

        var exitCode = await CliApplication.RunParsedAsync(
            arguments,
            new CliOutput(false, standardOutput, standardError),
            CancellationToken.None,
            accessAuthorizer: TestAccessAuthorizer.Allow,
            allowResidentHostForwarding: false);

        var modulePath = Path.Combine(
            directory.Path,
            "ansight",
            "sanitizers",
            "team-safe.ts");
        Assert.Equal(CliExitCodes.Success, exitCode);
        var moduleSource = File.ReadAllText(modulePath);
        Assert.Contains("sanitizeLog", moduleSource, StringComparison.Ordinal);
        Assert.Contains("sanitizeNetworkRequest", moduleSource, StringComparison.Ordinal);
        var typeDefinitionsPath = Path.Combine(
            directory.Path,
            "ansight",
            "sanitizers",
            "ansight-sanitizer.d.ts");
        Assert.True(File.Exists(typeDefinitionsPath));
        Assert.Contains("NetworkRequestItem", File.ReadAllText(typeDefinitionsPath), StringComparison.Ordinal);
        Assert.Contains("Sanitizer 'team-safe' created", standardOutput.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, standardError.ToString());
    }
}
