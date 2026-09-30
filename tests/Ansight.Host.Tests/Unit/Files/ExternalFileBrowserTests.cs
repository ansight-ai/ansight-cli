using Ansight.Host.Runtime.DeviceExecution;
using Ansight.Host.Runtime.Operations;
using Ansight.Host.Tests.TestSupport;

namespace Ansight.Host.Tests.Unit.Files;

public sealed class ExternalFileBrowserTests
{
    [Fact]
    public async Task BrowseReadAndCapture_UseTheExternalProviderAndRespectLeaseAndVersion()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(Path.Combine(fixture.Root, "Documents", "nested"));
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "Documents", "note.txt"), "hello notes");
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "Documents", ".hidden"), "secret");
        var listed = await fixture.Call("files.list_directory", new() { ["path"] = "Documents", ["recursive"] = true });
        Assert.True(listed.Success, listed.Message);
        var directory = Result(listed);
        Assert.Equal("simulator-filesystem", directory["provider"]?.GetValue<string>());
        Assert.Contains(directory["entries"]!.AsArray(), entry => entry?["relativePath"]?.GetValue<string>() == "Documents/note.txt");
        Assert.DoesNotContain(directory["entries"]!.AsArray(), entry => entry?["name"]?.GetValue<string>() == ".hidden");
        var first = await fixture.Call("files.download_file", new() { ["path"] = "Documents/note.txt", ["maxBytes"] = 5 });
        Assert.True(first.Success, first.Message);
        var bytes = Result(first);
        Assert.Equal("hello", Encoding.UTF8.GetString(Convert.FromBase64String(bytes["base64"]!.GetValue<string>())));
        Assert.True(bytes["hasMore"]!.GetValue<bool>());
        var version = bytes["version"]!.GetValue<string>();
        await File.AppendAllTextAsync(Path.Combine(fixture.Root, "Documents", "note.txt"), " changed");
        var stale = await fixture.Call("files.download_file", new() { ["path"] = "Documents/note.txt", ["offsetBytes"] = 5L, ["expectedVersion"] = version });
        Assert.False(stale.Success);
        Assert.Contains("changed", stale.Message);
        var captured = await fixture.Call(BinaryFileDownloadManager.BeginBinaryDownloadToolId, new() { ["path"] = "Documents/note.txt" });
        Assert.True(captured.Success, captured.Message);
        Assert.Equal("captured-file", captured.ArtifactSnapshotId);
        Assert.Equal("Documents/note.txt", fixture.CapturedPath);
        fixture.State.EndDeviceSession(fixture.Id);
        Assert.False((await fixture.Call("files.list_directory", new() { ["path"] = "" })).Success);
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("/etc/passwd")]
    [InlineData("Documents/../outside")]
    public async Task PathsCannotEscapeSandbox(string path)
    {
        using var fixture = new Fixture();
        var result = await fixture.Call("files.download_file", new() { ["path"] = path });
        Assert.False(result.Success);
    }

    [Fact]
    public async Task SymlinksAreListedButCannotBeReadOrTraversed()
    {
        using var fixture = new Fixture();
        var outside = Path.Combine(fixture.Environment.RootPath, "outside.txt");
        await File.WriteAllTextAsync(outside, "outside");
        File.CreateSymbolicLink(Path.Combine(fixture.Root, "link"), outside);
        var listing = Result(await fixture.Call("files.list_directory", new() { ["path"] = "" }));
        Assert.Equal("symlink", Assert.Single(listing["entries"]!.AsArray())?["kind"]?.GetValue<string>());
        Assert.False((await fixture.Call("files.download_file", new() { ["path"] = "link" })).Success);
    }

    [Fact]
    public async Task SqliteWalCopyIncludesUncheckpointedNotesAndQueriesCannotWrite()
    {
        using var fixture = new Fixture();
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_sqlite3());
        var path = Path.Combine(fixture.Root, "notes.sqlite");
        await using var database = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False");
        await database.OpenAsync();
        await using (var command = database.CreateCommand())
        {
            command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; CREATE TABLE notes(id INTEGER PRIMARY KEY, body TEXT); INSERT INTO notes(body) VALUES ('uncheckpointed note');";
            await command.ExecuteNonQueryAsync();
        }
        Assert.True(File.Exists(path + "-wal"));
        using var copy = await fixture.Service.CopyAsync(fixture.Id, "data", "notes.sqlite", true, CancellationToken.None);
        Assert.True(File.Exists(copy.Path + "-wal"));
        var viewer = new FileVisualizationService();
        var preview = await viewer.InspectLocalFileAsync(copy.Path);
        Assert.True(preview.IsSuccess, preview.Message);
        Assert.Equal(FileViewerKinds.Sqlite, preview.ViewerKind);
        var rows = await viewer.QueryLocalDatabaseAsync(copy.Path, "SELECT body FROM notes");
        Assert.True(rows.IsSuccess, rows.Message);
        Assert.Equal("uncheckpointed note", Assert.Single(Assert.Single(rows.Rows)));
        Assert.False((await viewer.QueryLocalDatabaseAsync(copy.Path, "DELETE FROM notes")).IsSuccess);
    }

    [Fact]
    public async Task AppToolsOfferSdkSetupInsteadOfAttemptingExternalExecution()
    {
        var service = new AppToolService(new TestAppToolBridge(), _ => true);
        var result = await service.QueryAsync("external-session");
        Assert.False(result.Success);
        Assert.Contains("Add the Ansight SDK", result.Message);
        Assert.Equal(ExecutionCapabilities.SdkSetupUrl, result.Envelope?.Payload?["action"]?["url"]?.GetValue<string>());
        Assert.DoesNotContain("Add the Ansight SDK", ExecutionCapabilities.Unavailable("telemetry.fps",
            new JsonObject { ["executionMode"] = "device" }, "Collector unavailable.")["message"]!.GetValue<string>());
    }

    private static JsonObject Result(RuntimeAppToolResponse response) => response.Envelope!.Payload!["result"]!.AsObject();

    private sealed class Fixture : IDisposable
    {
        public TestEnvironment Environment { get; } = new();
        public RuntimeState State { get; }
        public string Root { get; }
        public string Id { get; }
        public string? CapturedPath { get; private set; }
        public LiveSessionFileService Service { get; }
        public Fixture()
        {
            Root = Path.Combine(Environment.RootPath, "sandbox"); Directory.CreateDirectory(Root);
            State = new RuntimeState(new SessionCaptureStore(Environment.ApplicationPaths));
            var target = new WorkspaceTestTarget("ios", "sim-file-browser", "Test simulator", "test.files", false, false, true)
                { DeviceKind = DeviceKinds.Simulator, ExecutionMode = "device" };
            Id = State.CreateDeviceSession(target);
            var provider = new DevicePlatformProbe(target, new Commands(Root), null);
            Service = new LiveSessionFileService(State, new AppToolService(new TestAppToolBridge()), _ => provider,
                (tool, arguments, token) =>
                {
                    Assert.Equal("ansight_capture_sandbox_file", tool);
                    Assert.Equal(Id, arguments["sessionId"]?.GetValue<string>());
                    CapturedPath = arguments["path"]?.GetValue<string>();
                    return Task.FromResult(RequestResult.ToolResult(new JsonObject { ["snapshotId"] = "captured-file" }, false));
                });
        }
        public Task<RuntimeAppToolResponse> Call(string operation, JsonObject arguments)
            => Service.CallAsync(Id, operation, arguments, CancellationToken.None);
        public void Dispose() => Environment.Dispose();
    }

    private sealed class Commands(string root) : IDeviceCommandRunner
    {
        public Task<DeviceCommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken, int maximumBytes = 1048576)
        {
            Assert.Contains("sim-file-browser", arguments);
            Assert.Contains("test.files", arguments);
            return Task.FromResult(new DeviceCommandResult(0, Encoding.UTF8.GetBytes(arguments.Last() == "groups" ? "" : root), ""));
        }
    }
    private sealed class TestAppToolBridge : IAppToolBridge
    {
        public event EventHandler? ConnectionsChanged { add { } remove { } }
        public IReadOnlyList<string> GetConnectedSessionIds() => [];
        public bool IsSessionConnected(string sessionId) => false;
        public OperationResult ForceDisconnectSession(string sessionId) => throw new NotSupportedException();
        public Task<AppToolBridgeResponse> QueryToolsAsync(string sessionId, CancellationToken cancellationToken,
            AppToolBridgeRequestContext? requestContext = null) => throw new InvalidOperationException("An external file operation must not call the SDK.");
        public Task<AppToolBridgeResponse> CallToolAsync(string sessionId, string toolId, JsonObject? arguments,
            CancellationToken cancellationToken, AppToolBridgeRequestContext? requestContext = null)
            => throw new InvalidOperationException("An external file operation must not call the SDK.");
    }
}
