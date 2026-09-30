namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class RepositoryAutomationLinkTests
{
    [Fact]
    public async Task LinkedWorkspaceAutomaticallyConnectsAndDisconnectsTriggers()
    {
        using var environment = new TestSupport.TestEnvironment();
        var triggerDirectory = Path.Combine(environment.RootPath, "ansight", "triggers");
        Directory.CreateDirectory(triggerDirectory);
        File.WriteAllText(
            Path.Combine(triggerDirectory, "capture-error.ts"),
            """
            export const trigger = {
              "schemaVersion": 1,
              "eventKind": "session.log.received",
              "functionTimeoutMs": 100,
              "actionTimeoutSeconds": 10
            };

            export default async function captureError() {
              return null;
            }
            """);
        await using var runtime = environment.CreateRuntime(enableRepositoryAutomations: true);
        await runtime.StartAsync();

        var linked = runtime.Apps.Register(new AppRegistrationRequest(
            "com.example.app",
            "Example App",
            environment.RootPath));

        Assert.True(linked.IsSuccess);
        var connection = runtime.RepositoryAutomations.Inspect("com.example.app", environment.RootPath);
        Assert.True(connection.IsConnected, string.Join(Environment.NewLine, connection.Warnings));
        Assert.Single(runtime.RepositoryAutomations.GetTriggers());

        Assert.True(runtime.Apps.Remove("com.example.app").IsSuccess);
        Assert.Empty(runtime.RepositoryAutomations.GetTriggers());
    }
}
