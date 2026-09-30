using System.Text.Json;
using System.Text.Json.Nodes;
using Ansight.Host;

namespace Ansight.Cli.Commands.App;

internal static class AppCommands
{
    public static async Task<int> RunAsync(
        CliArguments arguments,
        CliOutput output,
        CancellationToken cancellationToken)
    {
        if (CliCommandHelp.IsRequested(arguments))
        {
            return CliCommandHelp.Write(output, arguments.Positionals.ElementAtOrDefault(1) == "watch"
                ? AppWatchCommands.Help : BuildHelp());
        }

        var action = arguments.RequirePositional(1, "app action").ToLowerInvariant();
        if (action == "execute")
        {
            return await AppExecutionService.RunAppExecuteAsync(
                    arguments,
                    output,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var options = CliRuntime.ResolveOptions(arguments);
        var startRuntime = action is "tools" or "call" or "batch" or "push-file";
        await using var lease = await CliRuntimeLease.CreateAsync(
            options,
            startRuntime,
            cancellationToken).ConfigureAwait(false);

        return action switch
        {
            "watch" => await AppWatchCommands.RunAsync(lease.Runtime, arguments, output, cancellationToken),
            "list" => ListApps(lease.Runtime, arguments, output),
            "get" => GetApp(lease.Runtime, arguments, output),
            "register" or "add" => RegisterApp(lease.Runtime, arguments, output),
            "remove" or "delete" => RemoveApp(lease.Runtime, arguments, output),
            "tools" => await ListToolsAsync(lease.Runtime, arguments, output, cancellationToken),
            "call" => await CallToolAsync(lease.Runtime, arguments, output, cancellationToken),
            "batch" => await CallToolsAsync(lease.Runtime, arguments, output, cancellationToken),
            "push-file" => await PushFileAsync(lease.Runtime, arguments, output, cancellationToken),
            _ => throw new CliUsageException(
                $"Unknown app action '{action}'. Expected list, get, register, remove, watch, tools, call, batch, execute, or push-file.")
        };
    }

    private static int ListApps(
        RuntimeCoordinator runtime,
        CliArguments arguments,
        CliOutput output)
    {
        arguments.EnsurePositionalCount(2, "ansight app list");
        var apps = runtime.Apps.List();
        output.Write(
            new AppListOutput("ansight.apps/v1", apps),
            () => apps.Count == 0
                ? "No apps are registered or present in session history."
                : string.Join(
                    Environment.NewLine,
                    apps.Select(app =>
                        $"{app.AppId}\t{app.Name}\tsessions={app.SessionCount}\tlive={app.LiveSessionCount}"
                        + $"\ttrends-monitoring={(app.AutomaticTrendsMonitoringEnabled ? "automatic" : "needs-codebase")}")));
        return CliExitCodes.Success;
    }

    private static int GetApp(
        RuntimeCoordinator runtime,
        CliArguments arguments,
        CliOutput output)
    {
        arguments.EnsurePositionalCount(3, "ansight app get <app-id>");
        var appId = arguments.RequirePositional(2, "app identifier");
        var app = runtime.Apps.Get(appId);
        output.Write(
            new AppDetailOutput("ansight.app/v1", appId, app is not null, app),
            () => app is null
                ? $"App '{appId}' was not found."
                : $"{app.AppId}\t{app.Name}\tsessions={app.SessionCount}\tlive={app.LiveSessionCount}"
                  + $"\ttrends-monitoring={(app.AutomaticTrendsMonitoringEnabled ? "automatic" : "needs-codebase")}");
        return app is null ? CliExitCodes.Failure : CliExitCodes.Success;
    }

    private static int RegisterApp(
        RuntimeCoordinator runtime,
        CliArguments arguments,
        CliOutput output)
    {
        arguments.EnsurePositionalCount(
            3,
            "ansight app register <app-id> [--name <name>] [--codebase <path>]");
        var result = runtime.Apps.Register(new AppRegistrationRequest(
            arguments.RequirePositional(2, "app identifier"),
            arguments.GetOption("name"),
            arguments.GetOption("codebase")));
        return WriteAppOperation("register", result, output);
    }

    private static int RemoveApp(
        RuntimeCoordinator runtime,
        CliArguments arguments,
        CliOutput output)
    {
        arguments.EnsurePositionalCount(3, "ansight app remove <app-id>");
        var result = runtime.Apps.Remove(arguments.RequirePositional(2, "app identifier"));
        return WriteAppOperation("remove", result, output);
    }

    private static int WriteAppOperation(
        string operation,
        AppOperationResult result,
        CliOutput output)
    {
        output.Write(
            new AppOperationOutput("ansight.app-operation/v1", operation, result),
            () => result.Message);
        return result.IsSuccess ? CliExitCodes.Success : CliExitCodes.Failure;
    }

    private static async Task<int> ListToolsAsync(
        RuntimeCoordinator runtime,
        CliArguments arguments,
        CliOutput output,
        CancellationToken cancellationToken)
    {
        arguments.EnsurePositionalCount(3, "ansight app tools <session-id>");
        var sessionId = arguments.RequirePositional(2, "session identifier");
        var filters = ReadToolCatalogFilters(arguments);
        var response = filters is null
            ? await runtime.AppTools.QuerySinceAsync(
                sessionId,
                arguments.GetOption("if-revision"),
                cancellationToken).ConfigureAwait(false)
            : await runtime.AppTools.QueryFilteredAsync(
                sessionId,
                filters,
                cancellationToken).ConfigureAwait(false);
        return WriteResponse("tools", sessionId, response, output);
    }

    private static JsonObject? ReadToolCatalogFilters(CliArguments arguments)
    {
        var filters = new JsonObject();
        var hasFocusedFilter = false;
        hasFocusedFilter |= AddToolCatalogStringFilter(filters, arguments, "query", "query");
        hasFocusedFilter |= AddToolCatalogStringFilter(filters, arguments, "feature", "feature");
        hasFocusedFilter |= AddToolCatalogStringFilter(filters, arguments, "category", "category");
        hasFocusedFilter |= AddToolCatalogStringFilter(filters, arguments, "id-prefix", "idPrefix");
        hasFocusedFilter |= AddToolCatalogStringFilter(filters, arguments, "tool-id", "toolId");

        if (arguments.HasFlag("policy"))
        {
            var policy = RequireNonEmptyOption(arguments, "policy").ToLowerInvariant();
            if (policy is not ("read" or "write" or "critical"))
            {
                throw new CliUsageException("--policy must be read, write, or critical.");
            }

            filters["policy"] = policy;
            hasFocusedFilter = true;
        }

        if (arguments.HasFlag("max-results"))
        {
            filters["maxResults"] = arguments.GetRequiredIntOption("max-results", 1, 50);
            hasFocusedFilter = true;
        }

        var executableOnly = arguments.HasFlag("executable-only");
        var includeUnavailable = arguments.HasFlag("include-unavailable");
        if (executableOnly && includeUnavailable)
        {
            throw new CliUsageException(
                "Use either --executable-only or --include-unavailable, not both.");
        }
        if (executableOnly || includeUnavailable)
        {
            filters["executableOnly"] = executableOnly;
            hasFocusedFilter = true;
        }

        var hasDetail = arguments.HasFlag("detail");
        if (hasDetail)
        {
            var detail = RequireNonEmptyOption(arguments, "detail").ToLowerInvariant();
            if (detail is not ("summary" or "full"))
            {
                throw new CliUsageException("--detail must be summary or full.");
            }

            filters["detail"] = detail;
        }

        if (!hasFocusedFilter && !hasDetail)
        {
            return null;
        }

        if (filters["executableOnly"] is null)
        {
            filters["executableOnly"] = hasFocusedFilter;
        }
        if (arguments.GetOption("if-revision") is { } ifRevision)
        {
            filters["ifRevision"] = ifRevision;
        }

        return filters;
    }

    private static bool AddToolCatalogStringFilter(
        JsonObject filters,
        CliArguments arguments,
        string optionName,
        string propertyName)
    {
        if (!arguments.HasFlag(optionName))
        {
            return false;
        }

        filters[propertyName] = RequireNonEmptyOption(arguments, optionName);
        return true;
    }

    private static string RequireNonEmptyOption(CliArguments arguments, string optionName)
    {
        var value = arguments.RequireOption(optionName).Trim();
        if (value.Length == 0)
        {
            throw new CliUsageException($"--{optionName} requires a non-empty value.");
        }

        return value;
    }

    private static async Task<int> CallToolAsync(
        RuntimeCoordinator runtime,
        CliArguments arguments,
        CliOutput output,
        CancellationToken cancellationToken)
    {
        arguments.EnsurePositionalCount(
            4,
            "ansight app call <session-id> <tool-id> [--arguments <JSON>|--arguments-file <path>] [evidence options]");
        var sessionId = arguments.RequirePositional(2, "session identifier");
        var toolId = arguments.RequirePositional(3, "tool identifier");
        var toolArguments = ReadArguments(arguments);
        var after = ReadAfter(arguments);
        var response = after is null
            ? await runtime.AppTools.CallAsync(
                sessionId,
                toolId,
                toolArguments,
                cancellationToken).ConfigureAwait(false)
            : await runtime.AppTools.CallWithEvidenceAsync(
                sessionId,
                toolId,
                toolArguments,
                after,
                cancellationToken).ConfigureAwait(false);
        return WriteResponse("call", sessionId, response, output, toolId);
    }

    private static async Task<int> CallToolsAsync(
        RuntimeCoordinator runtime,
        CliArguments arguments,
        CliOutput output,
        CancellationToken cancellationToken)
    {
        arguments.EnsurePositionalCount(
            3,
            "ansight app batch <session-id> --calls <JSON>|--calls-file <path> [--continue-on-error]");
        var sessionId = arguments.RequirePositional(2, "session identifier");
        var calls = ReadBatchCalls(arguments);
        var response = await runtime.AppTools.CallBatchAsync(
            sessionId,
            calls,
            arguments.HasFlag("continue-on-error"),
            cancellationToken).ConfigureAwait(false);
        return WriteResponse("batch", sessionId, response, output);
    }

    private static async Task<int> PushFileAsync(
        RuntimeCoordinator runtime,
        CliArguments arguments,
        CliOutput output,
        CancellationToken cancellationToken)
    {
        arguments.EnsurePositionalCount(
            5,
            "ansight app push-file <session-id> <local-path> <remote-directory> [options]");
        var sessionId = arguments.RequirePositional(2, "session identifier");
        var localPath = Path.GetFullPath(arguments.RequirePositional(3, "local file path"));
        if (!File.Exists(localPath))
        {
            throw new CliUsageException($"Local file '{localPath}' was not found.");
        }

        var response = await runtime.AppTools.PushFileAsync(
            sessionId,
            localPath,
            arguments.RequirePositional(4, "remote directory path"),
            arguments.GetOption("sandbox-root"),
            arguments.GetOption("file-name"),
            arguments.HasFlag("overwrite"),
            !arguments.HasFlag("no-create-directory"),
            cancellationToken).ConfigureAwait(false);
        return WriteResponse("push-file", sessionId, response, output);
    }

    private static JsonObject? ReadArguments(CliArguments arguments)
        => ReadJsonObject(arguments, "arguments", required: false);

    private static JsonObject? ReadAfter(CliArguments arguments)
    {
        var explicitAfter = ReadJsonObject(arguments, "after", required: false);
        var include = new JsonArray();
        if (arguments.HasFlag("after-tree"))
        {
            include.Add("visualTree");
        }
        if (arguments.HasFlag("after-screenshot"))
        {
            include.Add("screenshot");
        }

        var delaySource = arguments.GetOption("after-delay-ms");
        if (explicitAfter is null && include.Count == 0 && delaySource is null)
        {
            return null;
        }
        if (explicitAfter is not null && (include.Count > 0 || delaySource is not null))
        {
            throw new CliUsageException(
                "Use either --after/--after-file or the --after-tree, --after-screenshot, and --after-delay-ms shortcuts.");
        }
        if (explicitAfter is not null)
        {
            return explicitAfter;
        }

        var after = new JsonObject();
        if (include.Count > 0)
        {
            after["include"] = include;
        }
        if (delaySource is not null)
        {
            if (!int.TryParse(delaySource, out var delayMilliseconds)
                || delayMilliseconds is < 0 or > 2_000)
            {
                throw new CliUsageException("--after-delay-ms must be an integer from 0 through 2000.");
            }
            after["delayMilliseconds"] = delayMilliseconds;
        }
        return after;
    }

    private static IReadOnlyList<RuntimeAppToolBatchCall> ReadBatchCalls(CliArguments arguments)
    {
        var source = ReadJsonSource(arguments, "calls", required: true);
        try
        {
            var array = JsonNode.Parse(
                source!,
                documentOptions: new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                }) as JsonArray
                ?? throw new CliUsageException("App tool calls must be a JSON array.");
            if (array.Count is < 1 or > 32)
            {
                throw new CliUsageException("App tool calls must contain between 1 and 32 entries.");
            }

            var calls = new List<RuntimeAppToolBatchCall>(array.Count);
            foreach (var item in array)
            {
                if (item is not JsonObject call)
                {
                    throw new CliUsageException("Every app tool call must be a JSON object.");
                }
                var toolId = call["toolId"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(toolId))
                {
                    throw new CliUsageException("Every app tool call requires toolId.");
                }
                calls.Add(new RuntimeAppToolBatchCall(
                    toolId,
                    call["arguments"] as JsonObject,
                    call["after"] as JsonObject,
                    call["callId"]?.GetValue<string>()));
            }
            return calls;
        }
        catch (JsonException exception)
        {
            throw new CliUsageException($"App tool calls are not valid JSON: {exception.Message}");
        }
    }

    private static JsonObject? ReadJsonObject(
        CliArguments arguments,
        string optionName,
        bool required)
    {
        var source = ReadJsonSource(arguments, optionName, required);
        if (string.IsNullOrWhiteSpace(source))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(
                source,
                documentOptions: new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                }) as JsonObject
                   ?? throw new CliUsageException($"--{optionName} must be a JSON object.");
        }
        catch (JsonException exception)
        {
            throw new CliUsageException($"--{optionName} is not valid JSON: {exception.Message}");
        }
    }

    private static string? ReadJsonSource(
        CliArguments arguments,
        string optionName,
        bool required)
    {
        var inlineJson = arguments.GetOption(optionName);
        var filePath = arguments.GetOption($"{optionName}-file");
        if (inlineJson is not null && filePath is not null)
        {
            throw new CliUsageException($"Use only one of --{optionName} or --{optionName}-file.");
        }

        string? source = inlineJson;
        if (filePath is not null)
        {
            var fullPath = Path.GetFullPath(filePath);
            if (!File.Exists(fullPath))
            {
                throw new CliUsageException($"Arguments file '{fullPath}' was not found.");
            }

            source = File.ReadAllText(fullPath);
        }

        if (string.IsNullOrWhiteSpace(source))
        {
            if (required)
            {
                throw new CliUsageException($"Use --{optionName} or --{optionName}-file.");
            }
            return null;
        }
        return source;
    }

    private static int WriteResponse(
        string operation,
        string sessionId,
        RuntimeAppToolResponse response,
        CliOutput output,
        string? toolId = null)
    {
        var isSuccess = IsSuccessfulResponse(response);
        output.Write(
            new AppToolOutput(
                "ansight.app-tool/v1",
                operation,
                sessionId,
                toolId,
                isSuccess,
                response.Message,
                response.Envelope),
            () => FormatFriendlyResponse(response));
        return isSuccess ? CliExitCodes.Success : CliExitCodes.Failure;
    }

    private static bool IsSuccessfulResponse(RuntimeAppToolResponse response)
    {
        if (!response.Success || response.Envelope is null
            || string.Equals(response.Envelope.Type, "tool.error", StringComparison.Ordinal))
        {
            return false;
        }
        return response.Envelope.Payload?["success"]?.GetValue<bool>() != false;
    }

    private static string FormatFriendlyResponse(RuntimeAppToolResponse response)
    {
        if (response.Envelope?.Payload is not JsonObject payload)
        {
            return response.Message;
        }
        if (string.Equals(response.Envelope.Type, "tool.catalog", StringComparison.Ordinal))
        {
            var revision = payload["revision"]?.GetValue<string>() ?? "unknown";
            if (payload["unchanged"]?.GetValue<bool>() == true)
            {
                return $"Tool catalog is unchanged (revision {revision}).";
            }
            if (payload["tools"] is JsonArray tools)
            {
                var lines = tools.OfType<JsonObject>().Select(tool =>
                {
                    var executionStatus = tool["executable"]?.GetValue<bool>() != false
                        ? "executable"
                        : $"blocked:{tool["denial"]?["code"]?.GetValue<string>() ?? "unavailable"}";
                    return $"{tool["id"]?.GetValue<string>()}\t{tool["policy"]?.GetValue<string>()}"
                           + $"\t{executionStatus}"
                           + $"\t{tool["argumentEncoding"]?.GetValue<string>() ?? "legacy"}"
                           + $"\t{tool["description"]?.GetValue<string>()}";
                });
                return $"Catalog revision {revision} ({tools.Count} tools)"
                       + Environment.NewLine + string.Join(Environment.NewLine, lines);
            }
        }

        var error = payload["error"] as JsonObject;
        if (error is not null)
        {
            var code = error["code"]?.GetValue<string>() ?? "tool_error";
            var message = error["message"]?.GetValue<string>() ?? response.Message;
            return code == "stale_node_reference"
                ? $"{code}: {message}{Environment.NewLine}Re-query ui.query_nodes and retry with the returned reference."
                : $"{code}: {message}";
        }

        if (payload["results"] is JsonArray results)
        {
            var succeeded = results.OfType<JsonObject>().Count(
                result => result["success"]?.GetValue<bool>() == true);
            return $"Batch completed {results.Count} call(s): {succeeded} succeeded, {results.Count - succeeded} failed.";
        }
        return response.Message;
    }

    private static string BuildHelp()
        => """
           Manage applications and invoke tools exposed by a connected app

           Usage:
             ansight app list
             ansight app get <app-id>
             ansight app register <app-id> [--name <name>] [--codebase <path>]
             ansight app remove <app-id>
             ansight app watch add <app-id> [--platform ios|android] [--device-id <id>] [--capture-file <path>]
             ansight app watch list|enable|disable|remove [watch-id]
             ansight app tools <session-id> [catalog filters]
             ansight app call <session-id> <tool-id> [tool and evidence options]
             ansight app batch <session-id> --calls <JSON>|--calls-file <path>
             ansight app execute <session-id> --prompt <text>|--prompt-file <path>
             ansight app interact --session <id> [--jsonl] [--timeout-ms <ms>]
             ansight app execute --app <path-to-.app-or-.apk> --prompt <text>
             ansight app execute --device-id <id> --app-id <id> --prompt <text>
             ansight app execute --device-id <id> --device-id <id> --app-id <id> --prompt <text>
             ansight app push-file <session-id> <local-path> <remote-directory> [options]

           Commands:
             list         List registered apps and apps discovered from session history
             get          Show one app, its codebase metadata, and session counts
             register     Add or update app metadata and its trusted codebase; alias: add
             remove       Remove linked app metadata; alias: delete
             watch        Automatically capture app launches and exits on selected simulators
             tools        Query the authenticated tool catalog of a connected app
             call         Invoke one exact app tool using its published JSON argument schema
             batch        Execute up to 32 app tool calls in order
             execute      Interpret a prompt in a connected or launched app, act, and verify the result
             push-file    Upload a local file through the connected app's file tools

           Tool catalog options:
             --query <text>           Partial match across indexed tool metadata
             --feature <text>         Match a feature or domain hint
             --category <category>    Match one exact tool category
             --id-prefix <prefix>     Match a tool ID prefix
             --tool-id <id>           Match one exact tool ID
             --policy <policy>        Match read, write, or critical
             --executable-only        Exclude unavailable or denied tools
             --include-unavailable    Include unavailable or denied tools
             --max-results <count>    Return 1-50 direct matches; default: 20
             --detail <level>         Return summary metadata or full definitions
             --if-revision <revision> Return unchanged when the revision still matches

           Tool-call options:
             --arguments <JSON>       Inline JSON object passed to the tool
             --arguments-file <path>  Read the JSON object from a file
             --after <JSON>           Explicit post-call evidence request
             --after-file <path>      Read post-call evidence options from a file
             --after-tree             Capture ui.get_visual_tree after the call
             --after-screenshot       Capture ui.get_screenshot after the call
             --after-delay-ms <ms>    Wait 0-2000 ms before evidence capture

           Batch options:
             --calls <JSON>           Inline JSON array of {toolId, arguments, after, callId}
             --calls-file <path>      Read batch calls from a JSON file
             --continue-on-error      Run later calls after a failure

           App execution options:
             --prompt <text>          Natural-language goal to execute and verify
             --prompt-file <path>     Read the complete execution prompt from a file
             --secret <alias>         Grant one host-managed app secret; repeatable
             --reasoning <mode>      Reasoning mode: fast (default), balanced, or deep
             --model-transport <mode>         Model connection: auto (default), websocket, or http
             --team-id <uuid>         Select an organisation when more than one permits this app
             --app-graph              Include authorized published App Graph route guidance
             --trace                  Show task discovery decisions; capture full model, tool, and graph trace
             --app <path>             Install and launch an .app, .ipa, or .apk artifact
             --ipa <path>             .ipa-only alias for --app
             --application-path <path> Generic alias for --app
             --app-id <id>            Launch an installed app or override its artifact ID
             --device-id <id>         Select a launch target; repeat to execute concurrently
                                     iOS audio requires one device and no overlapping runs
             --device <id>            Alias for --device-id
             --platform <name>        Constrain launch to ios or android
             --device-kind <kind>     Constrain launch to simulator, emulator, or physical
             --headless              Do not open simulator/emulator windows; shown by default
             --execution-mode sdk|device  SDK connection (default) or SDK-less simulator/emulator UI
             --close-app-on-completion
                                     Close only the app after success, failure, or cancellation
                                     Leave the device, its window, and host running; opt-in
             --wait-seconds <n>       Session connection timeout after launch; default: 45
             --max-turns <count>      Maximum turns; default: 64
             --max-round-trips <n>    Maximum model round trips; default: 64
             --max-tool-calls <n>     Maximum tool calls; default: 512
             --stop-on-failure        Stop an ordered execution after its first failed instruction

           Push-file options:
             --sandbox-root <root>    Select an app-published sandbox root
             --file-name <name>       Override the destination filename
             --overwrite              Replace an existing destination file
             --no-create-directory    Require the remote directory to already exist

           Live tool calls work best with `ansight host run` active so the SDK session and
           subsequent CLI commands share the same resident runtime.

           App execution uses the hosted organisation service.
           An exact live session ID supports both SDK and external monitor captures.
           --execution-mode device with an installed app reuses a matching active monitor.
           Attached executions reserve the session while running and leave monitoring active;
           --close-app-on-completion explicitly closes the app, including a monitored app.

           A registered codebase is also the app's automatic monitoring registration. After
           each session is finalized, the host evaluates matching ansight/trends definitions
           from that codebase. App versions come from the session profile.

           Finding identifiers:
             ansight app list                  App IDs known from registration or session history
             ansight session list --connected Connected session IDs
             ansight app tools <session-id>    Tool IDs exposed by that connected app

           Examples:
             ansight app tools <session-id> --query "map surface" --policy read --detail summary --json
             ansight app tools <session-id> --tool-id com.example.maps.query_surface_contents --detail full --json
             ansight app call <session-id> ui.query_nodes --arguments '{"selectors":{"automationId":"save"}}'
             ansight app call <session-id> ui.perform_action --arguments-file action.json --after-tree
             ansight app batch <session-id> --calls-file workflow.json --continue-on-error --json
             ansight app execute <session-id> --prompt "Open settings and enable reminders" --json
             ansight app execute --app ./Example.app --prompt "Complete onboarding" --json
             ansight app execute --app ./Example.apk --device-id emulator-5554 --prompt-file goal.txt
             ansight app execute --device-id <id> --app-id com.example.app --prompt "Verify the profile"
             ansight app execute --device-id <ios-id> --device-id <android-id> --app-id com.example.app --prompt "Verify the profile"
             ansight app call <session-id> data.query --arguments-file query.json --json
           """;

}
