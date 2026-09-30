namespace Ansight.Host.Runtime.WebSocketSessions;

using System.Text.Json.Nodes;
using Ansight.Host;
using Ansight.Host.Runtime.AppTools;
using Ansight.Tools;

internal sealed partial class WebSocketSessionManager
{
    private const string ArtifactQueryToolId = "artifacts.query";

    private async Task CaptureSessionAppToolCatalogAsync(string sessionId)
    {
        try
        {
            var catalogResponse = await toolCatalog.QueryAsync(
                    sessionId,
                    filterArguments: null,
                    callerRevision: null,
                    CancellationToken.None,
                    new AppToolBridgeRequestContext("session-capture", "capture_app_tool_catalog"))
                .ConfigureAwait(false);
            if (!catalogResponse.Success
                || catalogResponse.Envelope?.Payload is not JsonObject toolCatalogPayload)
            {
                log.Info(
                    $"session_app_tool_catalog_capture_skipped sessionId={sessionId} message={catalogResponse.Message}");
                return;
            }

            JsonObject? artifactCatalog = null;
            if (HasExecutableTool(toolCatalogPayload, ArtifactQueryToolId))
            {
                var artifactResponse = await CallToolAsync(
                        sessionId,
                        ArtifactQueryToolId,
                        new JsonObject(),
                        CancellationToken.None,
                        new AppToolBridgeRequestContext("session-capture", "capture_artifact_provider_catalog"))
                    .ConfigureAwait(false);
                if (artifactResponse.Success
                    && artifactResponse.Envelope is not null
                    && !string.Equals(
                        artifactResponse.Envelope.Type,
                        ToolProtocolMessageTypes.ErrorType,
                        StringComparison.Ordinal)
                    && artifactResponse.Envelope.Payload is JsonObject artifactPayload)
                {
                    artifactCatalog = artifactPayload.DeepClone().AsObject();
                }
            }

            runtimeState.SetSessionAppToolCatalog(
                sessionId,
                new SessionAppToolCatalogSnapshot(
                    "ansight.session-app-tool-catalog/v1",
                    DateTimeOffset.UtcNow,
                    toolCatalogPayload.DeepClone().AsObject(),
                    artifactCatalog));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            log.Info(
                $"session_app_tool_catalog_capture_failed sessionId={sessionId} message={exception.GetBaseException().Message}");
        }
    }

    private void CaptureQueriedAppToolCatalog(string sessionId, AppToolBridgeResponse response)
    {
        if (!response.Success
            || response.Envelope?.Payload is not JsonObject toolCatalogPayload
            || toolCatalogPayload["tools"] is not JsonArray)
        {
            return;
        }

        JsonObject? artifactCatalog = null;
        if (runtimeState.TryGetSessionSnapshot(sessionId, out var snapshot))
        {
            artifactCatalog = snapshot?.AppToolCatalog?.ArtifactCatalog?.DeepClone().AsObject();
        }

        runtimeState.SetSessionAppToolCatalog(
            sessionId,
            new SessionAppToolCatalogSnapshot(
                "ansight.session-app-tool-catalog/v1",
                DateTimeOffset.UtcNow,
                toolCatalogPayload.DeepClone().AsObject(),
                artifactCatalog));
    }

    private static bool HasExecutableTool(JsonObject catalog, string toolId)
        => catalog["tools"] is JsonArray tools
           && tools.OfType<JsonObject>().Any(tool =>
               string.Equals(ReadCatalogString(tool["id"]), toolId, StringComparison.Ordinal)
               && ReadCatalogBoolean(tool["executable"], fallback: true));

    private static string? ReadCatalogString(JsonNode? node)
        => node is JsonValue value
           && value.TryGetValue<string>(out var text)
           && !string.IsNullOrWhiteSpace(text)
            ? text.Trim()
            : null;

    private static bool ReadCatalogBoolean(JsonNode? node, bool fallback)
        => node is JsonValue value && value.TryGetValue<bool>(out var result)
            ? result
            : fallback;
}
