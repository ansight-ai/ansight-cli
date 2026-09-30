using Ansight.Host.Runtime.Operations.Tools.NativeProfiling;
using Ansight.Host.Tests.TestSupport;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class NativeProfileInspectionServiceTests
{
    [Fact]
    public async Task InspectInstrumentsToc_ReportsRunsSchemasProcessesAndAuthoritativeArtifact()
    {
        using var environment = new TestEnvironment();
        var store = new NativeProfileCaptureStore(environment.ApplicationPaths);
        var captureId = Guid.NewGuid().ToString("N");
        var capturePath = store.CreateCaptureDirectory(captureId);
        var tracePath = Path.Combine(capturePath, "raw", "capture.trace");
        Directory.CreateDirectory(tracePath);
        await File.WriteAllBytesAsync(Path.Combine(tracePath, "template.data"), [1, 2, 3]);
        var tocPath = Path.Combine(capturePath, "derived", "instruments-toc.xml");
        await File.WriteAllTextAsync(
            tocPath,
            """
            <trace-toc>
              <run number="1">
                <processes>
                  <process pid="42" name="ExampleApp" path="/Applications/ExampleApp.app/ExampleApp" />
                </processes>
                <data>
                  <table schema="time-profile" />
                  <table schema="time-profile" />
                  <table schema="thread-info" />
                </data>
              </run>
            </trace-toc>
            """);
        await store.SaveManifestAsync(CreateManifest(
            captureId,
            NativeProfilePlatforms.Ios,
            "instruments",
            [
                Artifact(
                    InstrumentsProfileCaptureAdapter.TraceArtifactKind,
                    "raw/capture.trace",
                    isDirectory: true,
                    authoritative: true),
                Artifact(
                    InstrumentsProfileCaptureAdapter.TableOfContentsArtifactKind,
                    "derived/instruments-toc.xml")
            ]));
        var inspectionService = new NativeProfileInspectionService(
            store,
            new RecordingProcessRunner());

        var toc = inspectionService.InspectInstrumentsToc(captureId);
        var inspection = await inspectionService.InspectAsync(captureId);

        Assert.Equal("trace-toc", toc.RootElement);
        Assert.Equal(1, toc.RunCount);
        Assert.Equal(3, toc.TableCount);
        Assert.Equal(2, Assert.Single(toc.Schemas, schema => schema.Schema == "time-profile").Occurrences);
        var process = Assert.Single(toc.Processes);
        Assert.Equal("42", process.ProcessId);
        Assert.Equal("ExampleApp", process.Name);
        Assert.Equal(tracePath, inspection.AuthoritativeArtifact?.Path);
        Assert.Equal(NativeProfileInspectionService.AnalyzerVersion, inspection.AnalyzerVersion);
        Assert.NotNull(inspection.Instruments);
        Assert.Null(inspection.Perfetto);
    }

    [Fact]
    public async Task InspectPerfetto_ReportsConfigAndCoreTraceOverviewWhenProcessorExists()
    {
        using var environment = new TestEnvironment();
        var store = new NativeProfileCaptureStore(environment.ApplicationPaths);
        var captureId = Guid.NewGuid().ToString("N");
        var capturePath = store.CreateCaptureDirectory(captureId);
        var tracePath = Path.Combine(capturePath, "raw", "capture.perfetto-trace");
        await File.WriteAllBytesAsync(tracePath, [1, 2, 3, 4]);
        var configPath = Path.Combine(capturePath, "diagnostics", "perfetto-config.pbtxt");
        await File.WriteAllTextAsync(
            configPath,
            """
            duration_ms: 30000
            data_sources { config { name: "linux.ftrace" } }
            data_sources { config { name: "android.packages_list" } }
            """);
        await store.SaveManifestAsync(CreateManifest(
            captureId,
            NativeProfilePlatforms.Android,
            "perfetto",
            [
                Artifact(
                    PerfettoProfileCaptureAdapter.TraceArtifactKind,
                    "raw/capture.perfetto-trace",
                    authoritative: true),
                Artifact(
                    PerfettoProfileCaptureAdapter.ConfigArtifactKind,
                    "diagnostics/perfetto-config.pbtxt")
            ]));
        var toolPath = Path.Combine(environment.RootPath, "trace_processor_shell");
        await File.WriteAllTextAsync(toolPath, string.Empty);
        var runner = new RecordingProcessRunner();
        var inspectionService = new NativeProfileInspectionService(store, runner, toolPath);

        var inspection = await inspectionService.InspectAsync(captureId);

        var perfetto = Assert.IsType<Ansight.Host.Runtime.NativeProfiling.PerfettoCaptureInspection>(
            inspection.Perfetto);
        Assert.Equal(30_000, perfetto.ConfiguredDurationMilliseconds);
        Assert.Equal(["android.packages_list", "linux.ftrace"], perfetto.DataSources);
        Assert.True(perfetto.Toolchain.IsAvailable);
        var overview = Assert.IsType<Ansight.Host.Runtime.NativeProfiling.PerfettoQueryInspection>(
            perfetto.TraceOverview);
        Assert.Contains("duration_ns", overview.Columns);
        Assert.Single(overview.Rows);
        Assert.Contains(
            runner.Requests,
            request => request.Arguments.Count >= 3
                       && request.Arguments[0] == "query"
                       && request.Arguments[1] == tracePath);
    }

    [Fact]
    public async Task QueryPerfetto_BoundsRowsAndParsesQuotedCsv()
    {
        using var environment = new TestEnvironment();
        var store = new NativeProfileCaptureStore(environment.ApplicationPaths);
        var captureId = Guid.NewGuid().ToString("N");
        var capturePath = store.CreateCaptureDirectory(captureId);
        await File.WriteAllBytesAsync(
            Path.Combine(capturePath, "raw", "capture.perfetto-trace"),
            [1, 2, 3, 4]);
        await store.SaveManifestAsync(CreateManifest(
            captureId,
            NativeProfilePlatforms.Android,
            "perfetto",
            [Artifact(
                PerfettoProfileCaptureAdapter.TraceArtifactKind,
                "raw/capture.perfetto-trace",
                authoritative: true)]));
        var toolPath = Path.Combine(environment.RootPath, "trace_processor");
        await File.WriteAllTextAsync(toolPath, string.Empty);
        var runner = new RecordingProcessRunner(
            """
            "name",value
            "first, item",1
            "second",2
            "third",3
            """);
        var inspectionService = new NativeProfileInspectionService(store, runner, toolPath);

        var query = await inspectionService.QueryPerfettoAsync(
            captureId,
            "SELECT name, value FROM slice",
            2);

        Assert.Equal(["name", "value"], query.Columns);
        Assert.Equal(2, query.RowCount);
        Assert.True(query.WasTruncated);
        Assert.Equal("first, item", query.Rows[0][0]);
        var request = Assert.Single(runner.Requests, request => request.Arguments.FirstOrDefault() == "query");
        Assert.Contains("LIMIT 3", request.Arguments[2], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("DELETE FROM slice")]
    [InlineData("SELECT * FROM slice; SELECT * FROM process")]
    [InlineData("WITH removed AS (DELETE FROM slice) SELECT * FROM removed")]
    public async Task QueryPerfetto_RejectsNonReadOnlyOrMultipleStatements(string sql)
    {
        using var environment = new TestEnvironment();
        var store = new NativeProfileCaptureStore(environment.ApplicationPaths);
        var inspectionService = new NativeProfileInspectionService(
            store,
            new RecordingProcessRunner());

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => inspectionService.QueryPerfettoAsync(
            Guid.NewGuid().ToString("N"),
            sql,
            100));

        Assert.Contains("Perfetto", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeProfilingToolCatalog_RegistersFirstPassInspectionSuite()
    {
        using var environment = new TestEnvironment();
        using var profiling = new NativeProfilingService(environment.ApplicationPaths);
        var catalog = new NativeProfilingToolCatalog(new NativeProfileOperationServices(profiling));
        var names = catalog.BuildToolsListResult()["tools"]!
            .AsArray()
            .Select(definition => definition!["name"]!.GetValue<string>())
            .ToArray();

        Assert.Contains("ansight_native_list_captures", names);
        Assert.Contains("ansight_native_get_capture_manifest", names);
        Assert.Contains("ansight_native_get_capture_artifact", names);
        Assert.Contains("ansight_native_get_trace_overview", names);
        Assert.Contains("ansight_ios_get_instruments_toc", names);
        Assert.Contains("ansight_android_query_perfetto", names);
    }

    private static NativeProfileCaptureManifest CreateManifest(
        string captureId,
        string platform,
        string engine,
        IReadOnlyList<NativeProfileArtifact> artifacts)
        => new()
        {
            Schema = NativeProfileCaptureManifest.CurrentSchema,
            CaptureId = captureId,
            Platform = platform,
            Engine = engine,
            Preset = platform == NativeProfilePlatforms.Ios
                ? NativeProfilePresets.Cpu
                : NativeProfilePresets.System,
            AppId = "com.example.app",
            ApplicationPath = platform == NativeProfilePlatforms.Ios
                ? "/artifacts/Example.app"
                : "/artifacts/example.apk",
            DeviceId = "test-device",
            RequestedDurationSeconds = 30,
            State = NativeProfileCaptureState.Completed,
            CreatedUtc = DateTimeOffset.UtcNow.AddSeconds(-31),
            StartedUtc = DateTimeOffset.UtcNow.AddSeconds(-30),
            CompletedUtc = DateTimeOffset.UtcNow,
            StopReason = "capture-tool-completed",
            Artifacts = artifacts
        };

    private static NativeProfileArtifact Artifact(
        string kind,
        string relativePath,
        bool isDirectory = false,
        bool authoritative = false)
        => new(
            kind,
            relativePath,
            4,
            "test-sha256",
            isDirectory,
            authoritative);

    private sealed class RecordingProcessRunner : INativeProfilingProcessRunner
    {
        private readonly string queryOutput;

        public RecordingProcessRunner(string? queryOutput = null)
        {
            this.queryOutput = queryOutput
                               ?? "start_ts,end_ts,duration_ns,process_count,thread_count,slice_count,counter_count,scheduling_slice_count\n1,101,100,2,3,4,5,6\n";
        }

        public List<NativeProfilingProcessRequest> Requests { get; } = [];

        public Task<NativeProfilingProcessResult> RunAsync(
            NativeProfilingProcessRequest request,
            Action<string>? outputReceived,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(request.Arguments.SequenceEqual(["--version"])
                ? new NativeProfilingProcessResult(0, "Perfetto v52.0", string.Empty, false)
                : new NativeProfilingProcessResult(0, queryOutput, string.Empty, false));
        }
    }
}
