using System.Text.Json;
using System.Text.Json.Nodes;
using Ansight.Host.Runtime.Operations;
using Ansight.Host.Runtime.Tasks;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed partial class RepositoryTaskTests
{
    [Fact]
    public async Task Executor_RetainsMatchersAndComparedValuesForPassingSoftAndHardAssertions()
    {
        using var repository = CreateRepository(CreateTraceSingleCallModule());
        var task = Assert.Single(LoadRepository(repository.RootPath).Tasks);
        File.WriteAllText(task.ModulePath, """
            export default async function run({ expect, ansight }) {
              const value = { coordinate: 151.2521 };
              expect(value, { id: 'location', message: 'Location matches' }).toEqual({ coordinate: 151.2521 });
              value.coordinate = 0;
              expect.soft('actual', { id: 'soft-check' }).not.toBe('actual');
              await ansight.ui.find({ text: 'Location' });
              expect('copied', { id: 'clipboard', message: 'Clipboard matches the card' }).toBe('displayed');
              expect(true, { id: 'unreachable' }).toBe(true);
            }
            """);
        var result = await CreateExecutor((_, _, _) => Task.FromResult(RequestResult.ToolResult(new JsonObject(), false)))
            .ExecuteAsync(new RepositoryTaskExecutionRequest("assertions", task, "session", new JsonObject(), "assertions")
            { CaptureTrace = true }, CancellationToken.None);

        Assert.Equal(RepositoryTaskRunStatus.Failed, result.Status);
        Assert.Single(result.ToolCalls); // Expectations do not consume the task's one-action budget.
        Assert.Equal(3, result.Assertions.Count);
        Assert.True(result.Assertions[0].Passed);
        Assert.Equal("expect.toEqual", result.Assertions[0].Matcher);
        Assert.Equal(151.2521, result.Assertions[0].Actual!["coordinate"]!.GetValue<double>());
        Assert.Equal("expect.soft.not.toBe", result.Assertions[1].Matcher);
        Assert.False(result.Assertions[1].Passed);
        Assert.False(result.Assertions[2].Passed);
        Assert.Equal("displayed", result.Assertions[2].Expected!.GetValue<string>());
        Assert.Equal("copied", result.Assertions[2].Actual!.GetValue<string>());
        Assert.All(result.Assertions, assertion => Assert.InRange(assertion.CompletedAtUtc!.Value, result.StartedAtUtc.AddSeconds(-1), result.CompletedAtUtc));
        var protocol = RepositoryTaskProtocol.BuildRunResult(result);
        var retained = protocol["assertions"]!.Deserialize<RepositoryTaskAssertion[]>(JsonUtil.Compact)!;
        Assert.Equal("copied", retained[2].Actual!.GetValue<string>());
        Assert.Equal("expect.toBe", retained[2].Matcher);
        Assert.Null(RepositoryTaskProtocol.BuildRunResult(result, includeCallTrace: false)["assertions"]![2]!["actual"]);
    }

    [Fact]
    public async Task Executor_RetainsCompletedAssertionsWhenCancelledDuringTheNextCall()
    {
        using var repository = CreateRepository(CreateTraceSingleCallModule());
        var task = Assert.Single(LoadRepository(repository.RootPath).Tasks);
        File.WriteAllText(task.ModulePath, """
            export default async function run({ expect, ansight }) {
              expect('ready', { id: 'ready' }).toBe('ready');
              await ansight.ui.find({ text: 'Location' });
              expect(true, { id: 'unreached' }).toBe(true);
            }
            """);
        using var cancellation = new CancellationTokenSource();
        var result = await CreateExecutor((_, _, _) =>
        {
            cancellation.Cancel();
            return new TaskCompletionSource<RequestResult>().Task;
        }).ExecuteAsync(new RepositoryTaskExecutionRequest("cancel", task, "session", new JsonObject(), "cancel")
        { CaptureTrace = true }, cancellation.Token);

        Assert.Equal(RepositoryTaskRunStatus.Cancelled, result.Status);
        var assertion = Assert.Single(result.Assertions);
        Assert.Equal("ready", assertion.AssertionId);
        Assert.True(assertion.Passed);
        Assert.Equal("ready", assertion.Actual!.GetValue<string>());
    }

    [Fact]
    public async Task Executor_AssertionTraceDistinguishesUndefinedAndNonJsonValuesWithoutChangingOutcomes()
    {
        using var repository = CreateRepository(CreateTraceSingleCallModule());
        var task = Assert.Single(LoadRepository(repository.RootPath).Tasks);
        File.WriteAllText(task.ModulePath, """
            export default async function run({ expect }) {
              expect(undefined, { id: 'undefined' }).toBeUndefined();
              expect(null, { id: 'null' }).toBeNull();
              expect(NaN, { id: 'nan' }).toBe(NaN);
              expect(42n, { id: 'bigint' }).toBe(42n);
              const circular = {}; circular.self = circular;
              expect(circular, { id: 'circular' }).toBe(circular);
            }
            """);
        var result = await CreateExecutor((_, _, _) => throw new InvalidOperationException()).ExecuteAsync(
            new RepositoryTaskExecutionRequest("values", task, "session", new JsonObject(), "values")
            { CaptureTrace = true }, CancellationToken.None);

        Assert.Equal(RepositoryTaskRunStatus.Passed, result.Status);
        Assert.Equal(5, result.Assertions.Count);
        Assert.Equal("undefined", result.Assertions[0].Actual!["valueType"]!.GetValue<string>());
        Assert.Equal("undefined", result.Assertions[0].Expected!["valueType"]!.GetValue<string>());
        Assert.Null(result.Assertions[1].Actual);
        Assert.Equal("NaN", result.Assertions[2].Actual!["value"]!.GetValue<string>());
        Assert.Equal("42", result.Assertions[3].Actual!["value"]!.GetValue<string>());
        Assert.NotNull(result.Assertions[4].Actual!["captureError"]);
    }

    [Fact]
    public void TraceCapture_BoundsAssertionValuesWithoutChangingTheOriginal()
    {
        var value = new string('x', 50_000);
        var assertion = new RepositoryTaskAssertion("large", false, "Mismatch", JsonValue.Create(value), JsonValue.Create("actual"));
        var captured = RepositoryTaskCallTrace.CaptureAssertion(assertion);
        Assert.True(captured.Expected!["traceValueTruncated"]!.GetValue<bool>());
        Assert.Equal(RepositoryTaskCallTrace.MaximumArgumentCharacters, captured.Expected["preview"]!.GetValue<string>().Length);
        Assert.Equal("actual", captured.Actual!.GetValue<string>());
        Assert.Equal(value, assertion.Expected!.GetValue<string>());
    }
}
