using System.Text.Json;
using System.Text.Json.Nodes;
using Ansight.Host;

namespace Ansight.Cli.Commands.Ui;

internal static class UiCommands
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
        "detail",
        "app",
        "app-id",
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

    internal delegate Task<UiAutomationResult> ExecuteUiOperation(
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
        ExecuteUiOperation execute,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(execute);
        var action = arguments.RequirePositional(1, "ui action").ToLowerInvariant();
        arguments.EnsurePositionalCount(2, $"ansight ui {action} [options]");
        var operation = ResolveOperation(action);
        ValidateOptions(operation, action, arguments);
        var operationArguments = BuildOperationArguments(operation, arguments);
        var result = await execute(operation, operationArguments, cancellationToken)
            .ConfigureAwait(false);
        var commandOutput = new UiCommandOutput(
            "ansight.ui-operation/v1",
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
            "snapshot" => UiAutomationOperation.Snapshot,
            "find" => UiAutomationOperation.Find,
            "tap" => UiAutomationOperation.Tap,
            "type" => UiAutomationOperation.Type,
            "swipe" => UiAutomationOperation.Swipe,
            "pinch" => UiAutomationOperation.Pinch,
            "back" => UiAutomationOperation.Back,
            "wait" => UiAutomationOperation.Wait,
            "assert" => UiAutomationOperation.Assert,
            _ => throw new CliUsageException(
                $"Unknown ui action '{action}'. Expected snapshot, find, tap, type, swipe, pinch, back, wait, or assert.")
        };

    private static JsonObject BuildOperationArguments(
        UiAutomationOperation operation,
        CliArguments arguments)
    {
        var result = new JsonObject();
        AddOptionalString(result, "sessionId", arguments.GetOption("session") ?? arguments.GetOption("session-id"));
        AddOptionalString(result, "appId", arguments.GetOption("app") ?? arguments.GetOption("app-id"));
        AddOptionalString(result, "detail", arguments.GetOption("detail")?.ToLowerInvariant());

        if (operation != UiAutomationOperation.Snapshot
            && operation != UiAutomationOperation.Back)
        {
            AddSelector(result, arguments);
        }

        switch (operation)
        {
            case UiAutomationOperation.Snapshot:
                AddOptionalInteger(result, "maxNodes", arguments, "max-nodes", 1, 2_000);
                AddOptionalInteger(result, "maxDepth", arguments, "max-depth", 1, 64);
                AddOptionalBoolean(result, "includeProperties", arguments, "include-properties");
                AddOptionalBoolean(result, "includeBounds", arguments, "include-bounds");
                break;
            case UiAutomationOperation.Find:
                RequireSelector(result, "find");
                AddOptionalInteger(result, "limit", arguments, "limit", 1, 1_000);
                break;
            case UiAutomationOperation.Tap:
                RequireSelector(result, "tap");
                result["includeScreenshot"] = false;
                break;
            case UiAutomationOperation.Type:
                RequireSelector(result, "type");
                result["value"] = arguments.RequireOption("value");
                result["replaceExisting"] = !arguments.HasFlag("append");
                result["includeScreenshot"] = false;
                break;
            case UiAutomationOperation.Swipe:
                AddOptionalString(result, "orientation", arguments.GetOption("orientation"));
                AddOptionalString(result, "direction", arguments.GetOption("direction"));
                AddOptionalDouble(result, "length", arguments, "length", 0.05, 0.9);
                AddOptionalInteger(result, "durationMs", arguments, "duration-ms", 50, 2_000);
                AddOptionalDouble(result, "startNormalizedX", arguments, "start-x", 0, 1);
                AddOptionalDouble(result, "startNormalizedY", arguments, "start-y", 0, 1);
                AddOptionalDouble(result, "endNormalizedX", arguments, "end-x", 0, 1);
                AddOptionalDouble(result, "endNormalizedY", arguments, "end-y", 0, 1);
                result["includeScreenshot"] = false;
                break;
            case UiAutomationOperation.Pinch:
                result["scale"] = GetBoundedDoubleOption(arguments, "scale", 0.1, 4);
                AddOptionalDouble(result, "centerNormalizedX", arguments, "center-x", 0, 1);
                AddOptionalDouble(result, "centerNormalizedY", arguments, "center-y", 0, 1);
                AddOptionalDouble(result, "startDistance", arguments, "start-distance", 0.05, 0.8);
                AddOptionalDouble(result, "angleDegrees", arguments, "angle-degrees");
                AddOptionalInteger(result, "durationMs", arguments, "duration-ms", 50, 2_000);
                result["includeScreenshot"] = false;
                break;
            case UiAutomationOperation.Back:
                result["includeScreenshot"] = false;
                break;
            case UiAutomationOperation.Wait:
                var condition = arguments.GetOption("condition") ?? "visible";
                result["condition"] = condition;
                if (!string.Equals(condition, "stable", StringComparison.OrdinalIgnoreCase))
                {
                    RequireSelector(result, "wait");
                }
                AddOptionalInteger(result, "timeoutMs", arguments, "timeout-ms", 100, 60_000);
                AddOptionalInteger(result, "pollIntervalMs", arguments, "poll-ms", 100, 5_000);
                AddOptionalInteger(result, "stableSamples", arguments, "stable-samples", 2, 20);
                break;
            case UiAutomationOperation.Assert:
                RequireSelector(result, "assert");
                var expectedCount = arguments.GetOption("count") is null
                    ? (int?)null
                    : arguments.GetIntOption("count", 0, 0, 1_000_000);
                if (arguments.HasFlag("absent"))
                {
                    result["exists"] = false;
                }
                else
                {
                    AddOptionalBoolean(result, "exists", arguments, "exists");
                    if (arguments.GetOption("exists") is null && expectedCount == 0)
                    {
                        result["exists"] = false;
                    }
                }
                if (expectedCount.HasValue)
                {
                    result["expectedCount"] = expectedCount.Value;
                }
                AddOptionalString(result, "expectedText", arguments.GetOption("expected-text"));
                AddOptionalString(result, "expectedValue", arguments.GetOption("expected-value"));
                AddOptionalBoolean(result, "expectedVisible", arguments, "expected-visible");
                AddOptionalBoolean(result, "expectedEnabled", arguments, "expected-enabled");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation), operation, null);
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

    private static void RequireSelector(JsonObject arguments, string action)
    {
        if (arguments["automationId"] is null
            && arguments["text"] is null
            && arguments["role"] is null
            && arguments["ancestorAutomationId"] is null)
        {
            throw new CliUsageException(
                $"ansight ui {action} requires at least one selector: --automation-id, --text, --role, or --ancestor.");
        }
    }

    private static void AddOptionalString(JsonObject target, string propertyName, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            target[propertyName] = value.Trim();
        }
    }

    private static void AddOptionalInteger(
        JsonObject target,
        string propertyName,
        CliArguments arguments,
        string optionName,
        int minimum,
        int maximum)
    {
        if (arguments.GetOption(optionName) is not null)
        {
            target[propertyName] = arguments.GetIntOption(optionName, minimum, minimum, maximum);
        }
    }

    private static void AddOptionalDouble(
        JsonObject target,
        string propertyName,
        CliArguments arguments,
        string optionName,
        double? minimum = null,
        double? maximum = null)
    {
        if (arguments.GetOption(optionName) is not null)
        {
            target[propertyName] = minimum.HasValue && maximum.HasValue
                ? GetBoundedDoubleOption(arguments, optionName, minimum.Value, maximum.Value)
                : arguments.GetDoubleOption(optionName);
        }
    }

    private static double GetBoundedDoubleOption(
        CliArguments arguments,
        string optionName,
        double minimum,
        double maximum)
    {
        var value = arguments.GetDoubleOption(optionName);
        if (value < minimum || value > maximum)
        {
            throw new CliUsageException(
                $"--{optionName} must be a number between {minimum} and {maximum}.");
        }

        return value;
    }

    private static void ValidateOptions(
        UiAutomationOperation operation,
        string action,
        CliArguments arguments)
    {
        var flags = CommonFlagOptions.ToList();
        var values = CommonValueOptions.ToList();
        if (operation is not UiAutomationOperation.Snapshot and not UiAutomationOperation.Back)
        {
            flags.AddRange(SelectorFlagOptions);
            values.AddRange(SelectorValueOptions);
        }

        switch (operation)
        {
            case UiAutomationOperation.Snapshot:
                values.AddRange(["max-nodes", "max-depth", "include-properties", "include-bounds"]);
                break;
            case UiAutomationOperation.Find:
                values.Add("limit");
                break;
            case UiAutomationOperation.Tap:
                break;
            case UiAutomationOperation.Type:
                flags.Add("append");
                values.Add("value");
                break;
            case UiAutomationOperation.Swipe:
                values.AddRange([
                    "orientation", "direction", "length", "duration-ms",
                    "start-x", "start-y", "end-x", "end-y"
                ]);
                break;
            case UiAutomationOperation.Pinch:
                values.AddRange([
                    "scale", "center-x", "center-y", "start-distance",
                    "angle-degrees", "duration-ms"
                ]);
                break;
            case UiAutomationOperation.Back:
                break;
            case UiAutomationOperation.Wait:
                values.AddRange(["condition", "timeout-ms", "poll-ms", "stable-samples"]);
                break;
            case UiAutomationOperation.Assert:
                flags.Add("absent");
                values.AddRange([
                    "exists", "count", "expected-text", "expected-value",
                    "expected-visible", "expected-enabled"
                ]);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation), operation, null);
        }

        arguments.EnsureOptionContract($"ansight ui {action}", flags, values);
        if (arguments.GetOption("detail") is { } detail
            && !string.Equals(detail, "compact", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(detail, "full", StringComparison.OrdinalIgnoreCase))
        {
            throw new CliUsageException("--detail must be compact or full.");
        }
        ValidateTargetOptions(arguments);
        ValidateSelectorModifiers(operation, arguments);
        ValidateOperationRelationships(operation, arguments);
    }

    private static void ValidateTargetOptions(CliArguments arguments)
    {
        var hasSession = arguments.GetOption("session") is not null;
        var hasSessionId = arguments.GetOption("session-id") is not null;
        var hasApp = arguments.GetOption("app") is not null;
        var hasAppId = arguments.GetOption("app-id") is not null;
        if (hasSession && hasSessionId)
        {
            throw new CliUsageException("Use only one of --session or --session-id.");
        }

        if (hasApp && hasAppId)
        {
            throw new CliUsageException("Use only one of --app or --app-id.");
        }

        if ((hasSession || hasSessionId) && (hasApp || hasAppId))
        {
            throw new CliUsageException("Use a session target or an app target, not both.");
        }
    }

    private static void ValidateSelectorModifiers(
        UiAutomationOperation operation,
        CliArguments arguments)
    {
        if (operation is UiAutomationOperation.Snapshot or UiAutomationOperation.Back)
        {
            return;
        }

        var hasCriteria = arguments.GetOption("automation-id") is not null
                          || arguments.GetOption("text") is not null
                          || arguments.GetOption("role") is not null
                          || arguments.GetOption("ancestor") is not null;
        var hasModifiers = arguments.GetOption("index") is not null
                           || arguments.HasFlag("contains")
                           || arguments.HasFlag("case-sensitive");
        if (!hasCriteria && hasModifiers)
        {
            throw new CliUsageException(
                "--index, --contains, and --case-sensitive require an automation-id, text, role, or ancestor selector.");
        }
    }

    private static void ValidateOperationRelationships(
        UiAutomationOperation operation,
        CliArguments arguments)
    {
        if (operation == UiAutomationOperation.Swipe)
        {
            if (arguments.GetOption("orientation") is not null
                && arguments.GetOption("direction") is not null)
            {
                throw new CliUsageException("Use --orientation or --direction, not both.");
            }

            var coordinates = new[] { "start-x", "start-y", "end-x", "end-y" };
            var coordinateCount = coordinates.Count(name => arguments.GetOption(name) is not null);
            if (coordinateCount is > 0 and < 4)
            {
                throw new CliUsageException(
                    "--start-x, --start-y, --end-x, and --end-y must be supplied together.");
            }

            if (coordinateCount == 4
                && (arguments.GetOption("orientation") is not null
                    || arguments.GetOption("direction") is not null
                    || arguments.GetOption("length") is not null))
            {
                throw new CliUsageException(
                    "Exact swipe coordinates cannot be combined with --orientation, --direction, or --length.");
            }
        }

        if (operation == UiAutomationOperation.Pinch
            && (arguments.GetOption("center-x") is null) != (arguments.GetOption("center-y") is null))
        {
            throw new CliUsageException("--center-x and --center-y must be supplied together.");
        }

        if (operation == UiAutomationOperation.Wait)
        {
            var condition = arguments.GetOption("condition")?.ToLowerInvariant() ?? "visible";
            if (condition is not ("visible" or "hidden" or "stable"))
            {
                throw new CliUsageException("--condition must be visible, hidden, or stable.");
            }
        }

        if (operation == UiAutomationOperation.Assert && arguments.HasFlag("absent"))
        {
            if (arguments.GetOption("exists") is not null)
            {
                throw new CliUsageException("--absent cannot be combined with --exists.");
            }

            if (arguments.GetOption("expected-text") is not null
                || arguments.GetOption("expected-value") is not null
                || arguments.GetOption("expected-visible") is not null
                || arguments.GetOption("expected-enabled") is not null)
            {
                throw new CliUsageException(
                    "--absent cannot be combined with assertions on a selected node.");
            }

            if (arguments.GetOption("count") is not null
                && arguments.GetIntOption("count", 0, 0, 1_000_000) != 0)
            {
                throw new CliUsageException("--absent can only be combined with --count 0.");
            }
        }

        if (operation == UiAutomationOperation.Assert && !arguments.HasFlag("absent"))
        {
            var expectedExists = ReadOptionalBoolean(arguments, "exists");
            var expectedCount = arguments.GetOption("count") is null
                ? (int?)null
                : arguments.GetIntOption("count", 0, 0, 1_000_000);
            if (expectedExists.HasValue
                && expectedCount.HasValue
                && expectedExists.Value != (expectedCount.Value > 0))
            {
                throw new CliUsageException("--exists and --count describe contradictory expectations.");
            }

            var hasSelectedAssertion = arguments.GetOption("expected-text") is not null
                                       || arguments.GetOption("expected-value") is not null
                                       || arguments.GetOption("expected-visible") is not null
                                       || arguments.GetOption("expected-enabled") is not null;
            if (hasSelectedAssertion
                && (expectedExists == false || expectedCount == 0))
            {
                throw new CliUsageException(
                    "Selected-node assertions cannot be combined with --exists false or --count 0.");
            }
        }
    }

    private static void AddOptionalBoolean(
        JsonObject target,
        string propertyName,
        CliArguments arguments,
        string optionName)
    {
        var value = arguments.GetOption(optionName);
        if (value is null)
        {
            return;
        }

        if (!bool.TryParse(value, out var parsed))
        {
            throw new CliUsageException($"--{optionName} must be true or false.");
        }

        target[propertyName] = parsed;
    }

    private static bool? ReadOptionalBoolean(CliArguments arguments, string optionName)
    {
        var value = arguments.GetOption(optionName);
        if (value is null)
        {
            return null;
        }

        if (!bool.TryParse(value, out var parsed))
        {
            throw new CliUsageException($"--{optionName} must be true or false.");
        }

        return parsed;
    }

    private static string BuildHelp()
        => """
           Inspect and drive live app UI through the resident Ansight host

           Usage:
             ansight ui snapshot [--session <id>|--app <id>] [options]
             ansight ui find [selector options] [--session <id>|--app <id>] [options]
             ansight ui tap [selector options] [--session <id>|--app <id>]
             ansight ui type [selector options] --value <text> [--append]
             ansight ui swipe [selector options] [--direction <up|down|left|right>] [options]
             ansight ui pinch [selector options] --scale <0.1-4> [options]
             ansight ui back [--session <id>|--app <id>]
             ansight ui wait [selector options] [--condition <visible|hidden|stable>] [options]
             ansight ui assert [selector options] [assertion options]

           Selectors:
             --automation-id <id>             Stable automation/accessibility identifier
             --text <text>                    Visible or accessibility text
             --role <role>                    Semantic role such as button or textbox
             --ancestor <automation-id>       Require an ancestor with this automation identifier
             --index <zero-based-index>       Select one match when the selector is not unique
             --contains                       Use substring rather than exact string matching
             --case-sensitive                 Match selector strings case-sensitively

           Wait and assertion options:
             --condition <visible|hidden|stable>
             --timeout-ms <100-60000>         Default: 10000
             --poll-ms <100-5000>             Default: 250
             --stable-samples <2-20>          Unchanged samples required by a stable wait
             --absent                         Assert that no matching element exists
             --exists <true|false>             Assert whether any match exists
             --count <number>                 Assert an exact match count
             --expected-text <text>           Assert selected element text
             --expected-value <value>         Assert selected element value
             --expected-visible <true|false>  Assert selected element visibility
             --expected-enabled <true|false>  Assert selected element enabled state

           Gesture options:
             --direction <direction>          Swipe direction; defaults to up
             --orientation <path>             Compass path such as SW or E-to-W
             --length <0.05-0.9>              Normalized swipe length
             --start-x/--start-y/--end-x/--end-y <0-1>
                                             Exact normalized swipe coordinates; supply all four
             --duration-ms <50-2000>          Gesture duration
             --scale <0.1-4>                  Pinch scale; below 1 pinches inward
             --center-x <0-1> --center-y <0-1>
             --start-distance <0.05-0.8>      Initial normalized pinch contact distance
             --angle-degrees <value>          Pinch contact-axis angle

           Snapshot, find, and type options:
             --detail <compact|full>          Response detail for any UI command; default: compact
             --max-nodes <1-2000>             Snapshot node limit; default: 256
             --max-depth <1-64>               Snapshot traversal depth; default: 32
             --include-properties <true|false> Force detailed framework properties
             --include-bounds <true|false>     Include element bounds; default: true
             --limit <1-1000>                 Maximum find results; default: 100
             --append                         Append rather than replace existing text

           Compact responses share the host's UI projection. Full detail retains the
           original operation payload. --include-properties true defaults to full detail
           unless --detail compact is supplied explicitly.

           All commands are thin adapters over the host's semantic UI service. Actions use
           real device input and return structured before/after evidence. No task file or
           secondary automation engine is involved.

           Examples:
             ansight ui snapshot --session session-001 --json
             ansight ui find --text "Europe" --role button --json
             ansight ui tap --automation-id MapTab
             ansight ui type --automation-id SearchField --value "Kalymnos"
             ansight ui swipe --ancestor ResultsList --direction up --length 0.6
             ansight ui pinch --automation-id Map --scale 1.8
             ansight ui wait --text "Loaded" --condition visible --timeout-ms 15000
             ansight ui assert --automation-id SaveButton --expected-enabled true
           """;
}
