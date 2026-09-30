using System.Text.Json;
using Ansight.Host.Tests.TestSupport;
using Ansight.Pairing.Models;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class SimulatorCrashReportTests : IDisposable
{
    private readonly string rootPath = Path.Combine(
        Path.GetTempPath(),
        "ansight-simulator-crash-report-tests",
        Guid.NewGuid().ToString("N"));

    public SimulatorCrashReportTests()
    {
        Directory.CreateDirectory(rootPath);
    }

    [Fact]
    public void ParserReadsSimulatorIdentityAndCrashCorrelationFields()
    {
        var reportPath = WriteReport(
            "ExampleApp-2026-07-13-132625.ips",
            bundleId: "com.example.app",
            processId: 48040,
            capturedAt: "2026-07-13 13:26:18.3507 +1000",
            incidentId: "3ABA5C41-2147-4367-A728-44F68AB0B26A",
            simulatorUdid: "AC42C25B-229C-4EAC-B7F7-3BC888988022");

        var parsed = SimulatorCrashReportParser.TryParse(reportPath, out var report);

        Assert.True(parsed);
        Assert.NotNull(report);
        Assert.Equal("com.example.app", report.BundleId);
        Assert.Equal(48040, report.ProcessId);
        Assert.Equal("ExampleApp", report.ProcessName);
        Assert.Equal("3ABA5C41-2147-4367-A728-44F68AB0B26A", report.IncidentId);
        Assert.Equal("AC42C25B-229C-4EAC-B7F7-3BC888988022", report.SimulatorUdid);
        Assert.Equal(new DateTimeOffset(2026, 7, 13, 3, 26, 18, TimeSpan.Zero).AddTicks(3_507_000), report.CapturedAtUtc);
        Assert.Equal("1.2.3", report.AppVersion);
        Assert.Equal("456", report.BuildVersion);
    }

    [Fact]
    public void ParserRejectsPhysicalDeviceReport()
    {
        var reportPath = WriteReport(
            "ExampleApp-physical.ips",
            bundleId: "com.example.app",
            processId: 48040,
            capturedAt: "2026-07-13 13:26:18.3507 +1000",
            incidentId: "3ABA5C41-2147-4367-A728-44F68AB0B26A",
            simulatorUdid: null);

        var parsed = SimulatorCrashReportParser.TryParse(reportPath, out var report);

        Assert.False(parsed);
        Assert.Null(report);
    }

    [Fact]
    public void FinderReturnsAllExactBundleAndProcessMatches()
    {
        var sessionStartedAtUtc = new DateTimeOffset(2026, 7, 13, 3, 26, 0, TimeSpan.Zero);
        var disconnectedAtUtc = new DateTimeOffset(2026, 7, 13, 3, 26, 20, TimeSpan.Zero);
        WriteReport(
            "wrong-process.ips",
            bundleId: "com.example.app",
            processId: 999,
            capturedAt: "2026-07-13 13:26:20.0000 +1000",
            incidentId: "11111111-1111-1111-1111-111111111111",
            simulatorUdid: "AC42C25B-229C-4EAC-B7F7-3BC888988022");
        WriteReport(
            "older-match.ips",
            bundleId: "com.example.app",
            processId: 48040,
            capturedAt: "2026-07-13 13:26:10.0000 +1000",
            incidentId: "22222222-2222-2222-2222-222222222222",
            simulatorUdid: "AC42C25B-229C-4EAC-B7F7-3BC888988022");
        var nearestPath = WriteReport(
            "nearest-match.ips",
            bundleId: "com.example.app",
            processId: 48040,
            capturedAt: "2026-07-13 13:26:19.0000 +1000",
            incidentId: "33333333-3333-3333-3333-333333333333",
            simulatorUdid: "AC42C25B-229C-4EAC-B7F7-3BC888988022");

        var reports = SimulatorCrashReportFinder.FindMatches(
            rootPath,
            new SimulatorCrashReportMatchContext(
                "com.example.app",
                48040,
                sessionStartedAtUtc,
                disconnectedAtUtc,
                "1.2.3",
                "456"));

        Assert.Collection(
            reports,
            report => Assert.Equal("22222222-2222-2222-2222-222222222222", report.IncidentId),
            report =>
            {
                Assert.Equal(nearestPath, report.FilePath);
                Assert.Equal("33333333-3333-3333-3333-333333333333", report.IncidentId);
            });
    }

    [Fact]
    public void FinderAssociatesExactProcessReportFromStartOfSession()
    {
        var reportPath = WriteReport(
            "real-world-early-session-match.ips",
            bundleId: "au.com.sydneymetrotrees.iPadFieldAppCM",
            processId: 49830,
            capturedAt: "2026-07-13 15:16:12.7820 +1000",
            incidentId: "6A7CC0DA-63D5-4C61-A340-801CBF00AB72",
            simulatorUdid: "AC42C25B-229C-4EAC-B7F7-3BC888988022");

        var reports = SimulatorCrashReportFinder.FindMatches(
            rootPath,
            new SimulatorCrashReportMatchContext(
                "au.com.sydneymetrotrees.iPadFieldAppCM",
                49830,
                new DateTimeOffset(2026, 7, 13, 5, 16, 12, 181, TimeSpan.Zero),
                new DateTimeOffset(2026, 7, 13, 5, 16, 47, 204, TimeSpan.Zero),
                "2026.06.25",
                "607"));

        var report = Assert.Single(reports);
        Assert.Equal(reportPath, report.FilePath);
        Assert.Equal(49830, report.ProcessId);
    }

    [Fact]
    public void FinderRejectsReportCapturedBeforeSessionStarted()
    {
        WriteReport(
            "stale-match.ips",
            bundleId: "com.example.app",
            processId: 48040,
            capturedAt: "2026-07-13 13:25:00.0000 +1000",
            incidentId: "44444444-4444-4444-4444-444444444444",
            simulatorUdid: "AC42C25B-229C-4EAC-B7F7-3BC888988022");

        var reports = SimulatorCrashReportFinder.FindMatches(
            rootPath,
            new SimulatorCrashReportMatchContext(
                "com.example.app",
                48040,
                new DateTimeOffset(2026, 7, 13, 3, 26, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 7, 13, 3, 26, 20, TimeSpan.Zero),
                "1.2.3",
                "456"));

        Assert.Empty(reports);
    }

    [Fact]
    public void CollectorCopiesMatchedReportIntoSessionArtifacts()
    {
        using var environment = new TestEnvironment();
        var captureStore = new SessionCaptureStore(environment.ApplicationPaths);
        var runtimeState = new RuntimeState(captureStore);
        var sessionId = runtimeState.CreateSession(
            "com.example.app",
            "ExampleApp",
            System.Net.IPAddress.Loopback,
            configId: null,
            processSessionId: null);
        runtimeState.SetSessionDeviceProfile(
            sessionId,
            new DeviceAppProfile
            {
                Device = new DeviceProfile
                {
                    OsName = "ios",
                    IsEmulator = true,
                    IsVirtual = true
                },
                App = new DeviceApplicationProfile
                {
                    AppId = "com.example.app",
                    AppName = "ExampleApp",
                    ProcessId = 48040,
                    VersionName = "1.2.3",
                    BuildNumber = "456"
                }
            },
            profileJson: null);
        var reportPath = WriteReport(
            "ExampleApp-artifact.ips",
            bundleId: "com.example.app",
            processId: 48040,
            capturedAt: "2026-07-13 13:26:18.3507 +1000",
            incidentId: "55555555-5555-5555-5555-555555555555",
            simulatorUdid: "AC42C25B-229C-4EAC-B7F7-3BC888988022");
        Assert.True(SimulatorCrashReportParser.TryParse(reportPath, out var report));
        var collector = new SimulatorCrashReportCollector(
            runtimeState,
            environment.ApplicationPaths.ApplicationTempPath,
            rootPath);

        Assert.True(collector.TryCreateMatchContext(
            sessionId,
            new DateTimeOffset(2026, 7, 13, 3, 26, 20, TimeSpan.Zero),
            out var context));
        Assert.NotNull(context);
        Assert.Equal("com.example.app", context.BundleId);
        Assert.Equal(48040, context.ProcessId);
        Assert.True(runtimeState.TryGetSessionSnapshot(sessionId, out var sessionBeforeAttachment));
        Assert.Equal(sessionBeforeAttachment!.CreatedUtc, context.SessionStartedAtUtc);

        Assert.True(collector.AttachReport(sessionId, report!));

        Assert.True(runtimeState.TryGetSessionSnapshot(sessionId, out var snapshot));
        var artifact = Assert.Single(snapshot!.ArtifactSnapshots);
        Assert.Equal("simulator-crash-55555555-5555-5555-5555-555555555555", artifact.SnapshotId);
        Assert.Equal("crash-report", artifact.Kind);
        Assert.Equal("ansight.simulator.crash-report", artifact.Source);
        var entry = Assert.Single(artifact.Entries);
        Assert.Equal("ExampleApp-artifact.ips", entry.Name);
        Assert.Equal(".ips", entry.FileExtension);
        Assert.Equal("application/json", entry.MimeType);
        Assert.True(entry.SizeBytes > 0);
    }

    public void Dispose()
    {
        if (Directory.Exists(rootPath))
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    private string WriteReport(
        string fileName,
        string bundleId,
        int processId,
        string capturedAt,
        string incidentId,
        string? simulatorUdid)
    {
        var header = new Dictionary<string, object?>
        {
            ["app_name"] = "ExampleApp",
            ["timestamp"] = capturedAt,
            ["app_version"] = "1.2.3",
            ["build_version"] = "456",
            ["bundleID"] = bundleId,
            ["incident_id"] = incidentId
        };
        var processPath = simulatorUdid is null
            ? "/private/var/containers/Bundle/Application/APP/ExampleApp.app/ExampleApp"
            : $"/Users/USER/Library/Developer/CoreSimulator/Devices/{simulatorUdid}/data/Containers/Bundle/Application/APP/ExampleApp.app/ExampleApp";
        var body = new
        {
            procName = "ExampleApp",
            pid = processId,
            procPath = processPath,
            coalitionName = simulatorUdid is null ? "com.example.app" : $"com.apple.CoreSimulator.SimDevice.{simulatorUdid}",
            captureTime = capturedAt,
            bundleInfo = new Dictionary<string, string>
            {
                ["CFBundleShortVersionString"] = "1.2.3",
                ["CFBundleVersion"] = "456",
                ["CFBundleIdentifier"] = bundleId
            },
            incident = incidentId,
            exception = new
            {
                type = "EXC_CRASH",
                signal = "SIGABRT"
            }
        };
        var filePath = Path.Combine(rootPath, fileName);
        File.WriteAllText(
            filePath,
            JsonSerializer.Serialize(header) + Environment.NewLine + JsonSerializer.Serialize(body));
        return Path.GetFullPath(filePath);
    }
}
