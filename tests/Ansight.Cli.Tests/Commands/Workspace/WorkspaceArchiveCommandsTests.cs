using System.Text.Json.Nodes;
using Ansight.Host;
using Ansight.Infrastructure.Security;

namespace Ansight.Cli.Tests.Commands.Workspace;

public sealed class WorkspaceArchiveCommandsTests
{
    [Fact]
    public async Task ExportByPathAndImport_RoundTripWithVersionedJsonAndOverwriteProtection()
    {
        using var directory = TestDirectory.Create();
        var source = Path.Combine(directory.Path, "source");
        var destination = Path.Combine(directory.Path, "imported");
        var zip = Path.Combine(directory.Path, "workspace.zip");
        Assert.True(new WorkspaceAuthoringService().Initialize(new(source)).IsSuccess);
        var exported = await Run("workspace", "export", source, "--output", zip, "--json");
        Assert.Equal(CliExitCodes.Success, exported.ExitCode);
        var exportPayload = JsonNode.Parse(exported.Output)!;
        Assert.Equal("ansight.workspace-archive/v1", exportPayload["schema"]!.GetValue<string>());
        Assert.Equal("export", exportPayload["action"]!.GetValue<string>());
        Assert.Equal(19, exportPayload["result"]!["fileCount"]!.GetValue<int>());
        Assert.Equal(CliExitCodes.Failure, (await Run("workspace", "export", source, "--output", zip)).ExitCode);
        Assert.Equal(CliExitCodes.Success, (await Run("workspace", "export", source, "--output", zip, "--overwrite")).ExitCode);

        var imported = await Run("workspace", "import", zip, destination, "--json");
        Assert.Equal(CliExitCodes.Success, imported.ExitCode);
        Assert.Equal("import", JsonNode.Parse(imported.Output)!["action"]!.GetValue<string>());
        Assert.True(File.Exists(Path.Combine(destination, "ansight", "package.json")));
        var refused = await Run("workspace", "import", zip, destination, "--json");
        Assert.Equal(CliExitCodes.Failure, refused.ExitCode);
        Assert.False(JsonNode.Parse(refused.Output)!["result"]!["isSuccess"]!.GetValue<bool>());
        Assert.Equal(CliExitCodes.Success, (await Run("workspaces", "import", zip, destination, "--force")).ExitCode);
    }

    [Fact]
    public async Task ExportByAppId_UsesLinkedWorkspace_AndReportsMissingRegistrations()
    {
        using var directory = TestDirectory.Create();
        var workspace = Path.Combine(directory.Path, "workspace");
        var zip = Path.Combine(directory.Path, "workspace.zip");
        var dataDirectory = Path.Combine(directory.Path, "host-data");
        var storagePath = Path.Combine(dataDirectory, "secure-storage.json");
        var keyPath = FileEncryptionKeyProvider.ResolveDefaultKeyFilePath(storagePath);
        FileEncryptionKeyProvider.CreateKeyFile(keyPath);
        await using var runtime = new RuntimeCoordinator(new RuntimeOptions
        {
            BaseFolderPath = dataDirectory,
            SecureStorageFilePath = storagePath,
            SecureStorageKeyFilePath = keyPath
        });
        Assert.True(new WorkspaceAuthoringService().Initialize(new(workspace)).IsSuccess);
        Assert.True(runtime.Apps.Register(new("com.example.linked", "Linked", workspace)).IsSuccess);
        Assert.True(runtime.Apps.Register(new("com.example.unlinked", "Unlinked")).IsSuccess);
        using var context = CliCommandContext.Push(runtime, dataDirectory, null);

        var exported = await Run("workspace", "export", "--app-id", "com.example.linked", "--output", zip, "--data-dir", dataDirectory, "--json");
        Assert.Equal(CliExitCodes.Success, exported.ExitCode);
        var payload = JsonNode.Parse(exported.Output)!;
        Assert.Equal("com.example.linked", payload["appId"]!.GetValue<string>());
        Assert.Equal(workspace, payload["result"]!["workspacePath"]!.GetValue<string>());
        Assert.False(runtime.GetStatusSnapshot().IsRunning);

        var missing = await Run("workspace", "export", "--app-id", "missing", "--output", zip, "--data-dir", dataDirectory);
        Assert.Equal(CliExitCodes.Usage, missing.ExitCode);
        Assert.Contains("was not found", missing.Error);
        var unlinked = await Run("workspace", "export", "--app-id", "com.example.unlinked", "--output", zip, "--data-dir", dataDirectory);
        Assert.Equal(CliExitCodes.Usage, unlinked.ExitCode);
        Assert.Contains("no linked workspace", unlinked.Error);
    }

    [Theory]
    [InlineData("workspace", "export", "--output", "workspace.zip")]
    [InlineData("workspace", "export", "source")]
    [InlineData("workspace", "export", "source", "--app-id", "com.example.app", "--output", "workspace.zip")]
    [InlineData("workspace", "export", "--app-id", "--output", "workspace.zip")]
    [InlineData("workspace", "export", "source", "--output", "workspace.txt")]
    [InlineData("workspace", "import", "workspace.zip")]
    [InlineData("workspace", "import", "workspace.zip", "destination", "--app-id", "com.example.app")]
    public async Task InvalidArguments_ReportUsageErrors(params string[] arguments)
    {
        var result = await Run(arguments);
        Assert.Equal(CliExitCodes.Usage, result.ExitCode);
        Assert.NotEmpty(result.Error);
    }

    [Theory]
    [InlineData("workspace", "--output", "exports/workspace.zip")]
    [InlineData("workspaces", "--output=exports/workspace.zip", null)]
    public void ExportByAppId_ForwardsToResidentHostWithAbsoluteOutput(string command, string outputOption, string? outputValue)
    {
        var callerDirectory = Path.Combine(Path.GetTempPath(), "workspace-export-caller");
        var values = new List<string> { command, "export", "--app-id", "com.example.app", outputOption };
        if (outputValue is not null) values.Add(outputValue);
        var arguments = CliArguments.Parse(values);
        Assert.True(ControlClient.ShouldForward(arguments));
        var forwarded = CliArguments.Parse(ControlClient.CreateForwardedArguments(arguments, callerDirectory));
        Assert.Equal(Path.Combine(callerDirectory, "exports", "workspace.zip"), forwarded.GetOption("output"));
        Assert.Equal("com.example.app", forwarded.GetOption("app-id"));
        Assert.False(ControlClient.ShouldForward(CliArguments.Parse(["workspace", "export", "source", "--output", "workspace.zip"])));
        Assert.False(ControlClient.ShouldForward(CliArguments.Parse(["workspace", "import", "workspace.zip", "destination"])));
    }

    private static async Task<CommandResult> Run(params string[] values)
    {
        var arguments = CliArguments.Parse(values);
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = await CliApplication.RunParsedAsync(arguments, new CliOutput(arguments.IsJson, output, error),
            CancellationToken.None, accessAuthorizer: TestAccessAuthorizer.Allow, allowResidentHostForwarding: false);
        return new(exitCode, output.ToString(), error.ToString());
    }

    private sealed record CommandResult(int ExitCode, string Output, string Error);
}
