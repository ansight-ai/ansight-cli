using Ansight.Host.Runtime.NativeProfiling;
using Ansight.Host.Tests.TestSupport;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class NativeProfileCaptureStoreTests
{
    [Fact]
    public async Task DirectoryArtifact_RoundTripsWithStableTreeHash()
    {
        using var environment = new TestEnvironment();
        var store = new NativeProfileCaptureStore(environment.ApplicationPaths);
        var captureId = Guid.NewGuid().ToString("N");
        var capturePath = store.CreateCaptureDirectory(captureId);
        var tracePath = Path.Combine(capturePath, "raw", "capture.trace");
        Directory.CreateDirectory(Path.Combine(tracePath, "Trace1.run"));
        await File.WriteAllBytesAsync(Path.Combine(tracePath, "template.data"), [1, 2, 3]);
        await File.WriteAllBytesAsync(Path.Combine(tracePath, "Trace1.run", "samples.data"), [4, 5]);
        var producedArtifact = new NativeProfileProducedArtifact(
            InstrumentsProfileCaptureAdapter.TraceArtifactKind,
            tracePath,
            IsDirectory: true,
            Authoritative: true);

        var first = await store.DescribeArtifactAsync(captureId, producedArtifact);
        var second = await store.DescribeArtifactAsync(captureId, producedArtifact);
        var manifest = CreateManifest(captureId, first);
        await store.SaveManifestAsync(manifest);

        Assert.Equal(5, first.Length);
        Assert.Equal(first.Sha256, second.Sha256);
        Assert.True(first.IsDirectory);
        Assert.True(first.Authoritative);
        Assert.True(store.TryLoadManifest(captureId, out var loaded));
        Assert.NotNull(loaded);
        Assert.True(store.TryResolveArtifactPath(
            loaded,
            InstrumentsProfileCaptureAdapter.TraceArtifactKind,
            out var resolvedPath));
        Assert.Equal(tracePath, resolvedPath);
    }

    [Fact]
    public void CapturePath_RejectsArbitraryPathSegments()
    {
        using var environment = new TestEnvironment();
        var store = new NativeProfileCaptureStore(environment.ApplicationPaths);

        Assert.Throws<ArgumentException>(() => store.GetCapturePath("../../outside"));
    }

    private static NativeProfileCaptureManifest CreateManifest(
        string captureId,
        NativeProfileArtifact artifact)
        => new()
        {
            Schema = NativeProfileCaptureManifest.CurrentSchema,
            CaptureId = captureId,
            Platform = NativeProfilePlatforms.Ios,
            Engine = "instruments",
            Preset = NativeProfilePresets.Cpu,
            AppId = "com.example.app",
            ApplicationPath = "/artifacts/App.app",
            DeviceId = "simulator-id",
            RequestedDurationSeconds = 10,
            State = NativeProfileCaptureState.Completed,
            CreatedUtc = DateTimeOffset.UtcNow,
            Artifacts = [artifact]
        };
}
