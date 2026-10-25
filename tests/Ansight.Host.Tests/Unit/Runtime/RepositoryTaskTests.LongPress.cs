namespace Ansight.Host.Tests.Unit.Runtime;

public sealed partial class RepositoryTaskTests
{
    [Fact]
    public void LongPressTypeScriptContractSupportsSelectorsCoordinatesAndSequences()
    {
        if (LocalTypeScriptTaskCompiler.ResolveCompilerPath() is null) return;
        using var repository = CreateRepository("export const placeholder = true;");
        var path = Path.Combine(repository.RootPath, "long-press.ts");
        File.WriteAllText(path, """
            import type { TaskInvocation, UiActionResult } from "./ansight-task.d.ts";
            export default async function run({ ansight }: TaskInvocation) {
              const result: UiActionResult = await ansight.ui.longPress({ automationId: "card", durationMs: 1800 });
              await ansight.ui.longPress({ normalizedX: 0.4, normalizedY: 0.6 });
              await ansight.ui.runSequence({ actions: [
                { kind: "longPress", normalizedX: 0.4, normalizedY: 0.6, durationMs: 2500 }
              ] });
              // @ts-expect-error Hold duration is numeric.
              await ansight.ui.longPress({ automationId: "card", durationMs: "800" });
              // @ts-expect-error Session targeting belongs to the host.
              await ansight.ui.longPress({ automationId: "card", sessionId: "other" });
            }
            """);
        Assert.Empty(LocalTypeScriptTaskCompiler.Validate(repository.RootPath, path,
            RepositoryModuleContractArtifacts.GetTaskTypeDefinitions()));
    }

    [Fact]
    public async Task LongPressRuntimeRoutesToTheHostWithTheEnforcedSession()
    {
        using var repository = CreateRepository("""
            export const task = {
              "schemaVersion": 1, "appId": "com.example.app", "title": "Hold card",
              "description": "Open a context menu.", "timeoutSeconds": 10, "maximumActions": 2
            };
            export default async function run({ ansight, expect }) {
              const result = await ansight.ui.longPress({ automationId: "card", durationMs: 3500, sessionId: "wrong" });
              expect(result.performed, { id: "held" }).toBe(true);
            }
            """);
        var task = Assert.Single(LoadRepository(repository.RootPath).Tasks);
        var calls = 0;
        var executor = new JavaScriptRepositoryTaskExecutor("node", (name, arguments, _) =>
        {
            calls++;
            Assert.Equal("ansight_long_press_ui", name);
            Assert.Equal("hold-session", arguments["sessionId"]!.GetValue<string>());
            Assert.Equal("card", arguments["automationId"]!.GetValue<string>());
            Assert.Equal(3500, arguments["durationMs"]!.GetValue<int>());
            return Task.FromResult(RequestResult.ToolResult(new JsonObject { ["performed"] = true }, isError: false));
        }, hostApiSuites: RepositoryJavaScriptApiMethods.StandardHostToolSuites);
        var result = await executor.ExecuteAsync(new RepositoryTaskExecutionRequest(
            "hold-run", task, "hold-session", new JsonObject(), "hold-test"), CancellationToken.None);
        Assert.Equal(1, calls);
        Assert.True(result.Status == RepositoryTaskRunStatus.Passed, result.Message + "\n" + result.StandardError);
    }
}
