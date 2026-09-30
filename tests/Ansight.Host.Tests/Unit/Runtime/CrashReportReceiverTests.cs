using Ansight.Host.Runtime.WebSocketSessions;
using Ansight.Host.Tests.TestSupport;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class CrashReportReceiverTests : IDisposable
{
    private readonly TestEnvironment environment = new();
    private readonly SessionCaptureStore store;
    private readonly RuntimeState state;
    private readonly CrashReportReceiver receiver;
    private readonly string previousProcess = Guid.NewGuid().ToString("D");
    private readonly string currentProcess = Guid.NewGuid().ToString("D");
    private readonly string previousSession;
    private readonly string currentSession;

    public CrashReportReceiverTests()
    {
        store = new SessionCaptureStore(environment.ApplicationPaths);
        state = new RuntimeState(store);
        previousSession = CreateSession(previousProcess);
        currentSession = CreateSession(currentProcess);
        receiver = new CrashReportReceiver(state, store, environment.ApplicationPaths);
    }

    [Fact]
    public void HandoffPersistsReportAndTraceBeforeAcknowledgementAndSurvivesRestart()
    {
        var payload = CreatePayload();
        payload["report"]!["traceBase64"] = Convert.ToBase64String("native trace"u8);
        Assert.True(receiver.Receive(currentSession, payload).IsSuccess);

        var restartedStore = new SessionCaptureStore(environment.ApplicationPaths);
        Assert.True(restartedStore.TryLoad(previousSession, out var retained));
        var artifact = Assert.Single(retained!.ArtifactSnapshots);
        Assert.Equal("crash-report", artifact.Kind);
        Assert.Equal("sdk.native.crash-report", artifact.Source);
        Assert.Equal(2, artifact.FileCount);
        Assert.Equal("native trace", File.ReadAllText(Directory.GetFiles(
            environment.RootPath, "native.trace", SearchOption.AllDirectories).Single()));
        var reportPath = Directory.GetFiles(environment.RootPath, "report.json", SearchOption.AllDirectories).Single();
        Assert.Equal(previousProcess, JsonNode.Parse(File.ReadAllText(reportPath))!["previousProcessSessionId"]!.GetValue<string>());

        var restartedState = new RuntimeState(restartedStore);
        restartedState.CreateSession("com.example.app", "Example", IPAddress.Loopback, "registration", currentProcess);
        var restartedReceiver = new CrashReportReceiver(restartedState, restartedStore, environment.ApplicationPaths);
        Assert.True(restartedReceiver.Receive(currentSession, payload).IsSuccess);
        Assert.True(restartedState.TryGetSessionSnapshot(previousSession, out var duplicate));
        Assert.Single(duplicate!.ArtifactSnapshots);
    }

    [Fact]
    public void MissingOriginalSessionRetainsReportOnDeliverySession()
    {
        var payload = CreatePayload();
        var unknown = Guid.NewGuid().ToString("D");
        payload["targetSessionId"] = unknown;
        payload["targetProcessSessionId"] = unknown;
        payload["report"]!["previousProcessSessionId"] = unknown;
        Assert.True(receiver.Receive(currentSession, payload).IsSuccess);
        Assert.True(new SessionCaptureStore(environment.ApplicationPaths).TryLoad(currentSession, out var retained));
        Assert.Single(retained!.ArtifactSnapshots);
    }

    [Theory]
    [InlineData("reportId")]
    [InlineData("targetProcessSessionId")]
    [InlineData("deliveryProcessSessionId")]
    public void RejectsMismatchedEnvelopeIdentity(string key)
    {
        var payload = CreatePayload();
        payload[key] = "wrong";
        Assert.False(receiver.Receive(currentSession, payload).IsSuccess);
        Assert.True(state.TryGetSessionSnapshot(previousSession, out var retained));
        Assert.Empty(retained!.ArtifactSnapshots);
    }

    [Fact]
    public void RejectsTargetFromAnotherAppOrRegistration()
    {
        var otherProcess = Guid.NewGuid().ToString("D");
        var other = state.CreateSession("other.app", "Other", IPAddress.Loopback, "registration", otherProcess);
        var payload = CreatePayload();
        payload["targetSessionId"] = other;
        payload["targetProcessSessionId"] = otherProcess;
        payload["report"]!["previousProcessSessionId"] = otherProcess;
        Assert.False(receiver.Receive(currentSession, payload).IsSuccess);
        var otherRegistration = state.CreateSession("com.example.app", "Other", IPAddress.Loopback, "other-registration", otherProcess);
        payload["targetSessionId"] = otherRegistration;
        Assert.False(receiver.Receive(currentSession, payload).IsSuccess);
    }

    [Theory]
    [InlineData("traceBase64", "not base64!")]
    [InlineData("occurredAtUtc", "not a date")]
    [InlineData("schema", "unknown")]
    [InlineData("appId", "other.app")]
    public void RejectsInvalidReport(string field, string value)
    {
        var payload = CreatePayload();
        payload["report"]![field] = value;
        Assert.False(receiver.Receive(currentSession, payload).IsSuccess);
    }

    [Fact]
    public void RejectsOversizedReport()
    {
        var payload = CreatePayload();
        payload["report"]!["candidate"] = new string('x', CrashReportReceiver.MaximumReportBytes);
        Assert.False(receiver.Receive(currentSession, payload).IsSuccess);
    }

    [Fact]
    public void PersistenceFailureIsNotAcknowledgedAndCanBeRetried()
    {
        var blockedPath = Path.Combine(environment.ApplicationPaths.ApplicationTempPath, "native-crashes");
        Directory.CreateDirectory(environment.ApplicationPaths.ApplicationTempPath);
        File.WriteAllText(blockedPath, "block directory creation");
        var payload = CreatePayload();
        Assert.False(receiver.Receive(currentSession, payload).IsSuccess);
        File.Delete(blockedPath);
        Assert.True(receiver.Receive(currentSession, payload).IsSuccess);
    }

    [Fact]
    public void SessionIndexWriteFailureIsNotAcknowledgedAfterArtifactCopy()
    {
        Assert.True(state.TryGetSessionSnapshot(previousSession, out var snapshot));
        store.Save(snapshot!);
        var summaryPath = Directory.GetFiles(environment.RootPath, "session.json", SearchOption.AllDirectories)
            .Single(path => path.Contains(previousSession, StringComparison.Ordinal));
        File.Delete(summaryPath);
        Directory.CreateDirectory(summaryPath);
        var payload = CreatePayload();
        Assert.False(receiver.Receive(currentSession, payload).IsSuccess);
        Assert.True(state.TryGetSessionSnapshot(previousSession, out var pending));
        Assert.Single(pending!.ArtifactSnapshots);
        Directory.Delete(summaryPath);
        Assert.True(receiver.Receive(currentSession, payload).IsSuccess);
        Assert.True(new SessionCaptureStore(environment.ApplicationPaths).TryLoad(previousSession, out var retained));
        Assert.Single(retained!.ArtifactSnapshots);
    }

    private string CreateSession(string process) => state.CreateSession(
        "com.example.app", "Example", IPAddress.Loopback, "registration", process);

    private JsonObject CreatePayload() => new()
    {
        ["reportId"] = "native-report-1",
        ["targetProcessSessionId"] = previousProcess,
        ["targetSessionId"] = previousSession,
        ["deliveryProcessSessionId"] = currentProcess,
        ["report"] = new JsonObject
        {
            ["schema"] = "ansight.crash.v1",
            ["reportId"] = "native-report-1",
            ["previousProcessSessionId"] = previousProcess,
            ["appId"] = "com.example.app",
            ["occurredAtUtc"] = "2026-09-04T02:00:00Z",
            ["hostRequired"] = true,
            ["hostAcknowledged"] = false
        }
    };

    public void Dispose() => environment.Dispose();
}
