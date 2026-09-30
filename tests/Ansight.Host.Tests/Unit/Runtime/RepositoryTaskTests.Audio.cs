using System.Text.Json.Nodes;
using Ansight.Host.Audio;
using Ansight.Host.Runtime.Operations;
using Ansight.Host.Runtime.RepositoryContracts;
using Ansight.Host.Runtime.Tasks;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed partial class RepositoryTaskTests
{
    [Fact]
    public async Task Executor_MicrophonePermissionFailureRejectsTask()
    {
        const string message = "AUDIO INJECTION BLOCKED — MACOS ACCESSIBILITY PERMISSION REQUIRED. NO AUDIO WAS INJECTED.";
        using var repository = CreateRepository(CreateAudioModule());
        var task = Assert.Single(LoadRepository(repository.RootPath).Tasks);
        var executor = CreateAudioExecutor((toolName, _, _) => Task.FromResult(
            toolName == "ansight_get_audio_capabilities"
                ? RequestResult.ToolResult(new JsonObject { ["available"] = true }, isError: false)
                : RequestResult.ToolResult(new JsonObject
                {
                    ["code"] = "accessibility-permission-required", ["message"] = message,
                    ["deliveryStarted"] = false
                }, isError: true)));

        var result = await executor.ExecuteAsync(
            new RepositoryTaskExecutionRequest("permission-run", task, "session-audio", new JsonObject(), "permission-test"),
            CancellationToken.None);

        Assert.Equal(RepositoryTaskRunStatus.Error, result.Status);
        Assert.Contains(message, result.Message);
        Assert.DoesNotContain(result.Assertions, assertion => assertion.AssertionId == "audio-delivered");
    }

    [Fact]
    public void AudioInjection_AdvertisesTheMicrophoneApiName()
    {
        var method = RepositoryJavaScriptApiMethods.ResolveHostApiMethod("ansight_inject_audio");

        Assert.Equal("device", method.FeatureName);
        Assert.Equal("injectMicrophoneAudio", method.MethodName);
    }

    [Fact]
    public async Task Router_PassesExplicitParallelContextToNestedAudioTask()
    {
        var parent = CreateAudioModule();
        parent = parent[..parent.IndexOf("export default", StringComparison.Ordinal)] + """
            export default async function run({ ansight }) {
              await ansight.tasks.run({ taskId: 'map.child' });
            }
            """;
        using var repository = CreateRepository(parent);
        File.WriteAllText(Path.Combine(repository.RootPath, "ansight", "tasks", "map", "child.ts"), CreateAudioModule());
        var router = CreateConfiguredRouter(repository.RootPath);
        router.ConfigureHostToolRegistry(new OperationRegistry([
            OperationRegistration.TaskSessionBound(new StubHostTool("ansight_get_audio_capabilities")),
            OperationRegistration.TaskSessionBound(new StubHostTool("ansight_inject_audio"))
        ]));
        router.ConfigureToolExecutor((_, _, _, _) => throw new Xunit.Sdk.XunitException("Audio provider must not run."));
        var task = Assert.Single(router.Load(repository.RootPath, "com.example.app").Tasks,
            candidate => candidate.TaskId == "map.validate");
        var logs = new List<string>();

        var result = await router.ExecuteAsync(task, "session-audio", new JsonObject(), "nested-audio",
            CancellationToken.None, new OperationExecutionContext("batch", "test", true, logs.Add));

        Assert.Equal(RepositoryTaskRunStatus.Error, result.Status);
        Assert.Contains(AudioExecutionPolicy.ParallelErrorCode, result.Message);
        Assert.Single(logs);
    }

    [Theory]
    [InlineData("ansight.device.audioCapabilities()", "ansight_get_audio_capabilities")]
    [InlineData("ansight.device.injectMicrophoneAudio({ file: 'fixtures/quote.wav' })", "ansight_inject_audio")]
    public async Task Executor_ParallelAudioRejectsInJavaScriptAndAuditsWithoutCallingProvider(string call, string toolName)
    {
        var module = CreateAudioModule();
        module = module[..module.IndexOf("export default", StringComparison.Ordinal)] + $$"""
            export default async function run({ ansight, expect }) {
              try {
                await {{call}};
              } catch (error) {
                expect(error.message.includes('audio-parallel-execution-unsupported'), { id: 'audio-rejected' }).toBe(true);
                return;
              }
              throw new Error('Audio API did not reject');
            }
            """;
        using var repository = CreateRepository(module);
        var task = Assert.Single(LoadRepository(repository.RootPath).Tasks);
        var logs = new List<string>();
        var executor = CreateAudioExecutor((_, _, _) => throw new Xunit.Sdk.XunitException("Audio provider must not run."));
        var result = await executor.ExecuteAsync(
            new RepositoryTaskExecutionRequest("run-audio", task, "session-audio", new JsonObject(), "test-audio")
            {
                OperationContext = new OperationExecutionContext("parallel-batch", "audio-test", true, logs.Add)
            },
            CancellationToken.None);
        Assert.Equal(RepositoryTaskRunStatus.Passed, result.Status);
        var rejectedCall = Assert.Single(result.ToolCalls);
        Assert.Equal(toolName, rejectedCall.ToolName);
        Assert.True(rejectedCall.IsError);
        Assert.Contains(AudioExecutionPolicy.ParallelErrorCode, rejectedCall.Message);
        var log = Assert.Single(logs);
        Assert.Contains("parallel-batch", log);
        Assert.Contains("audio-test", log);
        Assert.Contains(toolName, log);
        Assert.Null(AudioExecutionPolicy.RejectParallelCall(toolName, null));
    }

    [Fact]
    public async Task Executor_AudioUsesPinnedSessionAndTrustedRepositoryWithoutHostToolDeclaration()
    {
        using var repository = CreateRepository(CreateAudioModule());
        Directory.CreateDirectory(Path.Combine(repository.RootPath, "fixtures"));
        File.WriteAllText(Path.Combine(repository.RootPath, "fixtures", "quote.wav"), "fixture identity");
        var task = Assert.Single(LoadRepository(repository.RootPath).Tasks);
        Assert.Empty(task.DeclaredHostTools);
        var calls = new List<string>();
        var executor = CreateAudioExecutor((toolName, arguments, _) =>
        {
            calls.Add(toolName);
            Assert.Equal("session-audio", arguments["sessionId"]!.GetValue<string>());
            Assert.Null(arguments["appId"]);
            Assert.Null(arguments["deviceId"]);
            Assert.Null(arguments["bundleIdentifier"]);
            Assert.Equal(repository.RootPath, AudioFixtureScope.CurrentRepositoryRoot);
            Assert.True(ToolExecutionCancellation.Current.CanBeCanceled);
            if (toolName == "ansight_get_audio_capabilities")
                return Task.FromResult(RequestResult.ToolResult(new JsonObject { ["available"] = true }, isError: false));

            Assert.Equal("fixtures/quote.wav", arguments["file"]!.GetValue<string>());
            var fixturePath = Path.Combine(AudioFixtureScope.CurrentRepositoryRoot!, arguments["file"]!.GetValue<string>());
            Assert.Equal("fixture identity", File.ReadAllText(fixturePath));
            return Task.FromResult(RequestResult.ToolResult(new JsonObject { ["status"] = "completed" }, isError: false));
        });
        using var outerScope = AudioFixtureScope.Push(Path.GetTempPath());

        var result = await executor.ExecuteAsync(
            new RepositoryTaskExecutionRequest("run-audio", task, "session-audio", new JsonObject(), "test-audio"),
            CancellationToken.None);

        Assert.Equal(RepositoryTaskRunStatus.Passed, result.Status);
        Assert.Equal(new[] { "ansight_get_audio_capabilities", "ansight_inject_audio" }, calls);
        Assert.Equal(Path.GetTempPath(), AudioFixtureScope.CurrentRepositoryRoot);
        Assert.Equal(2, result.ToolCalls.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Executor_AudioCancellationAndTaskDeadlineStopActualHostOperation(bool useTaskDeadline)
    {
        using var repository = CreateRepository(CreateAudioModule());
        var task = Assert.Single(LoadRepository(repository.RootPath).Tasks);
        if (useTaskDeadline) task = task with { Timeout = TimeSpan.FromSeconds(1) };
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = CreateAudioExecutor(async (toolName, _, _) =>
        {
            if (toolName == "ansight_get_audio_capabilities")
                return RequestResult.ToolResult(new JsonObject { ["available"] = true }, isError: false);
            var token = ToolExecutionCancellation.Current;
            Assert.True(token.CanBeCanceled);
            entered.SetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, token);
                throw new InvalidOperationException("Playback should be cancelled.");
            }
            finally
            {
                // Model bounded provider cleanup after its playback token is cancelled.
                await Task.Delay(150, CancellationToken.None);
                stopped.SetResult();
            }
        });
        var execution = executor.ExecuteAsync(
            new RepositoryTaskExecutionRequest("run-audio", task, "session-audio", new JsonObject(), "test-audio"),
            cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (!useTaskDeadline) cancellation.Cancel();

        var result = await execution.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(stopped.Task.IsCompletedSuccessfully, "Task returned before audio cleanup finished.");
        Assert.Equal(useTaskDeadline ? RepositoryTaskRunStatus.TimedOut : RepositoryTaskRunStatus.Cancelled, result.Status);
        Assert.Null(AudioFixtureScope.CurrentRepositoryRoot);
        Assert.False(ToolExecutionCancellation.Current.CanBeCanceled);
    }

    private static JavaScriptRepositoryTaskExecutor CreateAudioExecutor(RepositoryTaskToolExecutor execute)
        => new("node", execute, hostApiSuites: RepositoryJavaScriptApiMethods.StandardHostToolSuites);

    private static string CreateAudioModule()
        => """
           export const task = {
             "schemaVersion": 1,
             "appId": "com.example.app",
             "title": "Inject speech audio",
             "description": "Delivers a fixture to the selected virtual microphone.",
             "inputSchema": { "type": "object", "properties": {}, "additionalProperties": false },
             "timeoutSeconds": 10,
             "maximumActions": 2
           };
           export default async function run({ ansight, expect }) {
             const target = { sessionId: "other-session", appId: "other-app", deviceId: "other-device", bundleIdentifier: "other-bundle" };
             const capabilities = await ansight.device.audioCapabilities(target);
             expect(capabilities.available, { id: "audio-available" }).toBe(true);
             const result = await ansight.device.injectMicrophoneAudio({ ...target, file: "fixtures/quote.wav", repositoryRootPath: "/forged-root" });
             expect(result.status, { id: "audio-delivered" }).toBe("completed");
           }
           """;
}
