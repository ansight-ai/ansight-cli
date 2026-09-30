using System.Text.Json.Nodes;
using Ansight.Host.Runtime.DeviceExecution;
using Ansight.Host.Tests.TestSupport;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class DeviceExecutionEvidenceTests
{
    [Fact]
    public void MacCpuTimeUsesTheHostTimebaseWithoutOverflow()
    {
        Assert.Equal(1_000_000_000, DeviceProcessCounters.MacCpuNanoseconds(24_000_000, 125, 3));
        Assert.Equal(1_000_000_000, DeviceProcessCounters.MacCpuNanoseconds(1_000_000_000, 1, 1));
        Assert.Equal(8_000_000_000_000_000_000, DeviceProcessCounters.MacCpuNanoseconds(192_000_000_000_000_000, 125, 3));
    }

    [Fact]
    public void AndroidCountersUseKernelClockAndDistinctMemoryMeasurements()
    {
        var fields = Enumerable.Repeat("0", 22).ToArray();
        fields[0] = "S";
        fields[11] = "100";
        fields[12] = "50";
        fields[19] = "900";
        var sample = DeviceProcessCounters.ParseAndroid(23, "23 (name with ) spaces) " + string.Join(' ', fields),
            "TOTAL PSS: 1024\nTOTAL RSS: 2048", 100);
        Assert.Equal(1_500_000_000, sample.CpuNanoseconds);
        Assert.Equal(2_097_152, sample.ResidentBytes);
        Assert.Equal(1_048_576, sample.ProportionalBytes);
        Assert.Equal("23:900", sample.Identity);
        Assert.Null(DeviceProcessCounters.ParseAndroid(23, "permission denied", "permission denied", 100).ResidentBytes);
    }

    [Fact]
    public void FrameStatisticsIgnoreOverlapsIncompleteFramesAndDetectOverflow()
    {
        const string history = "Flags,IntendedVsync,FrameCompleted,\n0,1,100,\n0,2,200,\n1,3,300,\n0,4,9223372036854775807,";
        var first = AndroidFrameStatistics.Read(history, 0, 1);
        Assert.True(first.Supported);
        Assert.Null(first.FramesPerSecond);
        var next = AndroidFrameStatistics.Read(history + "\n0,5,400,\n0,5,400,", 200, 0.5);
        Assert.Equal(1, next.Frames);
        Assert.Equal(2, next.FramesPerSecond);
        var full = "Flags,FrameCompleted,\n" + string.Join('\n', Enumerable.Range(1, 120).Select(value => $"0,{value + 1000},"));
        var overflow = AndroidFrameStatistics.Read(full, 100, 1);
        Assert.True(overflow.Overflow);
        Assert.Null(overflow.FramesPerSecond);
        Assert.False(AndroidFrameStatistics.Read("No process found", 0, 1).Supported);
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("/etc/passwd")]
    [InlineData("files/../../outside")]
    [InlineData("files\\..\\outside")]
    [InlineData("files/secret\n")]
    public void SandboxRejectsNonRelativePaths(string path)
        => Assert.Throws<ArgumentException>(() => DevicePlatformProbe.ValidateRelativePath(path));

    [Fact]
    public void SandboxRejectsSymlinksEvenWhenIntermediate()
    {
        using var environment = new TestEnvironment();
        var root = Path.Combine(environment.ApplicationPaths.ApplicationDataPath, "sandbox");
        var outside = Path.Combine(environment.ApplicationPaths.ApplicationDataPath, "outside");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(Path.Combine(root, "link"), outside);
        Assert.Throws<IOException>(() => DevicePlatformProbe.ResolveContainedPath(root, "link/file"));
    }

    [Fact]
    public void RequirementsRejectMissingAppToolsAndLegacyDeviceTasks()
    {
        var capabilities = JsonNode.Parse("""{"executionMode":"device","appAvailable":false,"appTools":[],"capabilities":{"ui.semantic":{"available":true}}}""")!.AsObject();
        Assert.Null(ExecutionCapabilities.Missing(capabilities, new ExecutionRequirements { Capabilities = ["ui.semantic"] }));
        Assert.Equal("capability_unavailable", ExecutionCapabilities.Missing(capabilities,
            new ExecutionRequirements { AppTools = ["data.query"] })?["code"]?.GetValue<string>());
        Assert.NotNull(ExecutionCapabilities.Missing(capabilities, null, legacy: true));
    }

    [Fact]
    public void TrendsSeparatesExternalProvidersAndHostEnvironments()
    {
        var channel = new SessionMetricChannel { ChannelId = 1, Name = "Memory", ColorHex = "#000000", Source = "adb-process-v1", Kind = "pss", Unit = "bytes" };
        using var environment = new TestEnvironment();
        var state = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        var id = state.CreateDeviceSession(new WorkspaceTestTarget("android", "emulator-5554", "Phone", "test.app", false, false, true) { DeviceKind = DeviceKinds.Emulator });
        state.TryGetSessionSnapshot(id, out var session);
        var first = WorkspaceTrendsEvaluator.MeasurementIdentity(session!, channel, "definition");
        state.SetSessionCustomProperties(id, new JsonObject { ["deviceExecution"] = new JsonObject { ["hostProcessors"] = 16 } });
        state.TryGetSessionSnapshot(id, out session);
        Assert.NotEqual(first, WorkspaceTrendsEvaluator.MeasurementIdentity(session!, channel, "definition"));
        Assert.NotEqual("definition", first);
    }
}
