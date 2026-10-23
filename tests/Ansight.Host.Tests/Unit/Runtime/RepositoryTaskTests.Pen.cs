using Ansight.Host.Runtime.RepositoryContracts;
using Ansight.Host.Runtime.Tasks;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed partial class RepositoryTaskTests
{
    [Fact]
    public void PenPlaybackTypeContractSupportsFileAndRejectsAmbiguousSources()
    {
        if (LocalTypeScriptTaskCompiler.ResolveCompilerPath() is null) return;
        using var repository = CreateRepository("export const placeholder = true;");
        var path = Path.Combine(repository.RootPath, "pen.ts");
        File.WriteAllText(path, """
            import type { TaskInvocation, PlayPenResult } from "./ansight-task.d.ts";
            export default async function run({ ansight }: TaskInvocation) {
              const result: PlayPenResult = await ansight.ui.playPen({ file: "fixtures/demo.pen.json", mode: "hid+sdk", target: "pencil-canvas", hoverRecognizer: "pencil-hover" });
              const framework: PlayPenResult = await ansight.ui.playPen({ file: "fixtures/demo.pen.json", mode: "sdk", target: "drawing-canvas" });
              // @ts-expect-error Framework playback also requires an explicit target.
              await ansight.ui.playPen({ file: "demo.json", mode: "sdk" });
              // @ts-expect-error Framework playback does not drive native recognizers.
              await ansight.ui.playPen({ file: "demo.json", mode: "sdk", target: "canvas", hoverRecognizer: "native" });
              // @ts-expect-error File and inline samples are mutually exclusive.
              await ansight.ui.playPen({ file: "demo.json", samples: [] });
              // @ts-expect-error Hybrid playback requires an explicit target.
              await ansight.ui.playPen({ file: "demo.json", mode: "hid+sdk" });
              // @ts-expect-error A source is required.
              await ansight.ui.playPen({});
              // @ts-expect-error Retargeting a task is forbidden.
              await ansight.ui.playPen({ file: "demo.json", sessionId: "other" });
            }
            """);
        Assert.Empty(LocalTypeScriptTaskCompiler.Validate(repository.RootPath, path,
            RepositoryModuleContractArtifacts.GetTaskTypeDefinitions()));
    }

    [Fact]
    public async Task Executor_PenFilePlaybackPinsSessionAndFixtureScope()
    {
        const string module = """
            export const task = {
              "schemaVersion": 1,
              "appId": "com.example.app",
              "title": "Play pen file",
              "description": "Checks pen file playback.",
              "inputSchema": { "type": "object", "properties": {}, "additionalProperties": false },
              "timeoutSeconds": 10,
              "maximumActions": 3
            };
            export default async function run({ ansight, expect }) {
              const result = await ansight.ui.playPen({ file: "fixtures/demo.pen.json", mode: "hid+sdk", target: "pencil-canvas", hoverRecognizer: "pencil-hover", sessionId: "wrong-session" });
              expect(result.sampleCount, { id: "pen-delivered" }).toBe(6);
            }
            """;
        using var repository = CreateRepository(module);
        var task = Assert.Single(LoadRepository(repository.RootPath).Tasks);
        var calls = 0;
        var executor = new JavaScriptRepositoryTaskExecutor("node", (toolName, arguments, _) =>
        {
            calls++;
            Assert.Equal("ansight_stylus_ui", toolName);
            Assert.Equal("pen-session", arguments["sessionId"]!.GetValue<string>());
            Assert.Null(arguments["deviceId"]);
            Assert.Equal("fixtures/demo.pen.json", arguments["file"]!.GetValue<string>());
            Assert.Equal("hid+sdk", arguments["mode"]!.GetValue<string>());
            Assert.Equal(repository.RootPath, PenInputScope.CurrentRepositoryRoot);
            return Task.FromResult(RequestResult.ToolResult(new JsonObject { ["sampleCount"] = 6 }, isError: false));
        }, hostApiSuites: RepositoryJavaScriptApiMethods.StandardHostToolSuites);

        var result = await executor.ExecuteAsync(new RepositoryTaskExecutionRequest(
            "pen-run", task, "pen-session", new JsonObject(), "pen-test"), CancellationToken.None);

        Assert.Equal(1, calls);
        Assert.Equal(RepositoryTaskRunStatus.Passed, result.Status);
    }
}
