using System.Text.Json;
using Ansight.Host.Activity;
using Ansight.Infrastructure.Security;
using Ansight.RemoteSimulator.Core.Devices;

namespace Ansight.Cli.Tests.Commands.Companion;

public sealed class CompanionTaskTests
{
    [Fact]
    public async Task DeviceActivityReportsExternalRunsOnOnlyTheirTargetAndClearsOnCompletion()
    {
        using var directory = TestDirectory.Create();
        await using var runtime = CreateRuntime(directory.Path);
        await using var source = new CliCompanionTestingSource(runtime, new CliRemoteAnnotationSource(runtime));
        var request = new RemoteDeviceTestingRequest("SIMULATOR-ONE", "device-activity");
        Assert.True(source.SupportsActivity);
        using var caller = SessionActivityContext.ExecuteAs("agent", "External agent", "external-run");

        using (var test = runtime.Activity.Begin("test.run", "test", "session", "com.example.app",
            deviceIdentifier: "simulator-one", displayName: "Open area details"))
        using (runtime.Activity.Begin("agent.run", "agent", "session", "com.example.app",
            deviceIdentifier: "simulator-one"))
        {
            var current = await source.ExecuteAsync(request);
            var activity = Assert.Single(current.Details!["activities"]!.AsArray())!;
            Assert.Equal("Open area details", activity["title"]!.GetValue<string>());
            Assert.Equal("test", activity["kind"]!.GetValue<string>());
            var otherDevice = await source.ExecuteAsync(request with { DeviceUdid = "simulator-two" });
            Assert.Empty(otherDevice.Details!["activities"]!.AsArray());
        }

        var finished = await source.ExecuteAsync(request);
        Assert.Empty(finished.Details!["activities"]!.AsArray());
    }

    [Fact]
    public void TestIncludesAnIndependentEvidenceBasedValidationInstruction()
    {
        var instructions = CliCompanionTestingSource.BuildInstructions("  Open settings  ", "test");
        Assert.Equal("Open settings", instructions[0]);
        Assert.Contains("fresh app evidence", instructions[1]);
        Assert.Contains("Do not mark the instruction successful", instructions[1]);
        Assert.Contains("Open settings", instructions[1]);
        Assert.Single(CliCompanionTestingSource.BuildInstructions("Open settings", "app-execute"));
        Assert.Throws<ArgumentException>(() => CliCompanionTestingSource.BuildInstructions(" ", "test"));
        Assert.Throws<ArgumentException>(() => CliCompanionTestingSource.BuildInstructions("Do work", "shell"));
        Assert.Throws<ArgumentException>(() => CliCompanionTestingSource.BuildInstructions(new string('x', 12_001), "test"));
    }

