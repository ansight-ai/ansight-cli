using System.Text.Json.Nodes;
using Ansight.Cli.Commands.Audio;
using Ansight.Host.Audio;

namespace Ansight.Cli.Tests.Commands.Audio;

public sealed class AudioCommandsTests
{
    [Fact]
    public async Task PublicCommandRequiresResidentHostWithoutStartingAnotherRuntime()
    {
        Assert.Null(CliCommandContext.Current);
        await Assert.ThrowsAsync<CliHostUnavailableException>(() => AudioCommands.RunAsync(
            CliArguments.Parse(["audio", "capabilities", "--session", "existing-session"]),
            new CliOutput(false, TextWriter.Null, TextWriter.Null),
            CancellationToken.None));
    }

    [Theory]
    [InlineData("--session")]
    [InlineData("--session-id")]
    public async Task InjectPinsExplicitSessionAndResolvesCallerFile(string sessionOption)
    {
        using var cancellation = new CancellationTokenSource();
        using var standardOutput = new StringWriter();
        var arguments = CliArguments.Parse([
            "audio", "inject", sessionOption, "session-1", "--file", "fixtures/quote.wav",
            "--timeout-ms", "42000", "--wait-for-microphone-ms", "1200", "--json"
        ]);

        var exitCode = await AudioCommands.RunAsync(arguments,
            new CliOutput(true, standardOutput, TextWriter.Null),
            (operation, supplied, token) =>
            {
                Assert.Equal(AudioOperation.Inject, operation);
                Assert.Equal("session-1", supplied["sessionId"]!.GetValue<string>());
                Assert.Equal(Path.GetFullPath("fixtures/quote.wav"), supplied["file"]!.GetValue<string>());
                Assert.Equal(42000, supplied["timeoutMs"]!.GetValue<int>());
                Assert.Equal(1200, supplied["waitForMicrophoneMs"]!.GetValue<int>());
                Assert.Equal(cancellation.Token, token);
                return Task.FromResult(Result(operation));
            }, cancellation.Token);

        Assert.Equal(CliExitCodes.Success, exitCode);
        var payload = JsonNode.Parse(standardOutput.ToString())!;
        Assert.Equal("ansight.audio-injection/v1", payload["schema"]!.GetValue<string>());
        Assert.Equal("completed", payload["status"]!.GetValue<string>());
        Assert.Null(payload["result"]);
    }

    [Fact]
    public async Task CapabilitiesOnlyInspectsSelectedSession()
    {
        var exitCode = await AudioCommands.RunAsync(
            CliArguments.Parse(["audio", "capabilities", "--session", "session-1"]),
            new CliOutput(false, TextWriter.Null, TextWriter.Null),
            (operation, supplied, _) =>
            {
                Assert.Equal(AudioOperation.Capabilities, operation);
                Assert.Single(supplied);
                Assert.Equal("session-1", supplied["sessionId"]!.GetValue<string>());
                return Task.FromResult(Result(operation));
            }, CancellationToken.None);
        Assert.Equal(CliExitCodes.Success, exitCode);
    }

    [Theory]
    [InlineData("inject", "--file", "quote.wav")]
    [InlineData("inject", "--session", "session-1")]
    [InlineData("inject", "--session", "session-1", "--file", "quote.wav", "--timeout-ms", "60001")]
    [InlineData("inject", "--session", "session-1", "--file", "quote.wav", "--timeout-ms", "99")]
    [InlineData("inject", "--session", "session-1", "--file", "quote.wav", "--wait-for-microphone-ms", "10001")]
    [InlineData("inject", "--session", "session-1", "--file", "quote.wav", "--wait-for-microphone-ms", "-1")]
    [InlineData("capabilities", "--session", "session-1", "--file", "quote.wav")]
    [InlineData("capabilities", "--session", "session-1", "--device-id", "emulator-5554")]
    [InlineData("capabilities", "--session", "session-1", "--session-id", "session-2")]
    public async Task InvalidInvocationIsRejectedBeforeHostDispatch(params string[] command)
    {
        await Assert.ThrowsAsync<CliUsageException>(() => AudioCommands.RunAsync(
            CliArguments.Parse(["audio", .. command]),
            new CliOutput(false, TextWriter.Null, TextWriter.Null),
            (_, _, _) => throw new InvalidOperationException("Host must not be called."),
            CancellationToken.None));
    }

