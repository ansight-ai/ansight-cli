using System.Text.Json;
using System.Xml.Linq;

namespace Ansight.Cli.Commands.Licenses;

internal static class ThirdPartySoftwareCatalog
{
    public static ThirdPartySoftwareCatalogOutput Load()
    {
        return Load(ResolveDependencyManifestPath(), ResolveNuGetPackageRoot());
    }

    internal static ThirdPartySoftwareCatalogOutput Load(
        string? dependencyManifestPath,
        string? nuGetPackageRoot)
    {
        var entries = CreateKnownEntries()
            .ToDictionary(CreateKey, StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();

        if (string.IsNullOrWhiteSpace(dependencyManifestPath)
            || !File.Exists(dependencyManifestPath))
        {
            warnings.Add(
                "The runtime dependency manifest is not available in this single-file build; "
                + "the maintained bundled-software catalog is shown.");
        }
        else
        {
            AddRuntimePackages(entries, warnings, dependencyManifestPath, nuGetPackageRoot);
        }

        return new ThirdPartySoftwareCatalogOutput(
            "ansight.third-party-software/v1",
            DateTimeOffset.UtcNow,
            entries.Values
                .OrderBy(static entry => entry.Distribution, StringComparer.Ordinal)
                .ThenBy(static entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static entry => entry.Version, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            warnings);
    }

    private static void AddRuntimePackages(
        IDictionary<string, ThirdPartySoftwareEntry> entries,
        ICollection<string> warnings,
        string dependencyManifestPath,
        string? nuGetPackageRoot)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(dependencyManifestPath));
            if (!document.RootElement.TryGetProperty("libraries", out var libraries)
                || libraries.ValueKind != JsonValueKind.Object)
            {
                warnings.Add($"Dependency manifest '{dependencyManifestPath}' has no libraries catalog.");
                return;
            }

            foreach (var library in libraries.EnumerateObject())
            {
                if (!library.Value.TryGetProperty("type", out var type)
                    || !string.Equals(type.GetString(), "package", StringComparison.OrdinalIgnoreCase)
                    || ParsePackageIdentity(library.Name) is not { } package
                    || IsFirstPartyPackage(package.Name))
                {
                    continue;
                }

                var knownEntry = FindKnownPackage(entries.Values, package.Name);
                var metadata = LoadNuGetMetadata(package, nuGetPackageRoot);
                var entry = new ThirdPartySoftwareEntry(
                    package.Name,
                    package.Version,
                    metadata?.License ?? knownEntry?.License ?? "UNKNOWN",
                    "nuget",
                    "bundled",
                    knownEntry?.Usage ?? "Runtime dependency",
                    metadata?.ProjectUrl ?? knownEntry?.ProjectUrl,
                    metadata?.LicenseUrl ?? knownEntry?.LicenseUrl);

                if (string.Equals(entry.License, "UNKNOWN", StringComparison.Ordinal))
                {
                    warnings.Add(
                        $"License metadata for NuGet package {package.Name} {package.Version} could not be resolved.");
                }

                if (knownEntry is not null)
                {
                    entries.Remove(CreateKey(knownEntry));
                }

                entries[CreateKey(entry)] = entry;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            warnings.Add(
                $"Dependency manifest '{dependencyManifestPath}' could not be read: {exception.Message}");
        }
    }

    private static ThirdPartySoftwareEntry? LoadNuGetMetadata(
        PackageIdentity package,
        string? nuGetPackageRoot)
    {
        if (string.IsNullOrWhiteSpace(nuGetPackageRoot))
        {
            return null;
        }

        var packageDirectory = Path.Combine(
            nuGetPackageRoot,
            package.Name.ToLowerInvariant(),
            package.Version.ToLowerInvariant());
        if (!Directory.Exists(packageDirectory))
        {
            return null;
        }

        var nuspecPath = Directory.EnumerateFiles(packageDirectory, "*.nuspec", SearchOption.TopDirectoryOnly)
            .FirstOrDefault();
        if (nuspecPath is null)
        {
            return null;
        }

        try
        {
            var document = XDocument.Load(nuspecPath);
            var metadata = document.Descendants()
                .FirstOrDefault(static element => element.Name.LocalName == "metadata");
            var licenseElement = metadata?.Elements()
                .FirstOrDefault(static element => element.Name.LocalName == "license");
            var licenseUrl = ReadElement(metadata, "licenseUrl");
            var license = licenseElement?.Value.Trim();
            if (licenseElement?.Attribute("type")?.Value.Equals(
                    "file",
                    StringComparison.OrdinalIgnoreCase) == true
                && !string.IsNullOrWhiteSpace(license))
            {
                license = $"See packaged license file: {license}";
            }

            return new ThirdPartySoftwareEntry(
                package.Name,
                package.Version,
                string.IsNullOrWhiteSpace(license) ? "UNKNOWN" : license,
                "nuget",
                "bundled",
                "Runtime dependency",
                ReadElement(metadata, "projectUrl") ?? ReadRepositoryUrl(metadata),
                licenseUrl);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return null;
        }
    }

