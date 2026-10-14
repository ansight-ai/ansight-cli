using Ansight.Host.Tests.TestSupport;

namespace Ansight.Host.Tests.Integration;

public sealed class SessionUiTestReplayTests
{
    [Fact(Timeout = 30000)]
    public async Task ExplorerPublishesTestStatusChangesWhileCaptureRemainsLive()
    {
        using var environment = new TestEnvironment();
        var composition = new MefHostComposition(environment.ApplicationPaths,
            new FileBackedEncryptedStorage(environment.SecureStorageFilePath));
        await using var runtime = composition.Get<RuntimeCoordinator>();
        var state = composition.Get<IRuntimeState>();
        var id = state.CreateDeviceSession(new WorkspaceTestTarget("android", "phone", "Phone", "test.app", false, false, false)
        { ExecutionMode = "device", DeviceKind = DeviceKinds.Physical });
        var explorer = await runtime.SessionReplays.StartExplorerAsync(new SessionExplorerStartRequest());
        Assert.True(explorer.IsSuccess, explorer.Message);
        using var client = new HttpClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var events = await client.GetAsync(new Uri(explorer.ExplorerUrl!, "api/session-events"),
            HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        using var reader = new StreamReader(await events.Content.ReadAsStreamAsync(timeout.Token));
        await ReadEventAsync("ready");

        var run = new SessionUiTestRun("checkout", "Checkout", "run-1", "running", "task", DateTimeOffset.UtcNow);
        state.SetSessionUiTestRun(id, run);
        await ReadEventAsync("sessions-changed");
        var running = await GetSessionAsync();
        Assert.Equal("running", running["customProperties"]!["ui-test"]!["status"]!.GetValue<string>());

        state.SetSessionUiTestRun(id, run with { Status = "succeeded", CompletedUtc = DateTimeOffset.UtcNow });
        await ReadEventAsync("sessions-changed");
        var completed = await GetSessionAsync();
        Assert.True(completed["isConnected"]!.GetValue<bool>());
        Assert.Equal("Checkout", completed["customProperties"]!["ui-test"]!["testName"]!.GetValue<string>());
        Assert.Equal("succeeded", completed["customProperties"]!["ui-test"]!["status"]!.GetValue<string>());
        Assert.Equal(running["lastUpdatedUtc"]!.GetValue<string>(), completed["lastUpdatedUtc"]!.GetValue<string>());

        async Task<JsonNode> GetSessionAsync()
            => Assert.Single(JsonNode.Parse(await client.GetStringAsync(new Uri(explorer.ExplorerUrl!, "api/sessions"), timeout.Token))!.AsArray())!;

        async Task ReadEventAsync(string eventName)
        {
            var found = false;
            while (await reader.ReadLineAsync(timeout.Token) is { } line)
            {
                found |= line == $"event: {eventName}";
                if (found && line.Length == 0) return;
            }
            Assert.Fail($"Expected server event '{eventName}'.");
        }
    }
}
