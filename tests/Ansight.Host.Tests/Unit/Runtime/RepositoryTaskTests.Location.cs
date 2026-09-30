using Ansight.Host.Runtime.RepositoryContracts;
using Ansight.Host.Runtime.Tasks;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed partial class RepositoryTaskTests
{
    [Fact]
    public void PlayLocation_TypeScriptContractAcceptsPlaybackAndRejectsDeviceOverride()
    {
        if (LocalTypeScriptTaskCompiler.ResolveCompilerPath() is null) return;
        using var repository = CreateRepository("export const placeholder = true;");
        var path = Path.Combine(repository.RootPath, "play.ts");
        File.WriteAllText(path, """
            import type { TaskInvocation, DeviceLocationPlaybackResult } from "./ansight-task.d.ts";
            export default async function run({ ansight }: TaskInvocation) {
              const playback: DeviceLocationPlaybackResult = await ansight.device.playLocation({
                routeContent: "<gpx />", sourceFileName: "approach.gpx",
                mode: "fixed-speed", fixedSpeedKph: 20, speed: 4, loop: false
              });
              const id: string = playback.runId;
              // @ts-expect-error Device targeting is enforced by the host.
              await ansight.device.playLocation({ routeContent: "<gpx />", deviceId: "override" });
              // @ts-expect-error XML content is required.
              await ansight.device.playLocation({ sourceFileName: "approach.gpx" });
            }
            """);
        Assert.Empty(LocalTypeScriptTaskCompiler.Validate(
            repository.RootPath, path, RepositoryModuleContractArtifacts.GetTaskTypeDefinitions()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Executor_PlayLocationPinsSessionAndPropagatesStartFailure(bool fail)
    {
        const string module = """
            export const task = {
              "schemaVersion": 1,
              "appId": "com.example.app",
              "title": "Replay approach",
              "description": "Replay a GPX approach on the current simulator.",
              "inputSchema": { "type": "object", "properties": {}, "additionalProperties": false },
              "timeoutSeconds": 10,
              "maximumActions": 2
            };
            export default async function run({ ansight, expect }) {
              const result = await ansight.device.playLocation({
                sessionId: "wrong-session", deviceId: "wrong-device", appId: "wrong-app",
                sourceFileName: "approach.gpx", routeContent: "<gpx />",
                mode: "fixed-speed", fixedSpeedKph: 20
              });
              expect(result.runId, { id: "playback-started" }).toBe("playback-1");
            }
            """;
        using var repository = CreateRepository(module);
        var task = Assert.Single(LoadRepository(repository.RootPath).Tasks);
        Assert.Empty(task.DeclaredHostTools);
        Assert.Empty(LocalTaskExtractionCoordinator.ValidateTaskApiSurface(module));
        var calls = 0;
        var executor = new JavaScriptRepositoryTaskExecutor("node", (toolName, arguments, _) =>
        {
            calls++;
            Assert.Equal("ansight_play_device_location", toolName);
            Assert.Equal("session-location", arguments["sessionId"]!.GetValue<string>());
            Assert.Null(arguments["deviceId"]);
            Assert.Null(arguments["appId"]);
            Assert.Equal("<gpx />", arguments["routeContent"]!.GetValue<string>());
            Assert.Equal(20, arguments["fixedSpeedKph"]!.GetValue<int>());
            return Task.FromResult(RequestResult.ToolResult(new JsonObject
            {
                ["runId"] = "playback-1", ["message"] = fail ? "Invalid GPX track." : "Started."
            }, isError: fail));
        }, hostApiSuites: RepositoryJavaScriptApiMethods.StandardHostToolSuites);

        var result = await executor.ExecuteAsync(new RepositoryTaskExecutionRequest(
            "run-location", task, "session-location", new JsonObject(), "location-test"), CancellationToken.None);

        Assert.Equal(1, calls);
        Assert.Equal(fail ? RepositoryTaskRunStatus.Error : RepositoryTaskRunStatus.Passed, result.Status);
        if (fail) Assert.Contains("Invalid GPX track.", result.Message);
    }
}
