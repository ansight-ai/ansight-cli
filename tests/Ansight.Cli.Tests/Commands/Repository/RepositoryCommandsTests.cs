using Ansight.Infrastructure.Security;
namespace Ansight.Cli.Tests.Commands.Repository;

public sealed class RepositoryCommandsTests
{
    [Fact]
    public async Task Help_DescribesTaskExecution()
    {
        var result = await RunAsync(["repo", "help"]);

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.Contains("repo task run", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("--session-id", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("--input-file", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("--device-id", result.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(string.Empty, result.StandardError);
    }

    [Fact]
    public async Task TopLevelTaskHelp_UsesTaskOrientedSyntax()
    {
        var result = await RunAsync(["task", "help"]);

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.Contains("task run <task-id>", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("--app-id", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("--device-id", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("registered codebase", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("devices list", result.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(string.Empty, result.StandardError);
    }

    [Fact]
    public async Task TopLevelTaskList_UsesRegisteredCodebaseWhenRepositoryIsOmitted()
    {
        using var directory = TestDirectory.Create();
        var repositoryPath = Path.Combine(directory.Path, "repository");
        var dataDirectory = Path.Combine(directory.Path, "host");
        ConfigureTestCredentials(dataDirectory);
        await WriteTaskAsync(repositoryPath, "registered-task", "Registered task");

        var registration = await RunAsync(
        [
            "app",
            "register",
            "com.example.app",
            "--codebase",
            repositoryPath,
            "--data-dir",
            dataDirectory
        ]);
        var result = await RunAsync(
        [
            "task",
            "list",
            "--app-id",
            "com.example.app",
            "--data-dir",
            dataDirectory
        ]);

        Assert.Equal(CliExitCodes.Success, registration.ExitCode);
        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.Contains("registered-task\tRegistered task", result.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(string.Empty, result.StandardError);
    }

    [Fact]
    public async Task TopLevelTaskList_ExplicitRepositoryOverridesRegisteredCodebase()
    {
        using var directory = TestDirectory.Create();
        var registeredRepositoryPath = Path.Combine(directory.Path, "registered");
        var overrideRepositoryPath = Path.Combine(directory.Path, "override");
        var dataDirectory = Path.Combine(directory.Path, "host");
        ConfigureTestCredentials(dataDirectory);
        await WriteTaskAsync(registeredRepositoryPath, "registered-task", "Registered task");
        await WriteTaskAsync(overrideRepositoryPath, "override-task", "Override task");

        var registration = await RunAsync(
        [
            "app",
            "register",
            "com.example.app",
            "--codebase",
            registeredRepositoryPath,
            "--data-dir",
            dataDirectory
        ]);
        var result = await RunAsync(
        [
            "task",
            "list",
            "--app-id",
            "com.example.app",
            "--repository",
            overrideRepositoryPath,
            "--data-dir",
            dataDirectory
        ]);

        Assert.Equal(CliExitCodes.Success, registration.ExitCode);
        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.Contains("override-task\tOverride task", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("registered-task", result.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(string.Empty, result.StandardError);
    }

    [Fact]
    public async Task TopLevelTaskRun_RejectsSessionAndDeviceSelectorsTogether()
    {
        using var directory = TestDirectory.Create();
        var result = await RunAsync(
        [
            "task",
            "run",
            "onboarding",
            "--app-id",
            "com.example.app",
            "--repository",
            directory.Path,
            "--session-id",
            "session-1",
            "--device-id",
            "device-1",
            "--data-dir",
            Path.Combine(directory.Path, "host")
        ]);

        Assert.Equal(CliExitCodes.Usage, result.ExitCode);
        Assert.Contains("only one", result.StandardError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TaskRun_RequiresResidentHost()
    {
        using var directory = TestDirectory.Create();
        var result = await RunAsync(
        [
            "repo",
            "task",
            "run",
            "com.example.app",
            directory.Path,
            "onboarding",
            "--data-dir",
            Path.Combine(directory.Path, "host")
        ]);

        Assert.Equal(CliExitCodes.HostUnavailable, result.ExitCode);
        Assert.Contains("resident host", result.StandardError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TaskRun_RejectsConflictingInputSourcesBeforeExecution()
    {
        using var directory = TestDirectory.Create();
        var inputFile = Path.Combine(directory.Path, "input.json");
        await File.WriteAllTextAsync(inputFile, "{}");

        var result = await RunAsync(
        [
            "repo",
            "task",
            "run",
            "com.example.app",
            directory.Path,
            "onboarding",
            "--input",
            "{}",
            "--input-file",
            inputFile,
            "--data-dir",
            Path.Combine(directory.Path, "host")
        ]);

        Assert.Equal(CliExitCodes.Usage, result.ExitCode);
        Assert.Contains("only one", result.StandardError, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("[]", "JSON object")]
    [InlineData("{", "not valid JSON")]
    public async Task TaskRun_RequiresObjectInput(string input, string expectedError)
    {
        using var directory = TestDirectory.Create();
        var result = await RunAsync(
        [
            "repo",
            "task",
            "run",
            "com.example.app",
            directory.Path,
            "onboarding",
            "--input",
            input,
            "--data-dir",
            Path.Combine(directory.Path, "host")
        ]);

        Assert.Equal(CliExitCodes.Usage, result.ExitCode);
        Assert.Contains(expectedError, result.StandardError, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task WriteTaskAsync(string repositoryPath, string taskId, string title)
    {
        var taskDirectory = Path.Combine(repositoryPath, "ansight", "tasks");
        Directory.CreateDirectory(taskDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(taskDirectory, $"{taskId}.ts"),
            $$"""
              export const task = {
                "schemaVersion": 1,
                "title": "{{title}}",
                "description": "Task discovery fixture.",
                "inputSchema": {
                  "type": "object",
                  "properties": {},
                  "additionalProperties": false
                }
              };

              export default async function run({ expect }) {
                expect(true, { id: "ready" }).toBeTruthy();
              }
              """);
    }

    private static async Task<CommandResult> RunAsync(string[] commandArguments)
    {
        var dataIndex = Array.IndexOf(commandArguments, "--data-dir");
        if (dataIndex >= 0) ConfigureTestCredentials(commandArguments[dataIndex + 1]);
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
    private static void ConfigureTestCredentials(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        var key = Path.Combine(dataDirectory, "test-key");
        if (!File.Exists(key)) FileEncryptionKeyProvider.CreateKeyFile(key);
        new LocalSettingsStore(dataDirectory).SetCredentials(new CredentialSettings("protected-file", key, Path.Combine(dataDirectory, "test-store.json")));
    }
}
