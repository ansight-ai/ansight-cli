namespace Ansight.Host.Runtime.AppTools;

using System.Text.Json.Nodes;
using Ansight.Tools;

internal static class AppToolCallRecovery
{
    internal const string ToolNotFoundErrorCode = "tool_not_found";

    public static async Task<AppToolBridgeResponse> ExecuteAsync(
        IAppToolBridge appToolBridge,
        string sessionId,
        string toolId,
        JsonObject? arguments,
        JsonObject? after,
        bool queryBeforeCall,
        CancellationToken cancellationToken,
        AppToolBridgeRequestContext? requestContext)
    {
        ArgumentNullException.ThrowIfNull(appToolBridge);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolId);

        var normalizedToolId = toolId.Trim();
        var catalogArguments = CreateCatalogArguments(normalizedToolId);
        if (queryBeforeCall)
        {
            var catalogResponse = ValidateCatalogResponse(
                await appToolBridge.QueryToolsFilteredAsync(
                    sessionId,
                    catalogArguments,
                    cancellationToken,
                    requestContext).ConfigureAwait(false),
                normalizedToolId);
            if (!IsCallableCatalog(catalogResponse))
            {
                return catalogResponse;
            }
        }

        var response = await CallWithCatalogAuthorizationAsync(
            appToolBridge,
            sessionId,
            normalizedToolId,
            arguments,
            after,
            catalogArguments,
            cancellationToken,
            requestContext).ConfigureAwait(false);
        if (!RequiresCatalogRecovery(response))
        {
            return response;
        }

        var refreshedCatalogResponse = ValidateCatalogResponse(
            await appToolBridge.RefreshToolsFilteredAsync(
                sessionId,
                catalogArguments,
                cancellationToken,
                requestContext).ConfigureAwait(false),
            normalizedToolId);
        if (!IsCallableCatalog(refreshedCatalogResponse))
        {
            return refreshedCatalogResponse;
        }

        return await CallWithCatalogAuthorizationAsync(
            appToolBridge,
            sessionId,
            normalizedToolId,
            arguments,
            after,
            catalogArguments,
            cancellationToken,
            requestContext).ConfigureAwait(false);
    }

    private static async Task<AppToolBridgeResponse> CallWithCatalogAuthorizationAsync(
        IAppToolBridge appToolBridge,
        string sessionId,
        string toolId,
        JsonObject? arguments,
        JsonObject? after,
        JsonObject catalogArguments,
        CancellationToken cancellationToken,
        AppToolBridgeRequestContext? requestContext)
    {
        var response = await appToolBridge.CallToolWithEvidenceAsync(
            sessionId,
            toolId,
            arguments?.DeepClone().AsObject(),
            after?.DeepClone().AsObject(),
            cancellationToken,
            requestContext).ConfigureAwait(false);
        if (!response.RequiresCatalogQuery)
        {
            return response;
        }

        var catalogResponse = ValidateCatalogResponse(
            await appToolBridge.QueryToolsFilteredAsync(
                sessionId,
                catalogArguments,
                cancellationToken,
                requestContext).ConfigureAwait(false),
            toolId);
        if (!IsCallableCatalog(catalogResponse))
        {
            return catalogResponse;
        }

        return await appToolBridge.CallToolWithEvidenceAsync(
            sessionId,
            toolId,
            arguments?.DeepClone().AsObject(),
            after?.DeepClone().AsObject(),
            cancellationToken,
            requestContext).ConfigureAwait(false);
    }

    internal static bool RequiresCatalogRecovery(AppToolBridgeResponse response)
    {
        if (!response.Success
            || response.Envelope is null
            || !string.Equals(
                response.Envelope.Type,
                ToolProtocolMessageTypes.ErrorType,
                StringComparison.Ordinal)
            || response.Envelope.Payload is not JsonObject payload)
        {
            return false;
        }

        var code = ReadString(payload["code"])
                   ?? ReadString(payload["error"]?["code"]);
        return code is ToolNotFoundErrorCode;
    }

    private static JsonObject CreateCatalogArguments(string toolId)
        => new()
        {
            ["toolId"] = toolId,
            ["detail"] = "full",
            ["executableOnly"] = false,
            ["maxResults"] = 1
        };

    private static AppToolBridgeResponse ValidateCatalogResponse(
        AppToolBridgeResponse response,
        string toolId)
    {
        if (!response.Success || response.Envelope is null)
        {
            return response;
        }

        if (string.Equals(
                response.Envelope.Type,
                ToolProtocolMessageTypes.ErrorType,
                StringComparison.Ordinal))
        {
            return response;
        }

        if (!string.Equals(
                response.Envelope.Type,
                ToolProtocolMessageTypes.CatalogType,
                StringComparison.Ordinal))
        {
            return AppToolBridgeResponse.FromFailure(
                $"The app session returned '{response.Envelope.Type}' instead of an authenticated tool catalog.");
        }

        if (response.Envelope.Payload is not JsonObject payload
            || payload["tools"] is not JsonArray tools)
        {
            return AppToolBridgeResponse.FromFailure(
                "The app session did not return a usable authenticated tool catalog.");
        }

        var tool = tools
            .OfType<JsonObject>()
            .FirstOrDefault(candidate => string.Equals(
                ReadString(candidate["id"]),
                toolId,
                StringComparison.Ordinal));
        if (tool is null)
        {
            return AppToolBridgeResponse.FromUnavailable(
                $"Tool '{toolId}' was not exposed by the authenticated tool catalog.");
        }

        if (ReadBoolean(tool["executable"], fallback: true)
            && tool["denial"] is not JsonObject)
        {
            return response;
        }

        var denialReason = ReadString(tool["denial"]?["reason"])
                           ?? ReadString(tool["runtime"]?["reason"])
                           ?? $"Tool '{toolId}' is not executable in the current session.";
        return AppToolBridgeResponse.FromUnavailable(denialReason);
    }

    private static bool IsCallableCatalog(AppToolBridgeResponse response)
        => response.Success
           && response.Envelope is not null
           && string.Equals(
               response.Envelope.Type,
               ToolProtocolMessageTypes.CatalogType,
               StringComparison.Ordinal);

    private static string? ReadString(JsonNode? value)
        => value is JsonValue jsonValue
           && jsonValue.TryGetValue<string>(out var text)
           && !string.IsNullOrWhiteSpace(text)
            ? text.Trim()
            : null;

    private static bool ReadBoolean(JsonNode? value, bool fallback)
        => value is JsonValue jsonValue && jsonValue.TryGetValue<bool>(out var result)
            ? result
            : fallback;
}
