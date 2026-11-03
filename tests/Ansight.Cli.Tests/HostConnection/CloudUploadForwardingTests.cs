using System.IO.Pipes;
using System.Text.Json;
using Ansight.Host.Workspaces.Execution;

namespace Ansight.Cli.Tests.HostConnection;

public sealed class CloudUploadForwardingTests
{
    [Fact]
    public void ForwardingResolvesCallerPathsAndPreservesExplicitOrganisation()
    {
        using var directory = TestDirectory.Create();
        var explicitTeam = Guid.NewGuid().ToString();
        using var runnerScope = RunnerAiCredentialContext.Push(new RunnerAiCredentials("caller-key", Guid.NewGuid().ToString()));
        var arguments = CliArguments.Parse(
            ["cloud", "test", "import", "results.xml", "--data-dir", "data", "--secret-store-file", "secrets/store.json",
                "--secret-key-file=secrets/key", "--team-id", explicitTeam]);
        var normalized = ControlClient.CreateForwardedArguments(arguments, directory.Path);
        var options = CliRuntime.ResolveOptions(CliArguments.Parse(normalized));
        var forwarded = CliArguments.Parse(ControlClient.CreateUploadForwardedArguments(normalized, options));

        Assert.Equal(Path.Combine(directory.Path, "results.xml"), forwarded.Positionals[3]);
        Assert.Equal(Path.Combine(directory.Path, "data"), forwarded.GetOption("data-dir"));
        Assert.Equal(Path.Combine(directory.Path, "secrets", "store.json"), forwarded.GetOption("secret-store-file"));
        Assert.Equal(Path.Combine(directory.Path, "secrets", "key"), forwarded.GetOption("secret-key-file"));
        Assert.Equal(explicitTeam, forwarded.GetOption("team-id"));
        Assert.DoesNotContain("caller-key", forwarded.OriginalArguments);
    }

    [Fact]
    public void ForwardingIncludesResolvedCredentialFilesAndScopedRunnerOrganisation()
    {
        using var directory = TestDirectory.Create();
        var team = Guid.NewGuid().ToString();
        using var runnerScope = RunnerAiCredentialContext.Push(new RunnerAiCredentials("caller-key", team));
        var arguments = CliArguments.Parse(["cloud", "test", "upload", "run", "--data-dir", directory.Path]);
        var options = CliRuntime.ResolveOptions(arguments) with
        {
            SecureStorageFilePath = Path.Combine(directory.Path, "caller-store.json"),
            SecureStorageKeyFilePath = Path.Combine(directory.Path, "caller-key")
        };

        var forwarded = CliArguments.Parse(ControlClient.CreateUploadForwardedArguments(arguments.OriginalArguments.ToArray(), options));

        Assert.Equal(options.SecureStorageFilePath, forwarded.GetOption("secret-store-file"));
        Assert.Equal(options.SecureStorageKeyFilePath, forwarded.GetOption("secret-key-file"));
        Assert.Equal(team, forwarded.GetOption("team-id"));
    }

    [Fact(Timeout = 15000)]
    public async Task OlderHostRejectionDoesNotRetryUploadWithASecondRuntime()
    {
        using var directory = TestDirectory.Create();
        using var dataLock = CliDataDirectoryLock.Acquire(directory.Path);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pipeName = "au-" + Guid.NewGuid().ToString("N");
        await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await CliRuntime.WriteRecoveryMetadataAsync(directory.Path, null, null, timeout.Token, pipeName);
        var response = ReplyAsOlderHostAsync(pipe, timeout.Token);
        using var output = new StringWriter();

        var code = await CliApplication.RunParsedAsync(
            CliArguments.Parse(["session", "share", "capture", "--data-dir", directory.Path, "--json"]),
            new CliOutput(true, output, output), timeout.Token,
            allowResidentHostForwarding: true, accessAuthorizer: TestAccessAuthorizer.Allow);
        await response;

        Assert.Equal(CliExitCodes.HostUnavailable, code);
        Assert.Contains("Restart it once", output.ToString());
        Assert.DoesNotContain("ansight.lock", output.ToString());
        Assert.Throws<CliHostUnavailableException>(() => CliDataDirectoryLock.Acquire(directory.Path));
    }

    private static async Task ReplyAsOlderHostAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        await pipe.WaitForConnectionAsync(cancellationToken);
        using var reader = new StreamReader(pipe, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, leaveOpen: true);
        var request = JsonSerializer.Deserialize<ControlRequest>((await reader.ReadLineAsync(cancellationToken))!,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(ControlProtocol.AccountUploadRequestSchema, request!.Schema);
        Assert.Null(request.RunnerAiCredentials);
        Assert.Equal("test-user", request.CallerUserId);
        var response = new ControlResponse(ControlProtocol.ResponseSchema, CliExitCodes.Failure,
            "", "The CLI control request has an unsupported schema.");
        await writer.WriteLineAsync(JsonSerializer.Serialize(response,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)).AsMemory(), cancellationToken);
        await writer.FlushAsync(cancellationToken);
    }
}
