using System.Text.Json.Nodes;
using Ansight.Host;

namespace Ansight.Cli.Tests.Commands.Ui;

public sealed class UiCommandsTests
{
    public static IEnumerable<object[]> UiRoutes()
    {
        yield return [new[] { "ui", "snapshot" }, UiAutomationOperation.Snapshot];
        yield return [new[] { "ui", "find", "--text", "Save" }, UiAutomationOperation.Find];
        yield return [new[] { "ui", "tap", "--text", "Save" }, UiAutomationOperation.Tap];
        yield return [new[] { "ui", "type", "--role", "textbox", "--value", "hello" }, UiAutomationOperation.Type];
        yield return [new[] { "ui", "swipe", "--direction", "up" }, UiAutomationOperation.Swipe];
        yield return [new[] { "ui", "pinch", "--scale", "1.5" }, UiAutomationOperation.Pinch];
        yield return [new[] { "ui", "back" }, UiAutomationOperation.Back];
        yield return [new[] { "ui", "wait", "--condition", "stable" }, UiAutomationOperation.Wait];
        yield return [new[] { "ui", "assert", "--automation-id", "SaveButton" }, UiAutomationOperation.Assert];
    }

    [Theory]
    [MemberData(nameof(UiRoutes))]
    public async Task EveryUiVerbRoutesToItsHostOperation(
        string[] command,
        UiAutomationOperation expectedOperation)
    {
        UiAutomationOperation? observedOperation = null;

        var exitCode = await UiCommands.RunAsync(
            CliArguments.Parse(command),
            new CliOutput(false, TextWriter.Null, TextWriter.Null),
            (operation, _, _) =>
            {
                observedOperation = operation;
                return Task.FromResult(Success(operation));
            },
            CancellationToken.None);

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Equal(expectedOperation, observedOperation);
    }

    [Fact]
    public async Task TapBuildsCompoundSemanticSelector()
    {
        UiAutomationOperation? observedOperation = null;
        JsonObject? observedArguments = null;

        var exitCode = await UiCommands.RunAsync(
            CliArguments.Parse([
                "ui", "tap",
                "--session", "session-001",
                "--automation-id", "ResultCard",
                "--text", "Kalymnos",
                "--role", "button",
                "--ancestor", "ResultsList",
                "--index", "2",
                "--contains",
                "--case-sensitive"
            ]),
            new CliOutput(false, TextWriter.Null, TextWriter.Null),
            (operation, arguments, _) =>
            {
                observedOperation = operation;
                observedArguments = arguments;
                return Task.FromResult(Success(operation));
            },
            CancellationToken.None);

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Equal(UiAutomationOperation.Tap, observedOperation);
        Assert.Equal("session-001", observedArguments!["sessionId"]!.GetValue<string>());
        Assert.Equal("ResultCard", observedArguments["automationId"]!.GetValue<string>());
        Assert.Equal("Kalymnos", observedArguments["text"]!.GetValue<string>());
        Assert.Equal("button", observedArguments["role"]!.GetValue<string>());
        Assert.Equal("ResultsList", observedArguments["ancestorAutomationId"]!.GetValue<string>());
        Assert.Equal(2, observedArguments["index"]!.GetValue<int>());
        Assert.False(observedArguments["exact"]!.GetValue<bool>());
        Assert.True(observedArguments["caseSensitive"]!.GetValue<bool>());
        Assert.False(observedArguments["includeScreenshot"]!.GetValue<bool>());
    }

