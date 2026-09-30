using System.IO.Compression;
using System.Security.Cryptography;

namespace Ansight.Host.Workspaces.Targets;

internal sealed class WorkspaceTestApplicationPackage : IDisposable
{
    private readonly string? temporaryDirectoryPath;

    private WorkspaceTestApplicationPackage(
        string sourcePath,
        string installPath,
        string platform,
        string? requiredDeviceKind = null,
        string? temporaryDirectoryPath = null)
    {
        SourcePath = sourcePath;
        InstallPath = installPath;
        Platform = platform;
        RequiredDeviceKind = requiredDeviceKind;
        this.temporaryDirectoryPath = temporaryDirectoryPath;
    }

    public string SourcePath { get; }

    public string InstallPath { get; }

    public string Platform { get; }

    public string? RequiredDeviceKind { get; }

    public async Task<ApplicationChecksum?> GetChecksumAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(InstallPath))
        {
            return null;
        }

        await using var stream = new FileStream(
            InstallPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            useAsync: true);
        var checksum = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return new ApplicationChecksum(Convert.ToHexStringLower(checksum));
    }

    public static WorkspaceTestApplicationPackage Open(string applicationPath)
    {
        if (string.IsNullOrWhiteSpace(applicationPath))
        {
            throw new ArgumentException("An application path is required.", nameof(applicationPath));
        }

        var fullPath = Path.GetFullPath(applicationPath.Trim());
        var extension = Path.GetExtension(fullPath);
        if (string.Equals(extension, ".app", StringComparison.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(fullPath))
            {
                throw new DirectoryNotFoundException($"Application bundle '{fullPath}' was not found.");
            }

            return new WorkspaceTestApplicationPackage(fullPath, fullPath, DevicePlatforms.Ios);
        }

        if (string.Equals(extension, ".apk", StringComparison.OrdinalIgnoreCase))
        {
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException($"Application package '{fullPath}' was not found.", fullPath);
            }

            return new WorkspaceTestApplicationPackage(fullPath, fullPath, DevicePlatforms.Android);
        }

        if (!string.Equals(extension, ".ipa", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Unsupported application artifact '{fullPath}'. Expected an .app, .ipa, or .apk.",
                nameof(applicationPath));
        }

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Application package '{fullPath}' was not found.", fullPath);
        }

        return ExtractIpa(fullPath);
    }

    public void Dispose()
    {
        if (string.IsNullOrWhiteSpace(temporaryDirectoryPath)
            || !Directory.Exists(temporaryDirectoryPath))
        {
            return;
        }

        try
        {
            Directory.Delete(temporaryDirectoryPath, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static WorkspaceTestApplicationPackage ExtractIpa(string ipaPath)
    {
        var temporaryPath = Path.Combine(Path.GetTempPath(), $"ansight-ipa-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryPath);
        try
        {
            using var archive = ZipFile.OpenRead(ipaPath);
            var applicationRoots = archive.Entries
                .Select(static entry => GetIpaApplicationRoot(entry.FullName))
                .Where(static path => path is not null)
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (applicationRoots.Length != 1)
            {
                throw new InvalidDataException(
                    applicationRoots.Length == 0
                        ? $"IPA '{ipaPath}' does not contain a Payload/*.app bundle."
                        : $"IPA '{ipaPath}' contains multiple application bundles.");
            }

            var applicationRoot = applicationRoots[0];
            var destinationRoot = Path.GetFullPath(temporaryPath + Path.DirectorySeparatorChar);
            foreach (var entry in archive.Entries.Where(entry => IsWithinApplication(entry.FullName, applicationRoot)))
            {
                var normalizedEntryPath = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                var destinationPath = Path.GetFullPath(Path.Combine(temporaryPath, normalizedEntryPath));
                if (!destinationPath.StartsWith(destinationRoot, StringComparison.Ordinal))
                {
                    throw new InvalidDataException($"IPA '{ipaPath}' contains an unsafe archive path.");
                }

                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(destinationPath);
                    continue;
                }

                var parentPath = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrWhiteSpace(parentPath))
                {
                    Directory.CreateDirectory(parentPath);
                }

                entry.ExtractToFile(destinationPath, overwrite: true);
                ApplyUnixFileMode(entry, destinationPath);
            }

            var installPath = Path.Combine(
                temporaryPath,
                applicationRoot.Replace('/', Path.DirectorySeparatorChar));
            return new WorkspaceTestApplicationPackage(
                ipaPath,
                installPath,
                DevicePlatforms.Ios,
                DeviceKinds.Physical,
                temporaryPath);
        }
        catch
        {
            Directory.Delete(temporaryPath, recursive: true);
            throw;
        }
    }

    private static string? GetIpaApplicationRoot(string entryPath)
    {
        var parts = entryPath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2
               && string.Equals(parts[0], "Payload", StringComparison.OrdinalIgnoreCase)
               && parts[1].EndsWith(".app", StringComparison.OrdinalIgnoreCase)
            ? $"{parts[0]}/{parts[1]}"
            : null;
    }

    private static bool IsWithinApplication(string entryPath, string applicationRoot)
    {
        var normalizedPath = entryPath.Replace('\\', '/');
        return string.Equals(normalizedPath.TrimEnd('/'), applicationRoot, StringComparison.OrdinalIgnoreCase)
               || normalizedPath.StartsWith(applicationRoot + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static void ApplyUnixFileMode(ZipArchiveEntry entry, string destinationPath)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var rawMode = (entry.ExternalAttributes >> 16) & 0xFFF;
        if (rawMode != 0)
        {
            File.SetUnixFileMode(destinationPath, (UnixFileMode)rawMode);
        }
    }
}