    [Fact]
    public void ForwardingKeepsSessionAndNormalizesFileBeforeResidentHostDispatch()
    {
        var arguments = CliArguments.Parse([
            "audio", "inject", "--session", "session-1", "--file", "fixtures/quote.wav"
        ]);
        var callerDirectory = Path.Combine(Path.GetTempPath(), "ansight-audio-caller");
        Assert.True(ControlClient.ShouldForward(arguments));
        var forwarded = CliArguments.Parse(ControlClient.CreateForwardedArguments(arguments, callerDirectory));
        Assert.Equal(Path.Combine(callerDirectory, "fixtures", "quote.wav"), forwarded.GetOption("file"));
        Assert.Equal("session-1", forwarded.GetOption("session"));
        Assert.True(ControlClient.ShouldForward(CliArguments.Parse(["audio", "capabilities"])));
    }

    [Fact]
    public async Task CancellationReachesTheExecutingOperation()
    {
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var execution = AudioCommands.RunAsync(
            CliArguments.Parse(["audio", "inject", "--session", "session-1", "--file", "quote.wav"]),
            new CliOutput(false, TextWriter.Null, TextWriter.Null),
            async (_, _, token) =>
            {
                entered.SetResult();
                await Task.Delay(Timeout.Infinite, token);
                throw new InvalidOperationException("Cancellation must stop the operation.");
            }, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
    }

    [Theory]
    [InlineData("cancelled", CliExitCodes.Cancelled)]
    [InlineData("audio-route-unavailable", CliExitCodes.CapabilityUnavailable)]
    [InlineData("audio-device-missing", CliExitCodes.CapabilityUnavailable)]
    [InlineData("accessibility-permission-required", CliExitCodes.CapabilityUnavailable)]
    [InlineData("simulator-audio-route-stale", CliExitCodes.CapabilityUnavailable)]
    [InlineData("delivery-failed", CliExitCodes.Failure)]
    public async Task FailedDeliveryPreservesDiagnosticAndExitCategory(string code, int expectedExit)
    {
        using var standardOutput = new StringWriter();
        var exitCode = await AudioCommands.RunAsync(
            CliArguments.Parse(["audio", "inject", "--session", "session-1", "--file", "quote.wav"]),
            new CliOutput(true, standardOutput, TextWriter.Null),
            (operation, _, _) => Task.FromResult(new AudioOperationResult(operation, false, "Failure", new JsonObject
            {
                ["schema"] = "ansight.audio-injection/v1", ["operationId"] = "operation-1",
                ["status"] = "failed", ["code"] = code
            })), CancellationToken.None);
        Assert.Equal(expectedExit, exitCode);
        var payload = JsonNode.Parse(standardOutput.ToString())!;
        Assert.Equal("operation-1", payload["operationId"]!.GetValue<string>());
        Assert.Equal(code, payload["code"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingPermissionPrintsErrorAndFails(bool json)
    {
        const string message = "AUDIO INJECTION BLOCKED — MACOS ACCESSIBILITY PERMISSION REQUIRED.\nNO AUDIO WAS INJECTED.";
        using var output = new StringWriter();
        var exitCode = await AudioCommands.RunAsync(
            CliArguments.Parse(["audio", "inject", "--session", "session-1", "--file", "quote.wav"]),
            new CliOutput(json, output, TextWriter.Null),
            (operation, _, _) => Task.FromResult(new AudioOperationResult(operation, false, message, new JsonObject
            {
                ["status"] = "failed", ["code"] = "accessibility-permission-required",
                ["message"] = message, ["deliveryStarted"] = false
            })), CancellationToken.None);

        Assert.Equal(CliExitCodes.CapabilityUnavailable, exitCode);
        Assert.Equal(message, json
            ? JsonNode.Parse(output.ToString())!["message"]!.GetValue<string>()
            : output.ToString().Trim());
    }

    private static AudioOperationResult Result(AudioOperation operation)
        => new(operation, true, "Complete", new JsonObject
        {
            ["schema"] = operation == AudioOperation.Inject ? "ansight.audio-injection/v1" : "ansight.audio-capabilities/v1",
            ["status"] = "completed"
        });
}
