public static class ApplicationPathsFactory
{
    private const string CompanyFolderName = "Ansight";
    private const string LegacyDesktopFolderName = "Studio";
    private const string LegacyCliFolderName = "Cli";
    private const string SecureStorageFileName = "secure-storage.json";
    private const string SecureStorageKeyFileName = "secure-storage.key";

    public static DataToolApplicationPaths Create(string? isolatedFolderName = null)
    {
        var sharedBaseFolderPath = ResolveDefaultBaseFolderPath();
        var preferredBaseFolderPath = string.IsNullOrWhiteSpace(isolatedFolderName)
            ? sharedBaseFolderPath
            : Path.Combine(sharedBaseFolderPath, isolatedFolderName.Trim());

        if (string.IsNullOrWhiteSpace(isolatedFolderName))
        {
            TryMigrateLegacyBaseFolders(
                preferredBaseFolderPath,
                Path.Combine(sharedBaseFolderPath, LegacyDesktopFolderName),
                Path.Combine(sharedBaseFolderPath, LegacyCliFolderName));
        }
        else if (IsMacPlatform())
        {
            TryMigrateLegacyBaseFolders(
                preferredBaseFolderPath,
                ResolveLegacyLocalApplicationDataBaseFolderPath(isolatedFolderName));
        }

        try
        {
            return new DataToolApplicationPaths(preferredBaseFolderPath);
        }
        catch (UnauthorizedAccessException)
        {
            return new DataToolApplicationPaths(Path.Combine(AppContext.BaseDirectory, ".cache"));
        }
        catch (IOException)
        {
            return new DataToolApplicationPaths(Path.Combine(AppContext.BaseDirectory, ".cache"));
        }
    }

    public static string ResolveBaseFolderPath(IApplicationPaths applicationPaths)
    {
        ArgumentNullException.ThrowIfNull(applicationPaths);
        return applicationPaths is DataToolApplicationPaths dataToolApplicationPaths
            ? dataToolApplicationPaths.BaseFolderPath
            : Path.GetDirectoryName(applicationPaths.ApplicationDataPath)
              ?? applicationPaths.ApplicationDataPath;
    }

    public static string ResolveDefaultBaseFolderPath()
    {
        if (OperatingSystem.IsLinux())
        {
            var stateHome = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
            if (!string.IsNullOrWhiteSpace(stateHome))
            {
                return Path.GetFullPath(Path.Combine(stateHome.Trim(), "ansight"));
            }

            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(userProfile))
            {
                return Path.Combine(userProfile, ".local", "state", "ansight");
            }
        }

