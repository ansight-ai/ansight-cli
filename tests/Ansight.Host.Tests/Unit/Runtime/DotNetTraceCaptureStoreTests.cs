using Ansight.Host.Runtime.DotNetProfiling;
using Ansight.Host.Tests.TestSupport;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class DotNetTraceCaptureStoreTests
{
    [Fact]
    public async Task ManifestAndArtifact_RoundTripWithStableHash()
    {
        using var environment = new TestEnvironment();
        var store = new DotNetTraceCaptureStore(environment.ApplicationPaths);
        var captureId = Guid.NewGuid().ToString("N");
        var capturePath = store.CreateCaptureDirectory(captureId);
        var tracePath = Path.Combine(capturePath, "raw", "runtime.nettrace");
        await File.WriteAllBytesAsync(tracePath, [1, 2, 3, 4]);
        var artifact = await store.DescribeArtifactAsync(
            captureId,
            DotNetProfilingEngine.NetTraceArtifactKind,
            tracePath);
        var manifest = new DotNetTraceCaptureManifest
        {
            Schema = DotNetTraceCaptureManifest.CurrentSchema,
            CaptureId = captureId,
            AppId = "com.example.app",
            ApplicationPath = "/artifacts/App.app",
            CapturePreset = "startup-explain-v2",
            RequestedDurationSeconds = 10,
            State = DotNetTraceCaptureState.Completed,
            CreatedUtc = DateTimeOffset.UtcNow,
            Artifacts = [artifact]
        };

        await store.SaveManifestAsync(manifest);

        Assert.True(store.TryLoadManifest(captureId, out var loaded));
        Assert.NotNull(loaded);
        Assert.Equal(DotNetTraceCaptureManifest.CurrentSchema, loaded.Schema);
        Assert.Equal("9f64a747e1b97f131fabb6b447296c9b6f0201e79fb3c5356e6c77e89b6a806a", artifact.Sha256);
        Assert.True(store.TryResolveArtifactPath(loaded, DotNetProfilingEngine.NetTraceArtifactKind, out var resolvedPath));
        Assert.Equal(tracePath, resolvedPath);
    }

    [Fact]
    public void CapturePath_RejectsArbitraryPathSegments()
    {
        using var environment = new TestEnvironment();
        var store = new DotNetTraceCaptureStore(environment.ApplicationPaths);

        Assert.Throws<ArgumentException>(() => store.GetCapturePath("../../outside"));
    }

    [Fact]
    public async Task VersionOneManifest_RemainsReadableAfterArtifactContractMigration()
    {
        using var environment = new TestEnvironment();
        var store = new DotNetTraceCaptureStore(environment.ApplicationPaths);
        var captureId = Guid.NewGuid().ToString("N");
        var capturePath = store.CreateCaptureDirectory(captureId);
        var createdUtc = DateTimeOffset.UtcNow;
        var json = $$"""
                     {
                       "schema": "ansight.dotnet-trace-capture/v1",
                       "captureId": "{{captureId}}",
                       "appId": "com.example.legacy",
                       "projectPath": "/source/Legacy.csproj",
                       "configuration": "Release",
                       "targetPath": "/artifacts/Legacy.app",
                       "launchAdapter": "IosSimulator",
                       "capturePreset": "startup-explain-v1",
                       "requestedDurationSeconds": 10,
                       "state": "completed",
                       "createdUtc": "{{createdUtc:O}}",
                       "artifacts": [],
                       "warnings": []
                     }
                     """;
        await File.WriteAllTextAsync(Path.Combine(capturePath, "manifest.json"), json);

        Assert.True(store.TryLoadManifest(captureId, out var manifest));
        Assert.NotNull(manifest);
        Assert.Equal("/artifacts/Legacy.app", manifest.ApplicationPath);
        Assert.Equal("com.example.legacy", manifest.AppId);
        Assert.Equal(DevicePlatforms.Ios, manifest.Platform);
        Assert.Equal(DotNetTraceCaptureState.Completed, manifest.State);
    }
}
