using Ansight.Host.Tests.TestSupport;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class NetworkInspectionOperationTests
{
    private static readonly DateTimeOffset startedAtUtc = DateTimeOffset.Parse("2026-09-08T00:00:00Z");

    [Fact]
    public async Task Get_CombinesFiltersAndReturnsOnlyBodyMetadata()
    {
        using var fixture = new NetworkFixture();
        fixture.Add(
            Request("match", method: "POST", status: 503, body: TextBody("secret body")),
            Request("wrong-method", method: "GET", status: 503),
            Request("wrong-status", method: "POST", status: 200),
            Request("before-window", method: "POST", status: 503, seconds: -1),
            Request("wrong-host", method: "POST", status: 503, url: "https://other.test/checkout"));
        var result = await fixture.Get(new JsonObject
        {
            ["methods"] = new JsonArray("post", "POST"),
            ["statuses"] = new JsonArray(404, "5xx"),
            ["host"] = "API.EXAMPLE",
            ["query"] = "CHECKOUT",
            ["failedOnly"] = true,
            ["startUtc"] = startedAtUtc.ToString("O"),
            ["endUtc"] = startedAtUtc.ToString("O")
        });

        var payload = Success(result);
        var request = Assert.Single(payload["requests"]!.AsArray())!;
        Assert.Equal("match", request["id"]!.GetValue<string>());
        Assert.False(request.AsObject().ContainsKey("requestHeaders"));
        Assert.False(request["responseBody"]!.AsObject().ContainsKey("data"));
        Assert.Equal(11, request["responseBody"]!["capturedBytes"]!.GetValue<long>());
        Assert.Equal(1, payload["matchedRequestCount"]!.GetValue<int>());
        Assert.Equal("POST", Assert.Single(payload["filters"]!["methods"]!.AsArray())!.GetValue<string>());
        Assert.Equal(404, payload["filters"]!["statuses"]![0]!.GetValue<int>());
        Assert.False(payload["hasMore"]!.GetValue<bool>());
        Assert.Null(payload["nextCursor"]);
    }

    [Fact]
    public async Task Get_UsesStableSnapshotAcrossNewAndBackdatedArrivals()
    {
        using var fixture = new NetworkFixture();
        fixture.Add(Request("a", seconds: 2), Request("b", seconds: 2), Request("old", seconds: 1));
        var first = Success(await fixture.Get(new JsonObject
        {
            ["methods"] = new JsonArray("get"), ["limit"] = 1,
            ["startUtc"] = startedAtUtc.ToString("O"), ["endUtc"] = startedAtUtc.AddSeconds(10).ToString("O")
        }));
        Assert.Equal("a", first["requests"]![0]!["id"]!.GetValue<string>());
        var cursor = first["nextCursor"]!.GetValue<string>();
        fixture.Add(Request("new", seconds: 3), Request("backdated", seconds: 1));
        var second = Success(await fixture.Get(new JsonObject
        {
            ["cursor"] = cursor, ["methods"] = new JsonArray("GET"), ["limit"] = 1
        }));
        Assert.Equal("b", second["requests"]![0]!["id"]!.GetValue<string>());
        Assert.Equal(3, second["matchedRequestCount"]!.GetValue<int>());
        var replay = Success(await fixture.Get(new JsonObject { ["cursor"] = cursor, ["limit"] = 1 }));
        Assert.True(JsonNode.DeepEquals(second, replay));
        var third = Success(await fixture.Get(new JsonObject { ["cursor"] = second["nextCursor"]!.GetValue<string>() }));
        Assert.Equal("old", third["requests"]![0]!["id"]!.GetValue<string>());
        Assert.False(third["isTruncated"]!.GetValue<bool>());
        Assert.Null(third["nextCursor"]);
        var fresh = Success(await fixture.Get());
        Assert.Equal(5, fresh["matchedRequestCount"]!.GetValue<int>());
        Assert.Equal("new", fresh["requests"]![0]!["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task Get_RejectsConflictingForeignMalformedAndExpiredCursors()
    {
        var time = new ManualTimeProvider(startedAtUtc);
        using var fixture = new NetworkFixture(new NetworkRequestSnapshotStore(time));
        fixture.Add(Request("a"), Request("b"));
        var first = Success(await fixture.Get(new JsonObject { ["limit"] = 1, ["methods"] = new JsonArray("GET") }));
        var cursor = first["nextCursor"]!.GetValue<string>();
        Error(await fixture.Get(new JsonObject { ["cursor"] = cursor, ["methods"] = new JsonArray("POST") }), "conflict");
        var otherSession = fixture.State.CreateSession("other.app", "Other", IPAddress.Loopback, null, null);
        Error(await fixture.Get(new JsonObject { ["sessionId"] = otherSession, ["cursor"] = cursor }), "different session");
        Error(await fixture.Get(new JsonObject { ["cursor"] = "broken" }), "malformed");
        time.Advance(NetworkRequestSnapshotStore.SnapshotLifetime);
        Error(await fixture.Get(new JsonObject { ["cursor"] = cursor }), "expired");
    }

    [Fact]
    public async Task Get_EvictsOldSnapshotsAndRetainsRecentCursors()
    {
        using var fixture = new NetworkFixture();
        fixture.Add(Request("a"), Request("b"));
        var cursors = new List<string>();
        for (var index = 0; index <= NetworkRequestSnapshotStore.MaximumSnapshots; index++)
        {
            var page = Success(await fixture.Get(new JsonObject { ["limit"] = 1 }));
            cursors.Add(page["nextCursor"]!.GetValue<string>());
        }

        Error(await fixture.Get(new JsonObject { ["cursor"] = cursors[0] }), "expired");
        Success(await fixture.Get(new JsonObject { ["cursor"] = cursors[^1] }));
    }

    [Theory]
    [InlineData("{\"statuses\":[99]}")]
    [InlineData("{\"statuses\":[600]}")]
    [InlineData("{\"statuses\":[200.5]}")]
    [InlineData("{\"statuses\":[\"200\"]}")]
    [InlineData("{\"statuses\":[\"6xx\"]}")]
    [InlineData("{\"statuses\":[true]}")]
    [InlineData("{\"methods\":[1]}")]
    [InlineData("{\"methods\":\"GET\"}")]
    [InlineData("{\"query\":false}")]
    [InlineData("{\"failedOnly\":\"true\"}")]
    [InlineData("{\"limit\":0}")]
    [InlineData("{\"limit\":1001}")]
    [InlineData("{\"limit\":1.5}")]
    [InlineData("{\"startUtc\":\"yesterday\"}")]
    [InlineData("{\"startUtc\":\"2026-02-30T00:00:00Z\"}")]
    [InlineData("{\"startUtc\":\"2026-09-08T00:00:00Z\",\"endUtc\":\"2026-09-07T00:00:00Z\"}")]
    public async Task Get_RejectsMalformedFilters(string json)
    {
        using var fixture = new NetworkFixture();
        Error(await fixture.Get(JsonNode.Parse(json)!.AsObject()));
    }

    [Fact]
    public async Task Get_FailureSearchIncludesTransportErrorsAndEmptyArraysDoNotRestrict()
    {
        using var fixture = new NetworkFixture();
        fixture.Add(Request("transport", status: null, error: "NetworkTimeout"), Request("http", status: 404), Request("ok"));
        var failed = Success(await fixture.Get(new JsonObject
        {
            ["failedOnly"] = true, ["methods"] = new JsonArray(), ["statuses"] = new JsonArray()
        }));
        Assert.Equal(2, failed["matchedRequestCount"]!.GetValue<int>());
        var search = Success(await fixture.Get(new JsonObject { ["query"] = "networktimeout" }));
        Assert.Equal("transport", Assert.Single(search["requests"]!.AsArray())!["id"]!.GetValue<string>());
        var all = Success(await fixture.Get(new JsonObject { ["failedOnly"] = false }));
        Assert.Equal(3, all["matchedRequestCount"]!.GetValue<int>());
    }

    [Fact]
    public async Task GetRequest_PreservesRepeatedHeadersAndNeverIncludesBodyContent()
    {
        using var fixture = new NetworkFixture();
        fixture.Add(Request("headers", body: TextBody("retained"), headers:
        [
            new SessionNetworkHeader { Name = "X-Value", Value = "first" },
            new SessionNetworkHeader { Name = "X-Value", Value = "second" }
        ]));
        var payload = Success(await fixture.Detail("headers"));
        Assert.False(payload["headersTruncated"]!.GetValue<bool>());
        var request = payload["request"]!;
        Assert.Equal("HTTP/2", request["protocol"]!.GetValue<string>());
        Assert.Equal("OK", request["reasonPhrase"]!.GetValue<string>());
        Assert.Equal(new[] { "first", "second" }, request["responseHeaders"]!.AsArray().Select(header => header!["value"]!.GetValue<string>()));
        Assert.False(request["responseBody"]!.AsObject().ContainsKey("data"));
        Error(await fixture.Detail("missing"), "not found");
        var other = fixture.State.CreateSession("other.app", "Other", IPAddress.Loopback, null, null);
        Error(await fixture.Detail("headers", other), "not found");
    }

    [Fact]
    public async Task GetAndDetail_BoundSerializedPagesAndHeaderListsWithoutAlteringValues()
    {
        using var fixture = new NetworkFixture();
        var url = "https://api.example.test/" + new string('x', 15_000);
        fixture.Add(Enumerable.Range(0, 100).Select(index => Request($"request-{index:D3}", url: url)).ToArray());
        var response = await fixture.Get(new JsonObject { ["limit"] = 1000 });
        var page = Success(response);
        Assert.True(response.Payload!.ToJsonString().Length <= NetworkInspectionPayload.MaximumSerializedCharacters);
        Assert.InRange(page["returnedRequestCount"]!.GetValue<int>(), 1, 99);
        Assert.True(page["hasMore"]!.GetValue<bool>());
        Assert.All(page["requests"]!.AsArray(), item => Assert.Equal(url, item!["url"]!.GetValue<string>()));
        var headers = Enumerable.Range(0, 128).Select(index => new SessionNetworkHeader
        {
            Name = $"X-{index}", Value = new string('\t', 4_000)
        }).ToArray();
        // Non-whitespace values survive capture normalization, including escaped tabs.
        headers = headers.Select(header => new SessionNetworkHeader { Name = header.Name, Value = "a" + header.Value + "b" }).ToArray();
        fixture.Add(Request("large-headers", headers: headers));
        var detailResponse = await fixture.Detail("large-headers");
        var detail = Success(detailResponse);
        Assert.True(detailResponse.Payload!.ToJsonString().Length <= NetworkInspectionPayload.MaximumSerializedCharacters);
        Assert.True(detail["headersTruncated"]!.GetValue<bool>());
        var retained = detail["request"]!["responseHeaders"]!.AsArray();
        Assert.InRange(retained.Count, 1, 127);
        Assert.All(retained, item => Assert.Equal(headers[0].Value, item!["value"]!.GetValue<string>()));
    }

    [Fact]
    public void Get_ExplicitlyRejectsASingleOversizedSummaryAndSnapshotMemoryOverflow()
    {
        var store = new NetworkRequestSnapshotStore();
        var huge = Request("huge", url: new string('x', NetworkInspectionPayload.MaximumSerializedCharacters));
        var single = Snapshot([huge]);
        Assert.Contains("summary", Assert.Throws<ArgumentException>(() => store.Create(single, NetworkInspectionFilters.Read(null))).Message);
        var records = Enumerable.Range(0, 80).Select(index => Request($"r{index}", url: new string('x', 240_000))).ToArray();
        Assert.Contains("memory budget", Assert.Throws<ArgumentException>(() => store.Create(Snapshot(records), NetworkInspectionFilters.Read(null))).Message);
    }

    [Fact]
    public void SnapshotStore_EvictsOnMemoryPressureBeforeTheSnapshotCountLimit()
    {
        var time = new ManualTimeProvider(startedAtUtc);
        var store = new NetworkRequestSnapshotStore(time);
        var requests = Enumerable.Range(0, 24).Select(index => Request($"r{index}", url: new string('x', 240_000))).ToArray();
        var session = Snapshot(requests);
        var first = store.Create(session, NetworkInspectionFilters.Read(null));
        store.Retain(first);
        time.Advance(TimeSpan.FromSeconds(1));
        var second = store.Create(session, NetworkInspectionFilters.Read(null));
        store.Retain(second);
        time.Advance(TimeSpan.FromSeconds(1));
        var third = store.Create(session, NetworkInspectionFilters.Read(null));
        store.Retain(third);

        var exception = Assert.Throws<ArgumentException>(() => store.Resolve(NetworkRequestSnapshotStore.Cursor(first, 1), session.SessionId, out _));
        Assert.Contains("expired", exception.Message);
        Assert.Same(second, store.Resolve(NetworkRequestSnapshotStore.Cursor(second, 1), session.SessionId, out var offset));
        Assert.Equal(1, offset);
        Assert.Same(third, store.Resolve(NetworkRequestSnapshotStore.Cursor(third, 1), session.SessionId, out _));
    }

    [Fact]
    public async Task ReadBody_DistinguishesMissingFromEmptyAndSeparatesCaptureTruncation()
    {
        using var fixture = new NetworkFixture();
        fixture.Add(Request("none"), Request("empty", body: TextBody("")), Request("captured-prefix", body: TextBody("ok", truncated: true, totalBytes: 10)));
        Assert.Null(Success(await fixture.Body("none"))["body"]);
        var empty = Success(await fixture.Body("empty"))["body"]!;
        Assert.Equal("", empty["data"]!.GetValue<string>());
        Assert.Equal(0, empty["returnedByteCount"]!.GetValue<int>());
        Assert.Equal(0, empty["capturedBytes"]!.GetValue<long>());
        Assert.Null(empty["totalBytes"]);
        Assert.False(empty["isTruncated"]!.GetValue<bool>());
        var captured = Success(await fixture.Body("captured-prefix"))["body"]!;
        Assert.True(captured["truncated"]!.GetValue<bool>());
        Assert.False(captured["isTruncated"]!.GetValue<bool>());
        Assert.Equal(10, captured["totalBytes"]!.GetValue<long>());
        Error(await fixture.Body("absent"), "not found");
        Assert.Null(Success(await fixture.Body("captured-prefix", side: "request"))["body"]);
    }

    [Theory]
    [InlineData(1, "a", 1)]
    [InlineData(2, "a", 1)]
    [InlineData(3, "a", 1)]
    [InlineData(4, "a", 1)]
    [InlineData(5, "a😀", 5)]
    [InlineData(6, "a😀z", 6)]
    public async Task ReadBody_PreservesUtf8Boundaries(int maximumBytes, string expected, int returnedBytes)
    {
        using var fixture = new NetworkFixture();
        fixture.Add(Request("unicode", body: TextBody("a😀z")));
        var body = Success(await fixture.Body("unicode", maximumBytes))["body"]!;
        Assert.Equal(expected, body["data"]!.GetValue<string>());
        Assert.Equal(returnedBytes, body["returnedByteCount"]!.GetValue<int>());
        Assert.Equal(6, body["capturedBytes"]!.GetValue<long>());
        Assert.Equal(returnedBytes < 6, body["isTruncated"]!.GetValue<bool>());
        Assert.False(body["truncated"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(65536)]
    public async Task ReadBody_BoundsDecodedBinaryBytesAndBase64Expansion(int maximumBytes)
    {
        using var fixture = new NetworkFixture();
        var data = Enumerable.Range(0, 70_000).Select(index => (byte)(index % 256)).ToArray();
        fixture.Add(Request("binary", body: new SessionNetworkBody
        {
            Encoding = "base64", Data = Convert.ToBase64String(data), CapturedBytes = data.Length, Truncated = false
        }));
        var response = await fixture.Body("binary", maximumBytes);
        var body = Success(response)["body"]!;
        Assert.Equal(data[..maximumBytes], Convert.FromBase64String(body["data"]!.GetValue<string>()));
        Assert.Equal(maximumBytes, body["returnedByteCount"]!.GetValue<int>());
        Assert.Equal(70_000, body["capturedBytes"]!.GetValue<long>());
        Assert.True(body["isTruncated"]!.GetValue<bool>());
        Assert.True(response.Payload!.ToJsonString().Length <= NetworkInspectionPayload.MaximumSerializedCharacters);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task ReadBody_CompleteBinaryPreviewsPreserveBase64Padding(int byteCount)
    {
        using var fixture = new NetworkFixture();
        var data = new byte[] { 0, 255, 20, 15 }[..byteCount];
        fixture.Add(Request("binary", body: new SessionNetworkBody
        {
            Encoding = "base64", Data = Convert.ToBase64String(data), CapturedBytes = data.Length, Truncated = false
        }));
        var body = Success(await fixture.Body("binary"))["body"]!;
        Assert.Equal(Convert.ToBase64String(data), body["data"]!.GetValue<string>());
        Assert.Equal(byteCount, body["returnedByteCount"]!.GetValue<int>());
        Assert.False(body["isTruncated"]!.GetValue<bool>());
    }

    [Fact]
    public async Task ReadBody_EscapedTextFitsBudgetAndDefaultsTo16KiB()
    {
        using var fixture = new NetworkFixture();
        fixture.Add(Request("escaped", body: TextBody(new string('\u0001', 70_000))));
        var response = await fixture.Body("escaped", 65536);
        Assert.Equal(65536, Success(response)["body"]!["returnedByteCount"]!.GetValue<int>());
        Assert.True(response.Payload!.ToJsonString().Length <= NetworkInspectionPayload.MaximumSerializedCharacters);
        Assert.Equal(16384, Success(await fixture.Body("escaped"))["body"]!["returnedByteCount"]!.GetValue<int>());
        Error(await fixture.Body("escaped", 0), "between");
        Error(await fixture.Body("escaped", 65537), "between");
        Error(await fixture.Body("escaped", side: "headers"), "side");
    }

    private static SessionNetworkRequest Request(string id, int seconds = 0, string method = "GET", int? status = 200,
        string? url = null, string? error = null, SessionNetworkBody? body = null, SessionNetworkHeader[]? headers = null)
        => new()
        {
            Id = id,
            Source = "test",
            StartedAtUtc = startedAtUtc.AddSeconds(seconds),
            CompletedAtUtc = startedAtUtc.AddSeconds(seconds).AddMilliseconds(30),
            DurationMilliseconds = 30,
            Method = method,
            Url = url ?? "https://api.example.test/checkout",
            StatusCode = status,
            ErrorType = error,
            ErrorMessage = error,
            Protocol = "HTTP/2",
            ReasonPhrase = "OK",
            RedactSensitiveData = false,
            ResponseHeaders = headers ?? [],
            ResponseBody = body,
            ResponseBodySizeBytes = body?.TotalBytes
        };

    private static SessionNetworkBody TextBody(string text, bool truncated = false, long? totalBytes = null)
        => new()
        {
            Encoding = "utf8", Data = text, CapturedBytes = Encoding.UTF8.GetByteCount(text),
            ContentType = "text/plain", TotalBytes = totalBytes, Truncated = truncated
        };

    private static AppSessionSnapshot Snapshot(SessionNetworkRequest[] requests) => new()
    {
        SessionId = "snapshot", AppId = "test.app", ClientName = "Test", RemoteAddress = "127.0.0.1",
        ConfigId = null, CreatedUtc = startedAtUtc, LastUpdatedUtc = startedAtUtc,
        Status = "Connected", IsHistorical = false, MetricChannels = [], Metrics = [], NetworkRequests = requests
    };

    private static JsonObject Success(RequestResult result)
    {
        Assert.False(result.IsError);
        Assert.False(result.Payload!["isError"]!.GetValue<bool>(), result.Payload.ToJsonString());
        return result.Payload["structuredContent"]!.AsObject();
    }

    private static void Error(RequestResult result, string? fragment = null)
    {
        Assert.True(result.Payload!["isError"]!.GetValue<bool>());
        if (fragment is not null)
        {
            Assert.Contains(fragment, result.Payload["structuredContent"]!["message"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed class NetworkFixture : IDisposable
    {
        private readonly TestEnvironment environment = new();
        private readonly GetNetworkRequestsTool get;
        private readonly GetNetworkRequestTool detail;
        private readonly ReadNetworkBodyTool body;

        public NetworkFixture(NetworkRequestSnapshotStore? snapshots = null)
        {
            State = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
            SessionId = State.CreateSession("test.network", "Network", IPAddress.Loopback, null, null);
            var bridge = new EmptyBridge();
            var services = new OperationServices(State, environment.ApplicationPaths,
                new KnownAppStore(environment.ApplicationPaths), new EmptyPairingService(), new EmptyPairingCache(),
                bridge, null, new SessionResolver(State, bridge));
            get = new GetNetworkRequestsTool(services, snapshots);
            detail = new GetNetworkRequestTool(services);
            body = new ReadNetworkBodyTool(services);
        }

        public RuntimeState State { get; }
        public string SessionId { get; }
        public void Add(params SessionNetworkRequest[] requests) => State.AddSessionNetworkRequests(SessionId, requests);
        public Task<RequestResult> Get(JsonObject? arguments = null)
        {
            arguments ??= new JsonObject();
            if (!arguments.ContainsKey("sessionId"))
            {
                arguments["sessionId"] = SessionId;
            }
            return get.ExecuteAsync(arguments, null);
        }
        public Task<RequestResult> Detail(string requestId, string? sessionId = null)
            => detail.ExecuteAsync(new JsonObject { ["sessionId"] = sessionId ?? SessionId, ["requestId"] = requestId }, null);
        public Task<RequestResult> Body(string requestId, int? maximumBytes = null, string side = "response")
            => body.ExecuteAsync(new JsonObject
            {
                ["sessionId"] = SessionId, ["requestId"] = requestId, ["side"] = side, ["maxBytes"] = maximumBytes
            }, null);
        public void Dispose() => environment.Dispose();
    }

    private sealed class ManualTimeProvider(DateTimeOffset current) : TimeProvider
    {
        private DateTimeOffset currentUtc = current;
        public override DateTimeOffset GetUtcNow() => currentUtc;
        public void Advance(TimeSpan elapsed) => currentUtc += elapsed;
    }

    private sealed class EmptyBridge : IAppToolBridge
    {
        public event EventHandler? ConnectionsChanged { add { } remove { } }
        public IReadOnlyList<string> GetConnectedSessionIds() => [];
        public bool IsSessionConnected(string sessionId) => false;
        public OperationResult ForceDisconnectSession(string sessionId) => OperationResult.Failure("Unsupported.");
        public Task<AppToolBridgeResponse> QueryToolsAsync(string sessionId, CancellationToken cancellationToken, AppToolBridgeRequestContext? requestContext = null)
            => Task.FromResult(AppToolBridgeResponse.FromFailure("Unsupported."));
        public Task<AppToolBridgeResponse> CallToolAsync(string sessionId, string toolId, JsonObject? arguments, CancellationToken cancellationToken, AppToolBridgeRequestContext? requestContext = null)
            => Task.FromResult(AppToolBridgeResponse.FromFailure("Unsupported."));
    }

    private sealed class EmptyPairingService : IPairingConfigService
    {
        public PairingIssueResult Issue(string appName, string appId, PairingConfigDuration duration) => throw new NotSupportedException();
    }

    private sealed class EmptyPairingCache : IPairingConfigCache
    {
        public void Add(PairingConfig config) { }
        public void Add(CachedPairingConfig config) { }
        public IReadOnlyList<CachedPairingConfig> GetSnapshot() => [];
        public CachedPairingConfig? Find(string configId) => null;
        public bool Remove(string configId) => false;
    }
}
