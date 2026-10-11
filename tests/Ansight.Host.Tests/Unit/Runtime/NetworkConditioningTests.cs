using System.Text.Json.Nodes;
using Ansight.Host.Devices.Network;
using Ansight.Host.Runtime.RepositoryContracts;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class NetworkConditioningTests
{
    private static readonly NetworkConditionTarget target = new("simulator-test", "com.example.test", "session-test");

    [Theory]
    [InlineData("capabilities", "capabilities")]
    [InlineData("get", "get")]
    [InlineData("set", "set")]
    [InlineData("reset", "reset")]
    [InlineData("scenario", "runScenario")]
    public void ToolsExposeNestedDeviceNetworkApi(string action, string method)
    {
        var mapping = RepositoryJavaScriptApiMethods.ResolveHostApiMethod("ansight_network_conditioning_" + action);
        Assert.Equal("device.network", mapping.FeatureName);
        Assert.Equal(method, mapping.MethodName);
    }

    [Theory]
    [InlineData("{\"packetLoss\":0.5}")]
    [InlineData("{\"downloadKbps\":-1}")]
    [InlineData("{\"latencyMs\":10001}")]
    [InlineData("{\"stall\":true,\"blocked\":true}")]
    public void RejectsUnsupportedOrInvalidConditions(string json)
        => Assert.ThrowsAny<Exception>(() => NetworkConditionProfile.Read(JsonNode.Parse(json)));

    [Fact]
    public void SimulatorProcessSelectionIsExactAndUnique()
    {
        const string rows = "123 0 UIKitApplication:com.example.test[one]\n124 0 UIKitApplication:com.example.test.other[two]";
        Assert.Equal(123, MitmNetworkBackend.ParseProcessId(rows, "com.example.test"));
        Assert.Throws<InvalidOperationException>(() => MitmNetworkBackend.ParseProcessId(rows, "com.example"));
        Assert.Throws<InvalidOperationException>(() => MitmNetworkBackend.ParseProcessId(rows + "\n125 0 UIKitApplication:com.example.test[three]", "com.example.test"));
    }

    [Fact]
    public async Task LeasesHaveExclusiveOwnershipAndRestoreOnTaskExit()
    {
        var backend = new FakeBackend();
        var events = new List<string>();
        var manager = Manager(backend, events);
        string id;
        await using (NetworkConditioningScope.Begin("task-a"))
        {
            var lease = await manager.SetAsync(target, new() { DownloadKbps = 100 }, [], [], 5000, null, default);
            id = lease["leaseId"]!.GetValue<string>();
            await Assert.ThrowsAsync<InvalidOperationException>(() => manager.SetAsync(target, new(), [], [], 5000, null, default));
            await using (NetworkConditioningScope.Begin("task-b"))
                await Assert.ThrowsAsync<InvalidOperationException>(() => manager.ResetAsync(id));
            await manager.SetAsync(target, new() { Stall = true }, [], [], 300000, id, default);
            Assert.Equal(lease["expiresAtUtc"]!.ToJsonString(), (await manager.GetAsync(id, default))["expiresAtUtc"]!.ToJsonString());
            Assert.False(backend.Disposed);
        }
        Assert.True(backend.Disposed);
        Assert.Contains("task-ended", events);
        await manager.ResetAsync(id); // idempotent after expiration/task cleanup
        Assert.Null(NetworkConditioningScope.Current);
    }

    [Fact]
    public async Task LeaseExpiresWithoutTaskFinally()
    {
        var backend = new FakeBackend();
        var manager = Manager(backend, []);
        var result = await manager.SetAsync(target, new(), [], [], 1000, null, default);
        await backend.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(4));
        await Assert.ThrowsAsync<ArgumentException>(() => manager.GetAsync(result["leaseId"]!.GetValue<string>(), default));
    }

    [Fact]
    public async Task FailedStartDisposesBackendAndAllowsNextLease()
    {
        var backend = new FakeBackend { Fail = true };
        var manager = Manager(backend, []);
        await Assert.ThrowsAsync<IOException>(() => manager.SetAsync(target, new(), [], [], 5000, null, default));
        Assert.True(backend.Disposed);
        backend.Fail = false;
        await manager.SetAsync(target, new(), [], [], 5000, null, default);
        await manager.StopAsync();
    }

    [Fact]
    public async Task ScopeRunsEveryCleanupEvenWhenOneFails()
    {
        var cleaned = false;
        var scope = NetworkConditioningScope.Begin("task");
        scope.Register(() => { cleaned = true; return Task.CompletedTask; });
        scope.Register(() => throw new IOException("cleanup error"));
        await Assert.ThrowsAsync<AggregateException>(() => scope.DisposeAsync().AsTask());
        Assert.True(cleaned);
    }

    private static NetworkConditioningManager Manager(FakeBackend backend, List<string> events)
        => new(Path.GetTempPath(), (_, name, _) => { lock (events) events.Add(name); },
            (_, _) => Task.FromResult(123), (_, _, expires) => { backend.ExpiresAtUtc = expires; return backend; });

    private sealed class FakeBackend : INetworkConditioningBackend
    {
        public string Name => "test";
        public DateTimeOffset ExpiresAtUtc { get; set; }
        public bool Fail { get; set; }
        public bool Disposed { get; private set; }
        public TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task ApplyAsync(NetworkConditionProfile profile, IReadOnlyList<NetworkConditionStep> steps, CancellationToken token)
        {
            if (Fail) throw new IOException("start failed");
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
        public JsonObject Status() => new() { ["status"] = "ready" };
        public ValueTask DisposeAsync() { Disposed = true; Stopped.TrySetResult(); return ValueTask.CompletedTask; }
    }
}
