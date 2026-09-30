using System.Diagnostics;
using System.Text.Json;
using Ansight.Host;
using Ansight.Infrastructure;
using Ansight.Infrastructure.Security;

namespace Ansight.Cli;

internal static class CliRuntime
{
    private const string DataDirectoryEnvironmentVariable = "ANSIGHT_DATA_DIR";

    public static CliRuntimeOptions ResolveOptions(CliArguments arguments)
    {
        if (arguments.HasFlag("trust-mcp-certificate")
            || arguments.GetOption("mcp-port") is not null)
        {
            throw new CliUsageException(
                "MCP hosting has been removed. Use the native ansight CLI, including ansight ui for semantic app control.");
        }

        var dataDirectory = ResolveDataDirectory(arguments.GetOption("data-dir"));
        var credentials = LocalSettingsStore.ReadCredentials(dataDirectory);
        var sdkRoot = new[] { Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT"),
            Environment.GetEnvironmentVariable("ANDROID_HOME"), LocalSettingsStore.ReadAndroidSdkRoot(dataDirectory) }
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        return new CliRuntimeOptions(
            dataDirectory,
            arguments.GetOption("adb-path") ?? Environment.GetEnvironmentVariable("ANSIGHT_ADB_PATH")
            ?? (string.IsNullOrWhiteSpace(sdkRoot) ? null : Path.Combine(sdkRoot, "platform-tools", OperatingSystem.IsWindows() ? "adb.exe" : "adb")),
            arguments.GetOption("xcode-path") ?? Environment.GetEnvironmentVariable("ANSIGHT_XCODE_PATH"),
            arguments.GetOption("secret-store-file")
            ?? Environment.GetEnvironmentVariable("ANSIGHT_SECRET_STORE_FILE")
            ?? credentials?.StoreFile,
            arguments.GetOption("secret-key-file")
            ?? Environment.GetEnvironmentVariable(FileEncryptionKeyProvider.KeyFileEnvironmentVariable)
            ?? (FileEncryptionKeyProvider.HasExternalKeySource() ? null : credentials?.KeyFile),
            ReadOptionalPort(arguments, "discovery-port", "ANSIGHT_DISCOVERY_PORT"),
            ReadOptionalPort(arguments, "websocket-port", "ANSIGHT_WEBSOCKET_PORT"),
            !arguments.HasFlag("disable-repository-automations"),
            arguments.GetOptions("automation-repository")
                .Select(static path => Path.GetFullPath(path))
                .ToArray(),
            arguments.GetOption("node-path")
            ?? Environment.GetEnvironmentVariable("ANSIGHT_NODE_PATH")
            ?? "node");
    }

    public static RuntimeCoordinator CreateHostRuntime(CliRuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new RuntimeCoordinator(CreateHostOptions(options));
    }

    public static RuntimeOptions CreateHostOptions(CliRuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var featureLifetime = CliAccessContext.Current?.Lease.Token ?? CancellationToken.None;
        return new RuntimeOptions
        {
            FeatureLifetime = featureLifetime,
            BaseFolderPath = options.DataDirectory,
            SecureStorageFilePath = options.SecureStorageFilePath,
            SecureStorageKeyFilePath = options.SecureStorageKeyFilePath,
            AdbPath = options.AdbPath,
            XcodePath = options.XcodePath,
            DiscoveryPort = options.DiscoveryPort,
            WebSocketPort = options.WebSocketPort,
            EnableRepositoryAutomations = options.EnableRepositoryAutomations,
            AutomationRepositoryPaths = options.AutomationRepositoryPaths,
            JavaScriptExecutablePath = options.JavaScriptExecutablePath,
            SessionVideoEncoder = CliSessionVideoEncoderFactory.Create()
        };
    }

    public static IEncryptedStorage CreateEncryptedStorage(CliRuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var applicationPaths = new DataToolApplicationPaths(options.DataDirectory);
        return EncryptedStorageFactory.CreateDefault(
            applicationPaths,
            options.SecureStorageFilePath,
            options.SecureStorageKeyFilePath);
    }

    public static string ResolveMetadataPath(string dataDirectory)
        => Path.Combine(dataDirectory, "ansight-host.json");

