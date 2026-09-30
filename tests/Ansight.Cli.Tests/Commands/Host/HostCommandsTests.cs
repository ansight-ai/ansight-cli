using Ansight.Infrastructure.Security;
using Ansight.Cli.Tests.TestSupport;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Ansight.Host;

namespace Ansight.Cli.Tests.Commands.Host;

public sealed class HostCommandsTests
{
    [Fact]
    public async Task DataDirectoryConflictIsReportedAsHostUnavailableInsteadOfAccessDenied()
    {
        using var directory = CreateHostTestDirectory();
        using var dataLock = CliDataDirectoryLock.Acquire(directory.Path);
        using var output = new StringWriter();

        var code = await CliApplication.RunParsedAsync(
            CliArguments.Parse(["host", "run", "--json", "--data-dir", directory.Path]),
            new CliOutput(true, output, output),
            CancellationToken.None,
            allowResidentHostForwarding: false,
            accessAuthorizer: TestAccessAuthorizer.Deny);

        Assert.Equal(CliExitCodes.HostUnavailable, code);
        var result = JsonNode.Parse(output.ToString())!.AsObject();
        Assert.Equal("host_unavailable", result["code"]!.GetValue<string>());
        Assert.DoesNotContain("product access", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact(Timeout = 30000)]
    public async Task StandaloneServeContinuesBeyondCloudGrantExpiry()
    {
        using var directory = CreateHostTestDirectory();
        var clock = new ManualTimeProvider();
        var websocketPort = GetAvailableTcpPort();
        var explorerPort = GetAvailableTcpPort(excluding: websocketPort);
        using var cancellation = new CancellationTokenSource();
        using var output = new StringWriter();
        var authorizer = new TestAccessAuthorizer(_ => throw new InvalidOperationException("Local serve must not check cloud access."));
        var task = CliApplication.RunParsedAsync(CliArguments.Parse(
            ["serve", "--data-dir", directory.Path, "--path", "local-test",
                "--discovery-port", GetAvailableUdpPort().ToString(), "--websocket-port", websocketPort.ToString(),
                "--port", explorerPort.ToString(), "--disable-repository-automations"]),
            new CliOutput(false, output, output), cancellation.Token,
            allowResidentHostForwarding: false, accessAuthorizer: authorizer, timeProvider: clock);
        try
        {
            await WaitUntilAsync(() => task.IsCompleted || output.ToString().Contains("Explorer:"));
            Assert.False(task.IsCompleted, output.ToString());
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var url = $"http://127.0.0.1:{explorerPort}/local-test/";
            Assert.Contains("Ansight Local Replay", await client.GetStringAsync(url));
            clock.Advance(TimeSpan.FromDays(365));
            Assert.False(task.IsCompleted);
            Assert.True((await client.GetAsync(url + "api/sessions")).IsSuccessStatusCode);
        }
        finally
        {
            cancellation.Cancel();
            Assert.Equal(CliExitCodes.Success, await task.WaitAsync(TimeSpan.FromSeconds(10)));
        }
    }

    [Fact(Timeout = 30000)]
    public async Task HostAndLocalPlayerStartWithoutCloudAuthorization()
    {
        using var directory = CreateHostTestDirectory();
        using var cancellation = new CancellationTokenSource();
        using var output = new StringWriter();
        var clock = new ManualTimeProvider();
        var authorizer = new TestAccessAuthorizer(_ => throw new InvalidOperationException("Local host must not check cloud access."));
        var task = CliApplication.RunParsedAsync(CliArguments.Parse(
            ["host", "run", "--data-dir", directory.Path, "--discovery-port", GetAvailableUdpPort().ToString(),
                "--websocket-port", GetAvailableTcpPort().ToString(), "--serve-port", "0",
                "--disable-repository-automations"]), new CliOutput(false, output, output), cancellation.Token,
            allowResidentHostForwarding: false, accessAuthorizer: authorizer, timeProvider: clock);
        try
        {
            var metadata = await WaitForHostMetadataAsync(directory.Path, task);
            Assert.NotNull(metadata.ControlPipeName);
            using var client = new HttpClient { BaseAddress = new Uri(metadata.ExplorerUrl!), Timeout = TimeSpan.FromSeconds(5) };
            var access = JsonNode.Parse(await client.GetStringAsync("api/access"))!.AsObject();
            Assert.True(access["isAuthorized"]!.GetValue<bool>());
            Assert.Equal("local", access["reason"]!.GetValue<string>());
            Assert.Contains("Ansight Local Replay", await client.GetStringAsync(""));
            Assert.True((await client.GetAsync("api/sessions")).IsSuccessStatusCode);
            clock.Advance(TimeSpan.FromDays(365));
            Assert.False(task.IsCompleted);
            Assert.True((await client.GetAsync("api/sessions")).IsSuccessStatusCode);
            Assert.Equal(metadata.ControlPipeName, CliRuntime.ReadMetadata(directory.Path)?.ControlPipeName);
        }
        finally
        {
            cancellation.Cancel();
            Assert.Equal(CliExitCodes.Success, await task.WaitAsync(TimeSpan.FromSeconds(10)));
        }
    }

    [Theory]
    [InlineData("stop", "Stop")]
    [InlineData("restart", "Restart")]
    [InlineData("update", "Update")]
    public void DesktopTrayActionsResolveToHostActions(
        string actionName,
        string expected)
    {
        var resolved = CliDesktopHost.TryResolveRequestedAction(
            new CliDesktopAction(CliDesktopProtocol.ActionSchema, actionName),
            out var action);

        Assert.True(resolved);
        Assert.Equal(expected, action.ToString());
    }

    [Fact]
    public void DesktopTrayActionsRejectUnknownOrUntrustedActions()
    {
        Assert.False(CliDesktopHost.TryResolveRequestedAction(
            new CliDesktopAction(CliDesktopProtocol.ActionSchema, "install-anything"),
            out _));
        Assert.False(CliDesktopHost.TryResolveRequestedAction(
            new CliDesktopAction("untrusted/v1", "restart"),
            out _));
    }

    [Fact]
    public async Task RunWithPairRequiresAnAppIdentifierBeforeStartingTheHost()
    {
        using var directory = TestDirectory.Create();
        var dataDirectory = Path.Combine(directory.Path, "host-state");
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();

        var exitCode = await CliApplication.RunParsedAsync(
            CliArguments.Parse(["host", "run", "--pair", "--data-dir", dataDirectory]),
            new CliOutput(false, standardOutput, standardError),
            CancellationToken.None,
            accessAuthorizer: TestAccessAuthorizer.Allow,
            allowResidentHostForwarding: false);

        Assert.Equal(CliExitCodes.Usage, exitCode);
        Assert.Contains("--pair requires an app identifier", standardError.ToString(), StringComparison.Ordinal);
        Assert.Contains("ansight app list", standardError.ToString(), StringComparison.Ordinal);
        Assert.False(Directory.Exists(dataDirectory));
    }

    [Fact]
    public async Task HelpDescribesAllInOnePairing()
    {
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();

        var exitCode = await CliApplication.RunParsedAsync(
            CliArguments.Parse(["host", "help"]),
            new CliOutput(false, standardOutput, standardError),
            CancellationToken.None,
            accessAuthorizer: TestAccessAuthorizer.Allow,
            allowResidentHostForwarding: false);

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Contains("ansight host run --pair com.example.app", standardOutput.ToString(), StringComparison.Ordinal);
        Assert.Contains("ansight session list --connected --json", standardOutput.ToString(), StringComparison.Ordinal);
        Assert.Contains("ansight ui snapshot", standardOutput.ToString(), StringComparison.Ordinal);
        Assert.Contains("local explorer", standardOutput.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--serve-path", standardOutput.ToString(), StringComparison.Ordinal);
        Assert.Contains("explorer-port", standardOutput.ToString(), StringComparison.Ordinal);
        Assert.Contains("--no-serve", standardOutput.ToString(), StringComparison.Ordinal);
        Assert.Contains("--companion-access [session|always]", standardOutput.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, standardError.ToString());
    }

    [Fact]
    public async Task CompanionAccessRejectsUnknownModeBeforeStartingHost()
    {
        using var directory = TestDirectory.Create();
        var dataDirectory = Path.Combine(directory.Path, "host-state");
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();

        var exitCode = await CliApplication.RunParsedAsync(
            CliArguments.Parse(
                ["host", "run", "--companion-access", "sometimes", "--data-dir", dataDirectory]),
            new CliOutput(false, standardOutput, standardError),
            CancellationToken.None,
            accessAuthorizer: TestAccessAuthorizer.Allow,
            allowResidentHostForwarding: false);

        Assert.Equal(CliExitCodes.Usage, exitCode);
        Assert.Contains("--companion-access must be session or always", standardError.ToString(), StringComparison.Ordinal);
        Assert.False(Directory.Exists(dataDirectory));
    }

    [Fact(Timeout = 30000)]
    public async Task RunningHostAlwaysServesAndPublishesTheLocalExplorerUrl()
    {
        using var directory = CreateHostTestDirectory();
        var dataDirectory = Path.Combine(directory.Path, "host-state");
        var websocketPort = GetAvailableTcpPort();
        var explorerPort = GetAvailableTcpPort(excluding: websocketPort);
        var discoveryPort = GetAvailableUdpPort();
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();
        using var cancellation = new CancellationTokenSource();
        Uri? explorerUrl = null;
        new LocalSettingsStore(dataDirectory).SetExplorerPath("cli-explorer");
        new LocalSettingsStore(dataDirectory).SetExplorerPort(explorerPort.ToString());
        var hostTask = CliApplication.RunParsedAsync(
            CliArguments.Parse(
            [
                "host", "run",
                "--data-dir", dataDirectory,
                "--discovery-port", discoveryPort.ToString(),
                "--websocket-port", websocketPort.ToString()
            ]),
            new CliOutput(false, standardOutput, standardError),
            cancellation.Token,
            accessAuthorizer: TestAccessAuthorizer.Allow,
            allowResidentHostForwarding: false);

        try
        {
            var metadata = await WaitForHostMetadataAsync(dataDirectory, hostTask);
            Assert.True(CliRuntime.IsHostProcessRunning(metadata));
            Assert.Equal(CliReleaseIdentity.Current.Version, metadata.CliVersion);
            Assert.Equal(CliReleaseIdentity.Current.BuildNumber, metadata.CliBuildNumber);
            Assert.Equal(CliReleaseIdentity.Current.CommitSha, metadata.CommitSha);
            explorerUrl = new Uri(Assert.IsType<string>(metadata.ExplorerUrl));
            Assert.Equal("127.0.0.1", explorerUrl.Host);
            Assert.Equal(explorerPort, explorerUrl.Port);
            Assert.Equal("/cli-explorer/", explorerUrl.AbsolutePath);

            using var httpClient = new HttpClient();
            Assert.Contains(
                "Ansight Local Replay",
                await httpClient.GetStringAsync(explorerUrl),
                StringComparison.Ordinal);
            var settings = JsonNode.Parse(
                await httpClient.GetStringAsync(new Uri(explorerUrl, "api/settings")))!.AsObject();
            if (OperatingSystem.IsMacOS())
            {
                Assert.False(settings["companionAccessAvailable"]!.GetValue<bool>());
                Assert.Equal("disabled", settings["companionAccessMode"]!.GetValue<string>());
            }

            using var statusOutput = new StringWriter();
            using var statusError = new StringWriter();
            var statusExitCode = await CliApplication.RunParsedAsync(
                CliArguments.Parse(["host", "status", "--json", "--data-dir", dataDirectory]),
                new CliOutput(true, statusOutput, statusError),
                CancellationToken.None,
                accessAuthorizer: TestAccessAuthorizer.Allow,
                allowResidentHostForwarding: false);
            var status = JsonNode.Parse(statusOutput.ToString())!.AsObject();
            Assert.Equal(CliExitCodes.Success, statusExitCode);
            Assert.Equal(explorerUrl.ToString(), status["explorerUrl"]!.GetValue<string>());
            Assert.Equal(string.Empty, statusError.ToString());
        }
        finally
        {
            cancellation.Cancel();
        }

        Assert.Equal(CliExitCodes.Success, await hostTask.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.Contains($"Explorer: {explorerUrl}", standardOutput.ToString(), StringComparison.Ordinal);
        Assert.Contains(
            $"Version: {CliReleaseIdentity.Current.Version} ({CliReleaseIdentity.Current.BuildNumber}",
            standardOutput.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoServeCannotBeCombinedWithExplorerOptions()
    {
        using var directory = TestDirectory.Create();
        var dataDirectory = Path.Combine(directory.Path, "host-state");
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();

        var exitCode = await CliApplication.RunParsedAsync(
            CliArguments.Parse(
                ["host", "run", "--no-serve", "--open", "--data-dir", dataDirectory]),
            new CliOutput(false, standardOutput, standardError),
            CancellationToken.None,
            accessAuthorizer: TestAccessAuthorizer.Allow,
            allowResidentHostForwarding: false);

        Assert.Equal(CliExitCodes.Usage, exitCode);
        Assert.Contains(
            "--no-serve cannot be combined with --open, --serve-port, or --serve-path",
            standardError.ToString(),
            StringComparison.Ordinal);
        Assert.False(Directory.Exists(dataDirectory));
    }

    [Fact(Timeout = 30000)]
    public async Task RunningHostCanSkipTheLocalExplorer()
    {
        using var directory = CreateHostTestDirectory();
        var dataDirectory = Path.Combine(directory.Path, "host-state");
        var websocketPort = GetAvailableTcpPort();
        var discoveryPort = GetAvailableUdpPort();
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();
        using var cancellation = new CancellationTokenSource();
        var hostTask = CliApplication.RunParsedAsync(
            CliArguments.Parse(
            [
                "host", "run", "--no-serve", "--json",
                "--data-dir", dataDirectory,
                "--discovery-port", discoveryPort.ToString(),
                "--websocket-port", websocketPort.ToString()
            ]),
            new CliOutput(true, standardOutput, standardError),
            cancellation.Token,
            accessAuthorizer: TestAccessAuthorizer.Allow,
            allowResidentHostForwarding: false);

        try
        {
            var metadata = await WaitForHostMetadataAsync(dataDirectory, hostTask);
            Assert.True(CliRuntime.IsHostProcessRunning(metadata));
            Assert.Null(metadata.ExplorerUrl);
        }
        finally
        {
            cancellation.Cancel();
        }

        Assert.Equal(CliExitCodes.Success, await hostTask.WaitAsync(TimeSpan.FromSeconds(15)));
        var started = JsonNode.Parse(standardOutput.ToString())!.AsObject();
        Assert.Equal("ansight.host/v1", started["schema"]!.GetValue<string>());
        Assert.Equal(CliReleaseIdentity.Current.Version, started["cliVersion"]!.GetValue<string>());
        Assert.Equal(CliReleaseIdentity.Current.BuildNumber, started["cliBuildNumber"]!.GetValue<long>());
        Assert.Null(started["explorerUrl"]);
    }

    [Fact]
    public async Task HostLogsReportsTheMostRecentPersistedHostLog()
    {
        using var directory = CreateHostTestDirectory();
        var dataDirectory = Path.Combine(directory.Path, "host-state");
        var logDirectory = Path.Combine(dataDirectory, "logs");
        Directory.CreateDirectory(logDirectory);
        var olderLog = Path.Combine(logDirectory, "host-2026-08-25T01-00-00-100.log");
        var latestLog = Path.Combine(logDirectory, "host-2026-08-26T01-00-00-200.log");
        await File.WriteAllTextAsync(olderLog, "older");
        await File.WriteAllTextAsync(latestLog, "latest");
        File.SetLastWriteTimeUtc(olderLog, new DateTime(2026, 8, 25, 1, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(latestLog, new DateTime(2026, 8, 26, 1, 0, 0, DateTimeKind.Utc));
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();

        var exitCode = await CliApplication.RunParsedAsync(
            CliArguments.Parse(["host", "logs", "--data-dir", dataDirectory]),
            new CliOutput(false, standardOutput, standardError),
            CancellationToken.None,
            accessAuthorizer: TestAccessAuthorizer.Allow,
            allowResidentHostForwarding: false);

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Contains($"Host logs: {logDirectory}", standardOutput.ToString(), StringComparison.Ordinal);
        Assert.Contains($"Current: {latestLog}", standardOutput.ToString(), StringComparison.Ordinal);
        Assert.Contains("ansight session logs <session-id>", standardOutput.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, standardError.ToString());
    }

    [Fact]
    public void PairingAcceptedMessagesDescribeTheSessionAndSuggestThePortal()
    {
        var runtimeEvent = new RuntimePairingEvent(
            DateTimeOffset.UtcNow,
            RuntimePairingEventKind.PairingAccepted,
            "session-001",
            "com.example.app",
            "Example iPhone",
            "192.0.2.10",
            "invite-001",
            "Ok",
            "Accepted");

        Assert.Contains(
            "Accepted 'Example iPhone' for 'com.example.app'",
            HostSessionProgressMessages.BuildPairingAccepted(runtimeEvent),
            StringComparison.Ordinal);
        Assert.Contains(
            "Created 'session-001'",
            HostSessionProgressMessages.BuildSessionCreated(runtimeEvent),
            StringComparison.Ordinal);
        Assert.Equal(
            "[next] Open the local portal in another terminal: ansight serve --session 'session-001' --open",
            HostSessionProgressMessages.BuildPortalSuggestion(runtimeEvent.SessionId));
    }

    [Fact]
    public void CaptureMessagesDescribeTheLiveConnectionLifecycle()
    {
        var started = new RuntimeSessionCaptureEvent(
            DateTimeOffset.UtcNow,
            RuntimeSessionCaptureEventKind.Started,
            "session-001",
            "com.example.app",
            "Example iPhone",
            "WebSocket Open",
            "WebSocket handshake accepted.");
        var stopped = started with
        {
            Kind = RuntimeSessionCaptureEventKind.Stopped,
            Status = "WebSocket Closed",
            Message = "Client closed the connection."
        };

        Assert.Contains("recording session 'session-001'", HostSessionProgressMessages.BuildCaptureStarted(started), StringComparison.Ordinal);
        Assert.Contains("Client closed the connection", HostSessionProgressMessages.BuildCaptureStopped(stopped), StringComparison.Ordinal);
    }

    private static async Task<CliHostMetadata> WaitForHostMetadataAsync(
        string dataDirectory,
        Task<int> hostTask)
    {
        var timeoutAtUtc = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < timeoutAtUtc)
        {
            var metadata = CliRuntime.ReadMetadata(dataDirectory);
            if (metadata is not null)
            {
                return metadata;
            }

            if (hostTask.IsCompleted)
            {
                throw new InvalidOperationException($"Host exited before publishing metadata with exit code {await hostTask}.");
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("Host did not publish metadata within 15 seconds.");
    }

    private static int GetAvailableTcpPort(params int[] excluding)
    {
        while (true)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            if (!excluding.Contains(port)
                && (port < ProtocolDefaults.WebSocketSessionPortRangeStart
                    || port > ProtocolDefaults.WebSocketSessionPortRangeEnd))
            {
                return port;
            }
        }
    }

    private static int GetAvailableUdpPort()
    {
        using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)client.Client.LocalEndPoint!).Port;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private static TestDirectory CreateHostTestDirectory()
    {
        var directory = TestDirectory.Create();
        var key = Path.Combine(directory.Path, "test-key");
        FileEncryptionKeyProvider.CreateKeyFile(key);
        new LocalSettingsStore(directory.Path).SetCredentials(new CredentialSettings("protected-file", key, Path.Combine(directory.Path, "test-store.json")));
        var hostState = Path.Combine(directory.Path, "host-state");
        Directory.CreateDirectory(hostState);
        new LocalSettingsStore(hostState).SetCredentials(new CredentialSettings("protected-file", key, Path.Combine(hostState, "test-store.json")));
        return directory;
    }
}