    [Fact]
    public async Task RetryReturnsThePersistedRunAndCannotRetargetIt()
    {
        using var directory = TestDirectory.Create();
        await using var runtime = CreateRuntime(directory.Path);
        var id = Guid.NewGuid().ToString();
        var run = new CompanionTaskRun("run-one", id, "device-one", "com.example.app", "test",
            "Read the current screen", "succeeded", "Passed", DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow, null, "session", []);
        File.WriteAllText(Path.Combine(directory.Path, "companion-task-runs.json"),
            JsonSerializer.Serialize(new[] { run }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        await using var source = new CliCompanionTestingSource(runtime, new CliRemoteAnnotationSource(runtime));
        var request = new RemoteDeviceTestingRequest("device-one", "task-start", "com.example.app",
            Prompt: run.Prompt, ExecutionType: "test", RequestId: id);

        var retried = await source.ExecuteAsync(request);
        Assert.True(retried.IsSuccess);
        Assert.Equal("run-one", retried.Details!["task"]!["id"]!.GetValue<string>());
        Assert.False((await source.ExecuteAsync(request with
        {
            DeviceUdid = "other-device"
        })).IsSuccess);
        Assert.False((await source.ExecuteAsync(request with
        {
            Prompt = "Different task"
        })).IsSuccess);

        var wrongTarget = await source.ExecuteAsync(new RemoteDeviceTestingRequest(
            "other-device", "task-cancel", RunId: "run-one"));
        Assert.False(wrongTarget.IsSuccess);
    }

    [Fact]
    public void ProgressUsesUserVisibleActionsAndPreservesOutcomeEvidence()
    {
        var screenshot = new SimulatorAgentProgress(SimulatorAgentProgressStage.CallingTool,
            "Internal tool detail", 1, 2, 1, "ansight_screenshot");
        Assert.Equal("Taking screenshot", CliCompanionTestingSource.ProgressMessage(screenshot, false));

        var validation = screenshot with
        {
            Stage = SimulatorAgentProgressStage.Thinking,
            InstructionIndex = 2
        };
        Assert.Equal("Checking results", CliCompanionTestingSource.ProgressMessage(validation, true));

        var completed = screenshot with
        {
            Stage = SimulatorAgentProgressStage.InstructionCompleted,
            Message = "The requested control is visible."
        };
        Assert.Equal(completed.Message, CliCompanionTestingSource.ProgressMessage(completed, false));
        Assert.Null(CliCompanionTestingSource.ProgressMessage(screenshot with
        {
            Stage = SimulatorAgentProgressStage.ModelCompleted
        }, false));
    }

    [Fact]
    public async Task StoppingAnAlreadyFinishedRunPreservesItsResult()
    {
        using var directory = TestDirectory.Create();
        await using var runtime = CreateRuntime(directory.Path);
        await using var source = new CliCompanionTestingSource(runtime, new CliRemoteAnnotationSource(runtime));
        var started = await source.ExecuteAsync(new RemoteDeviceTestingRequest("device", "task-start", "com.example.app",
            Prompt: "Read the screen", ExecutionType: "test", RequestId: Guid.NewGuid().ToString()));
        var runId = started.Details!["task"]!["id"]!.GetValue<string>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        RemoteDeviceTestingResult finished;

        do
        {
            await Task.Delay(10, deadline.Token);
            finished = await source.ExecuteAsync(new RemoteDeviceTestingRequest("device", "task-status", RunId: runId));
        } while (finished.Details!["task"]!["isRunning"]!.GetValue<bool>());

        var stopped = await source.ExecuteAsync(new RemoteDeviceTestingRequest("device", "task-cancel", RunId: runId));
        Assert.False(stopped.Details!["task"]!["isRunning"]!.GetValue<bool>());
        Assert.Equal(finished.Details!["task"]!["status"]!.GetValue<string>(),
            stopped.Details!["task"]!["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task HostRestartMarksUnfinishedRunsInterrupted()
    {
        using var directory = TestDirectory.Create();
        await using var runtime = CreateRuntime(directory.Path);
        var run = new CompanionTaskRun("unfinished", Guid.NewGuid().ToString(), "device", "com.example.app", "test",
            "Read the current screen", "running", "Running", DateTimeOffset.UtcNow, null, null, null, []);
        File.WriteAllText(Path.Combine(directory.Path, "companion-task-runs.json"),
            JsonSerializer.Serialize(new[] { run }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        await using var source = new CliCompanionTestingSource(runtime, new CliRemoteAnnotationSource(runtime));

        var result = await source.ExecuteAsync(new RemoteDeviceTestingRequest("device", "task-status"));
        Assert.Equal("interrupted", result.Details!["task"]!["status"]!.GetValue<string>());
        Assert.False(result.Details!["task"]!["isRunning"]!.GetValue<bool>());
    }

    private static RuntimeCoordinator CreateRuntime(string path)
    {
        var storage = Path.Combine(path, "secure-storage.json");
        var key = FileEncryptionKeyProvider.ResolveDefaultKeyFilePath(storage);
        FileEncryptionKeyProvider.CreateKeyFile(key);

        return new RuntimeCoordinator(new RuntimeOptions
        {
            BaseFolderPath = path,
            SecureStorageFilePath = storage,
            SecureStorageKeyFilePath = key
        });
    }
}
