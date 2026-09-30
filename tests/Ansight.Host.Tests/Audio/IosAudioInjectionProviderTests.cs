using System.Text.Json.Nodes;
using Ansight.Host.Audio;
using Ansight.Host.Audio.Ios;

namespace Ansight.Host.Tests.Audio;

public sealed class IosAudioInjectionProviderTests
{
    private static readonly AudioTarget target = new("session-1", "app-1", "EC47CEBB-B438-484C-8597-6967D82C7059", "ios");

    [Fact]
    public async Task ActiveRouteMismatchRetainsRestartAndDeviceDiagnostics()
    {
        var provider = CreateProvider((_, _) => throw new AudioInjectionException(
            "simulator-audio-route-stale", "Simulator audio route is stale—restart required.",
            new JsonObject { ["restartRequired"] = true, ["expectedInputDeviceUid"] = "BlackHole2ch_UID" }));
        var result = await provider.GetCapabilitiesAsync(target, CancellationToken.None);
        Assert.False(result.Available);
        Assert.Equal("simulator-audio-route-stale", result.Code);
        Assert.True(result.Diagnostics["restartRequired"]!.GetValue<bool>());
        Assert.Equal("BlackHole2ch_UID", result.Diagnostics["expectedInputDeviceUid"]!.GetValue<string>());
    }

    [Fact]
    public async Task RouteMismatchFailsClosedBeforeReportingCapability()
    {
        var response = SuccessfulResponse();
        response["diagnostics"]!["audioDeviceUid"] = "built-in-speaker";
        var provider = CreateProvider((_, _) => Task.FromResult(response));
        var result = await provider.GetCapabilitiesAsync(target, CancellationToken.None);
        Assert.False(result.Available);
        Assert.Equal("input-route-unverified", result.Code);
    }

    [Fact]
    public async Task RecentDevicePreferenceCannotReplaceCheckedMenuEvidence()
    {
        var response = SuccessfulResponse();
        response["diagnostics"]!["inputRouteEvidence"] = "recent-device-preferences";
        var provider = CreateProvider((_, _) => Task.FromResult(response));
        Assert.False((await provider.GetCapabilitiesAsync(target, CancellationToken.None)).Available);
    }

    [Fact]
    public async Task MissingAccessibilityPreservesActionableFailure()
    {
        var provider = CreateProvider((_, _) => throw new AudioInjectionException("accessibility-permission-required", "Allow Accessibility."));
        var result = await provider.GetCapabilitiesAsync(target, CancellationToken.None);
        Assert.False(result.Available);
        Assert.Equal("accessibility-permission-required", result.Code);
        Assert.Equal("Allow Accessibility.", result.Message);
    }

    [Fact]
    public async Task StructuredHelperFailureRetainsCodeAndNoDeliveryEvidence()
    {
        var provider = CreateProvider((_, _) => Task.FromResult(new JsonObject
        {
            ["success"] = false,
            ["code"] = "audio-route-ambiguous",
            ["message"] = "Two simulators are booted.",
            ["diagnostics"] = new JsonObject { ["deliveryStarted"] = false }
        }));
        var result = await provider.GetCapabilitiesAsync(target, CancellationToken.None);
        Assert.False(result.Available);
        Assert.Equal("audio-route-ambiguous", result.Code);
        Assert.False(result.Diagnostics["deliveryStarted"]!.GetValue<bool>());
    }

    [Fact]
    public async Task PlaybackUsesExactSessionAndFixturePathWithoutClaimingCapture()
    {
        IReadOnlyList<string>? actualArguments = null;
        var provider = CreateProvider((arguments, _) =>
        {
            actualArguments = arguments;
            return Task.FromResult(SuccessfulResponse());
        });
        var fixture = new AudioFixture("/fixtures/a quote.wav", new("sha256", 1000, 16000, 1, 16, 16000), []);
        var result = await provider.InjectAsync(target, fixture, 4500, CancellationToken.None);
        Assert.Equal("coreaudio-loopback", provider.Backend);
        Assert.Equal("host-output-played", result.CompletionKind);
        Assert.Equal(16000, result.SubmittedFrames);
        Assert.False(result.Diagnostics["captureVerified"]!.GetValue<bool>());
        Assert.False(result.Diagnostics["transcriptionVerified"]!.GetValue<bool>());
        Assert.Equal(new[] { "inject", "--device-uid", "BlackHole2ch_UID", "--simulator-app", "/Applications/Xcode.app/Contents/Developer/Applications/Simulator.app",
            "--device-id", target.DeviceId, "--app-id", target.AppId, "--file", "/fixtures/a quote.wav", "--wait-ms", "4500" }, actualArguments);
    }

