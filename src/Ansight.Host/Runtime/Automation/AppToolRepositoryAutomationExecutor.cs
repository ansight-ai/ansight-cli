using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Automation;

internal sealed class AppToolRepositoryAutomationExecutor : IRepositoryAutomationExecutor
{
    private readonly IAppToolBridge appToolBridge;

    public AppToolRepositoryAutomationExecutor(IAppToolBridge appToolBridge)
    {
        this.appToolBridge = appToolBridge ?? throw new ArgumentNullException(nameof(appToolBridge));
    }

    public async Task<RepositoryAutomationExecutionResult> ExecuteAsync(
        RepositoryAutomationExecutionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var startedAtUtc = DateTimeOffset.UtcNow;
        var toolId = request.Trigger.Automation.AppToolId
                     ?? throw new InvalidOperationException("The app-tool automation does not declare a tool id.");
        using var timeoutCts = new CancellationTokenSource(request.Trigger.Automation.ActionTimeout);
        using var executionCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCts.Token);

        return await ExecuteActionAsync(
            request,
            toolId,
            request.Trigger.Automation.AppToolArguments,
            startedAtUtc,
            executionCts.Token,
            cancellationToken).ConfigureAwait(false);
    }

    internal async Task<RepositoryAutomationExecutionResult> ExecuteActionAsync(
        RepositoryAutomationExecutionRequest request,
        string toolId,
        JsonObject? arguments,
        DateTimeOffset startedAtUtc,
        CancellationToken executionToken,
        CancellationToken hostCancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolId);
        var sessionId = request.Event.SessionId;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return Complete(
                AutomationRunStatus.Rejected,
                startedAtUtc,
                "The matched event does not identify a live app session.",
                output: null);
        }

        if (!appToolBridge.IsSessionConnected(sessionId))
        {
            var unavailable = ExecutionCapabilities.Unavailable("app.tool:" + toolId, null, "No SDK app-tool provider is connected.");
            return Complete(AutomationRunStatus.Rejected, startedAtUtc, unavailable["message"]!.GetValue<string>(), unavailable);
        }
        var catalog = await appToolBridge.QueryToolsFilteredAsync(sessionId,
            new JsonObject { ["toolId"] = toolId, ["executableOnly"] = false, ["detail"] = "full", ["maxResults"] = 1 },
            executionToken).ConfigureAwait(false);
        if (!catalog.Success || catalog.Envelope?.Type != ToolProtocolMessageTypes.CatalogType
            || catalog.Envelope.Payload?["tools"] is not JsonArray tools)
            return Complete(AutomationRunStatus.Failed, startedAtUtc, catalog.Message, null);
        if (!tools.OfType<JsonObject>().Any(tool => tool["id"]?.GetValue<string>() == toolId
                && tool["executable"]?.GetValue<bool>() != false && tool["denial"] is null))
        {
            var unavailable = ExecutionCapabilities.Unavailable("app.tool:" + toolId, null, "The app does not advertise this executable tool.");
            return Complete(AutomationRunStatus.Rejected, startedAtUtc, unavailable["message"]!.GetValue<string>(), unavailable);
        }
        AppToolBridgeResponse response;
        var requestContext = new AppToolBridgeRequestContext(
            "automation",
            request.Trigger.Automation.AutomationId,
            request.Event.CorrelationId);
        try
        {
            response = await appToolBridge.CallToolWithCatalogRecoveryAsync(
                sessionId,
                toolId,
                arguments?.DeepClone() as JsonObject,
                after: null,
                executionToken,
                requestContext).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Cancelled(request, startedAtUtc, hostCancellationToken.IsCancellationRequested);
        }

        if (executionToken.IsCancellationRequested)
        {
            return Cancelled(request, startedAtUtc, hostCancellationToken.IsCancellationRequested);
        }

        if (!response.Success || response.Envelope is null)
        {
            return Complete(
                AutomationRunStatus.Failed,
                startedAtUtc,
                response.Message,
                CreateOutput(sessionId, toolId, response.Envelope));
        }

        var isError = string.Equals(
            response.Envelope.Type,
            ToolProtocolMessageTypes.ErrorType,
            StringComparison.Ordinal);
        return Complete(
            isError ? AutomationRunStatus.Failed : AutomationRunStatus.Succeeded,
            startedAtUtc,
            isError ? ReadError(response.Envelope.Payload) : "App tool automation completed.",
            CreateOutput(sessionId, toolId, response.Envelope));
    }

    private static RepositoryAutomationExecutionResult Cancelled(
        RepositoryAutomationExecutionRequest request,
        DateTimeOffset startedAtUtc,
        bool hostCancellationRequested)
    {
        var status = hostCancellationRequested
            ? AutomationRunStatus.Cancelled
            : AutomationRunStatus.TimedOut;
        var message = hostCancellationRequested
            ? "App tool automation execution was cancelled."
            : $"App tool automation exceeded its {request.Trigger.Automation.ActionTimeout.TotalSeconds:0}-second timeout.";
        return Complete(status, startedAtUtc, message, output: null);
    }

    private static JsonObject CreateOutput(
        string sessionId,
        string toolId,
        ToolProtocolEnvelope? envelope)
    {
        return new JsonObject
        {
            ["sessionId"] = sessionId,
            ["toolId"] = toolId,
            ["responseType"] = envelope?.Type,
            ["payload"] = envelope?.Payload?.DeepClone()
        };
    }

    private static string ReadError(JsonNode? payload)
    {
        if (payload is JsonObject payloadObject)
        {
            if (payloadObject["error"] is JsonObject errorObject
                && errorObject["message"]?.GetValue<string>() is { Length: > 0 } nestedMessage)
            {
                return nestedMessage;
            }

            if (payloadObject["message"]?.GetValue<string>() is { Length: > 0 } message)
            {
                return message;
            }
        }

        return "The app tool returned an error.";
    }

    private static RepositoryAutomationExecutionResult Complete(
        AutomationRunStatus status,
        DateTimeOffset startedAtUtc,
        string message,
        JsonObject? output)
        => new(
            status,
            startedAtUtc,
            DateTimeOffset.UtcNow,
            ExitCode: null,
            message,
            output,
            StandardError: string.Empty);
}
