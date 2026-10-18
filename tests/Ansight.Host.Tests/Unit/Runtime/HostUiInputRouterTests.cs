using Ansight.Host.Runtime.Operations;
using Ansight.Host.Tests.TestSupport;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class HostUiInputRouterTests
{
    [Fact]
    public async Task RecordingRetainsFailedAndInterruptedDeliveryWithoutBackendMessages()
    {
        using var environment = new TestEnvironment();
        var state = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        var sessionId = state.CreateSession("test.app", "Input", IPAddress.Loopback, null, null);
        var router = new UiInputRouter();
        router.ConfigureRecording(state);
        using (router.BeginEvidenceScope("outer"))
        {
            using (router.BeginEvidenceScope("inner"))
                await router.RecordInputAsync(sessionId, "tap", () => Task.FromResult(
                    UiInputResult.Failure("Private backend text")), .5, .5);
            await Assert.ThrowsAsync<OperationCanceledException>(() => router.RecordInputAsync(sessionId,
                "keyboard.press", () => Task.FromException<UiInputResult>(new OperationCanceledException())));
        }
        await router.RecordInputAsync(sessionId, "tap", () => Task.FromResult(
            new UiInputResult(true, "test", "Delivered")), .2, .3);
        Assert.True(state.TryGetSessionSnapshot(sessionId, out var snapshot));
        var actions = snapshot!.ApplicationEvents.Select(HostInputAction.Read).OfType<HostInputAction>().ToArray();
        Assert.Equal(new[] { "failed", "interrupted", "succeeded" }, actions.Select(action => action.Status));
        Assert.Equal(new string?[] { "inner", "outer", null }, actions.Select(action => action.EvidenceActionId));
        Assert.All(actions, action => Assert.True(action.CompletedAtUtc >= action.StartedAtUtc));
        Assert.DoesNotContain(snapshot.ApplicationEvents, item => item.Details?.Contains("Private backend text") == true);
    }

    [Fact]
    public void ResolveDeviceIdentifier_UsesRunTargetUntilScopeEnds()
    {
        var router = new UiInputRouter();

        using (router.BeginTargetScope("session-1", "physical-device-1"))
        {
            Assert.Equal(
                "physical-device-1",
                router.ResolveDeviceIdentifier("session-1", "reported-simulator"));
        }

        Assert.Equal(
            "reported-simulator",
            router.ResolveDeviceIdentifier("session-1", "reported-simulator"));
    }

    [Fact]
    public void BeginTargetScope_RejectsConcurrentBindingToDifferentDevice()
    {
        var router = new UiInputRouter();
        using var binding = router.BeginTargetScope("session-1", "physical-device-1");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            router.BeginTargetScope("session-1", "physical-device-2"));

        Assert.Contains("already bound", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BeginTargetScope_ReferenceCountsConcurrentBindingToSameDevice()
    {
        var router = new UiInputRouter();
        using var firstBinding = router.BeginTargetScope("session-1", "physical-device-1");
        var secondBinding = router.BeginTargetScope("session-1", "physical-device-1");

        secondBinding.Dispose();

        Assert.Equal(
            "physical-device-1",
            router.ResolveDeviceIdentifier("session-1", "reported-simulator"));
    }

    [Fact]
    public async Task CaptureAccessibilityAsync_UsesDedicatedDriver()
    {
        var router = new UiInputRouter();
        var driver = new RecordingAccessibilityDriver();
        router.ConfigureAccessibility(driver);
        var request = new UiAccessibilityRequest(
            "session-1",
            "device-1",
            "com.example.app");

        var result = await router.CaptureAccessibilityAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Same(request, driver.LastRequest);
    }

    [Fact]
    public async Task PrepareForInputAsync_UsesAccessibilityPreflightAndInvalidatesCachedTree()
    {
        var router = new UiInputRouter();
        var driver = new RecordingPreflightAccessibilityDriver();
        router.ConfigureAccessibility(driver);
        var accessibilityRequest = new UiAccessibilityRequest(
            "session-1",
            "device-1",
            "com.example.app");
        var preflightRequest = new UiInputPreflightRequest(
            "session-1",
            "device-1",
            "com.example.app");

        await router.CaptureAccessibilityAsync(accessibilityRequest, CancellationToken.None);
        await router.CaptureAccessibilityAsync(accessibilityRequest, CancellationToken.None);
        var result = await router.PrepareForInputAsync(preflightRequest, CancellationToken.None);
        await router.CaptureAccessibilityAsync(accessibilityRequest, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Same(preflightRequest, driver.LastPreflightRequest);
        Assert.Equal(2, driver.CaptureCount);
    }

    private sealed class RecordingAccessibilityDriver : IUiAccessibilityDriver
    {
        public UiAccessibilityRequest? LastRequest { get; private set; }

        public Task<UiAccessibilityResult> CaptureAccessibilityAsync(
            UiAccessibilityRequest request,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(new UiAccessibilityResult(
                true,
                "test-accessibility",
                "Captured.",
                new System.Text.Json.Nodes.JsonObject()));
        }
    }

    private sealed class RecordingPreflightAccessibilityDriver :
        IUiAccessibilityDriver,
        IUiInputPreflightDriver
    {
        public int CaptureCount { get; private set; }

        public UiInputPreflightRequest? LastPreflightRequest { get; private set; }

        public Task<UiAccessibilityResult> CaptureAccessibilityAsync(
            UiAccessibilityRequest request,
            CancellationToken cancellationToken = default)
        {
            CaptureCount++;
            return Task.FromResult(new UiAccessibilityResult(
                true,
                "test-accessibility",
                "Captured.",
                new System.Text.Json.Nodes.JsonObject()));
        }

        public Task<UiInputResult> PrepareForInputAsync(
            UiInputPreflightRequest request,
            CancellationToken cancellationToken = default)
        {
            LastPreflightRequest = request;
            return Task.FromResult(new UiInputResult(
                true,
                "test-preflight",
                "Prepared."));
        }
    }
}
