using Ansight.Host.Diagnostics;
using Ansight.Host.Tests.TestSupport;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class SessionHostMetadataTests
{
    [Fact]
    public void PropertiesUpdatesDoNotReplaceHostAndStoredSnapshotsKeepOriginalProvenance()
    {
        using var environment = new TestEnvironment();
        var store = new SessionCaptureStore(environment.ApplicationPaths);
        var state = new RuntimeState(store);
        var id = state.CreateSession("example.metadata", "Metadata", IPAddress.Loopback, null, null);
        Assert.True(state.TryGetSessionSnapshot(id, out var original));
        Assert.NotNull(original!.HostMetadata);
        Assert.False(string.IsNullOrWhiteSpace(original.HostMetadata.OsType));

        state.SetSessionCustomProperties(id, new JsonObject { ["app"] = new JsonObject { ["revision"] = "one" } });
        state.SetSessionCustomProperties(id, new JsonObject { ["app"] = new JsonObject { ["revision"] = "two" } });
        Assert.True(state.TryGetSessionSnapshot(id, out var updated));
        Assert.Equal(original.HostMetadata.CapturedAtUtc, updated!.HostMetadata!.CapturedAtUtc);
        Assert.Equal("two", updated.CustomProperties!["app"]!["revision"]!.GetValue<string>());

        store.Save(updated);
        var reopened = new SessionCaptureStore(environment.ApplicationPaths);
        Assert.True(reopened.TryLoad(id, out var loaded));
        Assert.Equal(original.HostMetadata.CapturedAtUtc, loaded!.HostMetadata!.CapturedAtUtc);
        Assert.Equal(original.HostMetadata.OsVersion, Assert.Single(reopened.LoadSummaries()).HostMetadata!.OsVersion);

        var importedState = new RuntimeState(reopened);
        var imported = importedState.ImportSessionSnapshot(loaded, new Dictionary<string, byte[]>());
        Assert.True(imported.IsSuccess);
        Assert.Equal(original.HostMetadata.CapturedAtUtc, imported.ImportedSession!.HostMetadata!.CapturedAtUtc);

        var legacy = CreateHistoricalSnapshot(null);
        var legacyImport = importedState.ImportSessionSnapshot(legacy, new Dictionary<string, byte[]>());
        Assert.True(legacyImport.IsSuccess);
        Assert.Null(legacyImport.ImportedSession!.HostMetadata);
    }

    [Fact]
    public async Task PlatformDiscoveryWinsOverEarlierProbeWithoutBlockingSdkConnection()
    {
        using var environment = new TestEnvironment();
        var state = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        var collector = new ControlledCollector();
        state.ConfigureHostMetadataCollector(collector);
        var id = state.CreateSession("example.metadata", "Metadata", IPAddress.Loopback, null, null);
        await collector.GeneralStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        state.SetSessionDeviceProfile(id, new DeviceAppProfile
        {
            Device = new DeviceProfile { OsName = "iOS" }
        }, null);
        await collector.PlatformStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        collector.PlatformResult.SetResult(new SessionHostToolMetadata("xcode", "available", "27.2", "27B52"));
        await WaitForMetadataAsync(state, id, "complete");
        collector.GeneralResult.SetResult(new SessionHostToolMetadata("node", "available", "24.0"));
        await collector.GeneralCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(state.TryGetSessionSnapshot(id, out var session));
        Assert.Equal("ios", session!.HostMetadata!.TargetPlatform);
        Assert.Equal("xcode", Assert.Single(session.HostMetadata.Tools).Name);
        Assert.Equal("27.2", session.HostMetadata.Tools[0].Version);
    }

    [Fact]
    public async Task ProbeFailureRetainsBasicHostInformationAndDoesNotEndCapture()
    {
        using var environment = new TestEnvironment();
        var state = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        state.ConfigureHostMetadataCollector(new FailingCollector());
        var id = state.CreateSession("example.metadata", "Metadata", IPAddress.Loopback, null, null);

        await WaitForMetadataAsync(state, id, "partial");

        Assert.True(state.TryGetSessionSnapshot(id, out var session));
        Assert.Equal("Connected", session!.Status);
        Assert.False(string.IsNullOrWhiteSpace(session.HostMetadata!.OsVersion));
        Assert.NotNull(session.HostMetadata.CollectionCompletedUtc);
    }

    [Fact]
    public void ExtractionRetainsHostToolVersionsAndOriginalCaptureTimestamp()
    {
        using var environment = new TestEnvironment();
        var state = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        var metadata = new SessionHostMetadata
        {
            CapturedAtUtc = DateTimeOffset.Parse("2026-11-06T00:00:00Z"),
            Status = "complete",
            OsType = "macos",
            CliVersion = "1.2.3",
            Tools = [new SessionHostToolMetadata("xcode", "available", "26.4", "26E231")]
        };
        var source = CreateHistoricalSnapshot(metadata);
        var imported = state.ImportSessionSnapshot(source, new Dictionary<string, byte[]>());
        Assert.True(imported.IsSuccess);

        var result = state.ExtractSessionTimelineRange(imported.ImportedSession!.SessionId,
            source.CreatedUtc.AddSeconds(1), source.LastUpdatedUtc.AddSeconds(-1), "Metadata slice");

        Assert.True(result.IsSuccess);
        Assert.Equal(metadata.CapturedAtUtc, result.ExtractedSession!.HostMetadata!.CapturedAtUtc);
        Assert.Equal("26.4", Assert.Single(result.ExtractedSession.HostMetadata.Tools).Version);
        Assert.NotEqual(metadata.CapturedAtUtc, result.ExtractedSession.CreatedUtc);
    }

    [Fact]
    public void AndroidStudioVersionComesFromInstallationWithoutLaunchingTheIde()
    {
        using var environment = new TestEnvironment();
        var path = Path.Combine(environment.ApplicationPaths.ApplicationDataPath, "product-info.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{"name":"Android Studio","version":"2026.1.1","buildNumber":"AI-261.1"}""");

        var tool = SessionHostMetadataCollector.ReadAndroidStudioProduct(path);

        Assert.Equal("available", tool.Status);
        Assert.Equal("2026.1.1", tool.Version);
        Assert.Equal("AI-261.1", tool.Build);
    }

    private static async Task WaitForMetadataAsync(RuntimeState state, string id, string status)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!timeout.IsCancellationRequested)
        {
            if (state.TryGetSessionSnapshot(id, out var session) && session!.HostMetadata!.Status == status)
            {
                return;
            }

            await Task.Delay(10, timeout.Token);
        }

        throw new TimeoutException("Host metadata did not finish.");
    }

    private static AppSessionSnapshot CreateHistoricalSnapshot(SessionHostMetadata? metadata)
    {
        return new AppSessionSnapshot
        {
            SessionId = "metadata-import",
            AppId = "example.metadata",
            ClientName = "Metadata",
            RemoteAddress = "host-device",
            ConfigId = null,
            CreatedUtc = DateTimeOffset.Parse("2026-11-06T00:00:00Z"),
            LastUpdatedUtc = DateTimeOffset.Parse("2026-11-06T00:00:10Z"),
            Status = "Completed",
            IsHistorical = true,
            HostMetadata = metadata,
            Logs = [new LogEntry(DateTimeOffset.Parse("2026-11-06T00:00:05Z"), "Retained evidence")],
            MetricChannels = [],
            Metrics = []
        };
    }

    private sealed class ControlledCollector : ISessionHostMetadataCollector
    {
        public TaskCompletionSource GeneralStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource PlatformStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource GeneralCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<SessionHostToolMetadata> GeneralResult { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<SessionHostToolMetadata> PlatformResult { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<SessionHostMetadata> CollectAsync(SessionHostMetadata snapshot)
        {
            var isGeneral = snapshot.TargetPlatform is null;
            (isGeneral ? GeneralStarted : PlatformStarted).TrySetResult();
            var result = await (isGeneral ? GeneralResult.Task : PlatformResult.Task);
            if (isGeneral)
            {
                GeneralCompleted.TrySetResult();
            }

            return snapshot with { Status = "complete", Tools = [result] };
        }
    }

    private sealed class FailingCollector : ISessionHostMetadataCollector
    {
        public Task<SessionHostMetadata> CollectAsync(SessionHostMetadata snapshot)
        {
            throw new IOException("Tool unavailable");
        }
    }
}
