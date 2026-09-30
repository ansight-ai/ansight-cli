using System.IO.Compression;
using System.Text;
using Ansight.Host.Runtime.DotNetProfiling;
using Ansight.Host.Workspaces;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class DotNetProfilingArtifactInspectorTests
{
    [Fact]
    public void TryRead_ReadsApplicationBundleManifest()
    {
        var rootPath = CreateTemporaryPath();
        try
        {
            var applicationPath = Path.Combine(rootPath, "Example.app");
            var manifestPath = Path.Combine(applicationPath, "ansight", "dotnet-profiling.json");
            Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
            File.WriteAllText(manifestPath, CreateManifestJson("ios-simulator"));
            using var package = WorkspaceTestApplicationPackage.Open(applicationPath);

            var manifest = DotNetProfilingArtifactInspector.TryRead(package);

            Assert.NotNull(manifest);
            Assert.Equal("ios-simulator", manifest.Target);
            Assert.Equal("127.0.0.1:9000,suspend,listen", manifest.BuildDiagnosticConfiguration());
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public void TryRead_ReadsAndroidPackageManifest()
    {
        var rootPath = CreateTemporaryPath();
        try
        {
            var applicationPath = Path.Combine(rootPath, "Example.apk");
            using (var archive = ZipFile.Open(applicationPath, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("assets/ansight/dotnet-profiling.json");
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(CreateManifestJson("android-emulator"));
            }

            using var package = WorkspaceTestApplicationPackage.Open(applicationPath);

            var manifest = DotNetProfilingArtifactInspector.TryRead(package);

            Assert.NotNull(manifest);
            Assert.Equal("android-emulator", manifest.Target);
            Assert.Equal("10.0.2.2:9000,suspend,connect", manifest.BuildDiagnosticConfiguration());
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public void EnsureCompatible_RejectsArtifactBuiltForAnotherTarget()
    {
        var manifest = new DotNetProfilingApplicationManifest
        {
            Schema = DotNetProfilingApplicationManifest.CurrentSchema,
            Target = "android-emulator",
            DiagnosticAddress = "10.0.2.2",
            DiagnosticPort = 9000,
            DiagnosticSuspend = true,
            DiagnosticListenMode = "connect",
            StartupProvider = DotNetStartupMarker.ProviderName,
            StartupEvent = DotNetStartupMarker.EventName
        };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            DotNetProfilingArtifactInspector.EnsureCompatible(
                manifest,
                DotNetCaptureLaunchAdapter.AndroidDevice));

        Assert.Contains("android-device", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TryRead_ReturnsNullWhenArtifactWasConfiguredManually()
    {
        var rootPath = CreateTemporaryPath();
        try
        {
            var applicationPath = Path.Combine(rootPath, "Example.app");
            Directory.CreateDirectory(applicationPath);
            using var package = WorkspaceTestApplicationPackage.Open(applicationPath);

            Assert.Null(DotNetProfilingArtifactInspector.TryRead(package));
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    private static string CreateTemporaryPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ansight-profile-artifact-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string CreateManifestJson(string target)
    {
        var address = target == "android-emulator" ? "10.0.2.2" : "127.0.0.1";
        var listenMode = target.StartsWith("android-", StringComparison.Ordinal) ? "connect" : "listen";
        return $$"""
            {
              "schema": "ansight.dotnet-profile-app/v1",
              "target": "{{target}}",
              "diagnosticAddress": "{{address}}",
              "diagnosticPort": 9000,
              "diagnosticSuspend": true,
              "diagnosticListenMode": "{{listenMode}}",
              "startupProvider": "Ansight-DotNet-Startup",
              "startupEvent": "StartupComplete"
            }
            """;
    }
}