    private static string? ReadElement(XElement? parent, string name)
    {
        return parent?.Elements()
            .FirstOrDefault(element => element.Name.LocalName == name)
            ?.Value
            .Trim() is { Length: > 0 } value
            ? value
            : null;
    }

    private static string? ReadRepositoryUrl(XElement? metadata)
    {
        return metadata?.Elements()
            .FirstOrDefault(static element => element.Name.LocalName == "repository")
            ?.Attribute("url")
            ?.Value
            .Trim() is { Length: > 0 } value
            ? value
            : null;
    }

    private static PackageIdentity? ParsePackageIdentity(string libraryName)
    {
        var separatorIndex = libraryName.LastIndexOf('/');
        return separatorIndex <= 0 || separatorIndex == libraryName.Length - 1
            ? null
            : new PackageIdentity(libraryName[..separatorIndex], libraryName[(separatorIndex + 1)..]);
    }

    private static bool IsFirstPartyPackage(string packageName)
    {
        return packageName.Equals("Ansight", StringComparison.OrdinalIgnoreCase)
               || packageName.StartsWith("Ansight.", StringComparison.OrdinalIgnoreCase);
    }

    private static ThirdPartySoftwareEntry? FindKnownPackage(
        IEnumerable<ThirdPartySoftwareEntry> entries,
        string packageName)
    {
        return entries.FirstOrDefault(entry =>
            entry.Source == "nuget"
            && string.Equals(entry.Name, packageName, StringComparison.OrdinalIgnoreCase));
    }

    private static string CreateKey(ThirdPartySoftwareEntry entry)
    {
        return $"{entry.Source}\u001f{entry.Name}\u001f{entry.Version}";
    }

