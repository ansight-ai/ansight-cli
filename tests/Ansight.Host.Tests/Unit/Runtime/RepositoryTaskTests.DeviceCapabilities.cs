using System.Text.Json.Nodes;
using Ansight.Host.Runtime.DeviceExecution;
using Ansight.Host.Runtime.Operations;
using Ansight.Host.Runtime.Tasks;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed partial class RepositoryTaskTests
{
    [Fact]
    public async Task DeviceTaskPreflightRejectsBeforeImportingTheModule()
    {
        using var repository = CreateRepository("""
            export const task = { "schemaVersion": 2, "title": "SDK dependency", "description": "Requires an SDK tool", "requires": { "appTools": ["data.query"] } };
            throw new Error("Module side effect executed");
            export default () => {};
            """);
        var task = Assert.Single(LoadRepository(repository.RootPath).Tasks);
        var executor = DeviceExecutor((_, _, _) => throw new InvalidOperationException("No calls are allowed."));
        var result = await executor.ExecuteAsync(new RepositoryTaskExecutionRequest("run", task, "device-session", new JsonObject(), "correlation"), CancellationToken.None);
        Assert.Equal(RepositoryTaskRunStatus.Rejected, result.Status);
        Assert.Contains("data.query", result.Message);
        Assert.DoesNotContain("side effect", result.StandardError);
        Assert.Empty(result.ToolCalls);
    }

    [Fact]
    public async Task DeviceTaskCanUsePortableFilesWithPinnedSession()
    {
        using var repository = CreateRepository("""
            export const task = { "schemaVersion": 2, "title": "Capture file", "description": "Portable evidence", "requires": { "capabilities": ["files.capture"] } };
            export default async ({ ansight, expect, run, app }) => {
              const capture = await ansight.files.capture({ path: "Documents/probe.bin", sessionId: "wrong-session" });
              expect(run.executionMode, { id: "device" }).toBe("device");
              expect(app.available, { id: "sdk-unavailable" }).toBe(false);
              expect(capture.snapshotId, { id: "captured" }).toBe("artifact-1");
            };
            """);
        var task = Assert.Single(LoadRepository(repository.RootPath).Tasks);
        var executor = DeviceExecutor((name, arguments, _) =>
        {
            Assert.Equal("ansight_capture_sandbox_file", name);
            Assert.Equal("device-session", arguments["sessionId"]?.GetValue<string>());
            return Task.FromResult(RequestResult.ToolResult(new JsonObject { ["snapshotId"] = "artifact-1" }, false));
        });
        var result = await executor.ExecuteAsync(new RepositoryTaskExecutionRequest("run", task, "device-session", new JsonObject(), "correlation"), CancellationToken.None);
        Assert.Equal(RepositoryTaskRunStatus.Passed, result.Status);
        Assert.Equal(3, result.Assertions.Count);
    }

    [Fact]
    public async Task DeviceTaskDynamicAppCallThrowsStructuredErrorWithoutDispatch()
    {
        using var repository = CreateRepository("""
            export const task = { "schemaVersion": 2, "title": "Check refusal", "description": "Checks dynamic calls" };
            export default async ({ app, expect }) => {
              let error;
              try { await app.callTool("custom.tool"); } catch (caught) { error = caught; }
              expect(error?.code, { id: "code" }).toBe("capability_unavailable");
              expect(error?.retryable, { id: "no-retry" }).toBe(false);
              expect(error?.message.includes("Add the Ansight SDK"), { id: "sdk-guidance" }).toBe(true);
            };
            """);
        var task = Assert.Single(LoadRepository(repository.RootPath).Tasks);
        var result = await DeviceExecutor((_, _, _) => throw new InvalidOperationException("Must not dispatch.")).ExecuteAsync(
            new RepositoryTaskExecutionRequest("run", task, "device-session", new JsonObject(), "correlation"), CancellationToken.None);
        Assert.Equal(RepositoryTaskRunStatus.Passed, result.Status);
        Assert.Empty(result.ToolCalls);
    }

    [Fact]
    public void LoaderRejectsMisspelledRequirements()
    {
        using var repository = CreateRepository("""
            export const task = { "schemaVersion": 2, "title": "Invalid", "description": "Misspelled requirement", "requires": { "appTool": ["data.query"] } };
            export default () => {};
            """);
        var result = LoadRepository(repository.RootPath);
        Assert.Empty(result.Tasks);
        Assert.Contains("appTool", Assert.Single(result.Warnings));
    }

    [Fact]
    public void LoaderRejectsRequirementsOnLegacyDescriptors()
    {
        using var repository = CreateRepository("""
            export const task = { "schemaVersion": 1, "title": "Invalid", "description": "Invalid requirements", "requires": { "capabilities": ["files.capture"] } };
            export default () => {};
            """);
        var result = LoadRepository(repository.RootPath);
        Assert.Empty(result.Tasks);
        Assert.Contains("schemaVersion 2", Assert.Single(result.Warnings));
    }

    private static JavaScriptRepositoryTaskExecutor DeviceExecutor(RepositoryTaskToolExecutor executor)
        => new("node", executor,
            hostApiSuites: new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["files"] = new Dictionary<string, string> { ["capture"] = "ansight_capture_sandbox_file" }
            },
            capabilityResolver: (_, _) => Task.FromResult(JsonNode.Parse("""
                {"executionMode":"device","appAvailable":false,"appTools":[],"capabilities":{"files.capture":{"available":true}}}
                """)!.AsObject()));
}
