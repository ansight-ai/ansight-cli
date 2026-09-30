using Ansight.Host.Runtime.Permissions;
using Ansight.Host.Runtime.DeviceExecution;
using Ansight.Host.Tests.TestSupport;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class NativePermissionTests
{
    private const string App = "com.example.app";
    private const string RecordAudio = "android.permission.RECORD_AUDIO";
    private static DeviceDescriptor Device(string platform = "android", string kind = "virtual")
        => new("test-device", "Test device", platform, "test-runtime", "Booted", true, true, kind);

    [Theory]
    [InlineData(32, 32, "android.permission.READ_EXTERNAL_STORAGE")]
    [InlineData(33, 32, "android.permission.READ_EXTERNAL_STORAGE")]
    [InlineData(33, 33, "android.permission.READ_MEDIA_IMAGES")]
    public async Task SharedPhotosUsesOsAndTargetSdk(int api, int targetSdk, string expected)
    {
        var runner = new FakeCommands();
        runner.Shell = command => command switch
        {
            "am get-current-user" => "10",
            "getprop ro.build.version.sdk" => api.ToString(),
            _ => Dump(targetSdk, [expected], false)
        };
        var result = await Service(runner).ExecuteAsync("query", "photos", null, "test-device", App, default);
        Assert.Equal(expected, Assert.Single(result.NativePermissions).Permission);
        Assert.Equal("denied", result.Status);
        Assert.DoesNotContain(runner.Calls, call => call.Contains("pm grant"));
    }

    [Fact]
    public async Task GrantPinsUserAndReadsStateAfterMutation()
    {
        var granted = false;
        var runner = new FakeCommands();
        runner.Shell = command =>
        {
            if (command == "am get-current-user") return "10";
            if (command == "getprop ro.build.version.sdk") return "36";
            if (command.StartsWith("pm grant")) { granted = true; return ""; }
            return Dump(36, [RecordAudio], granted);
        };
        var result = await Service(runner).ExecuteAsync("grant", "microphone", null, "test-device", App, default);
        Assert.True(result.IsSuccess);
        Assert.Equal("granted", result.Status);
        Assert.Contains(runner.Calls, call => call.Contains("pm grant --user 10 'com.example.app' 'android.permission.RECORD_AUDIO'"));
    }

    [Fact]
    public async Task GrantRejectsUndeclaredPermissionBeforeMutation()
    {
        var runner = new FakeCommands { Shell = command => command == "am get-current-user" ? "10" : Dump(36, [RecordAudio], false) };
        var result = await Service(runner).ExecuteAsync("grant", "android.permission.CAMERA", "android", "test-device", App, default);
        Assert.False(result.Supported);
        Assert.False(result.IsSuccess);
        Assert.DoesNotContain(runner.Calls, call => call.Contains("pm grant"));
    }

    [Fact]
    public async Task PartialNativeFailureRetainsObservedStates()
    {
        const string read = "android.permission.READ_CALENDAR";
        const string write = "android.permission.WRITE_CALENDAR";
        var granted = false;
        var runner = new FakeCommands();
        runner.Shell = command =>
        {
            if (command == "am get-current-user") return "10";
            if (command == "getprop ro.build.version.sdk") return "36";
            if (command.StartsWith("pm grant"))
            {
                if (command.Contains(write)) throw new IOException("Permission is fixed by policy.");
                granted = true;
                return "";
            }
            return Dump(36, [read, write], false).Replace(read + ": granted=false", read + ": granted=" + (granted ? "true" : "false"));
        };
        var result = await Service(runner).ExecuteAsync("grant", "calendar", null, "test-device", App, default);
        Assert.False(result.IsSuccess);
        Assert.Equal("limited", result.Status);
        Assert.Contains("fixed by policy", result.Message);
        Assert.Equal(["granted", "denied"], result.NativePermissions.Select(state => state.Status));
    }

    [Fact]
    public async Task PhotosReportsSelectedAccessAndRevokesIt()
    {
        const string images = "android.permission.READ_MEDIA_IMAGES";
        const string selected = "android.permission.READ_MEDIA_VISUAL_USER_SELECTED";
        var revoked = false;
        var runner = new FakeCommands();
        runner.Shell = command =>
        {
            if (command == "am get-current-user") return "10";
            if (command == "getprop ro.build.version.sdk") return "36";
            if (command.StartsWith("pm revoke")) { if (command.Contains(selected)) revoked = true; return ""; }
            return Dump(36, [images, selected], false).Replace(selected + ": granted=false", selected + ": granted=" + (revoked ? "false" : "true"));
        };
        var service = Service(runner);
        Assert.Equal("limited", (await service.ExecuteAsync("query", "photos", null, "test-device", App, default)).Status);
        var result = await service.ExecuteAsync("revoke", "photos", null, "test-device", App, default);
        Assert.Equal("denied", result.Status);
        Assert.True(revoked);
    }

    [Fact]
    public async Task PlatformMismatchAndPhysicalIosDoNotRunCommands()
    {
        var runner = new FakeCommands();
        await Assert.ThrowsAsync<ArgumentException>(() => Service(runner).ExecuteAsync("grant", RecordAudio, "ios", "test-device", App, default));
        var result = await Service(runner, Device("ios", "physical")).ExecuteAsync("query", "microphone", null, "test-device", App, default);
        Assert.Equal("unsupported", result.Status);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task IosQueriesNativeDatabaseAndOnlyMutatesServicesListedBySimctl()
    {
        using var environment = new TestEnvironment();
        var root = environment.ApplicationPaths.ApplicationDataPath;
        Directory.CreateDirectory(Path.Combine(root, "Library", "TCC"));
        File.WriteAllText(Path.Combine(root, "Library", "TCC", "TCC.db"), "fake native database");
        var nativeState = 2;
        var runner = new FakeCommands();
        runner.Other = (exe, args) =>
        {
            if (exe == "/usr/bin/sqlite3") return args[^1].StartsWith("PRAGMA")
                ? """[{"name":"client"},{"name":"service"},{"name":"client_type"},{"name":"auth_value"}]"""
                : nativeState < 0 ? "[]" : $"[{{\"value\":{nativeState}}}]";
            return args[0] switch
            {
                "get_app_container" => root,
                "help" => "    microphone - Allow audio input.\n",
                "privacy" => SetState(args[2]),
                "list" => new JsonObject { ["devices"] = new JsonObject { ["ios"] = new JsonArray(new JsonObject { ["udid"] = "test-device", ["dataPath"] = root }) } }.ToJsonString(),
                _ => throw new InvalidOperationException("Unexpected native command.")
            };
        };
        string SetState(string action) { nativeState = action == "grant" ? 2 : action == "reset" ? -1 : 0; return ""; }
        var service = Service(runner, Device("ios"));
        Assert.Equal("granted", (await service.ExecuteAsync("query", "microphone", null, "test-device", App, default)).Status);
        Assert.Equal("denied", (await service.ExecuteAsync("revoke", "microphone", "ios", "test-device", App, default)).Status);
        Assert.Equal("granted", (await service.ExecuteAsync("grant", "microphone", "ios", "test-device", App, default)).Status);
        Assert.Equal("notDetermined", (await service.ExecuteAsync("reset", "microphone", "ios", "test-device", App, default)).Status);
        var unsupported = await service.ExecuteAsync("grant", "camera", null, "test-device", App, default);
        Assert.False(unsupported.Supported);
        Assert.DoesNotContain(runner.Calls, call => call.Contains("privacy test-device grant camera"));
        Assert.Contains(runner.Calls, call => call.Contains("-readonly -json"));
    }

    [Theory]
    [InlineData(0, "denied")]
    [InlineData(2, "granted")]
    [InlineData(3, "limited")]
    [InlineData(99, "unknown")]
    public void TccPreservesLimitedAndUnknownValues(int value, string expected)
        => Assert.Equal(expected, NativePermissionService.ReadTccState(value));

    [Fact]
    public void LocationDistinguishesForegroundAndAlways()
    {
        var clients = JsonNode.Parse("""{"icom.example.app":{"BundleId":"com.example.app","Authorization":2}}""")!.AsObject();
        Assert.Equal("granted", NativePermissionService.ReadLocationState(clients, App, "location"));
        Assert.Equal("limited", NativePermissionService.ReadLocationState(clients, App, "location-always"));
        Assert.Equal("notDetermined", NativePermissionService.ReadLocationState(clients, "com.other.app", "location"));
        Assert.Equal("granted", NativePermissionService.ReadLocationState(JsonNode.Parse("""{"icom.example.app":{"Authorization":4}}""")!.AsObject(), App, "location-always"));
        Assert.Equal("denied", NativePermissionService.ReadLocationState(JsonNode.Parse("""{"icom.example.app":{"Authorization":1}}""")!.AsObject(), App, "location"));
        Assert.Equal("granted", NativePermissionService.ReadLocationState(JsonNode.Parse("""{"icom.example.app:":{"Authorization":2}}""")!.AsObject(), App, "location"));
        Assert.Equal("notDetermined", NativePermissionService.ReadLocationState(JsonNode.Parse("""{"icom.example.app":{"Registered":true}}""")!.AsObject(), App, "location"));
        Assert.Equal("unknown", NativePermissionService.ReadLocationState(JsonNode.Parse("""{"icom.example.app":{"Authorization":99}}""")!.AsObject(), App, "location"));
    }

    [Fact]
    public void LocationPlistHandlesNativeDatesAndBinaryData()
    {
        const string xml = """
            <plist version="1.0"><dict><key>icom.example.app</key><dict>
              <key>BundleId</key><string>com.example.app</string>
              <key>Authorization</key><integer>2</integer>
              <key>Time</key><date>2026-09-29T00:00:00Z</date>
              <key>Data</key><data>AQID</data>
            </dict></dict></plist>
            """;
        Assert.Equal("granted", NativePermissionService.ReadLocationPlistState(xml, App, "location"));
        Assert.Equal("limited", NativePermissionService.ReadLocationPlistState(xml, App, "location-always"));
    }

    [Fact]
    public void AndroidStateDoesNotLeakOtherUsersOrInferMissingState()
    {
        var dump = Dump(36, [RecordAudio], false);
        Assert.Equal("denied", NativePermissionService.ReadAndroidState(dump, 10, RecordAudio));
        Assert.Equal("granted", NativePermissionService.ReadAndroidState(dump, 0, RecordAudio));
        Assert.Equal("unknown", NativePermissionService.ReadAndroidState(dump, 12, RecordAudio));
        var missingForCurrentUser = dump.Replace("        " + RecordAudio + ": granted=false, flags=[]", "");
        Assert.Equal("unknown", NativePermissionService.ReadAndroidState(missingForCurrentUser, 10, RecordAudio));
        Assert.Equal("unknown", NativePermissionService.ReadAndroidState(missingForCurrentUser
            + "\nOther permissions:\n    " + RecordAudio + ": granted=true\n", 10, RecordAudio));
        Assert.Equal("unknown", NativePermissionService.ReadAndroidState(dump, 10, "android.permission.CAMERA"));
    }

    [Fact]
    public void AndroidStateReadsOnlyMatchingSharedUidAndUser()
    {
        var dump = Dump(36, [RecordAudio], false).Replace("        " + RecordAudio + ": granted=false, flags=[]", "")
            .Replace("Package [com.example.app]:", "Package [com.example.app]:\n    sharedUser=SharedUserSetting{123 com.example.shared/12345}")
            + "\nShared users:\n  SharedUser [com.example.shared] (123):\n    User 0:\n      " + RecordAudio + ": granted=false\n"
            + "    User 10:\n      " + RecordAudio + ": granted=true\n"
            + "  SharedUser [com.other.shared] (456):\n    User 10:\n      " + RecordAudio + ": granted=false\n";
        Assert.Equal("granted", NativePermissionService.ReadAndroidState(dump, 10, RecordAudio));
    }

    private static string Dump(int targetSdk, string[] permissions, bool granted)
        => $"Package [com.example.app]:\n    targetSdk={targetSdk}\n    requested permissions:\n"
            + string.Join("\n", permissions.Select(p => "      " + p)) + "\n    install permissions:\n"
            + "    User 0: installed=true\n      runtime permissions:\n"
            + string.Join("\n", permissions.Select(p => "        " + p + ": granted=true, flags=[]"))
            + "\n    User 10: installed=true\n      runtime permissions:\n"
            + string.Join("\n", permissions.Select(p => "        " + p + ": granted=" + (granted ? "true" : "false") + ", flags=[]")) + "\n";

    private static NativePermissionService Service(FakeCommands runner, DeviceDescriptor? target = null)
        => new(_ => Task.FromResult(new DeviceInventory([], [target ?? Device()], [])), runner,
            new RuntimeOptions { AdbPath = typeof(NativePermissionTests).Assembly.Location }, "simctl");

    private sealed class FakeCommands : IDeviceCommandRunner
    {
        public List<string> Calls { get; } = [];
        public Func<string, string> Shell { get; set; } = _ => throw new InvalidOperationException("Unexpected shell command.");
        public Func<string, IReadOnlyList<string>, string> Other { get; set; } = (_, _) => throw new InvalidOperationException("Unexpected command.");
        public Task<DeviceCommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken token, int maximumBytes = 1_048_576)
        {
            token.ThrowIfCancellationRequested();
            Calls.Add(executable + " " + string.Join(' ', arguments));
            var output = arguments.Count > 2 && arguments[2] == "shell" ? Shell(arguments[3]) : Other(executable, arguments);
            return Task.FromResult(arguments[0] == "help"
                ? new DeviceCommandResult(0, [], output)
                : new DeviceCommandResult(0, Encoding.UTF8.GetBytes(output), ""));
        }
    }
}
