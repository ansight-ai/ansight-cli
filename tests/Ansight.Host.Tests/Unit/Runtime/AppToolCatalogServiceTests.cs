using Ansight.Tools;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class AppToolCatalogServiceTests
{
    [Fact]
    public void InvalidateToolCatalog_ClearsCachedDefinitionsAndAuthorization()
    {
        using var socket = new ClientWebSocket();
        var connection = new LiveSessionConnection(
            "session-1",
            socket,
            new PairingSessionAuthorization(2, "grant-1", "read"));
        var catalog = new JsonObject
        {
            ["schema"] = "ansight.tool-catalog.v3",
            ["revision"] = "catalog-1",
            ["availabilityRevision"] = "availability-1",
            ["detail"] = "index",
            ["tools"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = "artifacts.request",
                    ["policy"] = "read"
                }
            }
        };
        connection.ToolCatalogCache.Update(catalog.DeepClone().AsObject());
        connection.ApplyToolCatalog(catalog);
        Assert.True(connection.CanCallTool(
            "artifacts.request",
            out var initialError,
            out var initialRequiresCatalogQuery),
            initialError);
        Assert.False(initialRequiresCatalogQuery);
        Assert.False(connection.CanCallTool(
            "artifacts.new_request",
            out var missingError,
            out var missingRequiresCatalogQuery));
        Assert.True(missingRequiresCatalogQuery);
        Assert.Contains("last authenticated tool catalog", missingError, StringComparison.Ordinal);

        connection.InvalidateToolCatalog();

        var snapshot = connection.ToolCatalogCache.GetSnapshot();
        Assert.Null(snapshot.Revision);
        Assert.Null(snapshot.AvailabilityRevision);
        Assert.Null(snapshot.IndexCatalog);
        Assert.False(connection.CanCallTool(
            "artifacts.request",
            out var invalidatedError,
            out var invalidatedRequiresCatalogQuery));
        Assert.True(invalidatedRequiresCatalogQuery);
        Assert.Contains("Query the authenticated tool catalog", invalidatedError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QueryAsync_ForceRefreshOmitsCachedRevisions()
    {
        var connections = new SessionConnectionRegistry();
        using var socket = new ClientWebSocket();
        var authorization = new PairingSessionAuthorization(2, "grant-1", "read");
        var requests = new List<JsonObject>();
        var responseNumber = 0;
        var service = new AppToolCatalogService(
            connections,
            (sessionId, envelope, cancellationToken, requestContext) =>
            {
                requests.Add(envelope.Payload?.DeepClone().AsObject() ?? new JsonObject());
                responseNumber++;
                return Task.FromResult(AppToolBridgeResponse.FromSuccess(
                    "Catalog returned.",
                    CreateIndexResponse(envelope, sessionId, responseNumber)));
            });
        connections.Register(
            "session-1",
            new LiveSessionConnection("session-1", socket, authorization));

        var initialResponse = await service.QueryAsync(
            "session-1",
            new JsonObject { ["detail"] = "summary" },
            callerRevision: null,
            CancellationToken.None,
            requestContext: null);
        var conditionalResponse = await service.QueryAsync(
            "session-1",
            new JsonObject { ["detail"] = "summary" },
            callerRevision: null,
            CancellationToken.None,
            requestContext: null);
        var refreshedResponse = await service.QueryAsync(
            "session-1",
            new JsonObject { ["detail"] = "summary" },
            callerRevision: null,
            CancellationToken.None,
            requestContext: null,
            forceRefresh: true);

        Assert.True(initialResponse.Success, initialResponse.Message);
        Assert.True(conditionalResponse.Success, conditionalResponse.Message);
        Assert.True(refreshedResponse.Success, refreshedResponse.Message);
        Assert.Equal(3, requests.Count);
        Assert.Null(requests[0]["ifRevision"]);
        Assert.Null(requests[0]["ifAvailabilityRevision"]);
        Assert.Equal("catalog-1", requests[1]["ifRevision"]?.GetValue<string>());
        Assert.Equal("availability-1", requests[1]["ifAvailabilityRevision"]?.GetValue<string>());
        Assert.Null(requests[2]["ifRevision"]);
        Assert.Null(requests[2]["ifAvailabilityRevision"]);
        Assert.Equal("catalog-3", refreshedResponse.Envelope?.Payload?["revision"]?.GetValue<string>());
    }

    [Fact]
    public async Task QueryAsync_AfterConnectionReplacement_DoesNotReuseThePriorConnectionsRevisions()
    {
        var connections = new SessionConnectionRegistry();
        using var firstSocket = new ClientWebSocket();
        using var secondSocket = new ClientWebSocket();
        var authorization = new PairingSessionAuthorization(2, "grant-1", "read");
        var requests = new List<JsonObject>();
        var service = new AppToolCatalogService(
            connections,
            (sessionId, envelope, cancellationToken, requestContext) =>
            {
                requests.Add(envelope.Payload?.DeepClone().AsObject() ?? new JsonObject());
                return Task.FromResult(AppToolBridgeResponse.FromSuccess(
                    "Catalog returned.",
                    new ToolProtocolEnvelope
                    {
                        Type = ToolProtocolMessageTypes.CatalogType,
                        Id = $"{envelope.Id}.response",
                        ReplyTo = envelope.Id,
                        SessionId = sessionId,
                        Payload = new JsonObject
                        {
                            ["schema"] = "ansight.tool-catalog.v3",
                            ["revision"] = "catalog-1",
                            ["availabilityRevision"] = "availability-1",
                            ["detail"] = "index",
                            ["evaluatedAtUtc"] = "2026-09-03T02:07:55.632732Z",
                            ["tools"] = new JsonArray()
                        }
                    }));
            });

        connections.Register(
            "session-1",
            new LiveSessionConnection("session-1", firstSocket, authorization));
        var firstResponse = await service.QueryAsync(
            "session-1",
            new JsonObject { ["detail"] = "summary" },
            callerRevision: null,
            CancellationToken.None,
            requestContext: null);

        connections.Register(
            "session-1",
            new LiveSessionConnection("session-1", secondSocket, authorization));
        var secondResponse = await service.QueryAsync(
            "session-1",
            new JsonObject { ["detail"] = "summary" },
            callerRevision: null,
            CancellationToken.None,
            requestContext: null);

        Assert.True(firstResponse.Success, firstResponse.Message);
        Assert.True(secondResponse.Success, secondResponse.Message);
        Assert.Equal(2, requests.Count);
        Assert.All(requests, request =>
        {
            Assert.Null(request["ifRevision"]);
            Assert.Null(request["ifAvailabilityRevision"]);
        });
    }

    private static ToolProtocolEnvelope CreateIndexResponse(
        ToolProtocolEnvelope request,
        string sessionId,
        int responseNumber)
        => new()
        {
            Type = ToolProtocolMessageTypes.CatalogType,
            Id = $"{request.Id}.response",
            ReplyTo = request.Id,
            SessionId = sessionId,
            Payload = new JsonObject
            {
                ["schema"] = "ansight.tool-catalog.v3",
                ["revision"] = $"catalog-{responseNumber}",
                ["availabilityRevision"] = $"availability-{responseNumber}",
                ["detail"] = "index",
                ["tools"] = new JsonArray()
            }
        };
}