    [Theory]
    [InlineData(15999, "host-output-played")]
    [InlineData(16000, "scheduled")]
    public async Task PartialOrScheduledAudioNeverCountsAsCompleted(long frames, string completion)
    {
        var response = SuccessfulResponse();
        response["diagnostics"]!["submittedFrames"] = frames;
        response["diagnostics"]!["completionKind"] = completion;
        var provider = CreateProvider((_, _) => Task.FromResult(response));
        var fixture = new AudioFixture("/fixtures/input.wav", new("sha256", 1000, 16000, 1, 16, 16000), []);
        var error = await Assert.ThrowsAsync<AudioInjectionException>(() => provider.InjectAsync(target, fixture, 0, CancellationToken.None));
        Assert.Equal("audio-delivery-incomplete", error.Code);
    }

    [Fact]
    public async Task CallerCancellationReachesNativeRunnerAndRemainsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = false;
        var provider = CreateProvider(async (_, token) =>
        {
            started.SetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { stopped = true; }
            return SuccessfulResponse();
        });
        var operation = provider.InjectAsync(target, new("fixture.wav", new("sha256", 1000, 16000, 1, 16, 16000), []), 0, cancellation.Token);
        await started.Task;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.True(stopped);
    }

    [Fact]
    public async Task NonMacHostDoesNotLaunchHelperAndAllSimulatorsShareLease()
    {
        var provider = new IosAudioInjectionProvider((_, _) => throw new InvalidOperationException("Helper must not run."),
            _ => Task.FromResult<string?>(null), false, null);
        var result = await provider.GetCapabilitiesAsync(target, CancellationToken.None);
        Assert.False(result.Available);
        Assert.Equal("platform-unavailable", result.Code);
        Assert.Equal(provider.GetLeaseKey(target), provider.GetLeaseKey(target with { DeviceId = Guid.NewGuid().ToString() }));
    }

    [Fact]
    public async Task ConfiguredLoopbackUidIsExplicitAndNeverSubstituted()
    {
        var response = SuccessfulResponse();
        response["diagnostics"]!["audioDeviceUid"] = "dedicated-loopback";
        var provider = new IosAudioInjectionProvider((arguments, _) =>
        {
            Assert.Contains("dedicated-loopback", arguments);
            Assert.DoesNotContain("BlackHole2ch_UID", arguments);
            return Task.FromResult(response);
        }, _ => Task.FromResult<string?>("/Applications/Xcode.app/Contents/Developer/Applications/Simulator.app"), true, "dedicated-loopback");
        Assert.True((await provider.GetCapabilitiesAsync(target, CancellationToken.None)).Available);
    }

    private static IosAudioInjectionProvider CreateProvider(Func<IReadOnlyList<string>, CancellationToken, Task<JsonObject>> run) =>
        new(run, _ => Task.FromResult<string?>("/Applications/Xcode.app/Contents/Developer/Applications/Simulator.app"), true, null);

    private static JsonObject SuccessfulResponse() => new()
    {
        ["success"] = true,
        ["diagnostics"] = new JsonObject
        {
            ["audioDeviceUid"] = "BlackHole2ch_UID",
            ["simulatorDeviceId"] = target.DeviceId,
            ["inputRouteVerified"] = true,
            ["inputRouteEvidence"] = "simulator-menu-checked-item",
            ["systemAudioDefaultsChanged"] = false,
            ["completionKind"] = "host-output-played",
            ["submittedFrames"] = 16000L
        }
    };
}
