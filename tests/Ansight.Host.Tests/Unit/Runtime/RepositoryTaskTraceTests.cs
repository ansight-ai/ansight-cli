using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ansight.Host.Runtime.Operations;
using Ansight.Host.Runtime.Tasks;
using Ansight.Host.Utilities;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed partial class RepositoryTaskTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Executor_SourceSnapshotRetainsLoadedTypeScriptAndDynamicHelpersAfterFilesChange(bool captureTrace)
    {
        var source = CreatePassingModule();
        using var repository = CreateRepository(source);
        var task = Assert.Single(LoadRepository(repository.RootPath).Tasks);
        var helperPath = Path.Combine(Path.GetDirectoryName(task.ModulePath)!, "helper.ts");
        const string helper = "export const value: string = 'original helper';";
        File.WriteAllText(helperPath, helper);
        // Dynamic imports are captured by the loader alongside the entry module.
        source = source.Replace("const found =", "await import('./helper.ts');\nconst found =", StringComparison.Ordinal);
        File.WriteAllText(task.ModulePath, source);
        var executor = CreateExecutor((toolName, arguments, _) =>
        {
            File.WriteAllText(task.ModulePath, "// changed after module load");
            File.WriteAllText(helperPath, "// changed helper");
            return Task.FromResult(RequestResult.ToolResult(toolName == "ansight_find_ui"
                ? new JsonObject { ["value"] = arguments["text"]?.DeepClone() }
                : new JsonObject { ["payload"] = new JsonObject { ["success"] = true,
                    ["result"] = new JsonObject { ["ready"] = true } } }, isError: false));
        });
        var result = await executor.ExecuteAsync(new RepositoryTaskExecutionRequest(
            "run-source", task, "session-1", new JsonObject { ["label"] = "Map" }, "source")
        { CaptureTrace = captureTrace }, CancellationToken.None);
        Assert.Equal(RepositoryTaskRunStatus.Passed, result.Status);
        if (!captureTrace) { Assert.Null(result.SourceTrace); return; }
        Assert.NotNull(result.SourceTrace);
        Assert.Null(result.SourceTrace.CaptureError);
        var main = Assert.Single(result.SourceTrace.Modules, module => module.Path.EndsWith("validate.ts"));
        Assert.Equal(source, main.Content);
        Assert.Equal("typescript", main.Language);
        Assert.False(main.WasTruncated);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source))), main.Sha256);
        Assert.Equal(helper, Assert.Single(result.SourceTrace.Modules, module => module.Path.EndsWith("helper.ts")).Content);
        var persisted = JsonSerializer.Deserialize<RepositoryTaskRunResult>(JsonSerializer.Serialize(result));
        Assert.Equal(main, persisted!.SourceTrace!.Modules[0]);
        Assert.NotNull(RepositoryTaskProtocol.BuildRunResult(result)["sourceTrace"]);
        Assert.Null(RepositoryTaskProtocol.BuildRunResult(result, includeCallTrace: false)["sourceTrace"]);
    }

    [Fact]
    public async Task Executor_SourceSnapshotBoundsContentAndHashesTheFullModule()
    {
        using var repository = CreateRepository(CreatePassingModule());
        var task = Assert.Single(LoadRepository(repository.RootPath).Tasks);
        var source = "export default async function run({ expect }) { expect(true, { id: 'ok' }).toBe(true); }\n//"
                     + new string('x', 300_000);
        File.WriteAllText(task.ModulePath, source);
        var result = await CreateExecutor((_, _, _) => throw new InvalidOperationException()).ExecuteAsync(
            new RepositoryTaskExecutionRequest("large-source", task, "session", new JsonObject(), "source")
            { CaptureTrace = true }, CancellationToken.None);
        Assert.Equal(RepositoryTaskRunStatus.Passed, result.Status);
        var module = Assert.Single(result.SourceTrace!.Modules);
        Assert.True(module.WasTruncated);
        Assert.Equal(262_144, module.Content.Length);
        Assert.Equal(source.Length, module.OriginalCharacterCount);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source))), module.Sha256);
    }

    [Fact]
    public async Task Executor_SourceSnapshotSurvivesModuleLoadFailure()
    {
        var source = CreatePassingModule() + "\nthrow new Error('load failed');";
        using var repository = CreateRepository(source);
        var task = Assert.Single(LoadRepository(repository.RootPath).Tasks);
        var result = await CreateExecutor((_, _, _) => throw new InvalidOperationException()).ExecuteAsync(
            new RepositoryTaskExecutionRequest("failure-source", task, "session", new JsonObject(), "source")
            { CaptureTrace = true }, CancellationToken.None);
        Assert.Equal(RepositoryTaskRunStatus.Error, result.Status);
        Assert.Equal(source, Assert.Single(result.SourceTrace!.Modules).Content);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Executor_CapturesSuppliedHostAndAppArgumentsAndResultsOnlyWhenTracing(bool captureTrace)
    {
        using var repository = CreateRepository(CreatePassingModule());
        var task = Assert.Single(LoadRepository(repository.RootPath).Tasks);
        var executor = CreateExecutor((toolName, arguments, _) => Task.FromResult(
            RequestResult.ToolResult(toolName == "ansight_find_ui"
                ? new JsonObject { ["value"] = arguments["text"]?.DeepClone() }
                : new JsonObject
                {
                    ["payload"] = new JsonObject
                    {
                        ["success"] = true,
                        ["result"] = new JsonObject { ["ready"] = true }
                    }
                }, isError: false)));

        var result = await executor.ExecuteAsync(new RepositoryTaskExecutionRequest(
            "run-trace", task, "session-1", new JsonObject { ["label"] = "Secret Garden" }, "correlation-trace")
        {
            CaptureTrace = captureTrace
        }, CancellationToken.None);

        Assert.Equal(RepositoryTaskRunStatus.Passed, result.Status);
        Assert.Equal(2, result.ToolCalls.Count);
        Assert.All(result.ToolCalls, call =>
        {
            Assert.True(call.StartedAtUtc >= result.StartedAtUtc);
            Assert.True(call.CompletedAtUtc >= call.StartedAtUtc);
            Assert.True(call.CompletedAtUtc <= result.CompletedAtUtc);
            Assert.True(call.DurationMilliseconds >= 0);
            Assert.Equal($"correlation-trace:{call.Sequence}", call.CorrelationId);
            if (captureTrace)
            {
                Assert.Equal("session-override", ReadCallPayload(call.Arguments)["sessionId"]?.GetValue<string>());
                Assert.NotNull(call.Result);
            }
            else
            {
                Assert.Null(call.Arguments);
                Assert.Null(call.Result);
                Assert.Null(call.ChildCalls);
            }
        });
        if (captureTrace)
        {
            Assert.Equal("Secret Garden", ReadCallPayload(result.ToolCalls[0].Arguments)["text"]?.GetValue<string>());
            Assert.Equal("Secret Garden", ReadCallPayload(result.ToolCalls[0].Result)["value"]?.GetValue<string>());
            Assert.True(ReadCallPayload(result.ToolCalls[1].Result)["payload"]?["result"]?["ready"]?.GetValue<bool>());
        }
    }

    [Theory]
    [InlineData("throw")]
    [InlineData("request-error")]
    [InlineData("tool-error")]
    public async Task Executor_CapturesTheInputAndErrorForFailedCalls(string failureMode)
    {
        using var repository = CreateRepository(CreateTraceSingleCallModule());
        var task = Assert.Single(LoadRepository(repository.RootPath).Tasks);
        var executor = CreateExecutor((_, _, _) => failureMode switch
        {
            "throw" => throw new InvalidOperationException("Call failed for trace test."),
            "request-error" => Task.FromResult(RequestResult.Error(400, "Call failed for trace test.")),
            _ => Task.FromResult(RequestResult.ToolResult(new JsonObject
            {
                ["message"] = "Call failed for trace test.",
                ["code"] = "trace_test_failure"
            }, isError: true))
        });

        var result = await executor.ExecuteAsync(new RepositoryTaskExecutionRequest(
            "run-error", task, "session-1", new JsonObject { ["text"] = "input retained" }, "correlation-error")
        {
            CaptureTrace = true
        }, CancellationToken.None);

        Assert.Equal(RepositoryTaskRunStatus.Error, result.Status);
        var call = Assert.Single(result.ToolCalls);
        Assert.True(call.IsError);
        Assert.Equal("input retained", ReadCallPayload(call.Arguments)["text"]?.GetValue<string>());
        Assert.Equal("Call failed for trace test.", ReadCallPayload(call.Result)["message"]?.GetValue<string>());
        Assert.True(call.CompletedAtUtc >= call.StartedAtUtc);
        if (failureMode == "request-error")
        {
            Assert.Equal(400, ReadCallPayload(call.Result)["errorCode"]?.GetValue<int>());
        }
        if (failureMode == "throw")
        {
            Assert.Equal(nameof(InvalidOperationException), ReadCallPayload(call.Result)["exceptionType"]?.GetValue<string>());
        }
    }

    [Fact]
    public async Task Executor_RetainsAnInFlightCancelledCallWithItsInputAndTiming()
    {
        using var repository = CreateRepository(CreateTraceSingleCallModule());
        using var cancellation = new CancellationTokenSource();
        var task = Assert.Single(LoadRepository(repository.RootPath).Tasks);
        var executor = CreateExecutor((_, _, _) =>
        {
            cancellation.Cancel();
            return new TaskCompletionSource<RequestResult>().Task;
        });

        var result = await executor.ExecuteAsync(new RepositoryTaskExecutionRequest(
            "run-cancel", task, "session-1", new JsonObject { ["text"] = "pending input" }, "correlation-cancel")
        {
            CaptureTrace = true
        }, cancellation.Token);

        Assert.Equal(RepositoryTaskRunStatus.Cancelled, result.Status);
        Assert.NotEmpty(result.SourceTrace!.Modules);
        var call = Assert.Single(result.ToolCalls);
        Assert.True(call.IsError);
        Assert.Equal("pending input", ReadCallPayload(call.Arguments)["text"]?.GetValue<string>());
        Assert.True(ReadCallPayload(call.Result)["cancelled"]?.GetValue<bool>());
        Assert.True(call.CompletedAtUtc >= call.StartedAtUtc);
        Assert.True(call.DurationMilliseconds >= 0);
    }

    [Theory]
    [InlineData(300_000, RepositoryTaskRunStatus.Passed)]
    [InlineData(600_000, RepositoryTaskRunStatus.Error)]
    public async Task Executor_BoundsPayloadsAndHashesTheirCompleteContentWithoutChangingCallBehavior(
        int outputCharacterCount,
        RepositoryTaskRunStatus expectedStatus)
    {
        using var repository = CreateRepository(CreateTraceSingleCallModule());
        var task = Assert.Single(LoadRepository(repository.RootPath).Tasks);
        var input = new JsonObject { ["text"] = new string('i', 40_000) };
        var output = new JsonObject { ["ok"] = true, ["content"] = new string('o', outputCharacterCount) };
        var executor = CreateExecutor((_, _, _) => Task.FromResult(RequestResult.ToolResult(output.DeepClone().AsObject(), isError: false)));

        var result = await executor.ExecuteAsync(new RepositoryTaskExecutionRequest(
            "run-large", task, "session-1", input, "correlation-large")
        {
            CaptureTrace = true
        }, CancellationToken.None);

        Assert.Equal(expectedStatus, result.Status);
        var call = Assert.Single(result.ToolCalls);
        AssertBoundedCallPayload(call.Arguments, input.ToJsonString(JsonUtil.Compact), RepositoryTaskCallTrace.MaximumArgumentCharacters);
        AssertBoundedCallPayload(call.Result, output.ToJsonString(JsonUtil.Compact), RepositoryTaskCallTrace.MaximumResultCharacters);
    }

    [Fact]
    public async Task Router_PreservesLargeComposedTracesAndTheirProtocolAndPersistedPayloads()
    {
        using var repository = CreateRepository(CreateComposingModule());
        var childSource = CreateChildModule()
            .Replace("\"maximumActions\": 1", "\"maximumActions\": 3", StringComparison.Ordinal)
            .Replace("run({ run, input, expect })", "run({ run, input, app, expect })", StringComparison.Ordinal)
            .Replace("expect(input.value", "await app.callTool(\"trace.first\", { value: input.value });\n"
                + "await app.callTool(\"trace.second\", { value: input.value });\n"
                + "await app.callTool(\"trace.third\", { value: input.value });\n"
                + "expect(input.value", StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(repository.RootPath, "ansight", "tasks", "map", "child.ts"), childSource);
        var router = CreateConfiguredRouter(repository.RootPath);
        router.ConfigureToolExecutor((_, _, _) => Task.FromResult(RequestResult.ToolResult(new JsonObject
        {
            ["value"] = new string('x', 300_000)
        }, isError: false)));
        var task = Assert.Single(router.Load(repository.RootPath, "com.example.app").Tasks, candidate => candidate.TaskId == "map.validate");

        using var trace = RepositoryTaskTraceScope.Begin(true);
        var result = await router.ExecuteAsync(task, "session-composition", new JsonObject(), "correlation-composition", CancellationToken.None);

        Assert.Equal(RepositoryTaskRunStatus.Passed, result.Status);
        Assert.Equal(2, result.ToolCalls.Count);
        var composition = result.ToolCalls[1];
        Assert.Equal("map.child", ReadCallPayload(composition.Arguments)["taskId"]?.GetValue<string>());
        Assert.Equal("nested-value", ReadCallPayload(composition.Result)["output"]?["value"]?.GetValue<string>());
        Assert.False(composition.Result!.WasTruncated);
        Assert.Equal(3, composition.ChildCalls?.Count);
        Assert.Equal(childSource, Assert.Single(composition.SourceTrace!.Modules).Content);
        Assert.Equal("map.child", composition.SourceTrace.TaskId);
        Assert.DoesNotContain("sourceTrace", composition.Result.Content);
        Assert.All(composition.ChildCalls!, child =>
        {
            Assert.Equal("nested-value", ReadCallPayload(child.Arguments)["value"]?.GetValue<string>());
            Assert.True(child.Result!.WasTruncated);
            Assert.Equal(RepositoryTaskCallTrace.MaximumResultCharacters, child.Result.Content.Length);
        });

        var protocol = RepositoryTaskProtocol.BuildRunResult(result);
        var protocolCalls = protocol["toolCalls"]!.Deserialize<RepositoryTaskToolCall[]>(JsonUtil.Compact)!;
        Assert.Equal(composition.StartedAtUtc, protocolCalls[1].StartedAtUtc);
        Assert.Equal(composition.CompletedAtUtc, protocolCalls[1].CompletedAtUtc);
        Assert.Equal(composition.Arguments, protocolCalls[1].Arguments);
        Assert.Equal(composition.Result, protocolCalls[1].Result);
        Assert.Equal(composition.ChildCalls![2], protocolCalls[1].ChildCalls![2]);
        var persisted = File.ReadLines(Path.Combine(repository.RootPath, "automation", "task-runs.jsonl"))
            .Select(line => JsonSerializer.Deserialize<RepositoryTaskRunResult>(line, JsonUtil.Compact)!)
            .Single(run => run.RunId == result.RunId);
        Assert.Equal(composition.Arguments, persisted.ToolCalls[1].Arguments);
        Assert.Equal(childSource, Assert.Single(persisted.ToolCalls[1].SourceTrace!.Modules).Content);
        Assert.Equal(composition.ChildCalls[2], persisted.ToolCalls[1].ChildCalls![2]);
    }

    [Fact]
    public void TraceCapture_PreservesTypedTextRedaction()
    {
        var suppliedArguments = new JsonObject { ["automationId"] = "login", ["value"] = "private-text" };

        var payload = RepositoryTaskCallTrace.CaptureArguments("ansight_type_text", suppliedArguments);

        Assert.Equal("<redacted 12 character(s)>", ReadCallPayload(payload)["value"]?.GetValue<string>());
        Assert.Equal("private-text", suppliedArguments["value"]?.GetValue<string>());
        Assert.DoesNotContain("private-text", payload.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Protocol_ReadsLegacyCallsWithoutPayloadsOrCompletionMetadata()
    {
        const string json = """
                            {"sequence":1,"toolName":"ansight_find_ui","startedAtUtc":"2026-01-01T00:00:00Z","durationMilliseconds":5,"isError":false,"message":"Done"}
                            """;

        var call = JsonSerializer.Deserialize<RepositoryTaskToolCall>(json, JsonUtil.Compact)!;

        Assert.Equal(1, call.Sequence);
        Assert.Null(call.Arguments);
        Assert.Null(call.Result);
        Assert.Null(call.ChildCalls);
        Assert.Null(call.CompletedAtUtc);
        Assert.Null(call.CorrelationId);
    }

    private static JsonNode ReadCallPayload(RepositoryTaskCallPayload? payload)
    {
        Assert.NotNull(payload);
        Assert.False(payload.WasTruncated);
        Assert.Equal(payload.Content.Length, payload.OriginalCharacterCount);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload.Content))), payload.Sha256);
        return JsonNode.Parse(payload.Content)!;
    }

    private static void AssertBoundedCallPayload(RepositoryTaskCallPayload? payload, string originalContent, int maximumCharacters)
    {
        Assert.NotNull(payload);
        Assert.True(payload.WasTruncated);
        Assert.Equal(maximumCharacters, payload.Content.Length);
        Assert.Equal(originalContent[..maximumCharacters], payload.Content);
        Assert.Equal(originalContent.Length, payload.OriginalCharacterCount);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(originalContent))), payload.Sha256);
    }

    private static string CreateTraceSingleCallModule()
        => """
           export const task = {
             "schemaVersion": 1,
             "appId": "com.example.app",
             "title": "Trace a call",
             "description": "Exercises trace payload capture.",
             "timeoutSeconds": 10,
             "maximumActions": 1
           };

           export default async function run({ input, ansight, expect }) {
             const response = await ansight.ui.find(input);
             expect(response.ok, { id: "call-ok" }).toBe(true);
             return { ok: true };
           }
           """;
}
