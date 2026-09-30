using Ansight.Host.Runtime.NativeProfiling;
using Ansight.Host.Tests.TestSupport;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class NativeProfileCaptureAdapterTests
{
    [Fact]
    public async Task ProcessSampleCapture_UsesPidDurationAndIntervalAndRetainsCallGraph()
    {
        using var environment = new TestEnvironment();
        var capturePath = Path.Combine(environment.RootPath, "process-sample-capture");
        Directory.CreateDirectory(Path.Combine(capturePath, "raw"));
        var runner = new SampleProcessRunner();
        var adapter = new ProcessSampleCaptureAdapter(runner, "/fake/sample");

        var result = await adapter.CaptureAsync(
            new ProcessSampleAdapterContext(
                capturePath,
                1234,
                TimeSpan.FromSeconds(3),
                1,
                _ => { }),
            CancellationToken.None);

        var request = Assert.Single(runner.Requests);
        Assert.Equal("/fake/sample", request.FileName);
        Assert.Equal(["1234", "3", "1", "-file", Path.Combine(capturePath, "raw", "sample.txt")], request.Arguments);
        Assert.Equal(TimeSpan.FromSeconds(18), request.Timeout);
        var artifact = Assert.Single(result.Artifacts);
        Assert.Equal(ProcessSampleCaptureAdapter.ArtifactKind, artifact.Kind);
        Assert.True(artifact.Authoritative);
        Assert.True(File.Exists(artifact.Path));
    }

    [Fact]
    public async Task InstrumentsCpuCapture_RecordsAndValidatesTraceBundle()
    {
        using var environment = new TestEnvironment();
        var capturePath = Path.Combine(environment.RootPath, "instruments-capture");
        Directory.CreateDirectory(Path.Combine(capturePath, "raw"));
        Directory.CreateDirectory(Path.Combine(capturePath, "derived"));
        var runner = new InstrumentsProcessRunner();
        var adapter = new InstrumentsProfileCaptureAdapter(runner);

        var result = await adapter.CaptureAsync(
            new NativeProfileAdapterContext(
                Guid.NewGuid().ToString("N"),
                capturePath,
                "/Applications/Xcode.app/usr/bin/xctrace",
                "/artifacts/App.app",
                "com.example.app",
                "simulator-id",
                NativeProfilePresets.Cpu,
                TimeSpan.FromSeconds(5),
                null!,
                _ => { }),
            CancellationToken.None);

        Assert.Equal(2, result.Artifacts.Count);
        Assert.Contains(result.Artifacts, artifact => artifact.Kind
                                                     == InstrumentsProfileCaptureAdapter.TraceArtifactKind
                                                     && artifact.IsDirectory
                                                     && artifact.Authoritative);
        Assert.Equal(2, runner.Requests.Count);
        Assert.Equal("/Applications/Xcode.app/usr/bin/xctrace", runner.Requests[0].FileName);
        Assert.Equal("record", runner.Requests[0].Arguments[0]);
        Assert.Contains("Time Profiler", runner.Requests[0].Arguments);
        Assert.Contains("simulator-id", runner.Requests[0].Arguments);
        Assert.Equal("/artifacts/App.app", runner.Requests[0].Arguments[^1]);
        Assert.Equal(TimeSpan.FromSeconds(7), runner.Requests[0].InterruptAfter);
        Assert.Contains("--toc", runner.Requests[1].Arguments);
    }

    [Theory]
    [InlineData(NativeProfilePresets.Memory, "Allocations")]
    [InlineData(NativeProfilePresets.Leaks, "Leaks")]
    public async Task InstrumentsMemoryCapture_UsesRequestedTemplate(string preset, string template)
    {
        using var environment = new TestEnvironment();
        var capturePath = Path.Combine(environment.RootPath, $"instruments-{preset}-capture");
        Directory.CreateDirectory(Path.Combine(capturePath, "raw"));
        Directory.CreateDirectory(Path.Combine(capturePath, "derived"));
        var runner = new InstrumentsProcessRunner();
        var adapter = new InstrumentsProfileCaptureAdapter(runner);

        await adapter.CaptureAsync(
            new NativeProfileAdapterContext(
                Guid.NewGuid().ToString("N"),
                capturePath,
                "/Applications/Xcode.app/usr/bin/xctrace",
                "/artifacts/App.app",
                "com.example.app",
                "simulator-id",
                preset,
                TimeSpan.FromSeconds(5),
                null!,
                _ => { }),
            CancellationToken.None);

        Assert.Contains(template, runner.Requests[0].Arguments);
    }

    [Fact]
    public async Task InstrumentsCapture_WhenRecordTimesOut_ReportsBoundedFailure()
    {
        using var environment = new TestEnvironment();
        var capturePath = Path.Combine(environment.RootPath, "timed-out-instruments-capture");
        Directory.CreateDirectory(Path.Combine(capturePath, "raw"));
        Directory.CreateDirectory(Path.Combine(capturePath, "derived"));
        var adapter = new InstrumentsProfileCaptureAdapter(new TimedOutProcessRunner());

        var exception = await Assert.ThrowsAsync<TimeoutException>(() => adapter.CaptureAsync(
            new NativeProfileAdapterContext(
                Guid.NewGuid().ToString("N"),
                capturePath,
                "/Applications/Xcode.app/usr/bin/xctrace",
                "/artifacts/App.app",
                "com.example.app",
                "simulator-id",
                NativeProfilePresets.Launch,
                TimeSpan.FromSeconds(1),
                null!,
                _ => { }),
            CancellationToken.None));

        Assert.Contains("timed out", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PerfettoCapture_StartsTraceBeforeColdApplicationLaunchAndPullsArtifact()
    {
        using var environment = new TestEnvironment();
        var capturePath = Path.Combine(environment.RootPath, "perfetto-capture");
        Directory.CreateDirectory(Path.Combine(capturePath, "raw"));
        Directory.CreateDirectory(Path.Combine(capturePath, "derived"));
        Directory.CreateDirectory(Path.Combine(capturePath, "diagnostics"));
        var runner = new PerfettoProcessRunner();
        var launcher = new RecordingAndroidProfileLauncher(
            () => runner.Requests.Any(request => request.Arguments.Contains("perfetto")));
        var adapter = new PerfettoProfileCaptureAdapter(runner, launcher, "/fake/adb");

        var result = await adapter.CaptureAsync(
            new NativeProfileAdapterContext(
                Guid.NewGuid().ToString("N"),
                capturePath,
                "/fake/adb",
                "/artifacts/App.apk",
                "com.example.app",
                "emulator-5554",
                NativeProfilePresets.Launch,
                TimeSpan.FromSeconds(2),
                null!,
                _ => { }),
            CancellationToken.None);

        Assert.True(launcher.WasLaunched);
        Assert.True(launcher.PerfettoHadStartedAtLaunch);
        Assert.Equal("emulator-5554", launcher.DeviceId);
        Assert.Contains(result.Artifacts, artifact => artifact.Kind
                                                     == PerfettoProfileCaptureAdapter.TraceArtifactKind
                                                     && artifact.Authoritative);
        var config = await File.ReadAllTextAsync(
            Path.Combine(capturePath, "diagnostics", "perfetto-config.pbtxt"));
        Assert.Contains("com.example.app", config, StringComparison.Ordinal);
        Assert.Contains("sched/sched_switch", config, StringComparison.Ordinal);
        var perfettoRequest = Assert.Single(
            runner.Requests,
            request => request.Arguments.Contains("perfetto"));
        Assert.Equal("/fake/adb", perfettoRequest.FileName);
        Assert.Equal("-", ReadOption(perfettoRequest.Arguments, "-c"));
        Assert.Contains("com.example.app", perfettoRequest.StandardInput, StringComparison.Ordinal);
        Assert.Contains(runner.Requests, request => request.Arguments.Contains("pull"));
    }

    [Theory]
    [InlineData(NativeProfilePresets.Memory, "linux.sys_stats", "proc_stats_poll_ms: 250", false)]
    [InlineData(NativeProfilePresets.NativeHeap, "android.heapprofd", "sampling_interval_bytes: 4096", false)]
    [InlineData(NativeProfilePresets.ManagedHeap, "android.java_hprof", "dump_smaps: true", true)]
    public async Task PerfettoMemoryCapture_UsesPresetSpecificDataSourceAndLaunchOrder(
        string preset,
        string dataSource,
        string configMarker,
        bool launchBeforeCapture)
    {
        using var environment = new TestEnvironment();
        var capturePath = Path.Combine(environment.RootPath, $"perfetto-{preset}-capture");
        Directory.CreateDirectory(Path.Combine(capturePath, "raw"));
        Directory.CreateDirectory(Path.Combine(capturePath, "derived"));
        Directory.CreateDirectory(Path.Combine(capturePath, "diagnostics"));
        var runner = new PerfettoProcessRunner();
        var launcher = new RecordingAndroidProfileLauncher(
            () => runner.Requests.Any(request => request.Arguments.Contains("perfetto")));
        var adapter = new PerfettoProfileCaptureAdapter(runner, launcher, "/fake/adb");

        var result = await adapter.CaptureAsync(
            new NativeProfileAdapterContext(
                Guid.NewGuid().ToString("N"),
                capturePath,
                "/fake/adb",
                "/artifacts/App.apk",
                "com.example.app",
                "emulator-5554",
                preset,
                TimeSpan.FromSeconds(2),
                null!,
                _ => { }),
            CancellationToken.None);

        var config = await File.ReadAllTextAsync(
            Path.Combine(capturePath, "diagnostics", "perfetto-config.pbtxt"));
        Assert.Contains(dataSource, config, StringComparison.Ordinal);
        Assert.Contains(configMarker, config, StringComparison.Ordinal);
        Assert.Equal(!launchBeforeCapture, launcher.PerfettoHadStartedAtLaunch);
        Assert.Contains(result.Artifacts, artifact => artifact.Authoritative);
        if (preset is NativeProfilePresets.NativeHeap or NativeProfilePresets.ManagedHeap)
        {
            Assert.Contains(result.Warnings, warning => warning.Contains(
                "profileable or debuggable",
                StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData(NativeProfilePresets.System, 28, "requires Android 10")]
    [InlineData(NativeProfilePresets.ManagedHeap, 29, "requires Android 11")]
    public async Task PerfettoCapture_RejectsUnsupportedAndroidApiLevel(
        string preset,
        int apiLevel,
        string expectedMessage)
    {
        using var environment = new TestEnvironment();
        var capturePath = Path.Combine(environment.RootPath, "perfetto-managed-heap-android-10");
        Directory.CreateDirectory(Path.Combine(capturePath, "raw"));
        Directory.CreateDirectory(Path.Combine(capturePath, "derived"));
        Directory.CreateDirectory(Path.Combine(capturePath, "diagnostics"));
        var adapter = new PerfettoProfileCaptureAdapter(
            new PerfettoProcessRunner(apiLevel),
            new RecordingAndroidProfileLauncher(),
            "/fake/adb");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.CaptureAsync(
            new NativeProfileAdapterContext(
                Guid.NewGuid().ToString("N"),
                capturePath,
                "/fake/adb",
                "/artifacts/App.apk",
                "com.example.app",
                "emulator-5554",
                preset,
                TimeSpan.FromSeconds(2),
                null!,
                _ => { }),
            CancellationToken.None));

        Assert.Contains(expectedMessage, exception.Message, StringComparison.Ordinal);
    }

    private sealed class InstrumentsProcessRunner : INativeProfilingProcessRunner
    {
        public List<NativeProfilingProcessRequest> Requests { get; } = [];

        public Task<NativeProfilingProcessResult> RunAsync(
            NativeProfilingProcessRequest request,
            Action<string>? outputReceived,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (request.Arguments.Contains("record"))
            {
                var outputPath = ReadOption(request.Arguments, "--output");
                Directory.CreateDirectory(Path.Combine(outputPath, "Trace1.run"));
                File.WriteAllBytes(Path.Combine(outputPath, "template.data"), [1, 2, 3]);
            }
            else if (request.Arguments.Contains("export"))
            {
                File.WriteAllText(ReadOption(request.Arguments, "--output"), "<trace-toc />");
            }

            return Task.FromResult(new NativeProfilingProcessResult(0, string.Empty, string.Empty, false));
        }
    }

    private sealed class SampleProcessRunner : INativeProfilingProcessRunner
    {
        public List<NativeProfilingProcessRequest> Requests { get; } = [];

        public async Task<NativeProfilingProcessResult> RunAsync(
            NativeProfilingProcessRequest request,
            Action<string>? outputReceived,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            await File.WriteAllTextAsync(
                ReadOption(request.Arguments, "-file"),
                "Call graph:\n    1 Thread_1\n",
                cancellationToken);
            return new NativeProfilingProcessResult(0, string.Empty, string.Empty, false);
        }
    }

    private sealed class TimedOutProcessRunner : INativeProfilingProcessRunner
    {
        public Task<NativeProfilingProcessResult> RunAsync(
            NativeProfilingProcessRequest request,
            Action<string>? outputReceived,
            CancellationToken cancellationToken)
            => Task.FromResult(new NativeProfilingProcessResult(-1, string.Empty, string.Empty, true));
    }

    private sealed class PerfettoProcessRunner : INativeProfilingProcessRunner
    {
        private readonly int apiLevel;

        public PerfettoProcessRunner(int apiLevel = 36)
        {
            this.apiLevel = apiLevel;
        }

        public List<NativeProfilingProcessRequest> Requests { get; } = [];

        public async Task<NativeProfilingProcessResult> RunAsync(
            NativeProfilingProcessRequest request,
            Action<string>? outputReceived,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (request.Arguments.SequenceEqual(["version"]))
            {
                return new NativeProfilingProcessResult(0, "Android Debug Bridge 1.0.41", string.Empty, false);
            }

            if (request.Arguments.Contains("getprop"))
            {
                return new NativeProfilingProcessResult(0, apiLevel.ToString(), string.Empty, false);
            }

            if (request.Arguments.Contains("perfetto"))
            {
                await Task.Delay(10, cancellationToken);
            }

            if (request.Arguments.Contains("pull"))
            {
                await File.WriteAllBytesAsync(request.Arguments[^1], [1, 2, 3, 4], cancellationToken);
            }

            return new NativeProfilingProcessResult(0, string.Empty, string.Empty, false);
        }
    }

    private sealed class RecordingAndroidProfileLauncher : IAndroidProfileLauncher
    {
        private readonly Func<bool>? hasPerfettoStarted;

        public RecordingAndroidProfileLauncher(Func<bool>? hasPerfettoStarted = null)
        {
            this.hasPerfettoStarted = hasPerfettoStarted;
        }

        public bool WasLaunched { get; private set; }

        public string? DeviceId { get; private set; }

        public bool? PerfettoHadStartedAtLaunch { get; private set; }

        public Task LaunchAsync(
            string adbPath,
            string deviceId,
            string appId,
            CancellationToken cancellationToken)
        {
            WasLaunched = true;
            DeviceId = deviceId;
            PerfettoHadStartedAtLaunch = hasPerfettoStarted?.Invoke();
            return Task.CompletedTask;
        }
    }

    private static string ReadOption(IReadOnlyList<string> arguments, string option)
    {
        var index = arguments.ToList().IndexOf(option);
        Assert.InRange(index, 0, arguments.Count - 2);
        return arguments[index + 1];
    }
}