        if (IsMacPlatform() && TryResolveMacUserHomeFolderPath(out var macUserHomeFolderPath))
        {
            return Path.Combine(
                macUserHomeFolderPath,
                "Library",
                "Application Support",
                CompanyFolderName);
        }

        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localApplicationData))
        {
            return Path.Combine(localApplicationData, CompanyFolderName);
        }

        throw new InvalidOperationException("The Ansight application data directory could not be resolved.");
    }

    internal static void TryMigrateLegacyBaseFolders(
        string preferredBaseFolderPath,
        params string[] legacyBaseFolderPaths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(preferredBaseFolderPath);
        ArgumentNullException.ThrowIfNull(legacyBaseFolderPaths);

        foreach (var legacyBaseFolderPath in legacyBaseFolderPaths)
        {
            if (string.IsNullOrWhiteSpace(legacyBaseFolderPath)
                || AreSamePath(legacyBaseFolderPath, preferredBaseFolderPath)
                || !Directory.Exists(legacyBaseFolderPath))
            {
                continue;
            }

            try
            {
                PrivateStorageDirectory.Ensure(preferredBaseFolderPath);
                CopyDirectoryContents(legacyBaseFolderPath, preferredBaseFolderPath);
                // Older CLI releases may still have this path cached for
                // the lifetime of their process. Keep the source available so a
                // newer process cannot invalidate their active file stores.
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Startup should continue if a legacy working-folder migration cannot complete.
                // A later launch can retry safely without disturbing the source.
            }
        }
    }

    private static string ResolveLegacyLocalApplicationDataBaseFolderPath(string folderName)
    {
        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localApplicationData))
        {
            localApplicationData = AppContext.BaseDirectory;
        }

        return Path.Combine(localApplicationData, CompanyFolderName, folderName.Trim());
    }

    private static void CopyDirectoryContents(string sourceDirectoryPath, string destinationDirectoryPath)
    {
        Directory.CreateDirectory(destinationDirectoryPath);
        CopySecureStorageBundle(sourceDirectoryPath, destinationDirectoryPath);
        foreach (var sourceFilePath in Directory.EnumerateFiles(sourceDirectoryPath))
        {
            if (IsSecureStorageBundleFile(sourceFilePath))
            {
                continue;
            }

            var destinationFilePath = Path.Combine(destinationDirectoryPath, Path.GetFileName(sourceFilePath));
            if (ShouldCopyFile(sourceFilePath, destinationFilePath))
            {
                File.Copy(sourceFilePath, destinationFilePath, overwrite: true);
            }
        }

        foreach (var sourceChildDirectoryPath in Directory.EnumerateDirectories(sourceDirectoryPath))
        {
            CopyDirectoryContents(
                sourceChildDirectoryPath,
                Path.Combine(destinationDirectoryPath, Path.GetFileName(sourceChildDirectoryPath)));
        }
    }

    private static void CopySecureStorageBundle(
        string sourceDirectoryPath,
        string destinationDirectoryPath)
    {
        var sourceStorageFilePath = Path.Combine(sourceDirectoryPath, SecureStorageFileName);
        var destinationStorageFilePath = Path.Combine(destinationDirectoryPath, SecureStorageFileName);
        if (!File.Exists(sourceStorageFilePath)
            || !ShouldCopyFile(sourceStorageFilePath, destinationStorageFilePath))
        {
            return;
        }

        var sourceKeyFilePath = Path.Combine(sourceDirectoryPath, SecureStorageKeyFileName);
        var destinationKeyFilePath = Path.Combine(destinationDirectoryPath, SecureStorageKeyFileName);
        if (File.Exists(sourceKeyFilePath))
        {
            File.Copy(sourceKeyFilePath, destinationKeyFilePath, overwrite: true);
        }

        File.Copy(sourceStorageFilePath, destinationStorageFilePath, overwrite: true);
    }

    private static bool IsSecureStorageBundleFile(string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        return string.Equals(fileName, SecureStorageFileName, StringComparison.OrdinalIgnoreCase)
               || string.Equals(fileName, SecureStorageKeyFileName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldCopyFile(string sourceFilePath, string destinationFilePath)
    {
        if (!File.Exists(destinationFilePath))
        {
            return true;
        }

        return File.GetLastWriteTimeUtc(sourceFilePath) > File.GetLastWriteTimeUtc(destinationFilePath);
    }

    private static bool AreSamePath(string left, string right)
    {
        var normalizedLeft = NormalizePath(left);
        var normalizedRight = NormalizePath(right);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(normalizedLeft, normalizedRight, comparison);
    }

    private static string NormalizePath(string path)
    {
        return Path.GetFullPath(path.Trim())
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool IsMacPlatform()
    {
        return OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst();
    }

    private static bool TryResolveMacUserHomeFolderPath(out string userHomeFolderPath)
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("HOME"),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        };

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            userHomeFolderPath = NormalizeMacUserHomeFolderPath(candidate);
            if (!string.IsNullOrWhiteSpace(userHomeFolderPath))
            {
                return true;
            }
        }

        userHomeFolderPath = string.Empty;
        return false;
    }

    private static string NormalizeMacUserHomeFolderPath(string folderPath)
    {
        var normalizedPath = NormalizePath(folderPath);
        var folderName = Path.GetFileName(normalizedPath);
        if (string.Equals(folderName, "Documents", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetDirectoryName(normalizedPath) ?? normalizedPath;
        }

        return normalizedPath;
    }
}
