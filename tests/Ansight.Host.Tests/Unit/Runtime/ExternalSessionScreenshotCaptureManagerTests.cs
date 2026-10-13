using Ansight.Host.Tests.TestSupport;
using Ansight.Infrastructure.Preferences;
using SkiaSharp;
using System.Net;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class ExternalSessionScreenshotCaptureManagerTests
{
    [Fact]
    public async Task OverlappingAttachmentsCannotLeaveAnUntrackedCaptureRunning()
    {
        using var context = new CaptureTestContext();
        var firstProbeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstProbe = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var captureCount = 0;
        context.Manager.ConfigurePhysicalIosCapture(async (_, _, _) =>
        {
            if (Interlocked.Increment(ref captureCount) == 1)
            {
                firstProbeStarted.TrySetResult();
                await releaseFirstProbe.Task;
            }
            return context.Bytes;
        });

        var firstAttach = context.AttachAsync();
        try
        {
            await firstProbeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var secondPolicy = await context.AttachAsync();
            Assert.Equal("host", secondPolicy.Mode);
            await TestWait.UntilAsync(() => context.Snapshot.Images.Count == 1);
            releaseFirstProbe.TrySetResult();
            var firstPolicy = await firstAttach.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal("app", firstPolicy.Mode);
            Assert.Single(context.Snapshot.Images);
            await context.Manager.StopAsync(context.SessionId, "test completed");
            Assert.Null(await context.Manager.CaptureFrameAsync(context.SessionId, CancellationToken.None));
        }
        finally
        {
            releaseFirstProbe.TrySetResult();
            await firstAttach;
            await context.Manager.StopAsync(context.SessionId, "test completed");
        }
    }

    [Fact]
    public async Task StopDuringProbePreventsLateAttachmentAndFrames()
    {
        using var context = new CaptureTestContext();
        var probeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProbe = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Manager.ConfigurePhysicalIosCapture(async (_, _, _) =>
        {
            probeStarted.TrySetResult();
            await releaseProbe.Task; // Model a device command that completes despite cancellation.
            return context.Bytes;
        });

        var attach = context.AttachAsync();
        try
        {
            await probeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await context.Manager.StopAsync(context.SessionId, "Live app connection closed.");
            releaseProbe.TrySetResult();
            var policy = await attach.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal("app", policy.Mode);
            Assert.Empty(context.Snapshot.Images);
            Assert.Null(await context.Manager.CaptureFrameAsync(context.SessionId, CancellationToken.None));
        }
        finally
        {
            releaseProbe.TrySetResult();
            await attach;
            await context.Manager.StopAsync(context.SessionId, "test completed");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InFlightFrameCannotExtendAStoppedCapture(bool disconnectSession)
    {
        using var context = new CaptureTestContext();
        var frameStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFrame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var captureCount = 0;
        context.Manager.ConfigurePhysicalIosCapture(async (_, _, _) =>
        {
            if (Interlocked.Increment(ref captureCount) > 1)
            {
                frameStarted.TrySetResult();
                await releaseFrame.Task;
                return context.ChangedBytes;
            }
            return context.Bytes;
        });

        try
        {
            await context.AttachAsync(100);
            await frameStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await TestWait.UntilAsync(() => context.Snapshot.Images.Count == 1);
            if (disconnectSession)
                context.State.SetSessionStatus(context.SessionId, "WebSocket Closed");
            var endUtc = context.Snapshot.LastUpdatedUtc;
            var stopping = disconnectSession ? Task.CompletedTask
                : context.Manager.StopAsync(context.SessionId, "Live app connection closed.");
            releaseFrame.TrySetResult();
            await stopping.WaitAsync(TimeSpan.FromSeconds(5));
            // A historical session must stop its own pump, even before finalization reaches it.
            if (disconnectSession)
            {
                await TestWait.UntilAsync(() =>
                {
                    try { using var scope = context.Manager.BeginTestRun(context.SessionId); return false; }
                    catch (InvalidOperationException) { return true; }
                }, timeout: TimeSpan.FromSeconds(5));
            }

            // Image persistence is asynchronous; let any incorrectly accepted frame finish saving.
            await Task.Delay(200);
            Assert.Single(context.Snapshot.Images);
            Assert.Equal(endUtc, context.Snapshot.LastUpdatedUtc);
        }
        finally
        {
            releaseFrame.TrySetResult();
            await context.Manager.StopAsync(context.SessionId, "test completed");
        }
    }

    [Fact]
    public void SdkSimulatorWithoutHostCaptureUsesAppManagedScreenshotScope()
    {
        using var environment = new TestEnvironment();
        var state = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        var manager = new ExternalSessionScreenshotCaptureManager(state, new UserPreferences(
            new FilePreferencesStore(Path.Combine(environment.RootPath, "preferences.json"))));
        var sessionId = state.CreateSession("test.app", "Test App", IPAddress.Loopback, null, null);
        state.SetSessionDeviceProfile(sessionId, null,
            """{"device":{"nativeDeviceId":"simulator-001","osName":"iOS","isVirtual":true}}""");

        using var scope = manager.BeginTestRun(sessionId);
        Assert.NotNull(scope);
    }

    [Fact]
    public async Task BackgroundObservationRecordsMarkerAndPausesWithoutFailingBeforeWatchPoll()
    {
        using var environment = new TestEnvironment();
        var state = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        var preferences = new UserPreferences(new FilePreferencesStore(
            Path.Combine(environment.RootPath, "preferences.json")));
        var manager = new ExternalSessionScreenshotCaptureManager(state, preferences);
        using var bitmap = new SKBitmap(20, 40);
        bitmap.Erase(SKColors.White);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        var bytes = data.ToArray();
        var sessionId = state.CreateDeviceSession(new WorkspaceTestTarget("ios", "test-phone", "iPhone",
            "test.app", false, false, false) { DeviceKind = DeviceKinds.Physical, ExecutionMode = "device" });
        state.SetSessionAppState(sessionId, AppLifecycleState.Foreground);
        var observedAt = DateTimeOffset.UtcNow;
        var backgroundRecorded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        state.RuntimeEventOccurred += (_, runtimeEvent) =>
        {
            if (runtimeEvent is RuntimeClientAppStateChangedEvent)
                backgroundRecorded.TrySetResult();
        };
        var captureCount = 0;
        var background = true;
        var failureCount = 0;
        manager.CaptureFailed += (_, _) => Interlocked.Increment(ref failureCount);
        manager.ConfigurePhysicalIosCapture((_, _, _) =>
        {
            if (Interlocked.Increment(ref captureCount) > 1)
            {
                if (Volatile.Read(ref background))
                    throw new ExternalSessionScreenshotCaptureManager.AppBackgroundedException(observedAt);
                resumed.TrySetResult();
            }
            return Task.FromResult(bytes);
        });
        try
        {
            Assert.True(state.TryGetSessionSnapshot(sessionId, out var session));
            await manager.AttachAsync(sessionId, session!.DeviceProfile, session.DeviceProfileJson,
                new ExternalSessionScreenshotCaptureRequest(1024, 100));
            await backgroundRecorded.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(400); // Longer than the three retries that previously failed the capture.
            Assert.True(state.IsDeviceSessionActive(sessionId));
            Assert.Equal(0, Volatile.Read(ref failureCount));
            Assert.Equal(2, Volatile.Read(ref captureCount));
            Assert.True(state.TryGetSessionSnapshot(sessionId, out session));
            Assert.Equal(AppLifecycleState.Background, session!.AppState);
            Assert.Equal(observedAt, session.AppStateChangedUtc);
            Assert.Single(session.ApplicationEvents, item => item.Label == "lifecycle.background");

            Volatile.Write(ref background, false);
            state.SetSessionAppState(sessionId, AppLifecycleState.Foreground);
            await resumed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(state.IsDeviceSessionActive(sessionId));
        }
        finally { await manager.StopAsync(sessionId, "test completed"); }
    }

    [Fact]
    public async Task RepeatedScreenshotErrorsStillFailDeviceCapture()
    {
        using var environment = new TestEnvironment();
        var state = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        var manager = new ExternalSessionScreenshotCaptureManager(state, new UserPreferences(
            new FilePreferencesStore(Path.Combine(environment.RootPath, "preferences.json"))));
        using var bitmap = new SKBitmap(20, 40);
        bitmap.Erase(SKColors.White);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        var bytes = data.ToArray();
        var captureCount = 0;
        manager.ConfigurePhysicalIosCapture((_, _, _) =>
        {
            if (Interlocked.Increment(ref captureCount) > 1) throw new IOException("ADB disconnected.");
            return Task.FromResult(bytes);
        });
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.CaptureFailed += (_, _) => failed.TrySetResult();
        var sessionId = state.CreateDeviceSession(new WorkspaceTestTarget("ios", "test-phone", "iPhone",
            "test.app", false, false, false) { DeviceKind = DeviceKinds.Physical, ExecutionMode = "device" });
        try
        {
            Assert.True(state.TryGetSessionSnapshot(sessionId, out var session));
            await manager.AttachAsync(sessionId, session!.DeviceProfile, session.DeviceProfileJson,
                new ExternalSessionScreenshotCaptureRequest(1024, 100));
            await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(state.IsDeviceSessionActive(sessionId));
            Assert.True(state.TryGetSessionSnapshot(sessionId, out session));
            Assert.Equal("Failed", session!.Status);
            Assert.DoesNotContain(session.ApplicationEvents, item => item.Label == "lifecycle.background");
        }
        finally { await manager.StopAsync(sessionId, "test completed"); }
    }

    [Fact]
    public async Task IntervalChangeWakesActiveCaptureAndLeavesOtherSessionsUnchanged()
    {
        using var environment = new TestEnvironment();
        var state = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        var preferences = new UserPreferences(new FilePreferencesStore(
            Path.Combine(environment.RootPath, "preferences.json")));
        var manager = new ExternalSessionScreenshotCaptureManager(state, preferences);
        using var bitmap = new SKBitmap(20, 40);
        bitmap.Erase(SKColors.White);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        var bytes = data.ToArray();
        var captures = 0;
        var nextFrame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.ConfigurePhysicalIosCapture((_, appId, _) =>
        {
            if (appId == "test.app" && Interlocked.Increment(ref captures) > 1)
                nextFrame.TrySetResult();
            return Task.FromResult(bytes);
        });
        var firstId = CreateSession("test.app");
        var otherId = CreateSession("other.app");
        try
        {
            foreach (var id in new[] { firstId, otherId })
            {
                Assert.True(state.TryGetSessionSnapshot(id, out var session));
                var policy = await manager.AttachAsync(id, session!.DeviceProfile, session.DeviceProfileJson,
                    new ExternalSessionScreenshotCaptureRequest(1024, 60_000));
                Assert.Equal(60_000, policy.IntervalMilliseconds);
                Assert.Equal("host", policy.Mode);
            }

            manager.SetInterval(firstId, 100);
            await nextFrame.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(state.IsDeviceSessionActive(firstId));
            Assert.True(state.TryGetSessionSnapshot(otherId, out var other));
            Assert.Single(other!.Images);
        }
        finally
        {
            await manager.StopAsync(firstId, "test completed");
            await manager.StopAsync(otherId, "test completed");
        }

        string CreateSession(string appId)
            => state.CreateDeviceSession(new WorkspaceTestTarget("ios", Guid.NewGuid().ToString(), "iPhone",
                appId, false, false, false) { DeviceKind = DeviceKinds.Physical, ExecutionMode = "device" });
    }
    private sealed class CaptureTestContext : IDisposable
    {
        private readonly TestEnvironment environment = new();

        public CaptureTestContext()
        {
            State = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
            Manager = new ExternalSessionScreenshotCaptureManager(State, new UserPreferences(
                new FilePreferencesStore(Path.Combine(environment.RootPath, "preferences.json"))));
            SessionId = State.CreateDeviceSession(new WorkspaceTestTarget("ios", "test-phone", "iPhone",
                "test.app", false, false, false) { DeviceKind = DeviceKinds.Physical, ExecutionMode = "device" });
            Bytes = CreateImage(SKColors.White);
            ChangedBytes = CreateImage(SKColors.Blue);
        }

        public RuntimeState State { get; }
        public ExternalSessionScreenshotCaptureManager Manager { get; }
        public string SessionId { get; }
        public byte[] Bytes { get; }
        public byte[] ChangedBytes { get; }
        public AppSessionSnapshot Snapshot
        {
            get
            {
                Assert.True(State.TryGetSessionSnapshot(SessionId, out var snapshot));
                return snapshot!;
            }
        }

        public Task<ExternalSessionScreenshotCapturePolicy> AttachAsync(int intervalMilliseconds = 60_000)
            => Manager.AttachAsync(SessionId, Snapshot.DeviceProfile, Snapshot.DeviceProfileJson,
                new ExternalSessionScreenshotCaptureRequest(1024, intervalMilliseconds));

        public void Dispose() => environment.Dispose();

        private static byte[] CreateImage(SKColor color)
        {
            using var bitmap = new SKBitmap(20, 40);
            bitmap.Erase(color);
            using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }
    }
}
