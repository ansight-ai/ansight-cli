using Ansight.Host.Tests.TestSupport;

namespace Ansight.Host.Tests.Unit.Workspaces;

public sealed class WorkspaceTestRecoveryTests
{
    private static readonly DateTimeOffset startedUtc = DateTimeOffset.Parse("2026-10-14T02:01:00Z");

    [Fact]
    public async Task StartupCancelsAndPersistsAnOrphanWithoutChangingItsRecording()
    {
        using var environment = new TestEnvironment();
        var store = new SessionCaptureStore(environment.ApplicationPaths);
        store.Save(CreateSession("orphan", "running"));
        Assert.True(store.TryLoad("orphan", out var original));
        await using var runtime = environment.CreateRuntime();

        await runtime.StartAsync();

        Assert.True(runtime.Sessions.TryGetSnapshot("orphan", out var recovered));
        Assert.Equal("cancelled", ReadRun(recovered!).Status);
        Assert.NotNull(ReadRun(recovered!).CompletedUtc);
        Assert.Equal(original!.LastUpdatedUtc, recovered!.LastUpdatedUtc);
        Assert.Equal("Keep recording", recovered.Name);
        Assert.True(recovered.IsPinned);
        Assert.Equal("Keep evidence", Assert.Single(recovered.Logs).Message);
        Assert.Equal("keep", recovered.CustomProperties!["app"]!["value"]!.GetValue<string>());
        Assert.Equal(0, runtime.WorkspaceTests.RecoverInterruptedSessionRuns());

        // Recovery must fix the saved record, not just the explorer's current view.
        AppSessionSnapshot? persisted = null;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            Assert.True(store.TryLoad("orphan", out persisted));
            if (ReadRun(persisted!).Status == "cancelled") break;
            await Task.Delay(25);
        }
        Assert.Equal("cancelled", ReadRun(persisted!).Status);
    }

    [Theory]
    [InlineData(SimulatorAgentRunStatus.Succeeded)]
    [InlineData(SimulatorAgentRunStatus.Failed)]
    [InlineData(SimulatorAgentRunStatus.Cancelled)]
    public void RecoveryUsesTheExactRecordedOutcome(SimulatorAgentRunStatus status)
    {
        using var environment = new TestEnvironment();
        new SessionCaptureStore(environment.ApplicationPaths).Save(CreateSession("recorded", "running"));
        using var runtime = environment.CreateRuntime();
        var audit = CreateAudit("recorded", status);
        Assert.Null(runtime.SimulatorAgent.PersistRunAudit(audit).ErrorMessage);

        Assert.Equal(1, runtime.WorkspaceTests.RecoverInterruptedSessionRuns());

        Assert.True(runtime.Sessions.TryGetSnapshot("recorded", out var recovered));
        Assert.Equal(status.ToString().ToLowerInvariant(), ReadRun(recovered!).Status);
        Assert.Equal(audit.CompletedUtc, ReadRun(recovered!).CompletedUtc);
    }

    [Fact]
    public void RecoveryPreservesTerminalAndLiveRunsAndIgnoresMalformedMetadata()
    {
        using var environment = new TestEnvironment();
        var store = new SessionCaptureStore(environment.ApplicationPaths);
        store.Save(CreateSession("finished", "succeeded"));
        var malformed = CreateSession("malformed", "running");
        malformed.CustomProperties!["ui-test"] = new JsonObject { ["status"] = 12 };
        store.Save(malformed);
        var composition = new MefHostComposition(environment.ApplicationPaths,
            new FileBackedEncryptedStorage(environment.SecureStorageFilePath));
        using var runtime = composition.Get<RuntimeCoordinator>();
        var state = composition.Get<IRuntimeState>();
        var liveId = state.CreateDeviceSession(new WorkspaceTestTarget("android", "phone", "Phone", "test.app", false, false, false)
        { ExecutionMode = "device", DeviceKind = DeviceKinds.Physical });
        state.SetSessionUiTestRun(liveId, new SessionUiTestRun("test", "Test", "live-run", "running", "task", startedUtc));

        Assert.Equal(0, runtime.WorkspaceTests.RecoverInterruptedSessionRuns());
        Assert.True(runtime.Sessions.TryGetSnapshot(liveId, out var live));
        Assert.Equal("running", ReadRun(live!).Status);
        Assert.True(runtime.Sessions.TryGetSnapshot("finished", out var finished));
        Assert.Equal("succeeded", ReadRun(finished!).Status);
    }

    [Fact]
    public void AnAuditForAnotherSessionCannotSupplyTheOutcome()
    {
        using var environment = new TestEnvironment();
        new SessionCaptureStore(environment.ApplicationPaths).Save(CreateSession("orphan", "running"));
        using var runtime = environment.CreateRuntime();
        Assert.Null(runtime.SimulatorAgent.PersistRunAudit(CreateAudit("other-session", SimulatorAgentRunStatus.Succeeded)
            with { RunId = "run-orphan" }).ErrorMessage);

        Assert.Equal(1, runtime.WorkspaceTests.RecoverInterruptedSessionRuns());
        Assert.True(runtime.Sessions.TryGetSnapshot("orphan", out var recovered));
        Assert.Equal("cancelled", ReadRun(recovered!).Status);
    }

    private static SessionUiTestRun ReadRun(AppSessionSnapshot snapshot)
        => snapshot.CustomProperties![SessionUiTestRun.PropertyGroup]!.Deserialize<SessionUiTestRun>(JsonUtil.Compact)!;

    private static AppSessionSnapshot CreateSession(string id, string status) => new()
    {
        SessionId = id, AppId = "test.app", ClientName = "Test", Name = "Keep recording",
        RemoteAddress = "127.0.0.1", CreatedUtc = startedUtc, LastUpdatedUtc = startedUtc.AddMinutes(1),
        ConfigId = "config", Status = "WebSocket Open", IsPinned = true, IsHistorical = true,
        MetricChannels = [], Metrics = [],
        Logs = [new LogEntry(startedUtc.AddMinutes(1), "Keep evidence")],
        CustomProperties = new JsonObject
        {
            ["app"] = new JsonObject { ["value"] = "keep" },
            [SessionUiTestRun.PropertyGroup] = JsonSerializer.SerializeToNode(
                new SessionUiTestRun("test", "Test", "run-" + id, status, "agent", startedUtc), JsonUtil.Compact)
        }
    };

    private static SimulatorAgentRunAudit CreateAudit(string sessionId, SimulatorAgentRunStatus status) => new(
        SchemaVersion: 1, RunId: "run-" + sessionId, SessionId: sessionId, Model: string.Empty,
        Status: status, Message: "Completed.", StartedUtc: startedUtc, CompletedUtc: startedUtc.AddSeconds(30),
        DurationMilliseconds: 30_000, MaximumTurnsPerInstruction: 1, MaximumToolCalls: 1,
        InstructionCount: 1, PassedInstructionCount: 0, FailedInstructionCount: 0, CancelledInstructionCount: 0,
        ModelPassCount: 0, FailedModelPassCount: 0, FunctionCallCount: 0, AnsightToolCallCount: 0,
        SuccessfulAnsightToolCallCount: 0, FailedAnsightToolCallCount: 0,
        Tokens: SimulatorAgentTokenUsage.Empty, RequestedInstructions: [], Instructions: [], ModelPasses: [], ToolCalls: []);
}
