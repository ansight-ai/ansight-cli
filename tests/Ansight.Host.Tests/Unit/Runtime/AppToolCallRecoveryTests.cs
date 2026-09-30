using Ansight.Tools;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class AppToolCallRecoveryTests
{
    [Fact]
    public async Task ExecuteAsync_RefreshesAndRetriesOnceForToolNotFound()
    {
        var bridge = new RecordingBridge(
            InitialCatalog(),
            InitialCatalog(),
            ToolError(AppToolCallRecovery.ToolNotFoundErrorCode),
            ToolResult());
        var requestContext = new AppToolBridgeRequestContext(
            "automation",
            "capture-artifact",
            "correlation-1");

        var response = await ((IAppToolBridge)bridge).CallToolWithCatalogRecoveryAsync(
            "session-1",
            "artifacts.request",
            new JsonObject { ["artifactId"] = "uld_asset" },
            after: null,
            CancellationToken.None,
            requestContext);

        Assert.True(response.Success, response.Message);
        Assert.Equal(ToolProtocolMessageTypes.ResultType, response.Envelope?.Type);
        Assert.Equal(["call", "refresh", "call"], bridge.Operations);
        Assert.Equal(2, bridge.CallCount);
        Assert.All(bridge.RequestContexts, context => Assert.Same(requestContext, context));
    }

    [Fact]
    public async Task ExecuteAsync_QueriesAndRetriesWhenHostRequiresCatalog()
    {
        var bridge = new RecordingBridge(
            InitialCatalog(),
            InitialCatalog(),
            AppToolBridgeResponse.FromFailure(
                "Query the authenticated tool catalog first.",
                requiresCatalogQuery: true),
            ToolResult());

        var response = await ((IAppToolBridge)bridge).CallToolWithCatalogRecoveryAsync(
            "session-1",
            "artifacts.request",
            new JsonObject(),
            after: null,
            CancellationToken.None);

        Assert.True(response.Success, response.Message);
        Assert.Equal(ToolProtocolMessageTypes.ResultType, response.Envelope?.Type);
        Assert.Equal(["call", "query", "call"], bridge.Operations);
        Assert.Equal(2, bridge.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_DoesNotRetryAnOrdinaryToolFailure()
    {
        var bridge = new RecordingBridge(
            InitialCatalog(),
            InitialCatalog(),
            ToolError("artifact_not_ready"),
            ToolResult());

        var response = await ((IAppToolBridge)bridge).CallToolWithCatalogRecoveryAsync(
            "session-1",
            "artifacts.request",
            new JsonObject(),
            after: null,
            CancellationToken.None);

        Assert.True(response.Success, response.Message);
        Assert.Equal(ToolProtocolMessageTypes.ErrorType, response.Envelope?.Type);
        Assert.Equal(["call"], bridge.Operations);
        Assert.Equal(1, bridge.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_DoesNotRetryAnAmbiguousTransportFailure()
    {
        var bridge = new RecordingBridge(
            InitialCatalog(),
            InitialCatalog(),
            AppToolBridgeResponse.FromFailure("Timed out waiting for the app response."),
            ToolResult());

        var response = await ((IAppToolBridge)bridge).CallToolWithCatalogRecoveryAsync(
            "session-1",
            "artifacts.request",
            new JsonObject(),
            after: null,
            CancellationToken.None);

        Assert.False(response.Success);
        Assert.Contains("Timed out", response.Message, StringComparison.Ordinal);
        Assert.Equal(["call"], bridge.Operations);
        Assert.Equal(1, bridge.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_DoesNotRetryWhenRefreshedCatalogNoLongerExposesTool()
    {
        var bridge = new RecordingBridge(
            InitialCatalog(),
            EmptyCatalog(),
            ToolError(AppToolCallRecovery.ToolNotFoundErrorCode),
            ToolResult());

        var response = await ((IAppToolBridge)bridge).CallToolWithCatalogRecoveryAsync(
            "session-1",
            "artifacts.request",
            new JsonObject(),
            after: null,
            CancellationToken.None);

        Assert.False(response.Success);
        Assert.Contains("not exposed", response.Message, StringComparison.Ordinal);
        Assert.Equal(["call", "refresh"], bridge.Operations);
        Assert.Equal(1, bridge.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_StopsAfterOneRetry()
    {
        var bridge = new RecordingBridge(
            InitialCatalog(),
            InitialCatalog(),
            ToolError(AppToolCallRecovery.ToolNotFoundErrorCode),
            ToolError(AppToolCallRecovery.ToolNotFoundErrorCode));

        var response = await ((IAppToolBridge)bridge).CallToolWithCatalogRecoveryAsync(
            "session-1",
            "artifacts.request",
            new JsonObject(),
            after: null,
            CancellationToken.None);

        Assert.True(response.Success, response.Message);
        Assert.Equal(ToolProtocolMessageTypes.ErrorType, response.Envelope?.Type);
        Assert.Equal(["call", "refresh", "call"], bridge.Operations);
        Assert.Equal(2, bridge.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_ReauthorizesWhenAnotherRefreshRacesTheRetry()
    {
        var bridge = new RecordingBridge(
            InitialCatalog(),
            InitialCatalog(),
            ToolError(AppToolCallRecovery.ToolNotFoundErrorCode),
            AppToolBridgeResponse.FromFailure(
                "Query the authenticated tool catalog first.",
                requiresCatalogQuery: true),
            ToolResult());

        var response = await ((IAppToolBridge)bridge).CallToolWithCatalogRecoveryAsync(
            "session-1",
            "artifacts.request",
            new JsonObject(),
            after: null,
            CancellationToken.None);

        Assert.True(response.Success, response.Message);
        Assert.Equal(ToolProtocolMessageTypes.ResultType, response.Envelope?.Type);
        Assert.Equal(["call", "refresh", "call", "query", "call"], bridge.Operations);
        Assert.Equal(3, bridge.CallCount);
    }

    private static AppToolBridgeResponse InitialCatalog()
        => AppToolBridgeResponse.FromSuccess(
            "Catalog loaded.",
            new ToolProtocolEnvelope
            {
                Type = ToolProtocolMessageTypes.CatalogType,
                Id = "catalog-response",
                SessionId = "session-1",
                Payload = new JsonObject
                {
                    ["tools"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["id"] = "artifacts.request",
                            ["policy"] = "read",
                            ["executable"] = true
                        }
                    }
                }
            });

    private static AppToolBridgeResponse EmptyCatalog()
        => AppToolBridgeResponse.FromSuccess(
            "Catalog loaded.",
            new ToolProtocolEnvelope
            {
                Type = ToolProtocolMessageTypes.CatalogType,
                Id = "empty-catalog-response",
                SessionId = "session-1",
                Payload = new JsonObject { ["tools"] = new JsonArray() }
            });

    private static AppToolBridgeResponse ToolError(string code)
        => AppToolBridgeResponse.FromSuccess(
            "Tool error returned.",
            new ToolProtocolEnvelope
            {
                Type = ToolProtocolMessageTypes.ErrorType,
                Id = $"{code}-response",
                SessionId = "session-1",
                Payload = new JsonObject
                {
                    ["code"] = code,
                    ["message"] = "The tool could not be resolved.",
                    ["retryable"] = true
                }
            });

    private static AppToolBridgeResponse ToolResult()
        => AppToolBridgeResponse.FromSuccess(
            "Tool result returned.",
            new ToolProtocolEnvelope
            {
                Type = ToolProtocolMessageTypes.ResultType,
                Id = "result-response",
                SessionId = "session-1",
                Payload = new JsonObject
                {
                    ["toolId"] = "artifacts.request",
                    ["success"] = true,
                    ["result"] = new JsonObject()
                }
            });

    private sealed class RecordingBridge(
        AppToolBridgeResponse initialCatalog,
        AppToolBridgeResponse refreshedCatalog,
        params AppToolBridgeResponse[] callResponses) : IAppToolBridge
    {
        private int callIndex;

        public event EventHandler? ConnectionsChanged
        {
            add { }
            remove { }
        }

        public List<string> Operations { get; } = [];

        public List<AppToolBridgeRequestContext?> RequestContexts { get; } = [];

        public int CallCount => callIndex;

        public IReadOnlyList<string> GetConnectedSessionIds() => ["session-1"];

        public bool IsSessionConnected(string sessionId) => true;

        public OperationResult ForceDisconnectSession(string sessionId)
            => OperationResult.Success("Disconnected.");

        public Task<AppToolBridgeResponse> QueryToolsAsync(
            string sessionId,
            CancellationToken cancellationToken,
            AppToolBridgeRequestContext? requestContext = null)
            => QueryToolsFilteredAsync(sessionId, arguments: null, cancellationToken, requestContext);

        public Task<AppToolBridgeResponse> QueryToolsFilteredAsync(
            string sessionId,
            JsonObject? arguments,
            CancellationToken cancellationToken,
            AppToolBridgeRequestContext? requestContext = null)
        {
            Operations.Add("query");
            RequestContexts.Add(requestContext);
            return Task.FromResult(initialCatalog);
        }

        public Task<AppToolBridgeResponse> RefreshToolsFilteredAsync(
            string sessionId,
            JsonObject? arguments,
            CancellationToken cancellationToken,
            AppToolBridgeRequestContext? requestContext = null)
        {
            Operations.Add("refresh");
            RequestContexts.Add(requestContext);
            return Task.FromResult(refreshedCatalog);
        }

        public Task<AppToolBridgeResponse> CallToolAsync(
            string sessionId,
            string toolId,
            JsonObject? arguments,
            CancellationToken cancellationToken,
            AppToolBridgeRequestContext? requestContext = null)
        {
            Operations.Add("call");
            RequestContexts.Add(requestContext);
            var responseIndex = Math.Min(callIndex, callResponses.Length - 1);
            callIndex++;
            return Task.FromResult(callResponses[responseIndex]);
        }
    }
}
