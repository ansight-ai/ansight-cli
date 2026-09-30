using System.Text.Json.Nodes;
using Ansight.Host;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.RemoteApp;

internal abstract class RemoteAppOperation : Operation
{
    protected RemoteAppOperation(OperationServices services)
        : base(services)
    {
    }

    protected async Task<RequestResult> BuildListAppToolsResultAsync(JsonObject? arguments, string? correlationId)
    {
        if (!sessionResolver.TryResolveLiveSession(arguments, out var snapshot, out var resolutionError))
        {
            return ToolError(resolutionError);
        }

        var response = await appToolBridge.QueryToolsFilteredAsync(
            snapshot!.SessionId,
            arguments,
            ToolExecutionCancellation.Current,
            new AppToolBridgeRequestContext("host-operation", "ansight_list_app_tools", correlationId));
        if (!response.Success || response.Envelope is null)
        {
            return RequestResult.ToolResult(
                new JsonObject
                {
                    ["sessionId"] = snapshot.SessionId,
                    ["appId"] = snapshot.AppId,
                    ["message"] = response.Message
                },
                isError: true);
        }

        if (string.Equals(response.Envelope.Type, ToolProtocolMessageTypes.ErrorType, StringComparison.Ordinal))
        {
            return RequestResult.ToolResult(
                new JsonObject
                {
                    ["sessionId"] = snapshot.SessionId,
                    ["appId"] = snapshot.AppId,
                    ["error"] = response.Envelope.Payload?.DeepClone()
                },
                isError: true);
        }

        return RequestResult.ToolResult(
            new JsonObject
            {
                ["sessionId"] = snapshot.SessionId,
                ["appId"] = snapshot.AppId,
                ["clientName"] = snapshot.ClientName,
                ["status"] = snapshot.Status,
                ["catalog"] = response.Envelope.Payload?.DeepClone()
            },
            isError: false);
    }

    protected async Task<RequestResult> BuildCallAppToolResultAsync(JsonObject? arguments, string? correlationId)
        => await BuildCallAppToolResultAsync(arguments, correlationId, "ansight_call_app_tool");

    protected async Task<RequestResult> BuildCallAppToolResultAsync(
        JsonObject? arguments,
        string? correlationId,
        string operationName,
        string requestSource = "host-operation")
    {
        if (!sessionResolver.TryResolveLiveSession(arguments, out var snapshot, out var resolutionError))
        {
            return ToolError(resolutionError);
        }

        var toolId = arguments?["toolId"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(toolId))
        {
            return ToolError("toolId is required.");
        }

        if (!appToolBridge.IsSessionConnected(snapshot!.SessionId))
            return RequestResult.ToolResult(ExecutionCapabilities.Unavailable("app.tool:" + toolId,
                new JsonObject { ["executionMode"] = snapshot.CaptureSource ?? "sdk" }, "No SDK app-tool provider is connected."), isError: true);

        var forwardedArguments = AppToolPayloadNormalizer.ApplyHostToolDefaults(
            toolId,
            arguments?["arguments"] as JsonObject,
            OperationDefaults.DefaultVisualTreeMaxDepth,
            OperationDefaults.DefaultScreenshotQuality,
            OperationDefaults.DefaultScreenshotMaxWidth,
            out var appliedDefaults);
        var after = arguments?["after"] as JsonObject;
        var response = await appToolBridge.CallToolWithCatalogRecoveryAsync(
            snapshot!.SessionId,
            toolId,
            forwardedArguments,
            after,
            ToolExecutionCancellation.Current,
            new AppToolBridgeRequestContext(requestSource, operationName, correlationId));
        if (!response.Success || response.Envelope is null)
        {
            return RequestResult.ToolResult(
                new JsonObject
                {
                    ["sessionId"] = snapshot.SessionId,
                    ["appId"] = snapshot.AppId,
                    ["toolId"] = toolId,
                    ["code"] = response.FailureCode,
                    ["capability"] = "app.tool:" + toolId,
                    ["retryable"] = response.FailureCode is null,
                    ["appliedHostDefaults"] = appliedDefaults?.DeepClone(),
                    ["message"] = response.Message
                },
                isError: true);
        }

        var isError = string.Equals(response.Envelope.Type, ToolProtocolMessageTypes.ErrorType, StringComparison.Ordinal);
        PersistedVisualTreeResult? persistedVisualTree = null;
        if (!isError
            && AppToolPayloadNormalizer.IsVisualTreeTool(toolId)
            && response.Envelope.Payload is JsonObject responsePayload
            && responsePayload["result"] is JsonObject visualTreeResult)
        {
            persistedVisualTree = SessionVisualTreePersistence.Persist(
                runtimeState,
                snapshot,
                toolId,
                visualTreeResult);
        }

        var artifacts = new JsonArray();
        var normalizedPayload = AppToolPayloadNormalizer.NormalizeCallToolPayload(
            snapshot,
            toolId,
            response.Envelope.Payload,
            artifacts);

        var structuredContent = new JsonObject
        {
            ["sessionId"] = snapshot.SessionId,
            ["appId"] = snapshot.AppId,
            ["toolId"] = toolId,
            ["responseType"] = response.Envelope.Type,
            ["payload"] = normalizedPayload
        };
        if (appliedDefaults is not null)
        {
            structuredContent["appliedHostDefaults"] = appliedDefaults;
        }

        if (artifacts.Count > 0)
        {
            structuredContent["artifacts"] = artifacts;
        }

        if (persistedVisualTree is not null)
        {
            structuredContent["persistedVisualTree"] = new JsonObject
            {
                ["persisted"] = persistedVisualTree.IsSuccess,
                ["snapshotId"] = persistedVisualTree.Snapshot?.SnapshotId,
                ["treeHash"] = persistedVisualTree.Snapshot?.TreeHash,
                ["message"] = persistedVisualTree.Message
            };
        }

        return RequestResult.ToolResult(
            structuredContent,
            isError: isError);
    }