    [Fact]
    public async Task TypeRequiresASelectorBeforeCallingTheHost()
    {
        var invoked = false;
        using var standardError = new StringWriter();

        var exception = await Assert.ThrowsAsync<CliUsageException>(() => UiCommands.RunAsync(
            CliArguments.Parse(["ui", "type", "--value", "hello"]),
            new CliOutput(false, TextWriter.Null, standardError),
            (operation, arguments, _) =>
            {
                invoked = true;
                return Task.FromResult(Success(operation));
            },
            CancellationToken.None));

        Assert.False(invoked);
        Assert.Contains("requires at least one selector", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WaitStableMayObserveTheWholeTreeWithoutASelector()
    {
        JsonObject? observedArguments = null;

        var exitCode = await UiCommands.RunAsync(
            CliArguments.Parse([
                "ui", "wait",
                "--condition", "stable",
                "--timeout-ms", "12000",
                "--stable-samples", "4"
            ]),
            new CliOutput(false, TextWriter.Null, TextWriter.Null),
            (operation, arguments, _) =>
            {
                observedArguments = arguments;
                return Task.FromResult(Success(operation));
            },
            CancellationToken.None);

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Equal("stable", observedArguments!["condition"]!.GetValue<string>());
        Assert.Equal(12000, observedArguments["timeoutMs"]!.GetValue<int>());
        Assert.Equal(4, observedArguments["stableSamples"]!.GetValue<int>());
    }

    [Fact]
    public async Task FailedHostAssertionReturnsFailureExitCodeAndStructuredOutput()
    {
        using var standardOutput = new StringWriter();

        var exitCode = await UiCommands.RunAsync(
            CliArguments.Parse([
                "ui", "assert",
                "--automation-id", "SaveButton",
                "--expected-enabled", "true",
                "--json"
            ]),
            new CliOutput(true, standardOutput, TextWriter.Null),
            (operation, _, _) => Task.FromResult(new UiAutomationResult(
                operation,
                false,
                "Expected enabled=true, but found false.",
                new JsonObject
                {
                    ["passed"] = false,
                    ["failures"] = new JsonArray("Expected enabled=true, but found false.")
                })),
            CancellationToken.None);

        Assert.Equal(CliExitCodes.Failure, exitCode);
        var json = JsonNode.Parse(standardOutput.ToString())!.AsObject();
        Assert.Equal("ansight.ui-operation/v1", json["schema"]!.GetValue<string>());
        Assert.False(json["succeeded"]!.GetValue<bool>());
        Assert.False(json["result"]!["passed"]!.GetValue<bool>());
    }

    [Fact]
    public async Task UnknownOperationOptionIsRejectedBeforeCallingTheHost()
    {
        var invoked = false;

        var exception = await Assert.ThrowsAsync<CliUsageException>(() => UiCommands.RunAsync(
            CliArguments.Parse([
                "ui", "swipe",
                "--automation-id", "ResultsList",
                "--directon", "down"
            ]),
            new CliOutput(false, TextWriter.Null, TextWriter.Null),
            (operation, arguments, _) =>
            {
                invoked = true;
                return Task.FromResult(Success(operation));
            },
            CancellationToken.None));

        Assert.False(invoked);
        Assert.Contains("--directon", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConflictingSessionAndAppTargetsAreRejected()
    {
        var exception = await Assert.ThrowsAsync<CliUsageException>(() => UiCommands.RunAsync(
            CliArguments.Parse([
                "ui", "tap",
                "--session", "session-001",
                "--app", "com.example.app",
                "--automation-id", "SaveButton"
            ]),
            new CliOutput(false, TextWriter.Null, TextWriter.Null),
            (_, _, _) => throw new InvalidOperationException("The host must not be called."),
            CancellationToken.None));

        Assert.Contains("not both", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AssertCountZeroAlsoRequiresAbsence()
    {
        JsonObject? observedArguments = null;

        var exitCode = await UiCommands.RunAsync(
            CliArguments.Parse([
                "ui", "assert",
                "--automation-id", "Toast",
                "--count", "0"
            ]),
            new CliOutput(false, TextWriter.Null, TextWriter.Null),
            (operation, arguments, _) =>
            {
                observedArguments = arguments;
                return Task.FromResult(Success(operation));
            },
            CancellationToken.None);

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.False(observedArguments!["exists"]!.GetValue<bool>());
        Assert.Equal(0, observedArguments["expectedCount"]!.GetValue<int>());
    }

    [Fact]
    public async Task ContradictoryAssertionExpectationsAreRejected()
    {
        var exception = await Assert.ThrowsAsync<CliUsageException>(() => UiCommands.RunAsync(
            CliArguments.Parse([
                "ui", "assert",
                "--automation-id", "Toast",
                "--exists", "true",
                "--count", "0"
            ]),
            new CliOutput(false, TextWriter.Null, TextWriter.Null),
            (_, _, _) => throw new InvalidOperationException("The host must not be called."),
            CancellationToken.None));

        Assert.Contains("contradictory", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PartialExactSwipeCoordinatesAreRejected()
    {
        var exception = await Assert.ThrowsAsync<CliUsageException>(() => UiCommands.RunAsync(
            CliArguments.Parse([
                "ui", "swipe",
                "--start-x", "0.5",
                "--start-y", "0.8"
            ]),
            new CliOutput(false, TextWriter.Null, TextWriter.Null),
            (_, _, _) => throw new InvalidOperationException("The host must not be called."),
            CancellationToken.None));

        Assert.Contains("must be supplied together", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SnapshotRejectsSelectorOptions()
    {
        var exception = await Assert.ThrowsAsync<CliUsageException>(() => UiCommands.RunAsync(
            CliArguments.Parse(["ui", "snapshot", "--automation-id", "SaveButton"]),
            new CliOutput(false, TextWriter.Null, TextWriter.Null),
            (_, _, _) => throw new InvalidOperationException("The host must not be called."),
            CancellationToken.None));

        Assert.Contains("--automation-id", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("compact")]
    [InlineData("full")]
    [InlineData("FULL")]
    public async Task SnapshotForwardsResponseDetailAndCaptureLimitsToTheUiService(string detail)
    {
        JsonObject? suppliedArguments = null;
        var exitCode = await UiCommands.RunAsync(
            CliArguments.Parse([
                "ui", "snapshot", "--detail", detail, "--max-nodes", "24",
                "--include-properties", "true", "--include-bounds", "false"
            ]),
            new CliOutput(false, TextWriter.Null, TextWriter.Null),
            (operation, arguments, _) =>
            {
                suppliedArguments = arguments;
                return Task.FromResult(Success(operation));
            },
            CancellationToken.None);

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Equal(detail.ToLowerInvariant(), suppliedArguments!["detail"]!.GetValue<string>());
        Assert.Equal(24, suppliedArguments["maxNodes"]!.GetValue<int>());
        Assert.True(suppliedArguments["includeProperties"]!.GetValue<bool>());
        Assert.False(suppliedArguments["includeBounds"]!.GetValue<bool>());
    }

    [Fact]
    public async Task FindAcceptsFullResponseDetailWithoutChangingTheSelector()
    {
        JsonObject? suppliedArguments = null;
        await UiCommands.RunAsync(
            CliArguments.Parse(["ui", "find", "--text", "Save", "--detail", "full", "--limit", "3"]),
            new CliOutput(false, TextWriter.Null, TextWriter.Null),
            (operation, arguments, _) =>
            {
                suppliedArguments = arguments;
                return Task.FromResult(Success(operation));
            },
            CancellationToken.None);

        Assert.Equal("full", suppliedArguments!["detail"]!.GetValue<string>());
        Assert.Equal("Save", suppliedArguments["text"]!.GetValue<string>());
        Assert.Equal(3, suppliedArguments["limit"]!.GetValue<int>());
    }

    [Fact]
    public async Task InvalidResponseDetailIsRejectedBeforeCallingTheHost()
    {
        var exception = await Assert.ThrowsAsync<CliUsageException>(() => UiCommands.RunAsync(
            CliArguments.Parse(["ui", "snapshot", "--detail", "summary"]),
            new CliOutput(false, TextWriter.Null, TextWriter.Null),
            (_, _, _) => throw new InvalidOperationException("The host must not be called."),
            CancellationToken.None));

        Assert.Contains("--detail must be compact or full", exception.Message, StringComparison.Ordinal);
    }

    private static UiAutomationResult Success(UiAutomationOperation operation)
        => new(
            operation,
            true,
            "ok",
            new JsonObject { ["capability"] = $"ui.{operation.ToString().ToLowerInvariant()}" });
}
