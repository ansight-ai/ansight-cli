using Ansight.Tools;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class AppToolCatalogDiscoveryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostInitialCaptureReply_ReleasesWaitingCapabilityDiscovery(bool senderReturnsFailure)
    {
        using var socket = new ClientWebSocket();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var connections = CreateConnections(socket);
        var captureStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new AppToolCatalogService(connections, async (sessionId, request, cancellation, context) =>
        {
            if (context?.Source == "session-capture")
            {
                captureStarted.SetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
                }
                catch (OperationCanceledException) when (senderReturnsFailure)
                {
                    return AppToolBridgeResponse.FromFailure("Request cancelled.");
                }
            }

            return CatalogResponse(request, "index", new JsonObject { ["id"] = "maui.visual_tree" });
        });

        var capture = service.QueryAsync("session-1", null, null, timeout.Token,
            new AppToolBridgeRequestContext("session-capture", "capture_app_tool_catalog"));
        await captureStarted.Task.WaitAsync(timeout.Token);
        var discovery = service.QueryAsync("session-1", null, null, timeout.Token,
            requestContext: null, includeDefinitions: false);

        // The old background request held discovery for the normal 20-second tool timeout.
        var response = await discovery.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(response.Success, response.Message);
        Assert.Equal("maui.visual_tree", response.Envelope?.Payload?["tools"]?[0]?["id"]?.GetValue<string>());
        Assert.False((await capture).Success);
    }

    [Fact]
    public async Task InterruptedDefinitionCapture_DoesNotReturnAnIncompleteCatalogToAWaitingFullQuery()
    {
        using var socket = new ClientWebSocket();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var definitionsStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var foregroundDetails = new List<string>();
        var service = new AppToolCatalogService(CreateConnections(socket), async (sessionId, request, cancellation, context) =>
        {
            var detail = request.Payload!["detail"]!.GetValue<string>();
            if (detail == "definitions" && context?.Source == "session-capture")
            {
                definitionsStarted.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
            }
            if (context is null) foregroundDetails.Add(detail);
            var tool = new JsonObject { ["id"] = "app.inspect", ["definitionRevision"] = "definition-1" };
            if (detail == "definitions") tool["argsSchema"] = new JsonObject { ["type"] = "object" };
            return CatalogResponse(request, detail, tool);
        });

        var capture = service.QueryAsync("session-1", null, null, timeout.Token,
            new AppToolBridgeRequestContext("session-capture", "capture_app_tool_catalog"));
        await definitionsStarted.Task.WaitAsync(timeout.Token);
        var query = service.QueryAsync("session-1", null, null, timeout.Token, requestContext: null);

        var response = await query.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False((await capture).Success);
        Assert.True(response.Success, response.Message);
        Assert.Equal(new[] { "index", "definitions" }, foregroundDetails);
        Assert.Equal("object", response.Envelope?.Payload?["tools"]?[0]?["argsSchema"]?["type"]?.GetValue<string>());
    }

    [Fact]
    public async Task CapabilityDiscovery_ReturnsTheEntireIndexWithoutRequestingDefinitions()
    {
        using var socket = new ClientWebSocket();
        var requests = new List<string>();
        var tools = Enumerable.Range(0, 100)
            .Select(index => new JsonObject { ["id"] = $"app.tool-{index}", ["executable"] = index != 99 })
            .ToArray();
        var service = new AppToolCatalogService(CreateConnections(socket), (sessionId, request, cancellation, context) =>
        {
            requests.Add(request.Payload!["detail"]!.GetValue<string>());
            return Task.FromResult(CatalogResponse(request, "index", tools));
        });

        var response = await service.QueryAsync("session-1", null, null, CancellationToken.None,
            requestContext: null, includeDefinitions: false);

        Assert.True(response.Success, response.Message);
        Assert.Equal(new[] { "index" }, requests);
        var catalog = Assert.IsType<JsonArray>(response.Envelope?.Payload?["tools"]);
        Assert.Equal(100, catalog.Count);
        Assert.Equal("app.tool-99", catalog[99]?["id"]?.GetValue<string>());
        Assert.False(catalog[99]?["executable"]?.GetValue<bool>());
    }

    [Fact]
    public async Task CaptureCallerCancellation_PropagatesAndReleasesTheCatalogLock()
    {
        using var socket = new ClientWebSocket();
        using var cancellation = new CancellationTokenSource();
        var captureStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new AppToolCatalogService(CreateConnections(socket), async (sessionId, request, token, context) =>
        {
            if (context?.Source == "session-capture")
            {
                captureStarted.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            return CatalogResponse(request, "index");
        });
        var capture = service.QueryAsync("session-1", null, null, cancellation.Token,
            new AppToolBridgeRequestContext("session-capture", "capture_app_tool_catalog"));
        await captureStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture);
        var response = await service.QueryAsync("session-1", null, null, CancellationToken.None,
            requestContext: null).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(response.Success, response.Message);
    }

    private static SessionConnectionRegistry CreateConnections(WebSocket socket)
    {
        var connections = new SessionConnectionRegistry();
        connections.Register("session-1", new LiveSessionConnection("session-1", socket,
            new PairingSessionAuthorization(2, "grant-1", "read")));
        return connections;
    }

    private static AppToolBridgeResponse CatalogResponse(ToolProtocolEnvelope request, string detail, params JsonObject[] tools)
        => AppToolBridgeResponse.FromSuccess("Catalog returned.", new ToolProtocolEnvelope
        {
            Type = ToolProtocolMessageTypes.CatalogType,
            Id = $"{request.Id}.response",
            ReplyTo = request.Id,
            SessionId = request.SessionId,
            Payload = new JsonObject
            {
                ["schema"] = "ansight.tool-catalog.v3",
                ["revision"] = "catalog-1",
                ["detail"] = detail,
                ["tools"] = new JsonArray(tools.Select(tool => tool.DeepClone()).ToArray())
            }
        });
}
