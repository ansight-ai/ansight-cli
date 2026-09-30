using Ansight.Host.Runtime.Operations;
using Ansight.Host.Tests.TestSupport;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed partial class SessionResolverTests
{
    [Theory]
    [InlineData("ios", "simulator")]
    [InlineData("android", "emulator")]
    public void DeviceSessionIsLiveWithoutSdkAndRevokedAtCompletion(string platform, string kind)
    {
        using var environment = new TestEnvironment();
        var state = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        var target = new WorkspaceTestTarget(platform, "native-1", "Phone", "com.example.app", false, false, true)
        { DeviceKind = kind, ExecutionMode = "device" };
        var id = state.CreateDeviceSession(target);
        var resolver = new SessionResolver(state, new DisconnectedAppToolBridge());
        Assert.True(resolver.TryResolveLiveSession(new JsonObject { ["sessionId"] = id }, out var snapshot, out var error), error);
        Assert.Equal("device", snapshot!.CaptureSource);
        Assert.Equal("native-1", JsonNode.Parse(snapshot.DeviceProfileJson!)!["device"]!["nativeDeviceId"]!.GetValue<string>());
        Assert.True(resolver.TryResolveLiveSession(new JsonObject { ["appId"] = target.ApplicationIdentifier, ["deviceId"] = "native-1" }, out _, out _));

        // A saved capture retains its provenance but never grants device access after reload.
        new SessionCaptureStore(environment.ApplicationPaths).Save(snapshot);
        var reloaded = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        Assert.True(reloaded.TryGetSessionSnapshot(id, out var saved));
        Assert.Equal("device", saved!.CaptureSource);
        Assert.False(new SessionResolver(reloaded, new DisconnectedAppToolBridge())
            .TryResolveLiveSession(new JsonObject { ["sessionId"] = id }, out _, out _));

        state.EndDeviceSession(id);
        Assert.False(resolver.TryResolveLiveSession(new JsonObject { ["sessionId"] = id }, out _, out _));
        Assert.True(state.TryGetSessionSnapshot(id, out var ended));
        Assert.Equal("Completed", ended!.Status);
    }
}
