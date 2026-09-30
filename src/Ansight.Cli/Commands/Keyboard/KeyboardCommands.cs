using System.Text.Json;
using System.Text.Json.Nodes;
using Ansight.Host;

namespace Ansight.Cli.Commands.Keyboard;

internal static class KeyboardCommands
{
    private static readonly string[] CommonFlagOptions =
    [
        "help",
        "beta",
        "json",
        "silent",
        "verbose",
        "diagnostic",
        "audit",
        "enable-repository-automations",
        "disable-repository-automations"
    ];

    private static readonly string[] CommonValueOptions =
    [
        "session",
        "session-id",
        "app",
        "app-id",
        "device",
        "device-id",
        "data-dir",
        "adb-path",
        "xcode-path",
        "secret-store-file",
        "secret-key-file",
        "discovery-port",
        "websocket-port",
        "automation-repository",
        "node-path"
    ];

    private static readonly string[] SelectorFlagOptions = ["contains", "case-sensitive"];

    private static readonly string[] SelectorValueOptions =
    [
        "automation-id",
        "text",
        "role",
        "ancestor",
        "index"
    ];

    internal delegate Task<UiAutomationResult> ExecuteKeyboardOperation(
        UiAutomationOperation operation,
        JsonObject arguments,
        CancellationToken cancellationToken);

