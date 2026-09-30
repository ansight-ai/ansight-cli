using System.Text;
using Ansight.Host.Runtime.DeviceExecution;
using Ansight.Host.Runtime.NativeLogs;
using Ansight.Host.Runtime.Operations;
using Ansight.Host.Runtime.Operations.Tools.SessionEvidence;
using Ansight.Host.Tests.TestSupport;
using Ansight.Infrastructure.Preferences;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class DeviceEvidenceCollectorTests
{
    [Fact]
    public async Task WatchedCollectorDoesNotRecordAReplacementProcess()
    {
        using var environment = new TestEnvironment();
        var state = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        var target = new WorkspaceTestTarget("android", "emulator-5554", "Phone", "test.app", false, false, false)
            { DeviceKind = DeviceKinds.Emulator, ExecutionMode = "device" };
        var id = state.CreateDeviceSession(target);
        var adb = Path.Combine(environment.RootPath, "adb");
        File.WriteAllText(adb, "mock executable");
        var preferences = new UserPreferences(new FilePreferencesStore(Path.Combine(environment.RootPath, "preferences.json"))) { AdbPath = adb };
        var logs = new NativeLogs();
        var collector = new DeviceSessionEvidence(state, preferences, logs, new AndroidCommands());
        try
        {
            await collector.AttachAsync(id, target, CancellationToken.None, expectedProcessIdentity: "23:previous-birth");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (true)
            {
                state.TryGetSessionSnapshot(id, out var session);
                var reason = session!.CustomProperties?["deviceExecution"]?["capabilities"]?["telemetry.process.cpu"]?["reason"]?.GetValue<string>();
                if (reason?.Contains("watched process ended", StringComparison.Ordinal) == true)
                {
                    Assert.Empty(session.MetricChannels);
                    Assert.Equal(0, logs.Attachments);
                    break;
                }
                await Task.Delay(50, timeout.Token);
            }
        }
        finally { await collector.StopAsync(id); }
    }

    [Fact]
    public async Task AndroidCollectorRetainsFilesAndRecoversFromInitialProcessFailure()
    {
        using var environment = new TestEnvironment();
        var state = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        var target = new WorkspaceTestTarget("android", "emulator-5554", "Phone", "test.app", false, false, true)
            { DeviceKind = DeviceKinds.Emulator, ExecutionMode = "device" };
        var id = state.CreateDeviceSession(target);
        var adb = Path.Combine(environment.RootPath, "adb");
        File.WriteAllText(adb, "mock executable");
        var preferences = new UserPreferences(new FilePreferencesStore(Path.Combine(environment.RootPath, "preferences.json"))) { AdbPath = adb };
        var commands = new AndroidCommands();
        var logs = new NativeLogs();
        var collector = new DeviceSessionEvidence(state, preferences, logs, commands);
        try
        {
            await collector.AttachAsync(id, target, CancellationToken.None);
            state.TryGetSessionSnapshot(id, out var initial);
            Assert.True(initial!.CustomProperties?["deviceExecution"]?["capabilities"]?["files.capture"]?["available"]?.GetValue<bool>());
            Assert.False(initial.CustomProperties?["deviceExecution"]?["capabilities"]?["telemetry.process.cpu"]?["available"]?.GetValue<bool>());
            Assert.Equal(new byte[] { 0, 255, 13, 10 }, await collector.RequireFiles(id).ReadFileAsync("data", "files/test.bin", 8, CancellationToken.None));
            var composition = new MefHostComposition(environment.ApplicationPaths,
                new FileBackedEncryptedStorage(environment.SecureStorageFilePath));
            using var dispatcher = new OperationDispatcher(state, environment.ApplicationPaths,
                composition.Get<IKnownAppStore>(), composition.Get<IPairingConfigService>(), composition.Get<IPairingConfigCache>(),
                composition.Get<AppService>(), composition.Get<PairingService>(), composition.Get<DotNetProfilingService>(),
                composition.Get<NativeProfilingService>(), composition.Get<IAppToolBridge>(), deviceEvidence: collector);
            var captured = await dispatcher.CallToolAsync("ansight_capture_sandbox_file", new JsonObject
            {
                ["sessionId"] = id, ["path"] = "files/test.bin"
            });
            Assert.False(captured.Payload?["isError"]?.GetValue<bool>(), captured.Payload?.ToJsonString());
            state.TryGetSessionSnapshot(id, out var retained);
            var artifact = Assert.Single(retained!.ArtifactSnapshots);
            var entry = artifact.Entries.Single(entry => entry.Name == "test.bin");
            Assert.True(SessionFileLocator.TryResolveArtifactEntryPath(environment.ApplicationPaths, retained, artifact, entry, out var artifactPath));
            Assert.Equal(new byte[] { 0, 255, 13, 10 }, await File.ReadAllBytesAsync(artifactPath));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (true)
            {
                state.TryGetSessionSnapshot(id, out var session);
                if (session!.MetricChannels.Any(channel => channel.Kind == "rendered-fps")
                    && session.MetricChannels.Any(channel => channel.Kind == "cpu-millicores")) break;
                await Task.Delay(50, timeout.Token);
            }
            Assert.Equal(1, logs.Attachments);
        }
        finally { await collector.StopAsync(id); }
        Assert.False(collector.IsCapturing(id));
        Assert.Equal(1, logs.Stops);
        var calls = commands.Calls;
        await Task.Delay(1100);
        Assert.Equal(calls, commands.Calls);
    }

    private sealed class NativeLogs : INativeSessionLogCaptureManager
    {
        public int Attachments { get; private set; }
        public int Stops { get; private set; }
        public Task AttachAsync(string sessionId, DeviceAppProfile? profile, string? profileJson)
        {
            Assert.Equal(23, profile!.App!.ProcessId);
            Attachments++;
            return Task.CompletedTask;
        }
        public Task StopAsync(string sessionId, string reason) { Stops++; return Task.CompletedTask; }
    }

    private sealed class AndroidCommands : IDeviceCommandRunner
    {
        private int samples;
        public int Calls { get; private set; }
        public Task<DeviceCommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken, int maximumBytes = 1_048_576)
        {
            Calls++;
            if (executable == "/usr/sbin/sysctl") return Result("test host");
            Assert.Equal("-s", arguments[0]);
            Assert.Equal("emulator-5554", arguments[1]);
            var command = arguments[3];
            if (command == "getconf CLK_TCK") return Result("100");
            if (command.StartsWith("dumpsys package")) return Result("versionName=1.0 versionCode=2 dataDir=/data/user/0/test.app");
            if (command.StartsWith("getprop")) return Result("test image");
            if (command == "am get-current-user") return Result("0");
            if (command.EndsWith(" pwd")) return Result("/data/user/0/test.app");
            if (command.Contains("head -c"))
            {
                Assert.Equal("exec-out", arguments[2]);
                Assert.StartsWith("run-as 'test.app' --user 0 sh -c ", command);
                Assert.Contains("realpath", command);
                Assert.Contains("case", command);
                return Task.FromResult(new DeviceCommandResult(0, [0, 255, 13, 10], ""));
            }
            if (command.StartsWith("pidof")) return Result("23");
            if (command.StartsWith("cat /proc/23/stat"))
            {
                if (++samples == 1) throw new IOException("Transient process sample failure");
                var fields = Enumerable.Repeat("0", 22).ToArray();
                fields[0] = "S"; fields[11] = (samples * 100).ToString(); fields[19] = "900";
                return Result("23 (test.app) " + string.Join(' ', fields));
            }
            if (command == "dumpsys meminfo 23") return Result("TOTAL RSS: 2048\nTOTAL PSS: 1024");
            if (command == "dumpsys gfxinfo 'test.app' framestats") return Result($"Flags,FrameCompleted,\n0,{samples * 100},");
            throw new InvalidOperationException("Unexpected command: " + command);
        }
        private static Task<DeviceCommandResult> Result(string value) => Task.FromResult(new DeviceCommandResult(0, Encoding.UTF8.GetBytes(value), ""));
    }
}