    private static string? ResolveDependencyManifestPath()
    {
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "ansight.deps.json")
        };
        if (!string.IsNullOrWhiteSpace(typeof(LicensesCommands).Assembly.Location))
        {
            candidates.Add(Path.ChangeExtension(
                typeof(LicensesCommands).Assembly.Location,
                ".deps.json"));
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? ResolveNuGetPackageRoot()
    {
        var configuredRoot = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            return Path.GetFullPath(configuredRoot);
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(userProfile)
            ? null
            : Path.Combine(userProfile, ".nuget", "packages");
    }

    private static IReadOnlyList<ThirdPartySoftwareEntry> CreateKnownEntries()
    {
        return
        [
            NuGet("Microsoft.Bcl.HashCode", "6.0.0", "MIT", "Hash code compatibility", "https://github.com/dotnet/maintenance-packages"),
            NuGet("Microsoft.Data.Sqlite.Core", "10.0.10", "MIT", "SQLite data access", "https://github.com/dotnet/efcore"),
            NuGet("Microsoft.Diagnostics.NETCore.Client", "0.2.510501", "MIT", ".NET diagnostics transport", "https://github.com/dotnet/diagnostics"),
            NuGet("Microsoft.Diagnostics.Tracing.TraceEvent", "3.2.5", "MIT", ".NET trace processing", "https://github.com/microsoft/perfview"),
            NuGet("Microsoft.Extensions.DependencyInjection.Abstractions", "6.0.0", "MIT", "Runtime dependency injection", "https://github.com/dotnet/runtime"),
            NuGet("Microsoft.Extensions.DependencyInjection", "6.0.0", "MIT", "Runtime dependency injection", "https://github.com/dotnet/runtime"),
            NuGet("Microsoft.Extensions.Logging.Abstractions", "6.0.0", "MIT", "Runtime logging", "https://github.com/dotnet/runtime"),
            NuGet("Microsoft.Extensions.Logging", "6.0.0", "MIT", "Runtime logging", "https://github.com/dotnet/runtime"),
            NuGet("Microsoft.Extensions.Options", "6.0.0", "MIT", "Runtime options", "https://github.com/dotnet/runtime"),
            NuGet("Microsoft.Extensions.Primitives", "6.0.0", "MIT", "Runtime primitives", "https://github.com/dotnet/runtime"),
            NuGet("SQLitePCLRaw.core", "3.0.5", "Apache-2.0", "SQLite native binding", "https://github.com/ericsink/SQLitePCL.raw"),
            NuGet("SQLitePCLRaw.provider.sqlite3", "3.0.5", "Apache-2.0", "System SQLite provider", "https://github.com/ericsink/SQLitePCL.raw"),
            NuGet("SQLitePCLRaw.provider.winsqlite3", "3.0.5", "Apache-2.0", "Windows SQLite provider", "https://github.com/ericsink/SQLitePCL.raw"),
            NuGet("SharpZipLib", "1.4.2", "MIT", "Session archive compression", "https://github.com/icsharpcode/SharpZipLib"),
            NuGet("SkiaSharp", "3.119.1", "MIT", "Image decoding and rendering", "https://github.com/mono/SkiaSharp"),
            NuGet("SkiaSharp.NativeAssets.Linux.NoDependencies", "3.119.1", "MIT", "Linux Skia runtime", "https://github.com/mono/SkiaSharp"),
            NuGet("SkiaSharp.NativeAssets.macOS", "3.119.1", "MIT", "macOS Skia runtime", "https://github.com/mono/SkiaSharp"),
            NuGet("SkiaSharp.NativeAssets.Win32", "3.119.1", "MIT", "Windows Skia runtime", "https://github.com/mono/SkiaSharp"),
            NuGet("System.ComponentModel.Composition", "7.0.0", "MIT", "Runtime composition", "https://github.com/dotnet/runtime"),
            NuGet("System.Formats.Nrbf", "10.0.10", "MIT", "Safe NRBF inspection", "https://github.com/dotnet/runtime"),
            NuGet("System.IO.Hashing", "10.0.10", "MIT", "Runtime hashing", "https://github.com/dotnet/runtime"),
            NuGet("System.Security.Permissions", "10.0.10", "MIT", "Runtime compatibility", "https://github.com/dotnet/runtime"),
            NuGet("System.Windows.Extensions", "10.0.10", "MIT", "Windows runtime compatibility", "https://github.com/dotnet/runtime"),
            NuGet("YamlDotNet", "18.1.0", "MIT", "YAML workspace definition parsing", "https://github.com/aaubry/YamlDotNet"),
            Bundled("SkiaSharp.QrCode lineage", null, "MIT", "QR generation", "https://github.com/guitarrapc/SkiaSharp.QrCode"),
            NuGet("Google.Protobuf", "3.31.1", "BSD-3-Clause", "Android Emulator audio RPC messages", "https://github.com/protocolbuffers/protobuf"),
            NuGet("Grpc.Net.Client", "2.71.0", "Apache-2.0", "Android Emulator audio RPC client", "https://github.com/grpc/grpc-dotnet"),
            NuGet("Grpc.Net.Common", "2.71.0", "Apache-2.0", "Android Emulator audio RPC support", "https://github.com/grpc/grpc-dotnet"),
            NuGet("Grpc.Core.Api", "2.71.0", "Apache-2.0", "Android Emulator audio RPC contracts", "https://github.com/grpc/grpc"),
            Bundled("AOSP EmulatorController audio protocol subset", null, "Apache-2.0", "Android Emulator microphone injection protocol definitions", "https://github.com/google/android-emulator-webrtc/blob/master/proto/emulator_controller.proto"),
            Bundled("wand / testa accessibility bridge lineage", null, "MIT", "Resident CoreSimulator accessibility-service capture", "https://github.com/hanfann/wand"),
            Bundled("libdatachannel", "0.24.3", "MPL-2.0", "Native Simulator WebRTC", "https://github.com/paullouisageneau/libdatachannel"),
            Bundled("libjuice", "libdatachannel 0.24.3 revision", "MPL-2.0", "Native ICE transport", "https://github.com/paullouisageneau/libjuice"),
            Bundled("usrsctp", "libdatachannel 0.24.3 revision", "BSD-3-Clause", "Native SCTP transport", "https://github.com/paullouisageneau/usrsctp"),
            Bundled("libsrtp", "libdatachannel 0.24.3 revision", "BSD-3-Clause", "Native SRTP transport", "https://github.com/cisco/libsrtp"),
            Bundled("plog", "libdatachannel 0.24.3 revision", "MPL-2.0", "Native logging", "https://github.com/SergiusTheBest/plog"),
            Bundled("Mbed TLS", "3.6.4", "Apache-2.0", "Native DTLS/SRTP cryptography", "https://github.com/Mbed-TLS/mbedtls"),
            Runtime(".NET runtime", null, "MIT and bundled third-party notices", "Runs the self-contained Ansight CLI", "https://github.com/dotnet/runtime", "https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT"),
            External(".NET diagnostics tools", null, "MIT", "Optional dotnet-trace and dotnet-dsrouter diagnostics", "https://github.com/dotnet/diagnostics"),
            External("Node.js", null, "MIT and bundled third-party licenses", "Runs repository tasks and sanitizer modules", "https://github.com/nodejs/node", "https://github.com/nodejs/node/blob/main/LICENSE"),
            External("Tesseract OCR", null, "Apache-2.0", "Detects sensitive text in screenshots during sanitization", "https://github.com/tesseract-ocr/tesseract"),
            External("scrcpy", null, "Apache-2.0", "Optional Android display capture and control", "https://github.com/Genymobile/scrcpy"),
            External("AXe", null, "MIT", "Optional iOS Simulator accessibility-service capture", "https://github.com/cameroncooke/AXe"),
            External("BlackHole", null, "GPL-3.0", "Optional user-installed CoreAudio loopback driver for iOS Simulator microphone injection; not bundled or redistributed", "https://github.com/ExistentialAudio/BlackHole", "https://github.com/ExistentialAudio/BlackHole/blob/master/LICENSE"),
            External("Appium", null, "Apache-2.0", "Optional physical-device automation", "https://github.com/appium/appium"),
            External("Android Debug Bridge", null, "Apache-2.0", "Android app installation, logs, and device automation", "https://android.googlesource.com/platform/packages/modules/adb")
        ];
    }

    private static ThirdPartySoftwareEntry NuGet(
        string name,
        string version,
        string license,
        string usage,
        string projectUrl)
    {
        return new ThirdPartySoftwareEntry(
            name,
            version,
            license,
            "nuget",
            "bundled",
            usage,
            projectUrl,
            ResolveSpdxLicenseUrl(license));
    }

    private static ThirdPartySoftwareEntry Bundled(
        string name,
        string? version,
        string license,
        string usage,
        string projectUrl)
    {
        return new ThirdPartySoftwareEntry(
            name,
            version,
            license,
            "vendored-native",
            "bundled",
            usage,
            projectUrl,
            ResolveSpdxLicenseUrl(license));
    }

    private static ThirdPartySoftwareEntry External(
        string name,
        string? version,
        string license,
        string usage,
        string projectUrl,
        string? licenseUrl = null)
    {
        return new ThirdPartySoftwareEntry(
            name,
            version,
            license,
            "external-tool",
            "external-not-redistributed",
            usage,
            projectUrl,
            licenseUrl ?? ResolveSpdxLicenseUrl(license));
    }

    private static ThirdPartySoftwareEntry Runtime(
        string name,
        string? version,
        string license,
        string usage,
        string projectUrl,
        string licenseUrl)
    {
        return new ThirdPartySoftwareEntry(
            name,
            version,
            license,
            "runtime",
            "bundled",
            usage,
            projectUrl,
            licenseUrl);
    }

    private static string? ResolveSpdxLicenseUrl(string license)
    {
        return license is "MIT" or "Apache-2.0" or "MPL-2.0" or "BSD-3-Clause"
            ? $"https://spdx.org/licenses/{license}.html"
            : null;
    }
}
