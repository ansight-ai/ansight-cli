using Ansight.Host.Runtime.DeviceExecution;
using Ansight.Host.Runtime.NativeProfiling;
using Ansight.Host.Tests.TestSupport;
using Ansight.Infrastructure.Security;
using System.IO.Compression;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class PhysicalIosInstrumentsCaptureTests
{
    [Fact]
    public void ActivityMonitorRecordsDeviceWideWithoutRequiringAnAttachableApp()
    {
        var arguments = PhysicalIosInstrumentsCapture.BuildRecordingArguments(
            PhysicalIosInstrumentsCapture.ActivityMonitorTemplate, "iphone-udid", 12583, "capture.trace");

        Assert.Contains("--all-processes", arguments);
        Assert.DoesNotContain("--attach", arguments);
        Assert.DoesNotContain("--launch", arguments);
        Assert.Equal("iphone-udid", arguments[arguments.ToList().IndexOf("--device") + 1]);
    }

    [Fact]
    public void SimulatorTimeProfilerStillAttachesToTheAppProcess()
    {
        var arguments = PhysicalIosInstrumentsCapture.BuildRecordingArguments(
            PhysicalIosInstrumentsCapture.TimeProfilerTemplate, "simulator-udid", 12583, "capture.trace");

        Assert.DoesNotContain("--all-processes", arguments);
        Assert.Contains("--attach", arguments);
        Assert.Equal("12583", arguments[arguments.ToList().IndexOf("--attach") + 1]);
    }

    [Theory]
    [InlineData("17314", 17314)]
    [InlineData("17314:2026-09-25T00:00:00Z", 17314)]
    public void ParsesWatchedProcessIdentity(string identity, int expected)
    {
        Assert.True(PhysicalIosInstrumentsCapture.TryParseProcessId(identity, out var processId));
        Assert.Equal(expected, processId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("not-a-process")]
    public void RejectsInvalidProcessIdentity(string identity)
        => Assert.False(PhysicalIosInstrumentsCapture.TryParseProcessId(identity, out _));

    [Fact]
    public void RejectsTraceContainingOnlyAnInstrumentsRunIssue()
    {
        var trace = Path.Combine(Path.GetTempPath(), "ansight-empty-trace-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(trace, "Trace1.run"));
            File.WriteAllText(Path.Combine(trace, "Trace1.run", "RunIssues.storedata"), "unsupported");
            Assert.False(PhysicalIosInstrumentsCapture.HasUsableTrace(trace));

            Directory.CreateDirectory(Path.Combine(trace, "corespace"));
            File.WriteAllText(Path.Combine(trace, "corespace", "sample.data"), "sample");
            Assert.True(PhysicalIosInstrumentsCapture.HasUsableTrace(trace));
        }
        finally { Directory.Delete(trace, recursive: true); }
    }

    [Fact]
    public void ImportsOnlyWatchedPidAndResolvesInstrumentsReferences()
    {
        var path = Path.Combine(Path.GetTempPath(), "ansight-activity-" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            File.WriteAllText(path, """
                <trace-query-result><node><schema name="activity-monitor-process-live">
                <col><mnemonic>start</mnemonic></col><col><mnemonic>pid</mnemonic></col>
                <col><mnemonic>cpu-percent</mnemonic></col>
                <col><mnemonic>memory-physical-footprint</mnemonic></col>
                <col><mnemonic>memory-real</mnemonic></col></schema>
                <row><start-time id="1">0</start-time><pid id="2">17843</pid><sentinel/>
                <size-in-bytes id="3">84395168</size-in-bytes><size-in-bytes>257343488</size-in-bytes></row>
                <row><start-time>1000000000</start-time><pid ref="2"/>
                <system-cpu-percent>51.335</system-cpu-percent><size-in-bytes ref="3"/>
                <size-in-bytes>257228800</size-in-bytes></row>
                <row><start-time>2000000000</start-time><pid>99</pid>
                <system-cpu-percent>90</system-cpu-percent><size-in-bytes>1</size-in-bytes>
                <size-in-bytes>1</size-in-bytes></row></node></trace-query-result>
                """);
            var started = DateTimeOffset.Parse("2026-09-25T03:08:26Z");

            var samples = PhysicalIosInstrumentsMetrics.Parse(path, started, 17843);

            Assert.Equal(2, samples.Count);
            Assert.Equal(started, samples[0].CapturedAtUtc);
            Assert.Null(samples[0].CpuMillicores);
            Assert.Equal(84395168, samples[0].PhysicalFootprintBytes);
            Assert.Equal(started.AddSeconds(1), samples[1].CapturedAtUtc);
            Assert.Equal(513, samples[1].CpuMillicores);
            Assert.Equal(84395168, samples[1].PhysicalFootprintBytes);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void FinalizedSessionRetainsOnlyWatchedPidTelemetryAfterReload()
    {
        using var environment = new TestEnvironment();
        var store = new SessionCaptureStore(environment.ApplicationPaths);
        var state = new RuntimeState(store);
        var sessionId = state.CreateDeviceSession(new WorkspaceTestTarget("ios", "iphone-udid", "iPhone",
            "test.app", false, false, false) { DeviceKind = DeviceKinds.Physical, ExecutionMode = "device" });
        state.EndDeviceSession(sessionId);
        var path = Path.Combine(environment.RootPath, "activity-monitor-process.xml");
        File.WriteAllText(path, """
            <trace-query-result><node><schema name="activity-monitor-process-live">
            <col><mnemonic>start</mnemonic></col><col><mnemonic>pid</mnemonic></col>
            <col><mnemonic>cpu-percent</mnemonic></col>
            <col><mnemonic>memory-physical-footprint</mnemonic></col>
            <col><mnemonic>memory-real</mnemonic></col></schema>
            <row><start-time>0</start-time><pid>12583</pid>
            <system-cpu-percent>28.745141400</system-cpu-percent>
            <size-in-bytes>97682568</size-in-bytes><size-in-bytes>198508544</size-in-bytes></row>
            <row><start-time>0</start-time><pid>99</pid>
            <system-cpu-percent>90</system-cpu-percent>
            <size-in-bytes>1</size-in-bytes><size-in-bytes>1</size-in-bytes></row>
            </node></trace-query-result>
            """);
        var startedUtc = DateTimeOffset.UtcNow;

        var result = PhysicalIosInstrumentsMetrics.Ingest(state, sessionId, path, startedUtc, 12583);
        PhysicalIosInstrumentsMetrics.MarkIngested(state, sessionId, result);
        Assert.True(state.TryGetSessionSnapshot(sessionId, out var session));
        store.Save(session!);
        var reloadedStore = new SessionCaptureStore(environment.ApplicationPaths);

        Assert.True(reloadedStore.TryLoad(sessionId, out var saved));
        Assert.Equal("Completed", saved!.Status);
        Assert.Equal(3, saved.MetricChannels.Count);
        Assert.Equal(3, saved.Metrics.Count);
        Assert.Equal(new long[] { 287, 97682568, 198508544 }, saved.Metrics.Select(sample => sample.Value));
        Assert.All(saved.Metrics, sample => Assert.Equal(startedUtc, sample.CapturedAtUtc));
        Assert.True(saved.CustomProperties!["instruments"]!["sampleMetricsIngested"]!.GetValue<bool>());
    }

    [Fact]
    public async Task RecoversMetricsFromRetainedTraceWhenXmlWasNotSaved()
    {
        if (!OperatingSystem.IsMacOS()) return;
        using var environment = new TestEnvironment();
        var composition = new MefHostComposition(environment.ApplicationPaths,
            new FileBackedEncryptedStorage(environment.SecureStorageFilePath));
        var state = composition.Get<IRuntimeState>();
        var sessionId = state.CreateSession("com.example.app", "Example", IPAddress.Loopback,
            configId: null, processSessionId: null);
        var directory = Path.Combine(Path.GetTempPath(), "ansight-trace-recovery-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var trace = Path.Combine(directory, "source", "capture.trace", "corespace");
            Directory.CreateDirectory(trace);
            File.WriteAllText(Path.Combine(trace, "sample.data"), "sample");
            var archive = Path.Combine(directory, "capture.trace.zip");
            ZipFile.CreateFromDirectory(Path.Combine(directory, "source"), archive);
            var started = DateTimeOffset.Parse("2026-09-25T03:08:26Z");

            var result = await PhysicalIosInstrumentsTraceRecovery.IngestAsync(state, sessionId,
                archive, directory, started, 17843, processRunner: new SampleExportRunner());

            Assert.Equal(1, result.RowCount);
            Assert.True(state.TryGetSessionSnapshot(sessionId, out var session));
            Assert.Equal(2, session!.MetricChannels.Count);
            Assert.Equal(2, session.Metrics.Count);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task RecoversRawProcessCountersWhenDerivedTableCrashes()
    {
        if (!OperatingSystem.IsMacOS()) return;
        using var environment = new TestEnvironment();
        var state = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        var sessionId = state.CreateDeviceSession(new WorkspaceTestTarget("ios", "iphone-udid", "iPhone",
            "test.app", false, false, false) { DeviceKind = DeviceKinds.Physical, ExecutionMode = "device" });
        var trace = Path.Combine(environment.RootPath, "source", "capture.trace", "corespace");
        Directory.CreateDirectory(trace);
        File.WriteAllText(Path.Combine(trace, "sample.data"), "sample");
        var archive = Path.Combine(environment.RootPath, "capture.trace.zip");
        ZipFile.CreateFromDirectory(Path.Combine(environment.RootPath, "source"), archive);
        var samplesPath = Path.Combine(environment.RootPath, "recovered.xml");
        var startedUtc = DateTimeOffset.UtcNow;

        var result = await PhysicalIosInstrumentsTraceRecovery.IngestAsync(state, sessionId,
            archive, environment.RootPath, startedUtc, 12588, processRunner: new RawSampleExportRunner(),
            exportedSamplesPath: samplesPath);

        Assert.Equal("sysmon-process", result.TableSchema);
        Assert.Equal(1, result.RowCount);
        Assert.True(state.TryGetSessionSnapshot(sessionId, out var session));
        Assert.Equal(3, session!.MetricChannels.Count);
        Assert.Equal(new long[] { 287, 97928328, 206045184 }, session.Metrics.Select(sample => sample.Value));
        Assert.All(session.Metrics, sample => Assert.Equal(startedUtc.AddSeconds(1), sample.CapturedAtUtc));
        Assert.Single(PhysicalIosInstrumentsMetrics.Parse(samplesPath, startedUtc, 12588));
    }

    [Fact]
    public async Task ReportsXctraceCrashWhenRetainedTraceCannotBeExported()
    {
        if (!OperatingSystem.IsMacOS()) return;
        using var environment = new TestEnvironment();
        var composition = new MefHostComposition(environment.ApplicationPaths,
            new FileBackedEncryptedStorage(environment.SecureStorageFilePath));
        var state = composition.Get<IRuntimeState>();
        var sessionId = state.CreateSession("com.example.app", "Example", IPAddress.Loopback,
            configId: null, processSessionId: null);
        var directory = Path.Combine(Path.GetTempPath(), "ansight-trace-crash-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var trace = Path.Combine(directory, "source", "capture.trace", "corespace");
            Directory.CreateDirectory(trace);
            File.WriteAllText(Path.Combine(trace, "sample.data"), "sample");
            var archive = Path.Combine(directory, "capture.trace.zip");
            ZipFile.CreateFromDirectory(Path.Combine(directory, "source"), archive);

            var error = await Assert.ThrowsAsync<IOException>(() =>
                PhysicalIosInstrumentsTraceRecovery.IngestAsync(state, sessionId, archive, directory,
                    DateTimeOffset.UtcNow, 17843, processRunner: new SampleExportRunner(139)));

            Assert.Contains("exit 139", error.Message);
            Assert.True(state.TryGetSessionSnapshot(sessionId, out var session));
            Assert.Empty(session!.Metrics);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private sealed class SampleExportRunner(int exitCode = 0) : INativeProfilingProcessRunner
    {
        public Task<NativeProfilingProcessResult> RunAsync(NativeProfilingProcessRequest request,
            Action<string>? outputReceived, CancellationToken cancellationToken)
        {
            Assert.Contains("--xpath", request.Arguments);
            if (exitCode == 0)
            {
                var output = request.Arguments[Array.IndexOf(request.Arguments.ToArray(), "--output") + 1];
                File.WriteAllText(output, """
                <trace-query-result><node><schema name="activity-monitor-process-live">
                <col><mnemonic>start</mnemonic></col><col><mnemonic>pid</mnemonic></col>
                <col><mnemonic>cpu-percent</mnemonic></col>
                <col><mnemonic>memory-physical-footprint</mnemonic></col></schema>
                <row><start-time>0</start-time><pid>17843</pid>
                <system-cpu-percent>50</system-cpu-percent>
                <size-in-bytes>84395168</size-in-bytes></row></node></trace-query-result>
                """);
            }
            return Task.FromResult(new NativeProfilingProcessResult(exitCode, string.Empty, string.Empty, false));
        }
    }

    private sealed class RawSampleExportRunner : INativeProfilingProcessRunner
    {
        public Task<NativeProfilingProcessResult> RunAsync(NativeProfilingProcessRequest request,
            Action<string>? outputReceived, CancellationToken cancellationToken)
        {
            Assert.Contains("--xpath", request.Arguments);
            if (request.Arguments.Any(argument => argument.Contains("activity-monitor-process-live", StringComparison.Ordinal)))
                return Task.FromResult(new NativeProfilingProcessResult(139, string.Empty, string.Empty, false));
            Assert.Contains(request.Arguments, argument => argument.Contains("sysmon-process", StringComparison.Ordinal));
            var output = request.Arguments[Array.IndexOf(request.Arguments.ToArray(), "--output") + 1];
            File.WriteAllText(output, """
                <trace-query-result><node><schema name="sysmon-process">
                <col><mnemonic>time</mnemonic></col><col><mnemonic>pid</mnemonic></col>
                <col><mnemonic>cpu-percent</mnemonic></col>
                <col><mnemonic>memory-physical-footprint</mnemonic></col>
                <col><mnemonic>memory-resident-size</mnemonic></col></schema>
                <row><start-time id="1">1000000000</start-time><pid>99</pid>
                <system-cpu-percent id="2">28.745141400</system-cpu-percent>
                <size-in-bytes id="3">97928328</size-in-bytes><size-in-bytes id="4">206045184</size-in-bytes></row>
                <row><start-time ref="1"/><pid>12588</pid><system-cpu-percent ref="2"/>
                <size-in-bytes ref="3"/><size-in-bytes ref="4"/></row></node></trace-query-result>
                """);
            return Task.FromResult(new NativeProfilingProcessResult(0, string.Empty, string.Empty, false));
        }
    }
}
