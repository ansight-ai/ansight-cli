using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Ansight.Host.Workspaces;

namespace Ansight.Host.Runtime.DotNetProfiling;

internal static class DotNetProfilingArtifactInspector
{
    private const int MaximumManifestBytes = 64 * 1024;
    private const string ManifestRelativePath = "ansight/dotnet-profiling.json";
    private const string AndroidManifestEntryPath = "assets/" + ManifestRelativePath;

    public static DotNetProfilingApplicationManifest? TryRead(
        WorkspaceTestApplicationPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);

        var json = Directory.Exists(package.InstallPath)
            ? TryReadApplicationBundle(package.InstallPath)
            : TryReadAndroidPackage(package.InstallPath);
        if (json is null)
        {
            return null;
        }

        DotNetProfilingApplicationManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<DotNetProfilingApplicationManifest>(json)
                       ?? throw new InvalidDataException("The Ansight profiling manifest is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The Ansight profiling manifest is not valid JSON.", exception);
        }

        Validate(manifest);
        return manifest;
    }

    public static void EnsureCompatible(
        DotNetProfilingApplicationManifest manifest,
        DotNetCaptureLaunchAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var expectedTarget = adapter switch
        {
            DotNetCaptureLaunchAdapter.AndroidEmulator => "android-emulator",
            DotNetCaptureLaunchAdapter.AndroidDevice => "android-device",
            DotNetCaptureLaunchAdapter.IosSimulator => "ios-simulator",
            DotNetCaptureLaunchAdapter.IosDevice => "ios-device",
            _ => throw new ArgumentOutOfRangeException(nameof(adapter), adapter, null)
        };
        var expectedConfiguration = adapter switch
        {
            DotNetCaptureLaunchAdapter.AndroidEmulator => "10.0.2.2:9000,suspend,connect",
            DotNetCaptureLaunchAdapter.AndroidDevice => "127.0.0.1:9000,suspend,connect",
            DotNetCaptureLaunchAdapter.IosSimulator or DotNetCaptureLaunchAdapter.IosDevice =>
                "127.0.0.1:9000,suspend,listen",
            _ => throw new ArgumentOutOfRangeException(nameof(adapter), adapter, null)
        };

        if (!string.Equals(manifest.Target, expectedTarget, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The profiling artifact was built for '{manifest.Target}', but the selected target requires "
                + $"'{expectedTarget}'. Rebuild the app with AnsightProfilingTarget={expectedTarget}.");
        }

        var actualConfiguration = manifest.BuildDiagnosticConfiguration();
        if (!string.Equals(actualConfiguration, expectedConfiguration, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The profiling artifact declares DiagnosticConfiguration='{actualConfiguration}', "
                + $"but the selected target requires '{expectedConfiguration}'.");
        }
    }

    private static string? TryReadApplicationBundle(string applicationPath)
    {
        var manifestPath = Path.Combine(
            applicationPath,
            ManifestRelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        var fileInfo = new FileInfo(manifestPath);
        if (fileInfo.Length > MaximumManifestBytes)
        {
            throw new InvalidDataException(
                $"The Ansight profiling manifest exceeds {MaximumManifestBytes} bytes.");
        }

        return File.ReadAllText(manifestPath, Encoding.UTF8);
    }

    private static string? TryReadAndroidPackage(string applicationPath)
    {
        if (!string.Equals(Path.GetExtension(applicationPath), ".apk", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        using var archive = ZipFile.OpenRead(applicationPath);
        var entry = archive.Entries.FirstOrDefault(candidate => string.Equals(
            candidate.FullName.Replace('\\', '/'),
            AndroidManifestEntryPath,
            StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            return null;
        }

        if (entry.Length > MaximumManifestBytes)
        {
            throw new InvalidDataException(
                $"The Ansight profiling manifest exceeds {MaximumManifestBytes} bytes.");
        }

        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static void Validate(DotNetProfilingApplicationManifest manifest)
    {
        if (!string.Equals(
                manifest.Schema,
                DotNetProfilingApplicationManifest.CurrentSchema,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Unsupported Ansight profiling manifest schema '{manifest.Schema}'. "
                + $"Expected '{DotNetProfilingApplicationManifest.CurrentSchema}'.");
        }

        if (manifest.Target is not ("android-emulator" or "android-device" or "ios-simulator" or "ios-device"))
        {
            throw new InvalidDataException(
                $"Unsupported Ansight profiling target '{manifest.Target}'.");
        }

        if (string.IsNullOrWhiteSpace(manifest.DiagnosticAddress)
            || manifest.DiagnosticPort is < 1 or > 65_535
            || manifest.DiagnosticListenMode is not ("connect" or "listen"))
        {
            throw new InvalidDataException(
                "The Ansight profiling manifest contains an invalid diagnostic endpoint.");
        }

        if (!manifest.DiagnosticSuspend)
        {
            throw new InvalidDataException(
                "The Ansight startup profiling artifact must suspend until the trace collector connects.");
        }

        if (!string.Equals(
                manifest.StartupProvider,
                DotNetStartupMarker.ProviderName,
                StringComparison.Ordinal)
            || !string.Equals(
                manifest.StartupEvent,
                DotNetStartupMarker.EventName,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The Ansight profiling manifest declares an unsupported application-ready event contract.");
        }
    }
}
