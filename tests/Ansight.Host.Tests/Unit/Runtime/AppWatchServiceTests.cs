using Ansight.Host.Tests.TestSupport;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class AppWatchServiceTests
{
    [Fact]
    public async Task LifecycleMarkersUseFirstObservationBeforeExitConfirmationAndIgnoreProbeErrors()
    {
        using var environment = new TestEnvironment();
        var backend = new Backend();
        var service = new AppWatchService(environment.RootPath, backend);
        await service.AddAsync("test.app", "ios", "phone", []);
        var device = new DeviceDescriptor("phone", "Phone", "ios", "test", "booted", true, true, DeviceKinds.Physical);
        var foregroundAt = DateTimeOffset.UtcNow;
        var backgroundAt = foregroundAt.AddSeconds(5);
        backend.Observations = [new(device, "42", AppState: global::Ansight.AppLifecycleState.Foreground,
            ObservedAtUtc: foregroundAt)];
        await service.PollAsync();
        var capture = Assert.Single(backend.Captures);
        Assert.Equal(new LifecycleObservation(global::Ansight.AppLifecycleState.Foreground, foregroundAt),
            Assert.Single(capture.AppStates));

        backend.Observations = [new(device, null, AppState: global::Ansight.AppLifecycleState.Background,
            ObservedAtUtc: backgroundAt)];
        await service.PollAsync();
        Assert.Equal(0, capture.Stops);
        Assert.Equal(new LifecycleObservation(global::Ansight.AppLifecycleState.Background, backgroundAt),
            capture.AppStates[1]);

        // Errors and process disappearance alone cannot establish a lifecycle state.
        backend.Observations = [new(device, null, "Appium timeout")];
        await service.PollAsync();
        backend.Observations = [new(device, null)];
        await service.PollAsync();
        Assert.Equal(2, capture.AppStates.Count);
        Assert.Equal(0, capture.Stops);

        // Returning before exit confirmation belongs to the existing recording.
        var returnedAt = backgroundAt.AddSeconds(2);
        backend.Observations = [new(device, "42", AppState: global::Ansight.AppLifecycleState.Foreground,
            ObservedAtUtc: returnedAt)];
        await service.PollAsync();
        Assert.Single(backend.Captures);
        Assert.Equal(new LifecycleObservation(global::Ansight.AppLifecycleState.Foreground, returnedAt),
            capture.AppStates[2]);
    }

    [Fact]
    public async Task FailedCaptureReleasesDeviceAndReconnectsWithoutAnAppRestart()
    {
        using var environment = new TestEnvironment();
        var backend = new Backend();
        var service = new AppWatchService(environment.RootPath, backend);
        await service.AddAsync("test.app", "ios", "simulator", []);
        await service.PollAsync();
        var failed = Assert.Single(backend.Captures);
        failed.IsActive = false;
        backend.Failure = new IOException("Appium connection unavailable");
        await service.PollAsync();
        Assert.Equal("capture.failed", failed.Reason);
        Assert.Null(Assert.Single(service.List()).SessionId);
        backend.Failure = null;
        backend.Observations = [ObserveDevice("simulator", "ios", error: "Appium connection unavailable")];
        await service.PollAsync();
        Assert.Equal("capture.failed", failed.Reason);
        Assert.Null(Assert.Single(service.List()).SessionId);

        backend.Observations = null;
        await service.PollAsync();
        Assert.Equal(2, backend.Captures.Count);
        Assert.Equal(backend.Captures[1].SessionId, Assert.Single(service.List()).SessionId);
        Assert.Equal("capturing", Assert.Single(service.List()).State);
    }

    [Fact]
    public async Task ScreenshotIntervalsPersistIndependentlyAndUpdateActiveCapturesWithoutRestarting()
    {
        using var environment = new TestEnvironment();
        var backend = new Backend();
        var service = new AppWatchService(environment.RootPath, backend);
        var first = await service.AddAsync("test.app", "ios", "simulator", [],
            screenshotIntervalMilliseconds: 1_000);
        var second = await service.AddAsync("other.app", "ios", "simulator", []);
        await service.PollAsync();
        var capture = backend.Captures[0];
        Assert.Equal(1_000, capture.ScreenshotIntervalMilliseconds);

        await service.SetScreenshotIntervalAsync(first.Id, 500);
        Assert.Equal(500, capture.ScreenshotIntervalMilliseconds);
        Assert.Equal(0, capture.Stops);
        Assert.Equal(capture.SessionId, service.List().Single(status => status.Watch.Id == first.Id).SessionId);
        var reloaded = new AppWatchService(environment.RootPath, new Backend()).List();
        Assert.Equal(500, reloaded.Single(status => status.Watch.Id == first.Id).Watch.ScreenshotIntervalMilliseconds);
        Assert.Equal(2_000, reloaded.Single(status => status.Watch.Id == second.Id).Watch.ScreenshotIntervalMilliseconds);

        // Re-adding without an interval keeps the previously saved value.
        var updated = await service.AddAsync("test.app", "ios", "simulator", []);
        Assert.Equal(500, updated.ScreenshotIntervalMilliseconds);
        await service.SetEnabledAsync(first.Id, false);
        await service.SetScreenshotIntervalAsync(first.Id, 1_000);
        Assert.False(service.List().Single(status => status.Watch.Id == first.Id).Watch.Enabled);
    }

    [Fact]
    public void LegacyWatchesRetainTheTwoSecondDefault()
    {
        using var environment = new TestEnvironment();
        File.WriteAllText(Path.Combine(environment.RootPath, "app-watches.json"),
            """[{"id":"old-watch","appId":"test.app","enabled":true,"captureFiles":[]}]""");
        var watch = Assert.Single(new AppWatchService(environment.RootPath, new Backend()).List()).Watch;
        Assert.Equal(2_000, watch.ScreenshotIntervalMilliseconds);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(99)]
    [InlineData(60_001)]
    public async Task InvalidScreenshotIntervalsLeaveSavedSettingsUnchanged(int interval)
    {
        using var environment = new TestEnvironment();
        var service = new AppWatchService(environment.RootPath, new Backend());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.AddAsync("test.app", null, null,
            [], screenshotIntervalMilliseconds: interval));
        Assert.Empty(service.List());
        var watch = await service.AddAsync("test.app", "ios", "physical-udid", ["Documents/data.json"],
            captureInstruments: true, screenshotIntervalMilliseconds: 1_000);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.SetScreenshotIntervalAsync(watch.Id, interval));
        var saved = Assert.Single(new AppWatchService(environment.RootPath, new Backend()).List()).Watch;
        Assert.Equal(1_000, saved.ScreenshotIntervalMilliseconds);
        Assert.True(saved.CaptureInstruments);
        Assert.Equal("Documents/data.json", Assert.Single(saved.CaptureFiles));
    }

    [Fact]
    public async Task InstrumentsOptionPersistsForExplicitIosWatch()
    {
        using var environment = new TestEnvironment();
        var service = new AppWatchService(environment.RootPath, new Backend());
        var watch = await service.AddAsync("test.app", "ios", "physical-udid", [],
            captureInstruments: true);
        Assert.True(watch.CaptureInstruments);
        Assert.True(Assert.Single(new AppWatchService(environment.RootPath, new Backend()).List())
            .Watch.CaptureInstruments);
        await Assert.ThrowsAsync<ArgumentException>(() => service.AddAsync("other.app", "android",
            "emulator-1", [], captureInstruments: true));
        await Assert.ThrowsAsync<ArgumentException>(() => service.AddAsync("other.app", "ios",
            null, [], captureInstruments: true));
    }

    [Fact]
    public async Task AppOnlyWatchDiscoversLateDevicesAndKeepsIndependentRecordings()
    {
        using var environment = new TestEnvironment();
        var backend = new Backend { Observations = [] };
        var service = new AppWatchService(environment.RootPath, backend);
        var watch = await service.AddAsync("test.app", null, null, []);
        await service.PollAsync();
        Assert.Equal("waiting-for-device", Assert.Single(service.List()).State);
        var stored = Assert.Single(new AppWatchService(environment.RootPath, backend).List()).Watch;
        Assert.Null(stored.Platform);
        Assert.Null(stored.DeviceId);

        backend.Observations.Add(ObserveDevice("ios-one", "ios"));
        await service.PollAsync();
        var first = Assert.Single(backend.Captures);
        backend.Observations.Add(ObserveDevice("android-two", "android"));
        await service.PollAsync();
        var both = Assert.Single(service.List());
        Assert.Equal(2, backend.Captures.Count);
        Assert.Equal(2, both.Devices.Count(device => device.SessionId is not null));
        Assert.Null(both.SessionId); // No arbitrary device is presented as the watch's only session.

        backend.Observations[0] = ObserveDevice("ios-one", "ios", identity: null);
        await service.PollAsync();
        await service.PollAsync();
        Assert.Equal("process.exited", first.Reason);
        Assert.Equal(0, backend.Captures[1].Stops);
        Assert.Equal(backend.Captures[1].SessionId, Assert.Single(service.List()).SessionId);

        backend.Observations[0] = ObserveDevice("ios-one", "ios", identity: "42:new-birth");
        await service.PollAsync();
        Assert.Equal(3, backend.Captures.Count);
        await service.SetEnabledAsync(watch.Id, false);
        Assert.All(backend.Captures, capture => Assert.Equal(1, capture.Stops));
        Assert.All(Assert.Single(service.List()).Devices, device => Assert.Null(device.SessionId));
    }

    [Fact]
    public async Task DeviceErrorsAndLossAreIsolatedFromOtherRecordings()
    {
        using var environment = new TestEnvironment();
        var backend = new Backend { Observations = [ObserveDevice("one", "ios"), ObserveDevice("two", "ios")] };
        var service = new AppWatchService(environment.RootPath, backend);
        var watch = await service.AddAsync("test.app", "ios", null, []);
        await service.PollAsync();
        backend.Observations[0] = ObserveDevice("one", "ios", error: "Simulator probe failed");
        backend.Observations[1] = ObserveDevice("two", "ios", identity: "43:restarted");
        await service.PollAsync();
        Assert.Equal(3, backend.Captures.Count);
        Assert.Equal(0, backend.Captures[0].Stops);
        Assert.Equal("process.restarted", backend.Captures[1].Reason);
        Assert.Equal("capturing-probe-error", service.List()[0].State);

        backend.Observations.RemoveAt(0);
        backend.InventoryWarning = "Partial device discovery";
        await service.PollAsync();
        await service.PollAsync();
        Assert.Equal(0, backend.Captures[0].Stops);
        backend.InventoryWarning = null;
        await service.PollAsync();
        await service.PollAsync();
        Assert.Equal("device.unavailable", backend.Captures[0].Reason);
        Assert.Equal(0, backend.Captures[2].Stops);
        Assert.Equal("capturing", service.List()[0].State);
        await service.RemoveAsync(watch.Id);
        Assert.Equal("watch.removed", backend.Captures[2].Reason);
    }

    [Fact]
    public async Task BusyTargetDoesNotBlockOtherDiscoveredDevices()
    {
        using var environment = new TestEnvironment();
        var backend = new Backend { Observations = [ObserveDevice("one", "ios"), ObserveDevice("two", "android")] };
        backend.BusyDevices.Add("one");
        var service = new AppWatchService(environment.RootPath, backend);
        await service.AddAsync("test.app", null, null, []);
        await service.PollAsync();
        Assert.Equal("two", Assert.Single(backend.Captures).DeviceId);
        Assert.Equal("waiting-for-device-owner", service.List()[0].Devices.Single(device => device.DeviceId == "one").State);
        backend.BusyDevices.Clear();
        await service.PollAsync();
        Assert.Equal(2, backend.Captures.Count);
    }

    private static AppWatchObservation ObserveDevice(string id, string platform, string? identity = "42:birth", string? error = null)
        => new(new DeviceDescriptor(id, id, platform, "test", "booted", true, true,
            platform == "ios" ? DeviceKinds.Simulator : DeviceKinds.Emulator), identity, error);

    [Fact]
    public async Task ConfigurationPersistsAndRepeatedAddUpdatesTheSameWatch()
    {
        using var environment = new TestEnvironment();
        var backend = new Backend();
        var service = new AppWatchService(environment.RootPath, backend);
        var first = await service.AddAsync("test.app", "ios", "simulator", ["Documents/notes.sqlite"]);
        await service.SetEnabledAsync(first.Id, false);
        Assert.False(Assert.Single(new AppWatchService(environment.RootPath, backend).List()).Watch.Enabled);
        var second = await service.AddAsync("test.app", "ios", "simulator", ["Documents/new.sqlite"]);
        Assert.Equal(first.Id, second.Id);
        second.CaptureFiles[0] = "changed-by-caller";
        var reloaded = new AppWatchService(environment.RootPath, backend);
        var status = Assert.Single(reloaded.List());
        Assert.True(status.Watch.Enabled);
        Assert.Equal("Documents/new.sqlite", Assert.Single(status.Watch.CaptureFiles));
        Assert.Equal("Documents/new.sqlite", Assert.Single(service.List()[0].Watch.CaptureFiles));
        await reloaded.RemoveAsync(first.Id);
        Assert.Empty(new AppWatchService(environment.RootPath, backend).List());
    }

    [Theory]
    [InlineData("test.app", "windows", "device", "file")]
    [InlineData("bad;app", "ios", "device", "file")]
    [InlineData("test.app", "ios", "", "file")]
    [InlineData("test.app", "ios", "device", "../file")]
    [InlineData("test.app", "android", "device", "/file")]
    public async Task InvalidConfigurationsAreRejected(string app, string platform, string device, string file)
    {
        using var environment = new TestEnvironment();
        var service = new AppWatchService(environment.RootPath, new Backend());
        await Assert.ThrowsAsync<ArgumentException>(() => service.AddAsync(app, platform, device, [file]));
        Assert.Empty(service.List());
    }

    [Fact]
    public async Task ExistingProcessAttachesOnceAndTwoConfirmedAbsencesFinalizeWithFiles()
    {
        using var environment = new TestEnvironment();
        var backend = new Backend();
        var service = new AppWatchService(environment.RootPath, backend);
        await service.AddAsync("test.app", "ios", "simulator", ["Documents/notes.sqlite"]);
        await service.PollAsync();
        await service.PollAsync(); // Background/foreground transitions do not change process identity.
        Assert.Equal("capturing", Assert.Single(service.List()).State);
        var capture = Assert.Single(backend.Captures);
        backend.Identity = null;
        await service.PollAsync();
        Assert.Equal(0, capture.Stops);
        backend.Failure = new IOException("Transient device connection failure");
        await service.PollAsync();
        Assert.Equal("capturing-probe-error", service.List()[0].State);
        backend.Failure = null;
        await service.PollAsync();
        Assert.Equal(0, capture.Stops); // Errors reset the absence confirmation.
        await service.PollAsync();
        Assert.Equal("process.exited", capture.Reason);
        Assert.Equal("Documents/notes.sqlite", Assert.Single(capture.Files));
        Assert.Equal(capture.SessionId, service.List()[0].LastSessionId);
        Assert.Null(service.List()[0].SessionId);
        Assert.Equal("waiting-for-app", service.List()[0].State);
    }

    [Fact]
    public async Task RestartCreatesANewSessionAndSkipsFilesFromTheReplacementProcess()
    {
        using var environment = new TestEnvironment();
        var backend = new Backend();
        var service = new AppWatchService(environment.RootPath, backend);
        await service.AddAsync("test.app", "android", "simulator", ["files/notes.sqlite"]);
        await service.PollAsync();
        backend.Identity = "42:second-birth"; // PID reuse also counts as restart.
        await service.PollAsync();
        Assert.Equal(2, backend.Captures.Count);
        Assert.Equal("process.restarted", backend.Captures[0].Reason);
        Assert.Empty(backend.Captures[0].Files);
        Assert.Equal(backend.Captures[1].SessionId, service.List()[0].SessionId);
        Assert.Equal(backend.Captures[0].SessionId, service.List()[0].LastSessionId);
    }

    [Fact]
    public async Task BusyDeviceWaitsAndUnavailableDeviceFinalizesWithoutFiles()
    {
        using var environment = new TestEnvironment();
        var backend = new Backend { Busy = true };
        var service = new AppWatchService(environment.RootPath, backend);
        await service.AddAsync("test.app", "ios", "simulator", ["Documents/notes.sqlite"]);
        await service.PollAsync();
        Assert.Equal("waiting-for-device-owner", service.List()[0].State);
        Assert.Empty(backend.Captures);
        backend.Busy = false;
        await service.PollAsync();
        backend.Available = false;
        await service.PollAsync();
        await service.PollAsync();
        Assert.Equal("waiting-for-device", service.List()[0].State);
        var capture = Assert.Single(backend.Captures);
        Assert.Equal("device.unavailable", capture.Reason);
        Assert.Empty(capture.Files);
        backend.Available = true;
        await service.PollAsync();
        Assert.Equal(2, backend.Captures.Count);
    }

    [Fact]
    public async Task DisableAndRemoveDrainActiveSessionsAndReleaseOwnershipOnce()
    {
        using var environment = new TestEnvironment();
        var backend = new Backend();
        var service = new AppWatchService(environment.RootPath, backend);
        var watch = await service.AddAsync("test.app", "ios", "simulator", ["Documents/notes.sqlite"]);
        await service.PollAsync();
        await service.SetEnabledAsync(watch.Id, false);
        await service.PollAsync();
        Assert.Equal("disabled", service.List()[0].State);
        Assert.Equal("watch.disabled", Assert.Single(backend.Captures).Reason);
        Assert.Single(backend.Captures[0].Files);
        await service.SetEnabledAsync(watch.Id, true);
        await service.PollAsync();
        await service.RemoveAsync(watch.Id);
        Assert.Empty(service.List());
        Assert.Equal("watch.removed", backend.Captures[1].Reason);
        Assert.All(backend.Captures, capture => Assert.Equal(1, capture.Stops));
    }

    [Fact]
    public async Task HostShutdownFinalizesAndReloadedWatchAttachesToStillRunningProcess()
    {
        using var environment = new TestEnvironment();
        var backend = new Backend();
        var service = new AppWatchService(environment.RootPath, backend);
        await service.AddAsync("test.app", "ios", "simulator", []);
        service.Start(CancellationToken.None);
        await backend.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync();
        await service.StopAsync();
        Assert.False(service.IsRunning);
        Assert.Equal("host.stopped", Assert.Single(backend.Captures).Reason);
        Assert.Equal(1, backend.Captures[0].Stops);
        var restored = new AppWatchService(environment.RootPath, backend);
        await restored.PollAsync();
        Assert.Equal(2, backend.Captures.Count);
        Assert.Equal("capturing", restored.List()[0].State);
        await restored.SetEnabledAsync(restored.List()[0].Watch.Id, false);
    }

    [Fact]
    public async Task FinalizationFailureRemainsVisibleAndDoesNotBlockANewRun()
    {
        using var environment = new TestEnvironment();
        var backend = new Backend();
        var service = new AppWatchService(environment.RootPath, backend);
        await service.AddAsync("test.app", "ios", "simulator", []);
        await service.PollAsync();
        backend.Captures[0].FailStop = true;
        backend.Identity = "43:next";
        await service.PollAsync();
        Assert.Equal(2, backend.Captures.Count);
        Assert.Contains("Finalizing", service.List()[0].Message);
        Assert.Equal("capturing", service.List()[0].State);
    }

    private sealed class Backend : IAppWatchBackend
    {
        public string? Identity { get; set; } = "42:first-birth";
        public bool Available { get; set; } = true;
        public bool Busy { get; set; }
        public HashSet<string> BusyDevices { get; } = [];
        public Exception? Failure { get; set; }
        public List<Capture> Captures { get; } = [];
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<AppWatchObservation>? Observations { get; set; }
        public string? InventoryWarning { get; set; }
        public Task<AppWatchDiscovery> ObserveAsync(AppWatchDefinition watch, CancellationToken cancellationToken)
            => Failure is not null ? Task.FromException<AppWatchDiscovery>(Failure) : Task.FromResult(new AppWatchDiscovery(
                Observations ?? (Available ? [new AppWatchObservation(new DeviceDescriptor("simulator", "Phone", watch.Platform ?? "ios", "test", "booted", true, true,
                    watch.Platform == "android" ? DeviceKinds.Emulator : DeviceKinds.Simulator), Identity)] : []), InventoryWarning));
        public Task<IAppWatchCapture?> TryStartAsync(AppWatchDefinition watch, AppWatchObservation observation, CancellationToken cancellationToken)
        {
            if (Busy || BusyDevices.Contains(observation.Device.Identifier)) return Task.FromResult<IAppWatchCapture?>(null);
            var capture = new Capture(Guid.NewGuid().ToString("N"))
            {
                DeviceId = observation.Device.Identifier,
                ScreenshotIntervalMilliseconds = watch.ScreenshotIntervalMilliseconds
            };
            Captures.Add(capture);
            Started.TrySetResult();
            return Task.FromResult<IAppWatchCapture?>(capture);
        }
    }

    private sealed class Capture(string sessionId) : IAppWatchCapture
    {
        public string? DeviceId { get; init; }
        public string SessionId { get; } = sessionId;
        public bool IsActive { get; set; } = true;
        public int Stops { get; private set; }
        public string? Reason { get; private set; }
        public IReadOnlyList<string> Files { get; private set; } = [];
        public bool FailStop { get; set; }
        public int ScreenshotIntervalMilliseconds { get; set; }
        public List<LifecycleObservation> AppStates { get; } = [];
        public void SetAppState(global::Ansight.AppLifecycleState state, DateTimeOffset observedAtUtc)
            => AppStates.Add(new LifecycleObservation(state, observedAtUtc));
        public void SetScreenshotInterval(int intervalMilliseconds)
            => ScreenshotIntervalMilliseconds = intervalMilliseconds;
        public Task StopAsync(string reason, IReadOnlyList<string> captureFiles)
        {
            Stops++;
            Reason = reason;
            Files = captureFiles;
            return FailStop ? Task.FromException(new IOException("File read failed")) : Task.CompletedTask;
        }
    }

    private sealed record LifecycleObservation(global::Ansight.AppLifecycleState State, DateTimeOffset ObservedAtUtc);
}
