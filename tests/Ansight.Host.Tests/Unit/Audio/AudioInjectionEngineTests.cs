using System.Buffers.Binary;
using System.Text.Json.Nodes;
using Ansight.Host.Audio;
using Ansight.Host.Audio.Ios;
using Ansight.Host.Runtime.Operations;
using Ansight.Host.Tests.TestSupport;

namespace Ansight.Host.Tests.Unit.Audio;

[Collection("IosAudioRecovery")]
public sealed class AudioInjectionEngineTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ansight-audio-test-" + Guid.NewGuid().ToString("N"));
    private readonly string route = Guid.NewGuid().ToString("N");

    public AudioInjectionEngineTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public void DeliveryReportRemainsReadableFromTimelineAfterTemporaryFilesAreRemoved()
    {
        using var environment = new TestEnvironment();
        var state = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        var sessionId = state.CreateSession("com.example.audio", "Audio app", IPAddress.Loopback,
            configId: null, processSessionId: null);
        var report = new JsonObject { ["evidenceId"] = "audio-test-report", ["status"] = "completed" };
        var temporary = Path.Combine(root, "temporary-delivery");
        Directory.CreateDirectory(temporary);

        AudioInjectionEngine.Persist(state, new AudioTarget(sessionId, "com.example.audio", "emulator-5554", "android"), report, temporary);
        Directory.Delete(temporary, recursive: true);

        Assert.True(state.TryGetSessionSnapshot(sessionId, out var session));
        var snapshot = Assert.Single(session!.ArtifactSnapshots);
        var entry = Assert.Single(snapshot.Entries);
        Assert.True(SessionFileLocator.TryResolveArtifactEntryPath(environment.ApplicationPaths, session, snapshot, entry, out var retainedPath));
        Assert.True(File.Exists(retainedPath));
        Assert.Equal(report.ToJsonString(), JsonNode.Parse(File.ReadAllText(retainedPath))!.ToJsonString());
        Assert.Equal("delivery.json", Path.GetFileName(retainedPath));
    }

    [Fact]
    public async Task DeliveryUsesValidatedSnapshotAndPersistsMetadataAfterDeletingWaveform()
    {
        var file = WriteFixture();
        JsonObject? evidence = null;
        var provider = new FakeProvider(route, async (target, fixture, token) =>
        {
            Assert.Equal("exact-session", target.SessionId);
            Assert.Equal("exact-device", target.DeviceId);
            Assert.NotEqual(file, fixture.Path);
            await File.WriteAllTextAsync(file, "changed after validation", token);
            Assert.Equal(160, fixture.Info.FrameCount);
            Assert.Equal(364, new FileInfo(fixture.Path).Length);
            return new("host-output-played", fixture.Info.FrameCount, new JsonObject());
        });
        await using var engine = CreateEngine(provider, persist: (_, report, directory) =>
        {
            Assert.False(File.Exists(Path.Combine(directory, "fixture.wav")));
            evidence = report.DeepClone().AsObject();
        });

        var result = await engine.InjectAsync("exact-session", file, 1000, 0, CancellationToken.None);

        Assert.Equal("completed", result["status"]!.GetValue<string>());
        Assert.False(result["captureVerified"]!.GetValue<bool>());
        Assert.False(result["transcriptionVerified"]!.GetValue<bool>());
        Assert.Equal(64, result["fixture"]!["sha256"]!.GetValue<string>().Length);
        Assert.Equal(result.ToJsonString(), evidence!.ToJsonString());
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(root, "runs")));
    }

    [Fact]
    public async Task InvalidFixtureNeverReachesDeviceOrProvider()
    {
        var file = Path.Combine(root, "bad.wav");
        File.WriteAllText(file, "invalid");
        await using var engine = CreateEngine(new FakeProvider(route, (_, _, _) => throw new Xunit.Sdk.XunitException("Provider called")),
            resolve: (_, _) => throw new Xunit.Sdk.XunitException("Device queried"));
        var result = await engine.InjectAsync("exact-session", file, 1000, 0, CancellationToken.None);
        Assert.Equal("invalid-audio", result["code"]!.GetValue<string>());
        Assert.False(result["deliveryStarted"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("caller")]
    [InlineData("disconnect")]
    [InlineData("timeout")]
    [InlineData("shutdown")]
    public async Task CancellationDrainsProviderBeforeReturningAndReleasesRoute(string reason)
    {
        var file = WriteFixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = false;
        var connected = true;
        using var cancellation = new CancellationTokenSource();
        var provider = new FakeProvider(route, async (_, _, token) =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { await Task.Delay(50); stopped = true; }
            throw new InvalidOperationException("Unreachable");
        });
        await using var engine = CreateEngine(provider, connected: _ => Volatile.Read(ref connected));
        var run = engine.InjectAsync("exact-session", file, reason == "timeout" ? 200 : 2000, 0, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task? stop = null;
        if (reason == "caller") cancellation.Cancel();
        if (reason == "disconnect") Volatile.Write(ref connected, false);
        if (reason == "shutdown") stop = engine.StopAsync();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(3));
        if (stop is not null) await stop;
        Assert.True(stopped);
        Assert.Equal("cancelled", result["status"]!.GetValue<string>());
        Assert.Equal(reason == "disconnect" ? "session-disconnected" : reason == "timeout" ? "timeout" : "cancelled", result["code"]!.GetValue<string>());
        using var lease = AudioRouteLease.Acquire(route);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(root, "runs")));
    }

    [Fact]
    public async Task ConcurrentHostsCannotInjectIntoTheSameRoute()
    {
        using var held = AudioRouteLease.Acquire(route);
        var provider = new FakeProvider(route, (_, _, _) => throw new Xunit.Sdk.XunitException("Provider called"));
        await using var engine = CreateEngine(provider);
        var result = await engine.InjectAsync("exact-session", WriteFixture(), 1000, 0, CancellationToken.None);
        Assert.Equal("audio-route-busy", result["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task EvidenceFailureDoesNotMisreportCompletedDeliveryOrTriggerReplay()
    {
        var calls = 0;
        var provider = new FakeProvider(route, (_, fixture, _) =>
        {
            calls++;
            return Task.FromResult(new AudioProviderDelivery("host-output-played", fixture.Info.FrameCount, new JsonObject()));
        });
        await using var engine = CreateEngine(provider, persist: (_, _, _) => throw new IOException("disk full"));
        var result = await engine.InjectAsync("exact-session", WriteFixture(), 1000, 0, CancellationToken.None);
        Assert.Equal(1, calls);
        Assert.Equal("evidence-persistence-failed", result["code"]!.GetValue<string>());
        Assert.True(result["deliveryCompleted"]!.GetValue<bool>());
        Assert.Null(result["evidenceId"]);
    }

    [Fact]
    public async Task PartialProviderSubmissionCannotReportSuccess()
    {
        var provider = new FakeProvider(route, (_, fixture, _) => Task.FromResult(new AudioProviderDelivery("host-output-played", fixture.Info.FrameCount - 1, new JsonObject())));
        await using var engine = CreateEngine(provider);
        var result = await engine.InjectAsync("exact-session", WriteFixture(), 1000, 0, CancellationToken.None);
        Assert.Equal("failed", result["status"]!.GetValue<string>());
        Assert.Equal("delivery-incomplete", result["code"]!.GetValue<string>());
        Assert.True(result["deliveryStarted"]!.GetValue<bool>());
    }

    [Fact]
    public async Task UnexpectedEvidenceFailureCannotLeaveShutdownWaitingForever()
    {
        var provider = new FakeProvider(route, (_, fixture, _) => Task.FromResult(new AudioProviderDelivery("host-output-played", fixture.Info.FrameCount, new JsonObject())));
        await using var engine = CreateEngine(provider, persist: (_, _, _) => throw new NotSupportedException("unexpected persistence failure"));
        var result = await engine.InjectAsync("exact-session", WriteFixture(), 1000, 0, CancellationToken.None);
        Assert.Equal("evidence-persistence-failed", result["code"]!.GetValue<string>());
        await engine.StopAsync().WaitAsync(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<AudioInjectionException>(() => engine.GetCapabilitiesAsync("exact-session", CancellationToken.None));
        using var lease = AudioRouteLease.Acquire(route);
    }

    [Fact]
    public void FixtureValidationChecksFormatLengthDurationAndHash()
    {
        var good = Wave(160);
        Assert.Equal(10, AudioFixtureLoader.Parse(good, "snapshot").Info.DurationMs);
        Assert.Throws<AudioInjectionException>(() => AudioFixtureLoader.Parse(good[..^1], "snapshot"));
        var stereo = (byte[])good.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(stereo.AsSpan(22), 2);
        Assert.Throws<AudioInjectionException>(() => AudioFixtureLoader.Parse(stereo, "snapshot"));
        var wrongRate = (byte[])good.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(wrongRate.AsSpan(24), 44100);
        Assert.Throws<AudioInjectionException>(() => AudioFixtureLoader.Parse(wrongRate, "snapshot"));
        Assert.Throws<AudioInjectionException>(() => AudioFixtureLoader.Parse(Wave(240001), "snapshot"));
        Assert.Equal(15000, AudioFixtureLoader.Parse(Wave(240000), "snapshot").Info.DurationMs);
    }

    [Fact]
    public void TaskPathsCannotEscapeRepositoryThroughTraversalOrSymlinks()
    {
        var repository = Path.Combine(root, "repo");
        Directory.CreateDirectory(repository);
        var fixture = WriteFixture();
        Assert.Throws<AudioInjectionException>(() => AudioFixtureLoader.ResolvePath("../quote.wav", repository));
        Assert.Throws<AudioInjectionException>(() => AudioFixtureLoader.ResolvePath(fixture, repository));
        Assert.Throws<AudioInjectionException>(() => AudioFixtureLoader.ResolvePath("https://example.com/audio.wav", repository));
        File.CreateSymbolicLink(Path.Combine(repository, "escape.wav"), fixture);
        Assert.Throws<AudioInjectionException>(() => AudioFixtureLoader.ResolvePath("escape.wav", repository));
        File.Copy(fixture, Path.Combine(repository, "good.wav"));
        Assert.Equal(File.ReadAllBytes(fixture), File.ReadAllBytes(AudioFixtureLoader.ResolvePath("good.wav", repository)));
    }

    [Theory]
    [InlineData("simulator-audio-route-stale", true)]
    [InlineData("microphone-not-ready", false)]
    public async Task OnlyConfirmedStaleRouteSchedulesRecoveryAndNeverReplaysInjection(string code, bool requiresRestart)
    {
        var target = new AudioTarget("session", "app", Guid.NewGuid().ToString(), "ios");
        var recovery = new IosAudioRouteRecovery(root);
        var attempts = 0;
        var provider = new FakeProvider(route, (_, _, _) =>
        {
            attempts++;
            throw new AudioInjectionException(code, "Route unavailable.", new JsonObject
            {
                ["deliveryStarted"] = false,
                ["expectedInputDeviceUid"] = "BlackHole2ch_UID",
                ["actualInputDeviceUid"] = "BuiltInMicrophoneDevice"
            });
        });
        await using var engine = new AudioInjectionEngine((_, _) => Task.FromResult(target), _ => true,
            (_, _, _) => { }, [provider], Path.Combine(root, "runs"), iosAudioRecovery: recovery);

        var result = await engine.InjectAsync(target.SessionId, WriteFixture(), 1000, 0, CancellationToken.None);

        Assert.Equal(1, attempts);
        Assert.Equal("failed", result["status"]!.GetValue<string>());
        Assert.Equal(code, result["code"]!.GetValue<string>());
        Assert.False(result["deliveryStarted"]!.GetValue<bool>());
        Assert.False(result["deliveryMayBePartial"]!.GetValue<bool>());
        Assert.Equal("BlackHole2ch_UID", result["diagnostics"]!["expectedInputDeviceUid"]!.GetValue<string>());

        Assert.Equal(requiresRestart, await new IosAudioRouteRecovery(root).RecoverBeforeLaunchAsync(
            target.DeviceId, target.AppId, _ => Task.FromResult<DateTimeOffset?>(null),
            _ => Task.CompletedTask, CancellationToken.None));
    }

    private AudioInjectionEngine CreateEngine(IAudioInjectionProvider provider,
        Func<string, bool>? connected = null,
        Action<AudioTarget, JsonObject, string>? persist = null,
        Func<string, CancellationToken, Task<AudioTarget>>? resolve = null)
        => new(resolve ?? ((id, _) => Task.FromResult(new AudioTarget(id, "app", "exact-device", "ios"))),
            connected ?? (_ => true), persist ?? ((_, _, _) => { }), [provider], Path.Combine(root, "runs"));

    private string WriteFixture()
    {
        var file = Path.Combine(root, "quote.wav");
        File.WriteAllBytes(file, Wave(160));
        return file;
    }

    private static byte[] Wave(int frames)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8); writer.Write(36 + frames * 2); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(16000); writer.Write(32000);
        writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(frames * 2);
        writer.Write(new byte[frames * 2]);
        return stream.ToArray();
    }

    private sealed class FakeProvider(string route, Func<AudioTarget, AudioFixture, CancellationToken, Task<AudioProviderDelivery>> inject) : IAudioInjectionProvider
    {
        public string Platform => "ios";
        public string Backend => "coreaudio-loopback";
        public string GetLeaseKey(AudioTarget target) => route;
        public Task<AudioProviderCapabilities> GetCapabilitiesAsync(AudioTarget target, CancellationToken cancellationToken)
            => Task.FromResult(new AudioProviderCapabilities(true, "ready", "Ready", new JsonObject()));
        public Task<AudioProviderDelivery> InjectAsync(AudioTarget target, AudioFixture fixture, int waitForMicrophoneMs, CancellationToken cancellationToken)
            => inject(target, fixture, cancellationToken);
    }
}
