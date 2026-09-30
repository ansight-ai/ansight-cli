using System.Text.Json.Nodes;
using Ansight.Host;

namespace Ansight.Cli.Tests.Commands.Keyboard;

public sealed class KeyboardCommandsTests
{
    public static IEnumerable<object[]> KeyboardRoutes()
    {
        yield return [new[] { "keyboard", "open", "--role", "textbox" }, UiAutomationOperation.KeyboardOpen];
        yield return [new[] { "keyboard", "is-open" }, UiAutomationOperation.KeyboardIsOpen];
        yield return [new[] { "keyboard", "dismiss" }, UiAutomationOperation.KeyboardDismiss];
    }

    [Theory]
    [MemberData(nameof(KeyboardRoutes))]
    public async Task EveryKeyboardVerbRoutesToItsHostOperation(
        string[] command,
        UiAutomationOperation expectedOperation)
    {
        UiAutomationOperation? observedOperation = null;

        var exitCode = await KeyboardCommands.RunAsync(
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
    public async Task OpenBuildsCompoundTextInputSelector()
    {
        JsonObject? observedArguments = null;

        var exitCode = await KeyboardCommands.RunAsync(
            CliArguments.Parse([
                "keyboard", "open",
                "--session", "session-001",
                "--automation-id", "SearchField",
                "--role", "textbox",
                "--ancestor", "SearchPanel",
                "--index", "1",
                "--contains",
                "--case-sensitive"
            ]),
            new CliOutput(false, TextWriter.Null, TextWriter.Null),
            (operation, arguments, _) =>
            {
                observedArguments = arguments;
                return Task.FromResult(Success(operation));
            },
            CancellationToken.None);

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Equal("session-001", observedArguments!["sessionId"]!.GetValue<string>());
        Assert.Equal("SearchField", observedArguments["automationId"]!.GetValue<string>());
        Assert.Equal("textbox", observedArguments["role"]!.GetValue<string>());
        Assert.Equal("SearchPanel", observedArguments["ancestorAutomationId"]!.GetValue<string>());
        Assert.Equal(1, observedArguments["index"]!.GetValue<int>());
        Assert.False(observedArguments["exact"]!.GetValue<bool>());
        Assert.True(observedArguments["caseSensitive"]!.GetValue<bool>());
        Assert.False(observedArguments["includeScreenshot"]!.GetValue<bool>());
    }

    [Fact]
    public async Task AppAndDeviceFormACompoundSessionTarget()
    {
        JsonObject? observedArguments = null;

        var exitCode = await KeyboardCommands.RunAsync(
            CliArguments.Parse([
                "keyboard", "is-open",
                "--app", "com.example.app",
                "--device", "emulator-5554"
            ]),
            new CliOutput(false, TextWriter.Null, TextWriter.Null),
            (operation, arguments, _) =>
            {
                observedArguments = arguments;
                return Task.FromResult(Success(operation));
            },
            CancellationToken.None);

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Equal("com.example.app", observedArguments!["appId"]!.GetValue<string>());
        Assert.Equal("emulator-5554", observedArguments["deviceId"]!.GetValue<string>());
    }

    [Fact]
    public async Task SessionCannotBeCombinedWithDevice()
    {
        var exception = await Assert.ThrowsAsync<CliUsageException>(() => KeyboardCommands.RunAsync(
            CliArguments.Parse([
                "keyboard", "dismiss",
                "--session", "session-001",
                "--device-id", "emulator-5554"
            ]),
            new CliOutput(false, TextWriter.Null, TextWriter.Null),
            (_, _, _) => throw new InvalidOperationException("The host must not be called."),
            CancellationToken.None));

        Assert.Contains("--session/--session-id alone", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenRequiresASelectorBeforeCallingTheHost()
    {
        var invoked = false;

        var exception = await Assert.ThrowsAsync<CliUsageException>(() => KeyboardCommands.RunAsync(
            CliArguments.Parse(["keyboard", "open"]),
            new CliOutput(false, TextWriter.Null, TextWriter.Null),
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
    public async Task IsOpenRejectsSelectorOptions()
    {
        var exception = await Assert.ThrowsAsync<CliUsageException>(() => KeyboardCommands.RunAsync(
            CliArguments.Parse(["keyboard", "is-open", "--role", "textbox"]),
            new CliOutput(false, TextWriter.Null, TextWriter.Null),
            (_, _, _) => throw new InvalidOperationException("The host must not be called."),
            CancellationToken.None));

        Assert.Contains("--role", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task JsonOutputUsesKeyboardSchema()
    {
        using var standardOutput = new StringWriter();

        var exitCode = await KeyboardCommands.RunAsync(
            CliArguments.Parse(["keyboard", "is-open", "--json"]),
            new CliOutput(true, standardOutput, TextWriter.Null),
            (operation, _, _) => Task.FromResult(new UiAutomationResult(
                operation,
                true,
                "The system software keyboard is open.",
                new JsonObject { ["isOpen"] = true })),
            CancellationToken.None);

        Assert.Equal(CliExitCodes.Success, exitCode);
        var json = JsonNode.Parse(standardOutput.ToString())!.AsObject();
        Assert.Equal("ansight.keyboard-operation/v1", json["schema"]!.GetValue<string>());
        Assert.Equal("is-open", json["operation"]!.GetValue<string>());
        Assert.True(json["result"]!["isOpen"]!.GetValue<bool>());
    }

    private static UiAutomationResult Success(UiAutomationOperation operation)
        => new(
            operation,
            true,
            "ok",
            new JsonObject { ["capability"] = $"keyboard.{operation.ToString().ToLowerInvariant()}" });
}