    public static async Task WriteMetadataAsync(
        string dataDirectory,
        RuntimeStatusSnapshot status,
        string? controlPipeName,
        Uri? explorerUrl,
        CompanionAccessStatus? companionAccess,
        string? logFilePath,
        CancellationToken cancellationToken)
    {
        var identity = CliReleaseIdentity.Current;
        var metadata = new CliHostMetadata(
            Environment.ProcessId,
            GetCurrentProcessStartUtc(),
            status.IsRunning,
            controlPipeName,
            dataDirectory,
            DateTimeOffset.UtcNow,
            explorerUrl?.ToString(),
            companionAccess,
            identity.Version,
            identity.BuildNumber,
            identity.CommitSha,
            logFilePath);
        var path = ResolveMetadataPath(dataDirectory);
        var temporaryPath = path + ".tmp";
        await File.WriteAllTextAsync(
            temporaryPath,
            JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken).ConfigureAwait(false);
        TryRestrictFilePermissions(temporaryPath);
        File.Move(temporaryPath, path, overwrite: true);
        TryRestrictFilePermissions(path);
    }

    public static async Task WriteRecoveryMetadataAsync(string dataDirectory, Uri? explorerUrl,
        string? logFilePath, CancellationToken cancellationToken)
    {
        var identity = CliReleaseIdentity.Current;
        var metadata = new CliHostMetadata(Environment.ProcessId, GetCurrentProcessStartUtc(), true,
            null, dataDirectory, DateTimeOffset.UtcNow, explorerUrl?.ToString(), null,
            identity.Version, identity.BuildNumber, identity.CommitSha, logFilePath);
        var path = ResolveMetadataPath(dataDirectory);
        var temporaryPath = path + ".tmp";
        await File.WriteAllTextAsync(temporaryPath,
            JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken).ConfigureAwait(false);
        TryRestrictFilePermissions(temporaryPath);
        File.Move(temporaryPath, path, overwrite: true);
        TryRestrictFilePermissions(path);
    }

    public static CliHostMetadata? ReadMetadata(string dataDirectory)
    {
        var path = ResolveMetadataPath(dataDirectory);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<CliHostMetadata>(File.ReadAllText(path));
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return null;
        }
    }

    public static bool IsProcessRunning(int processId)
    {
        if (processId <= 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static bool IsHostProcessRunning(CliHostMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (metadata.ProcessId <= 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(metadata.ProcessId);
            if (process.HasExited)
            {
                return false;
            }

            if (metadata.ProcessStartedUtc is null)
            {
                return true;
            }

            var actualStartUtc = new DateTimeOffset(process.StartTime.ToUniversalTime());
            return (actualStartUtc - metadata.ProcessStartedUtc.Value).Duration() < TimeSpan.FromSeconds(2);
        }
        catch (Exception exception) when (exception is ArgumentException
                                           or InvalidOperationException
                                           or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    public static void TryDeleteMetadata(string dataDirectory)
    {
        try
        {
            File.Delete(ResolveMetadataPath(dataDirectory));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    internal static string ResolveDataDirectory(string? configuredPath)
    {
        var explicitPath = FirstNonEmpty(
            configuredPath,
            Environment.GetEnvironmentVariable(DataDirectoryEnvironmentVariable));
        if (explicitPath is not null)
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(explicitPath));
        }

        try
        {
            return ApplicationPathsFactory.Create().BaseFolderPath;
        }
        catch (InvalidOperationException)
        {
            throw new CliUsageException(
                $"A data directory could not be resolved. Set --data-dir or {DataDirectoryEnvironmentVariable}.");
        }
    }

    private static int? ReadOptionalPort(
        CliArguments arguments,
        string optionName,
        string environmentVariable)
    {
        var source = arguments.GetOption(optionName)
                     ?? Environment.GetEnvironmentVariable(environmentVariable);
        if (string.IsNullOrWhiteSpace(source))
        {
            return null;
        }

        if (!int.TryParse(source, out var port) || port is < 1 or > 65_535)
        {
            throw new CliUsageException(
                $"--{optionName} or {environmentVariable} must be a port between 1 and 65535.");
        }

        return port;
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static DateTimeOffset? GetCurrentProcessStartUtc()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return new DateTimeOffset(process.StartTime.ToUniversalTime());
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                           or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static void TryRestrictFilePermissions(string path)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(path))
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or PlatformNotSupportedException)
        {
        }
    }
}