    protected async Task<RequestResult> BuildCallAppToolsResultAsync(JsonObject? arguments, string? correlationId)
    {
        if (!sessionResolver.TryResolveLiveSession(arguments, out var snapshot, out var resolutionError))
        {
            return ToolError(resolutionError);
        }

        if (arguments?["calls"] is not JsonArray calls || calls.Count is < 1 or > 32)
        {
            return ToolError("calls must contain between 1 and 32 call objects.");
        }

        var batchCalls = new List<AppToolBatchCall>(calls.Count);
        foreach (var call in calls.OfType<JsonObject>())
        {
            var toolId = call["toolId"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(toolId))
            {
                return ToolError("Every batched call requires toolId.");
            }

            batchCalls.Add(new AppToolBatchCall(
                toolId,
                call["arguments"] as JsonObject,
                call["after"] as JsonObject,
                call["callId"]?.GetValue<string>()));
        }

        if (batchCalls.Count != calls.Count)
        {
            return ToolError("Every batched call must be a JSON object.");
        }

        var response = await appToolBridge.CallToolsAsync(
            snapshot!.SessionId,
            batchCalls,
            arguments?["continueOnError"]?.GetValue<bool>() ?? false,
            ToolExecutionCancellation.Current,
            new AppToolBridgeRequestContext("host-operation", "ansight_call_app_tools", correlationId));
        if (!response.Success || response.Envelope is null)
        {
            return RequestResult.ToolResult(new JsonObject
            {
                ["sessionId"] = snapshot.SessionId,
                ["appId"] = snapshot.AppId,
                ["message"] = response.Message
            }, isError: true);
        }

        var artifacts = new JsonArray();
        var normalizedPayload = AppToolPayloadNormalizer.NormalizeBatchPayload(
            snapshot,
            response.Envelope.Payload,
            artifacts);
        var structuredContent = new JsonObject
        {
            ["sessionId"] = snapshot.SessionId,
            ["appId"] = snapshot.AppId,
            ["responseType"] = response.Envelope.Type,
            ["payload"] = normalizedPayload
        };
        if (artifacts.Count > 0)
        {
            structuredContent["artifacts"] = artifacts;
        }

        var isError = string.Equals(response.Envelope.Type, ToolProtocolMessageTypes.ErrorType, StringComparison.Ordinal)
                      || normalizedPayload?["success"]?.GetValue<bool>() == false;
        return RequestResult.ToolResult(structuredContent, isError);
    }

    protected async Task<RequestResult> BuildPushFileToAppResultAsync(JsonObject? arguments, string? correlationId)
    {
        if (!sessionResolver.TryResolveLiveSession(arguments, out var snapshot, out var resolutionError))
        {
            return ToolError(resolutionError);
        }

        AppFilePushRequest request;
        JsonObject forwardedArguments;
        try
        {
            request = AppFilePush.CreateRequest(arguments);
            forwardedArguments = await AppFilePush.CreateRemoteArgumentsAsync(
                request,
                ToolExecutionCancellation.Current);
        }
        catch (Exception exception)
        {
            return RequestResult.ToolResult(
                new JsonObject
                {
                    ["sessionId"] = snapshot!.SessionId,
                    ["appId"] = snapshot.AppId,
                    ["toolId"] = AppFilePush.ToolId,
                    ["message"] = exception.Message
                },
                isError: true);
        }

        var response = await appToolBridge.CallToolWithCatalogRecoveryAsync(
            snapshot!.SessionId,
            AppFilePush.ToolId,
            forwardedArguments,
            after: null,
            ToolExecutionCancellation.Current,
            new AppToolBridgeRequestContext("host-operation", "ansight_push_file_to_app", correlationId));
        if (!response.Success || response.Envelope is null)
        {
            return RequestResult.ToolResult(
                new JsonObject
                {
                    ["sessionId"] = snapshot.SessionId,
                    ["appId"] = snapshot.AppId,
                    ["toolId"] = AppFilePush.ToolId,
                    ["localFilePath"] = request.LocalFilePath,
                    ["message"] = response.Message
                },
                isError: true);
        }

        var isError = string.Equals(response.Envelope.Type, ToolProtocolMessageTypes.ErrorType, StringComparison.Ordinal);
        var artifacts = new JsonArray();
        var normalizedPayload = AppToolPayloadNormalizer.NormalizeCallToolPayload(
            snapshot,
            AppFilePush.ToolId,
            response.Envelope.Payload,
            artifacts);

        var structuredContent = new JsonObject
        {
            ["sessionId"] = snapshot.SessionId,
            ["appId"] = snapshot.AppId,
            ["clientName"] = snapshot.ClientName,
            ["toolId"] = AppFilePush.ToolId,
            ["localFilePath"] = request.LocalFilePath,
            ["destinationDirectoryPath"] = request.DirectoryPath,
            ["destinationFileName"] = forwardedArguments["fileName"]?.GetValue<string>(),
            ["responseType"] = response.Envelope.Type,
            ["payload"] = normalizedPayload,
            ["message"] = isError
                ? "File push failed."
                : $"Pushed '{Path.GetFileName(request.LocalFilePath)}' into the app sandbox."
        };

        if (!string.IsNullOrWhiteSpace(request.SandboxRoot))
        {
            structuredContent["root"] = request.SandboxRoot;
        }

        if (artifacts.Count > 0)
        {
            structuredContent["artifacts"] = artifacts;
        }

        return RequestResult.ToolResult(
            structuredContent,
            isError: isError);
    }
}
