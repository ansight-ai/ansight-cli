namespace Ansight.Host.Runtime.WebSocketSessions;

using System.Text.Json.Nodes;
using Ansight.Tools;

internal sealed class AppToolCatalogService
{
    private static readonly TimeSpan initialCaptureTimeout = TimeSpan.FromSeconds(2);
    private readonly SessionConnectionRegistry connections;
    private readonly Func<
        string,
        ToolProtocolEnvelope,
        CancellationToken,
        AppToolBridgeRequestContext?,
        Task<AppToolBridgeResponse>> requestSender;

    internal AppToolCatalogService(
        SessionConnectionRegistry connections,
        Func<
            string,
            ToolProtocolEnvelope,
            CancellationToken,
            AppToolBridgeRequestContext?,
            Task<AppToolBridgeResponse>> requestSender)
    {
        this.connections = connections;
        this.requestSender = requestSender;
    }

    internal async Task<AppToolBridgeResponse> QueryAsync(
        string sessionId,
        JsonObject? filterArguments,
        string? callerRevision,
        CancellationToken cancellationToken,
        AppToolBridgeRequestContext? requestContext,
        bool forceRefresh = false,
        bool includeDefinitions = true)
    {
        // Initial capture is best effort and shares the catalog lock with execution.
        // A missing startup reply must not block an agent behind the 20-second tool timeout.
        using var captureCancellation = requestContext?.Source == "session-capture"
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : null;
        captureCancellation?.CancelAfter(initialCaptureTimeout);
        try
        {
            return await QueryCoreAsync(sessionId, filterArguments, callerRevision,
                captureCancellation?.Token ?? cancellationToken, requestContext, forceRefresh, includeDefinitions).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (captureCancellation?.IsCancellationRequested == true
                                                  && !cancellationToken.IsCancellationRequested)
        {
            return AppToolBridgeResponse.FromFailure("Initial app tool catalog capture timed out; execution can retry discovery.");
        }
    }

    private async Task<AppToolBridgeResponse> QueryCoreAsync(
        string sessionId,
        JsonObject? filterArguments,
        string? callerRevision,
        CancellationToken cancellationToken,
        AppToolBridgeRequestContext? requestContext,
        bool forceRefresh,
        bool includeDefinitions)
    {
        if (!TryGetCatalogCache(sessionId, out var connection, out var cache, out var cacheError))
        {
            return AppToolBridgeResponse.FromFailure(cacheError);
        }

        var observedUpdateVersion = cache!.GetSnapshot().UpdateVersion;
        await connection!.ToolCatalogQueryLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(connections.Get(sessionId.Trim()), connection))
            {
                return AppToolBridgeResponse.FromFailure("The app connection changed during catalog discovery; retry on the current connection.");
            }
            if (forceRefresh)
            {
                connection.InvalidateToolCatalog();
            }
            else if (cache.GetSnapshot().UpdateVersion != observedUpdateVersion
                     && HasRequiredDefinitions(cache, filterArguments, includeDefinitions)
                     && cache.BuildCatalog(filterArguments, includeDefinitions) is { } coalescedCatalog)
            {
                return CreateCoalescedResponse(
                    sessionId,
                    callerRevision,
                    cache.GetSnapshot(),
                    coalescedCatalog);
            }
            else if (cache.TryTakeInitialCaptureReuse()
                     && HasRequiredDefinitions(cache, filterArguments, includeDefinitions)
                     && cache.BuildCatalog(filterArguments, includeDefinitions) is { } initialCatalog)
            {
                return CreateCoalescedResponse(
                    sessionId,
                    callerRevision,
                    cache.GetSnapshot(),
                    initialCatalog);
            }

            var response = await QueryLockedAsync(
                    sessionId,
                    filterArguments,
                    callerRevision,
                    connection,
                    cache!,
                    includeDefinitions,
                    cancellationToken,
                    requestContext)
                .ConfigureAwait(false);
            if (string.Equals(requestContext?.Source, "session-capture", StringComparison.Ordinal)
                && response.Success)
            {
                cache.MarkInitialCaptureReusable();
            }

            return response;
        }
        finally
        {
            connection.ToolCatalogQueryLock.Release();
        }
    }

    private static bool HasRequiredDefinitions(AppToolCatalogCache cache, JsonObject? arguments, bool includeDefinitions)
        => !includeDefinitions || string.Equals(ReadString(arguments?["detail"]), "summary", StringComparison.OrdinalIgnoreCase)
           || cache.GetMissingDefinitionIds(cache.GetSelectedToolIds(arguments)).Count == 0;

    private static AppToolBridgeResponse CreateCoalescedResponse(
        string sessionId,
        string? callerRevision,
        AppToolCatalogCacheSnapshot snapshot,
        JsonObject catalog)
    {
        var payload = !string.IsNullOrWhiteSpace(callerRevision)
                      && string.Equals(callerRevision.Trim(), snapshot.Revision, StringComparison.Ordinal)
            ? new JsonObject
            {
                ["schema"] = "ansight.tool-catalog.v3",
                ["revision"] = snapshot.Revision,
                ["unchanged"] = true
            }
            : catalog;
        return AppToolBridgeResponse.FromSuccess(
            $"Reused the concurrent app tool catalog query for session '{sessionId}'.",
            new ToolProtocolEnvelope
            {
                Type = ToolProtocolMessageTypes.CatalogType,
                Id = CreateToolRequestId("coalesced-query"),
                SessionId = sessionId,
                Capability = ToolProtocolMessageTypes.Capability,
                Payload = payload
            });
    }

    private async Task<AppToolBridgeResponse> QueryLockedAsync(
        string sessionId,
        JsonObject? filterArguments,
        string? callerRevision,
        LiveSessionConnection connection,
        AppToolCatalogCache cache,
        bool includeDefinitions,
        CancellationToken cancellationToken,
        AppToolBridgeRequestContext? requestContext)
    {
        var snapshot = cache.GetSnapshot();
        var indexRequestPayload = new JsonObject
        {
            ["detail"] = "index"
        };
        if (!string.IsNullOrWhiteSpace(snapshot.Revision))
        {
            indexRequestPayload["ifRevision"] = snapshot.Revision;
        }

        if (!string.IsNullOrWhiteSpace(snapshot.AvailabilityRevision))
        {
            indexRequestPayload["ifAvailabilityRevision"] = snapshot.AvailabilityRevision;
        }

        var indexResponse = await SendCatalogQueryAsync(
            sessionId,
            indexRequestPayload,
            cancellationToken,
            requestContext).ConfigureAwait(false);
        if (!indexResponse.Success
            || indexResponse.Envelope is null
            || string.Equals(indexResponse.Envelope.Type, ToolProtocolMessageTypes.ErrorType, StringComparison.Ordinal))
        {
            return indexResponse;
        }

        if (indexResponse.Envelope.Payload is JsonObject indexPayload)
        {
            cache.Update(indexPayload);
        }

        snapshot = cache.GetSnapshot();
        ApplyCachedCatalog(connection, snapshot.IndexCatalog);
        if (!string.IsNullOrWhiteSpace(callerRevision)
            && string.Equals(callerRevision.Trim(), snapshot.Revision, StringComparison.Ordinal))
        {
            return AppToolBridgeResponse.FromSuccess(
                $"The app tool catalog for session '{sessionId}' is unchanged.",
                CreateCachedCatalogEnvelope(
                    indexResponse.Envelope,
                    new JsonObject
                    {
                        ["schema"] = "ansight.tool-catalog.v3",
                        ["revision"] = snapshot.Revision,
                        ["unchanged"] = true
                    }));
        }

        var selectedToolIds = cache.GetSelectedToolIds(filterArguments);
        includeDefinitions &= !string.Equals(
            ReadString(filterArguments?["detail"]),
            "summary",
            StringComparison.OrdinalIgnoreCase);
        var missingDefinitionIds = includeDefinitions
            ? cache.GetMissingDefinitionIds(selectedToolIds)
            : Array.Empty<string>();
        if (missingDefinitionIds.Count > 0)
        {
            var definitionsResponse = await SendCatalogQueryAsync(
                sessionId,
                new JsonObject
                {
                    ["detail"] = "definitions",
                    ["ids"] = new JsonArray(
                        missingDefinitionIds.Select(toolId => (JsonNode?)toolId).ToArray())
                },
                cancellationToken,
                requestContext).ConfigureAwait(false);
            if (!definitionsResponse.Success
                || definitionsResponse.Envelope is null
                || string.Equals(definitionsResponse.Envelope.Type, ToolProtocolMessageTypes.ErrorType, StringComparison.Ordinal))
            {
                return definitionsResponse;
            }

            if (definitionsResponse.Envelope.Payload is JsonObject definitionsPayload)
            {
                cache.Update(definitionsPayload);
            }

            snapshot = cache.GetSnapshot();
            ApplyCachedCatalog(connection, snapshot.IndexCatalog);
        }

        var catalog = cache.BuildCatalog(filterArguments, includeDefinitions);
        if (catalog is null)
        {
            return AppToolBridgeResponse.FromFailure(
                $"The app session '{sessionId}' did not return a usable tool catalog.");
        }

        return AppToolBridgeResponse.FromSuccess(
            $"Received cached tool catalog for app session '{sessionId}'.",
            CreateCachedCatalogEnvelope(indexResponse.Envelope, catalog));
    }

    private Task<AppToolBridgeResponse> SendCatalogQueryAsync(
        string sessionId,
        JsonObject payload,
        CancellationToken cancellationToken,
        AppToolBridgeRequestContext? requestContext)
    {
        var envelope = new ToolProtocolEnvelope
        {
            Type = ToolProtocolMessageTypes.QueryType,
            Id = CreateToolRequestId("query"),
            SessionId = sessionId,
            Capability = ToolProtocolMessageTypes.Capability,
            Payload = payload
        };
        return requestSender(sessionId, envelope, cancellationToken, requestContext);
    }

    private bool TryGetCatalogCache(
        string sessionId,
        out LiveSessionConnection? connection,
        out AppToolCatalogCache? cache,
        out string error)
    {
        connection = null;
        cache = null;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            error = "Session ID is required.";
            return false;
        }

        var normalizedSessionId = sessionId.Trim();
        connection = connections.Get(normalizedSessionId);

        if (connection is null)
        {
            error = $"Session '{normalizedSessionId}' is not currently connected.";
            return false;
        }

        cache = connection.ToolCatalogCache;

        error = string.Empty;
        return true;
    }

    private static void ApplyCachedCatalog(
        LiveSessionConnection connection,
        JsonObject? indexCatalog)
    {
        if (indexCatalog is not null)
        {
            connection.ApplyToolCatalog(indexCatalog.DeepClone().AsObject());
        }
    }

    private static ToolProtocolEnvelope CreateCachedCatalogEnvelope(
        ToolProtocolEnvelope source,
        JsonObject payload)
        => new()
        {
            Type = ToolProtocolMessageTypes.CatalogType,
            Id = source.Id,
            ReplyTo = source.ReplyTo,
            SessionId = source.SessionId,
            SentAt = source.SentAt,
            Capability = source.Capability,
            Payload = payload
        };

    private static string CreateToolRequestId(string kind)
        => $"{kind}-{Guid.CreateVersion7():N}";

    private static string? ReadString(JsonNode? value)
        => value is JsonValue jsonValue
           && jsonValue.TryGetValue<string>(out var text)
           && !string.IsNullOrWhiteSpace(text)
            ? text.Trim()
            : null;
}