    public static async Task<int> RunAsync(
        CliArguments arguments,
        CliOutput output,
        CancellationToken cancellationToken)
    {
        if (CliCommandHelp.IsRequested(arguments))
        {
            return CliCommandHelp.Write(output, BuildHelp());
        }

        var options = CliRuntime.ResolveOptions(arguments);
        await using var lease = await CliRuntimeLease.CreateAsync(
            options,
            start: true,
            cancellationToken).ConfigureAwait(false);
        return await RunAsync(
            arguments,
            output,
            lease.Runtime.Ui.ExecuteAsync,
            cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<int> RunAsync(
        CliArguments arguments,
        CliOutput output,
        ExecuteKeyboardOperation execute,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(execute);
        var action = arguments.RequirePositional(1, "keyboard action").ToLowerInvariant();
        arguments.EnsurePositionalCount(2, $"ansight keyboard {action} [options]");
        var operation = ResolveOperation(action);
        ValidateOptions(operation, action, arguments);
        var operationArguments = BuildOperationArguments(operation, arguments);
        var result = await execute(operation, operationArguments, cancellationToken)
            .ConfigureAwait(false);
        var commandOutput = new KeyboardCommandOutput(
            "ansight.keyboard-operation/v1",
            action,
            result.IsSuccess,
            result.Message,
            result.Payload);
        output.Write(
            commandOutput,
            () => result.Payload.Count == 0
                ? result.Message
                : result.Payload.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return result.IsSuccess ? CliExitCodes.Success : CliExitCodes.Failure;
    }

    private static UiAutomationOperation ResolveOperation(string action)
        => action switch
        {
            "open" => UiAutomationOperation.KeyboardOpen,
            "is-open" => UiAutomationOperation.KeyboardIsOpen,
            "dismiss" => UiAutomationOperation.KeyboardDismiss,
            _ => throw new CliUsageException(
                $"Unknown keyboard action '{action}'. Expected open, is-open, or dismiss.")
        };

    private static JsonObject BuildOperationArguments(
        UiAutomationOperation operation,
        CliArguments arguments)
    {
        var result = new JsonObject();
        AddOptionalString(result, "sessionId", arguments.GetOption("session") ?? arguments.GetOption("session-id"));
        AddOptionalString(result, "appId", arguments.GetOption("app") ?? arguments.GetOption("app-id"));
        AddOptionalString(result, "deviceId", arguments.GetOption("device") ?? arguments.GetOption("device-id"));
        if (operation == UiAutomationOperation.KeyboardOpen)
        {
            AddSelector(result, arguments);
        }

        if (operation is UiAutomationOperation.KeyboardOpen or UiAutomationOperation.KeyboardDismiss)
        {
            result["includeScreenshot"] = false;
        }

        return result;
    }

    private static void AddSelector(JsonObject target, CliArguments arguments)
    {
        AddOptionalString(target, "automationId", arguments.GetOption("automation-id"));
        AddOptionalString(target, "text", arguments.GetOption("text"));
        AddOptionalString(target, "role", arguments.GetOption("role"));
        AddOptionalString(target, "ancestorAutomationId", arguments.GetOption("ancestor"));
        if (arguments.GetOption("index") is not null)
        {
            target["index"] = arguments.GetIntOption("index", 0, 0, int.MaxValue);
        }
        if (arguments.HasFlag("contains"))
        {
            target["exact"] = false;
        }
        if (arguments.HasFlag("case-sensitive"))
        {
            target["caseSensitive"] = true;
        }
    }

    private static void ValidateOptions(
        UiAutomationOperation operation,
        string action,
        CliArguments arguments)
    {
        var flags = CommonFlagOptions.ToList();
        var values = CommonValueOptions.ToList();
        if (operation == UiAutomationOperation.KeyboardOpen)
        {
            flags.AddRange(SelectorFlagOptions);
            values.AddRange(SelectorValueOptions);
        }

        arguments.EnsureOptionContract($"ansight keyboard {action}", flags, values);
        ValidateTargetOptions(arguments);
        if (operation == UiAutomationOperation.KeyboardOpen)
        {
            RequireSelector(arguments);
        }
    }

    private static void RequireSelector(CliArguments arguments)
    {
        var hasSelector = arguments.GetOption("automation-id") is not null
                          || arguments.GetOption("text") is not null
                          || arguments.GetOption("role") is not null
                          || arguments.GetOption("ancestor") is not null;
        if (!hasSelector)
        {
            throw new CliUsageException(
                "ansight keyboard open requires at least one selector: --automation-id, --text, --role, or --ancestor.");
        }
    }

    private static void ValidateTargetOptions(CliArguments arguments)
    {
        var hasSession = arguments.GetOption("session") is not null;
        var hasSessionId = arguments.GetOption("session-id") is not null;
        var hasApp = arguments.GetOption("app") is not null;
        var hasAppId = arguments.GetOption("app-id") is not null;
        var hasDevice = arguments.GetOption("device") is not null;
        var hasDeviceId = arguments.GetOption("device-id") is not null;
        if (hasSession && hasSessionId)
        {
            throw new CliUsageException("Use only one of --session or --session-id.");
        }

        if (hasApp && hasAppId)
        {
            throw new CliUsageException("Use only one of --app or --app-id.");
        }

        if (hasDevice && hasDeviceId)
        {
            throw new CliUsageException("Use only one of --device or --device-id.");
        }

        if ((hasSession || hasSessionId) && (hasApp || hasAppId || hasDevice || hasDeviceId))
        {
            throw new CliUsageException(
                "Use --session/--session-id alone, or select with --app/--app-id and/or --device/--device-id.");
        }
    }

    private static void AddOptionalString(JsonObject target, string propertyName, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            target[propertyName] = value.Trim();
        }
    }

    private static string BuildHelp()
        => """
           Inspect and control the system software keyboard through the resident Ansight host

           Usage:
             ansight keyboard open [selector options] [target options]
             ansight keyboard is-open [target options]
             ansight keyboard dismiss [target options]

           Commands:
             open       Focus the selected text input and verify that the keyboard opens
             is-open    Report whether the software keyboard is currently visible
             dismiss    Dismiss an open keyboard and verify that it closes

           Target options:
             --session <id>                    Exact connected Ansight session
             --session-id <id>                 Long form of --session
             --app <id>                        Connected session for an application
             --app-id <id>                     Long form of --app
             --device <id>                     Connected session on a native device
             --device-id <id>                  Long form of --device

           Combine an app and device target to select that app on that device. A device
           target by itself must have exactly one connected Ansight app session. An exact
           session target cannot be combined with app or device targets.

           Open selector options:
             --automation-id <id>             Stable automation/accessibility identifier
             --text <text>                    Visible or accessibility text
             --role <role>                    Semantic role such as textbox
             --ancestor <automation-id>       Require an ancestor with this automation identifier
             --index <zero-based-index>       Select one match when the selector is not unique
             --contains                       Use substring rather than exact string matching
             --case-sensitive                 Match selector strings case-sensitively

           These commands use the same keyboard state, device input, and before/after
           evidence paths as repository tasks. Android reads InputMethodManager state so
           physical keyboards such as Gboard do not depend on UIAutomator visibility.

           Examples:
             ansight keyboard open --automation-id SearchField --device emulator-5554
             ansight keyboard is-open --app com.example.app --device emulator-5554 --json
             ansight keyboard dismiss --session session-001
           """;
}
