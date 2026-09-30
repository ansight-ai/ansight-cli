using System.Text.Json;
using Ansight.Host;

namespace Ansight.Cli.Tests.Commands.Profiling;

public sealed class ProfilingAnalysisCommandsTests
{
    [Fact]
    public void CreateRequest_MapsSelectionAndCpuLimit()
    {
        var arguments = CliArguments.Parse([
            "profile",
            "dotnet",
            "cpu",
            "capture-001",
            "--start-ms",
            "12.5",
            "--end-ms",
            "34.5",
            "--process-id",
            "42",
            "--thread-id",
            "7",
            "--limit",
            "25"
        ]);

        var request = ProfilingAnalysisCommands.CreateRequest("cpu", arguments, out var resultLimit);

        Assert.Equal("capture-001", request.CaptureId);
        Assert.Equal(12.5, request.Selection!.StartMilliseconds);
        Assert.Equal(34.5, request.Selection.EndMilliseconds);
        Assert.Equal(42, request.Selection.ProcessId);
        Assert.Equal(7, request.Selection.ThreadId);
        Assert.Equal(25, request.HotspotLimit);
        Assert.Equal(25, resultLimit);
    }

    [Fact]
    public void CreateRequest_RejectsReversedSelection()
    {
        var arguments = CliArguments.Parse([
            "profile",
            "dotnet",
            "overview",
            "capture-001",
            "--start-ms",
            "20",
            "--end-ms",
            "10"
        ]);

        var exception = Assert.Throws<CliUsageException>(() =>
            ProfilingAnalysisCommands.CreateRequest("overview", arguments, out _));

        Assert.Contains("--end-ms", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("overview", "overview")]
    [InlineData("startup", "startupTimeline")]
    [InlineData("cpu", "cpu")]
    [InlineData("call-tree", "callTree")]
    [InlineData("threads", "threads")]
    [InlineData("gc", "gc")]
    [InlineData("jit", "jit")]
    [InlineData("exceptions", "exceptions")]
    public void CreateOutput_ExposesEveryAnalysisView(
        string action,
        string expectedProperty)
    {
        var result = ProfilingAnalysisCommands.CreateOutput(action, CreateAnalysisResult(), 10);
        using var standardOutput = new StringWriter();
        new CliOutput(true, standardOutput, TextWriter.Null).Write(result);

        using var document = JsonDocument.Parse(standardOutput.ToString());
        Assert.Equal(
            "ansight.dotnet-analysis/v1",
            document.RootElement.GetProperty("schema").GetString());
        Assert.Equal(action, document.RootElement.GetProperty("analysisKind").GetString());
        Assert.True(document.RootElement.TryGetProperty(expectedProperty, out _));
        Assert.False(string.IsNullOrWhiteSpace(ProfilingAnalysisCommands.Render(result)));
    }

    private static DotNetTraceAnalysisResult CreateAnalysisResult()
    {
        var analysis = new DotNetTraceAnalysis(
            new DotNetTraceOverview(
                0,
                100,
                100,
                1_000,
                200,
                1,
                2,
                1,
                3.5,
                1,
                1,
                95.5),
            [new DotNetCpuHotspot("Example.Run", "Example", 20, 40, 10, 20)],
            new DotNetCallTreeNode(
                "root",
                null,
                200,
                [new DotNetCallTreeNode("Example.Run", "Example", 40, [])]),
            [new DotNetGcPause(1, 0, "AllocSmall", "NonConcurrentGC", 20, 23.5, 3.5, 42, 7)],
            [new DotNetJitMethod("Example.Run", 128, 10, 42, 7)],
            [new DotNetExceptionGroup("ExampleException", "Example", 2, 30, 40)],
            [new DotNetThreadActivity(42, 7, "Example", "Main", 200, 100)],
            [new DotNetStartupMilestone(
                "application-ready",
                "Ansight-DotNet-Startup",
                "ApplicationReady",
                50,
                42,
                7)],
            new Dictionary<string, int> { ["Microsoft-Windows-DotNETRuntime"] = 1_000 },
            new Dictionary<string, int> { ["Method/JittingStarted"] = 1 },
            ["Example warning."]);
        return new DotNetTraceAnalysisResult(
            "capture-001",
            "dotnet-traceevent-v2",
            new DotNetTraceSelection(null, null, null, null),
            new DotNetTraceEvidence("dotnet-nettrace", "raw/runtime.nettrace", 1_024, "abc123"),
            analysis);
    }
}
