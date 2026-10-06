using Ansight.Host.Runtime.RepositoryContracts;
using Ansight.Host.Runtime.Tasks;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed partial class RepositoryTaskTests
{
    [Fact]
    public void Motion_TypeScriptContractExposesShakeAndTimedSamples()
    {
        if (LocalTypeScriptTaskCompiler.ResolveCompilerPath() is null) return;
        using var repository = CreateRepository("export const placeholder = true;");
        var path = Path.Combine(repository.RootPath, "motion.ts");
        File.WriteAllText(path, """
            import type { TaskInvocation, DeviceMotionResult } from "./ansight-task.d.ts";
            export default async function run({ ansight }: TaskInvocation) {
              const shake: DeviceMotionResult = await ansight.device.shake({ intensity: 24, repetitions: 2 });
              const iosShake: DeviceMotionResult = await ansight.device.shake();
              const sequence: DeviceMotionResult = await ansight.device.playAccelerometer({
                samples: [{ x: 0, y: 9.81, z: 0, holdMs: 100 }]
              });
              const count: number = shake.sampleCount + sequence.sampleCount + (iosShake.gestureCount ?? 0);
              // @ts-expect-error Samples are required.
              await ansight.device.playAccelerometer({});
              // @ts-expect-error Device targeting is enforced by the host.
              await ansight.device.shake({ deviceId: "emulator-5554" });
            }
            """);
        Assert.Empty(LocalTypeScriptTaskCompiler.Validate(
            repository.RootPath, path, RepositoryModuleContractArtifacts.GetTaskTypeDefinitions()));
    }

    [Fact]
    public async Task Executor_MotionMethodsPinTaskSession()
    {
        const string module = """
            export const task = {
              "schemaVersion": 1,
              "appId": "com.example.app",
              "title": "Simulate motion",
              "description": "Checks a shake response.",
              "inputSchema": { "type": "object", "properties": {}, "additionalProperties": false },
              "timeoutSeconds": 10,
              "maximumActions": 3
            };
            export default async function run({ ansight, expect }) {
              const result = await ansight.device.shake({ intensity: 22, deviceId: "wrong-device" });
              expect(result.sampleCount, { id: "shake-delivered" }).toBe(6);
            }
            """;
        using var repository = CreateRepository(module);
        var task = Assert.Single(LoadRepository(repository.RootPath).Tasks);
        var calls = 0;
        var executor = new JavaScriptRepositoryTaskExecutor("node", (toolName, arguments, _) =>
        {
            calls++;
            Assert.Equal("ansight_shake_device", toolName);
            Assert.Equal("motion-session", arguments["sessionId"]!.GetValue<string>());
            Assert.Null(arguments["deviceId"]);
            Assert.Equal(22, arguments["intensity"]!.GetValue<int>());
            return Task.FromResult(RequestResult.ToolResult(new JsonObject { ["sampleCount"] = 6 }, isError: false));
        }, hostApiSuites: RepositoryJavaScriptApiMethods.StandardHostToolSuites);

        var result = await executor.ExecuteAsync(new RepositoryTaskExecutionRequest(
            "motion-run", task, "motion-session", new JsonObject(), "motion-test"), CancellationToken.None);

        Assert.Equal(1, calls);
        Assert.Equal(RepositoryTaskRunStatus.Passed, result.Status);
    }
}
