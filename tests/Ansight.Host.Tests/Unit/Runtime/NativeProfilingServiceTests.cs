using Ansight.Host.Runtime.NativeProfiling;
using Ansight.Host.Tests.TestSupport;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class NativeProfilingServiceTests
{
    [Fact]
    public async Task ProcessSample_CompletesAndPersistsProcessMetadataAndCallGraph()
    {
        using var environment = new TestEnvironment();
        using var service = new NativeProfilingEngine(
            environment.ApplicationPaths,
            processSampleAdapter: new CompletingProcessSampleAdapter());

        var captureId = await service.StartProcessSampleAsync(
            new ProcessSampleCaptureRequest(
                Environment.ProcessId,
                TimeSpan.FromSeconds(3),
                1));
        var snapshot = await WaitForTerminalCaptureAsync(service, captureId);

        Assert.Equal(NativeProfileCaptureState.Completed, snapshot.State);
        Assert.Equal(NativeProfileCaptureManifest.ProcessSampleSchema, snapshot.Manifest.Schema);
        Assert.Equal(NativeProfilePlatforms.Process, snapshot.Manifest.Platform);
        Assert.Equal(NativeProfilePresets.StackSample, snapshot.Manifest.Preset);
        Assert.Equal(Environment.ProcessId, snapshot.Manifest.ProcessId);
        Assert.Equal(1, snapshot.Manifest.SampleIntervalMilliseconds);
        Assert.False(string.IsNullOrWhiteSpace(snapshot.Manifest.ProcessName));
        Assert.Contains(
            snapshot.Manifest.Artifacts,
            artifact => artifact.Kind == ProcessSampleCaptureAdapter.ArtifactKind
                        && artifact.Authoritative
                        && artifact.Length > 0);
        Assert.Equal("/fake/sample", snapshot.Manifest.CaptureToolPath);
    }

    [Fact]
    public async Task ProcessSample_RejectsSecondActiveSampleForPidAndCanCancelFirst()
    {
        using var environment = new TestEnvironment();
        using var service = new NativeProfilingEngine(
            environment.ApplicationPaths,
            processSampleAdapter: new BlockingProcessSampleAdapter());
        var request = new ProcessSampleCaptureRequest(
            Environment.ProcessId,
            TimeSpan.FromSeconds(3),
            1);

        var captureId = await service.StartProcessSampleAsync(request);
        await WaitForCaptureStateAsync(service, captureId, NativeProfileCaptureState.Capturing);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.StartProcessSampleAsync(request));
        Assert.Contains("already has active stack sample", exception.Message, StringComparison.Ordinal);
        Assert.True(service.CancelCapture(captureId));

        var snapshot = await WaitForTerminalCaptureAsync(service, captureId);
        Assert.Equal(NativeProfileCaptureState.Cancelled, snapshot.State);
        Assert.Equal("cancelled", snapshot.Manifest.StopReason);
    }

    [Fact]
    public async Task Capture_CompletesDevicePreparationAndPersistsAuthoritativeArtifact()
    {
        using var environment = new TestEnvironment();
        var applicationPath = CreateAndroidArtifact(environment);
        var devices = CreateAndroidDeviceService();
        using var service = new NativeProfilingEngine(
            environment.ApplicationPaths,
            deviceService: devices,
            adapters: [new CompletingAdapter()]);

        var captureId = await service.StartCaptureAsync(CreateRequest(applicationPath));
        var snapshot = await WaitForTerminalCaptureAsync(service, captureId);

        Assert.Equal(NativeProfileCaptureState.Completed, snapshot.State);
        Assert.Equal("capture-tool-completed", snapshot.Manifest.StopReason);
        Assert.Contains(
            snapshot.Manifest.Artifacts,
            artifact => artifact.Kind == CompletingAdapter.ArtifactKind
                        && artifact.Authoritative
                        && artifact.Length == 4);
        Assert.Equal(["install", "terminate"], devices.Operations);
        Assert.Equal(applicationPath, devices.InstalledApplicationPath);
        Assert.Equal("com.example.app", devices.TerminatedApplicationIdentifier);
        Assert.Equal("/fake/adb", snapshot.Manifest.CaptureToolPath);
        Assert.NotEmpty(snapshot.RecentLogLines);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public async Task Capture_RespectsWindowPreference(bool isBooted, bool headless, bool commandHeadless)
    {
        using var environment = new TestEnvironment();
        var applicationPath = CreateAndroidArtifact(environment);
        var devices = CreateAndroidDeviceService();
        devices.Inventory = devices.Inventory with
        {
            Devices = devices.Inventory.Devices.Select(device => device with { IsBooted = isBooted }).ToArray()
        };
        using var service = new NativeProfilingEngine(
            environment.ApplicationPaths,
            deviceService: devices,
            adapters: [new CompletingAdapter()]);

        string captureId;
        using (DeviceLaunchContext.Push(commandHeadless))
        {
            captureId = await service.StartCaptureAsync(CreateRequest(applicationPath) with { Headless = headless });
        }

        var snapshot = await WaitForTerminalCaptureAsync(service, captureId);

        Assert.Equal(NativeProfileCaptureState.Completed, snapshot.State);
        if (isBooted)
        {
            Assert.Empty(devices.StartedDeviceIdentifiers);
            Assert.Equal(headless || commandHeadless ? 0 : 1, devices.ShownDeviceIdentifiers.Count);
        }
        else
        {
            Assert.Equal(["emulator-5554"], devices.StartedDeviceIdentifiers);
            Assert.Equal(headless || commandHeadless, devices.StartOptions!.Headless);
            Assert.Empty(devices.ShownDeviceIdentifiers);
        }
    }

    [Fact]
    public async Task StartCapture_RejectsSecondActiveCaptureForSameDeviceAndCanCancelFirst()
    {
        using var environment = new TestEnvironment();
        var applicationPath = CreateAndroidArtifact(environment);
        var devices = CreateAndroidDeviceService();
        using var service = new NativeProfilingEngine(
            environment.ApplicationPaths,
            deviceService: devices,
            adapters: [new BlockingAdapter()]);

        var captureId = await service.StartCaptureAsync(CreateRequest(applicationPath));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.StartCaptureAsync(CreateRequest(applicationPath)));
        Assert.Contains("already has active native capture", exception.Message, StringComparison.Ordinal);
        Assert.True(service.CancelCapture(captureId));

        var snapshot = await WaitForTerminalCaptureAsync(service, captureId);
        Assert.Equal(NativeProfileCaptureState.Cancelled, snapshot.State);
        Assert.Equal("cancelled", snapshot.Manifest.StopReason);
    }

    private static NativeProfileCaptureRequest CreateRequest(string applicationPath)
        => new(
            NativeProfilePlatforms.Android,
            applicationPath,
            "com.example.app",
            "emulator-5554",
            NativeProfilePresets.System,
            TimeSpan.FromSeconds(1));

    private static FakeHostDeviceService CreateAndroidDeviceService()
        => new()
        {
            Inventory = new DeviceInventory(
                [],
                [
                    new DeviceDescriptor(
                        "emulator-5554",
                        "Test emulator",
                        DevicePlatforms.Android,
                        "Android 16",
                        "booted",
                        IsBooted: true,
                        IsAvailable: true,
                        DeviceKinds.Emulator)
                ],
                [])
        };

    private static string CreateAndroidArtifact(TestEnvironment environment)
    {
        var applicationPath = Path.Combine(environment.RootPath, "TestApp.apk");
        File.WriteAllBytes(applicationPath, [1, 2, 3]);
        return applicationPath;
    }

    private static async Task<NativeProfileCaptureSnapshot> WaitForTerminalCaptureAsync(
        NativeProfilingEngine service,
        string captureId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            if (service.TryGetCapture(captureId, out var snapshot)
                && snapshot is not null
                && snapshot.IsTerminal)
            {
                return snapshot;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
        }
    }

    private static async Task WaitForCaptureStateAsync(
        NativeProfilingEngine service,
        string captureId,
        NativeProfileCaptureState expectedState)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            if (service.TryGetCapture(captureId, out var snapshot)
                && snapshot?.State == expectedState)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
        }
    }

    private sealed class CompletingAdapter : INativeProfileCaptureAdapter
    {
        public const string ArtifactKind = "test-perfetto-trace";

        public string Platform => NativeProfilePlatforms.Android;

        public string Engine => "test-perfetto";

        public bool SupportsPreset(string preset)
            => preset == NativeProfilePresets.System;

        public Task<NativeProfileToolchain> GetToolchainAsync(CancellationToken cancellationToken)
            => Task.FromResult(new NativeProfileToolchain(
                Platform,
                Engine,
                true,
                "/fake/adb",
                "test-adb 1.0",
                null,
                "Available."));

        public async Task<NativeProfileAdapterCaptureResult> CaptureAsync(
            NativeProfileAdapterContext context,
            CancellationToken cancellationToken)
        {
            var artifactPath = Path.Combine(context.CapturePath, "raw", "capture.perfetto-trace");
            await File.WriteAllBytesAsync(artifactPath, [1, 2, 3, 4], cancellationToken);
            return new NativeProfileAdapterCaptureResult(
                [
                    new NativeProfileProducedArtifact(
                        ArtifactKind,
                        artifactPath,
                        IsDirectory: false,
                        Authoritative: true)
                ],
                []);
        }
    }

    private sealed class BlockingAdapter : INativeProfileCaptureAdapter
    {
        public string Platform => NativeProfilePlatforms.Android;

        public string Engine => "blocking-perfetto";

        public bool SupportsPreset(string preset)
            => preset == NativeProfilePresets.System;

        public Task<NativeProfileToolchain> GetToolchainAsync(CancellationToken cancellationToken)
            => Task.FromResult(new NativeProfileToolchain(
                Platform,
                Engine,
                true,
                "/fake/adb",
                "test-adb 1.0",
                null,
                "Available."));

        public async Task<NativeProfileAdapterCaptureResult> CaptureAsync(
            NativeProfileAdapterContext context,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The blocking adapter should only exit through cancellation.");
        }
    }

    private sealed class CompletingProcessSampleAdapter : IProcessSampleCaptureAdapter
    {
        public string Engine => "sample";

        public Task<NativeProfileToolchain> GetToolchainAsync(CancellationToken cancellationToken)
            => Task.FromResult(new NativeProfileToolchain(
                NativeProfilePlatforms.Process,
                Engine,
                true,
                "/fake/sample",
                null,
                null,
                "Available."));

        public async Task<NativeProfileAdapterCaptureResult> CaptureAsync(
            ProcessSampleAdapterContext context,
            CancellationToken cancellationToken)
        {
            var artifactPath = Path.Combine(context.CapturePath, "raw", "sample.txt");
            await File.WriteAllTextAsync(artifactPath, "Call graph:\n", cancellationToken);
            return new NativeProfileAdapterCaptureResult(
                [
                    new NativeProfileProducedArtifact(
                        ProcessSampleCaptureAdapter.ArtifactKind,
                        artifactPath,
                        IsDirectory: false,
                        Authoritative: true)
                ],
                []);
        }
    }

    private sealed class BlockingProcessSampleAdapter : IProcessSampleCaptureAdapter
    {
        public string Engine => "sample";

        public Task<NativeProfileToolchain> GetToolchainAsync(CancellationToken cancellationToken)
            => Task.FromResult(new NativeProfileToolchain(
                NativeProfilePlatforms.Process,
                Engine,
                true,
                "/fake/sample",
                null,
                null,
                "Available."));

        public async Task<NativeProfileAdapterCaptureResult> CaptureAsync(
            ProcessSampleAdapterContext context,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The blocking sampler should only exit through cancellation.");
        }
    }
}
