using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Ansight.Adb;
using Ansight.Host;
using Ansight.Infrastructure.Security;
using SkiaSharp;
using Ansight.Host.Diagnostics;

namespace Ansight.Cli.Commands.Doctor;

internal static class DoctorCommand
{
    public static async Task<int> RunAsync(
        CliArguments arguments,
        CliOutput output,
        CancellationToken cancellationToken)
    {
        if (CliCommandHelp.IsRequested(arguments))
        {
            return CliCommandHelp.Write(output, BuildHelp());
        }

        var options = CliRuntime.ResolveOptions(arguments);
        if (arguments.HasFlag("target") && string.IsNullOrWhiteSpace(arguments.GetOption("target")))
            throw new CliUsageException("--target requires android-device or android-emulator.");
        var target = ResolveTarget(arguments);
        if (target is not ("auto" or "android-device" or "android-emulator"))
            throw new CliUsageException("--target must be android-device or android-emulator.");
        var full = arguments.HasFlag("full");
        var credentialsOnly = arguments.HasFlag("credentials-only");
        if (credentialsOnly && (full || target != "auto"))
            throw new CliUsageException("--credentials-only cannot be combined with --full or --target.");
        if (full && target != "auto") throw new CliUsageException("--target cannot be combined with --full.");
        if (arguments.GetOption("include-secret-metadata") is not null)
            throw new CliUsageException("--include-secret-metadata takes no value. Omit the flag to exclude secret metadata.");
        if (!full && (arguments.HasFlag("include-secret-metadata") || arguments.GetOption("output") is not null))
            throw new CliUsageException("--include-secret-metadata and --output require --full.");
        if (full)
        {
            if (arguments.GetOptions("require").Count > 0)
                throw new CliUsageException("--require applies to the quick health check. Full reports describe observed state.");
            var report = await new SystemReportService(CliRuntime.CreateHostOptions(options))
                .CollectAsync(arguments.HasFlag("include-secret-metadata"), cancellationToken).ConfigureAwait(false);
            if (arguments.GetOption("output") is { } destination)
                await SaveReportAsync(report, destination, cancellationToken).ConfigureAwait(false);
            output.Write(report, () => SystemReportRenderer.Render(report));
            return CliExitCodes.Success;
        }
        var checks = new List<DoctorCheck>();
        checks.Add(CheckDataDirectory(options.DataDirectory));

        var secureStoragePath = string.IsNullOrWhiteSpace(options.SecureStorageFilePath)
            ? Path.Combine(options.DataDirectory, "data", "secure-storage.json")
            : Path.GetFullPath(options.SecureStorageFilePath);
        var secretProvider = EncryptedStorageFactory.ResolveDefaultProviderName(
            options.SecureStorageFilePath,
            options.SecureStorageKeyFilePath);
        switch (secretProvider)
        {
            case "macos-keychain":
                checks.Add(CheckPlatformVault(
                    "macos-keychain",
                    "macOS Keychain is the active credential vault."));
                break;
            case "linux-secret-service":
                var vaultReady = LinuxSecretServiceEncryptedStorage.TryProbe(true, out var vaultMessage);
                checks.Add(new DoctorCheck("credential-vault", vaultReady ? "linux-secret-service" : "unavailable",
                    vaultReady, true, vaultMessage, null));
                break;
            case "windows-dpapi":
                checks.Add(CheckSecretStore(secureStoragePath));
                checks.Add(CheckPlatformVault(
                    "windows-dpapi",
                    "The file master key is protected for the current Windows user by DPAPI."));
                break;
            case "protected-file":
                checks.Add(CheckSecretStore(secureStoragePath));
                checks.Add(CheckSecretKey(options));
                if (File.Exists(secureStoragePath)) checks.Add(CheckStoreDecryption(secureStoragePath, options.SecureStorageKeyFilePath));
                break;
            default:
                checks.Add(new DoctorCheck(
                    "credential-vault",
                    "unavailable",
                    false,
                    true,
                    "No OS credential vault is available. Configure a systemd credential or an externally protected secret key file for this headless host.",
                    null));
                break;
        }
        if (!credentialsOnly)
        {
            checks.AddRange(CheckDotNetTools());
            checks.Add(CheckExecutable("node", required: false));
            checks.Add(await TesseractDoctor.CheckAsync(
                    Environment.GetEnvironmentVariable(TesseractDoctor.ExecutablePathEnvironmentVariable),
                    Environment.GetEnvironmentVariable("PATH"),
                    cancellationToken)
                .ConfigureAwait(false));
            var scrcpyResolution = await ScrcpyToolLocator.ResolveAsync(
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            checks.Add(CreateScrcpyCheck(scrcpyResolution));
            checks.Add(CheckSkiaSharp());
            if (OperatingSystem.IsMacOS())
            {
                checks.Add(await MacAccessibilityDoctor.CheckAsync(cancellationToken).ConfigureAwait(false));
                checks.Add(await BlackHoleDoctor.CheckAsync(cancellationToken).ConfigureAwait(false));
            }

            if (OperatingSystem.IsMacOS())
            {
                var appiumPath = FindExecutable("appium");
                checks.Add(CheckExecutable("appium", required: false));
                checks.AddRange(await AppiumDoctor.CheckAsync(
                        appiumPath,
                        Environment.GetEnvironmentVariable("ANSIGHT_APPIUM_SERVER_URL"),
                        cancellationToken)
                    .ConfigureAwait(false));
            }
            else
            {
                foreach (var name in new[] { "tool.appium", "device.ios.appium.xcuitest", "device.ios.appium.server" })
                    checks.Add(NotApplicable(name));
            }

            var deviceService = new DeviceService(CliRuntime.CreateHostOptions(options));
            var capabilities = await deviceService.GetCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
            checks.AddRange(capabilities.Select(CreateDeviceCheck));
            checks.AddRange(await AndroidReadinessDoctor.CheckAsync(target, options.AdbPath, cancellationToken));
        }
        RequireChecks(checks, arguments.GetOptions("require"));

        var result = new DoctorResult(
            "ansight.doctor/v1",
            RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
            Environment.Version.ToString(),
            options.DataDirectory,
            checks,
            checks.All(static check => check.IsSuccess || !check.IsRequired),
            DateTimeOffset.UtcNow);
        output.Write(result, () => Render(result));
        return result.IsHealthy ? CliExitCodes.Success : CliExitCodes.CapabilityUnavailable;
    }

    private static async Task SaveReportAsync(SystemReport report, string destination, CancellationToken token)
    {
        var path = Path.GetFullPath(destination);
        var temporary = path + ".pending-" + Guid.NewGuid().ToString("N");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(temporary, options))
                await JsonSerializer.SerializeAsync(stream, report, new JsonSerializerOptions(JsonSerializerDefaults.Web)
                { WriteIndented = true, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }, token);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static string ResolveTarget(CliArguments arguments)
    {
        var target = arguments.GetOption("target") ?? "auto";
        if (target != "auto" || arguments.HasFlag("full") || arguments.HasFlag("credentials-only")) return target;
        return arguments.GetOptions("require").Contains("android.connection", StringComparer.OrdinalIgnoreCase)
            ? "android-device"
            : "auto";
    }

    internal static DoctorCheck CreateDeviceCheck(DeviceCapability capability)
    {
        ArgumentNullException.ThrowIfNull(capability);
        if ((capability.Platform == "ios" || capability.Platform.StartsWith("ios.", StringComparison.Ordinal)) && !OperatingSystem.IsMacOS())
            return NotApplicable($"device.{capability.Platform}");
        return new DoctorCheck(
            $"device.{capability.Platform}",
            capability.Status ?? (capability.IsAvailable ? "available" : "unavailable"),
            capability.IsAvailable,
            false,
            capability.Message,
            string.IsNullOrWhiteSpace(capability.Backend) ? null : capability.Backend);
    }

    private static DoctorCheck NotApplicable(string name)
        => new(name, "not-applicable", true, false,
            "Local iOS automation requires macOS; this check does not apply to this host.", null);

    internal static void RequireChecks(
        IList<DoctorCheck> checks,
        IReadOnlyList<string> requestedCheckNames)
    {
        ArgumentNullException.ThrowIfNull(checks);
        ArgumentNullException.ThrowIfNull(requestedCheckNames);
        var requestedNames = requestedCheckNames
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Select(static name => name.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (requestedNames.Count == 0)
        {
            return;
        }

        var matchedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < checks.Count; index++)
        {
            var check = checks[index];
            if (!requestedNames.Contains(check.Name))
            {
                continue;
            }

            checks[index] = check with { IsRequired = true, IsSuccess = check.IsSuccess && check.Status != "not-applicable" };
            matchedNames.Add(check.Name);
        }

        var missingNames = requestedNames
            .Except(matchedNames, StringComparer.OrdinalIgnoreCase)
            .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (missingNames.Length > 0)
        {
            throw new CliUsageException(
                $"Unknown doctor check name: {string.Join(", ", missingNames)}.");
        }
    }

    private static DoctorCheck CheckDataDirectory(string dataDirectory)
    {
        try
        {
            Directory.CreateDirectory(dataDirectory);
            var probePath = Path.Combine(dataDirectory, $".ansight-write-probe-{Guid.NewGuid():N}");
            File.WriteAllText(probePath, string.Empty);
            File.Delete(probePath);
            return new DoctorCheck(
                "data-directory",
                "available",
                true,
                true,
                "The data directory is writable.",
                dataDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new DoctorCheck(
                "data-directory",
                "unavailable",
                false,
                true,
                exception.Message,
                dataDirectory);
        }
    }

    private static DoctorCheck CheckSecretStore(string path)
    {
        if (!File.Exists(path))
        {
            return new DoctorCheck(
                "secret-store",
                "not-created",
                true,
                true,
                "The encrypted secret store has not been created. It will use AES-256-GCM when first used.",
                path);
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("schema", out var schema)
                || schema.ValueKind != JsonValueKind.String
                || !string.Equals(
                    schema.GetString(),
                    "ansight.secure-storage/v2",
                    StringComparison.Ordinal))
            {
                return new DoctorCheck(
                    "secret-store",
                    "plaintext-legacy",
                    false,
                    true,
                    "The legacy plaintext secret store has not been migrated. Run an auth or secret command with the intended master key to migrate it in place.",
                    path);
            }
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or JsonException)
        {
            return new DoctorCheck(
                "secret-store",
                "invalid",
                false,
                true,
                exception.Message,
                path);
        }

        if (OperatingSystem.IsWindows())
        {
            return new DoctorCheck(
                "secret-store",
                "encrypted",
                true,
                true,
                "The AES-256-GCM secret store exists. Windows ACL validation is not implemented yet.",
                path);
        }

        try
        {
            var mode = File.GetUnixFileMode(path);
            var unsafeBits = mode & (UnixFileMode.GroupRead
                                     | UnixFileMode.GroupWrite
                                     | UnixFileMode.GroupExecute
                                     | UnixFileMode.OtherRead
                                     | UnixFileMode.OtherWrite
                                     | UnixFileMode.OtherExecute);
            return new DoctorCheck(
                "secret-store",
                unsafeBits == 0 ? "permission-restricted" : "insecure-permissions",
                unsafeBits == 0,
                true,
                unsafeBits == 0
                    ? "The AES-256-GCM secret store is readable and writable only by its owner."
                    : $"The encrypted secret store has unsafe Unix permissions: {mode}.",
                path);
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or PlatformNotSupportedException)
        {
            return new DoctorCheck(
                "secret-store",
                "unknown",
                false,
                true,
                exception.Message,
                path);
        }
    }

    internal static DoctorCheck CheckStoreDecryption(string path, string? keyFile)
    {
        try
        {
            var key = FileEncryptionKeyProvider.Resolve(path, keyFile);
            try { _ = FileBackedEncryptedStorage.OpenReadOnly(path, key); }
            finally { CryptographicOperations.ZeroMemory(key); }
            return new("secret-store-access", "readable", true, true, "The configured key can read the existing encrypted store.", path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or CryptographicException)
        {
            return new("secret-store-access", "unavailable", false, true, exception.Message, path);
        }
    }

    private static DoctorCheck CheckSecretKey(CliRuntimeOptions options)
    {
        var environmentKey = Environment.GetEnvironmentVariable(
            FileEncryptionKeyProvider.MasterKeyEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(environmentKey))
        {
            try
            {
                var key = FileEncryptionKeyProvider.DecodeKey(
                    environmentKey,
                    FileEncryptionKeyProvider.MasterKeyEnvironmentVariable);
                CryptographicOperations.ZeroMemory(key);
                return new DoctorCheck(
                    "secret-master-key",
                    "environment",
                    true,
                    true,
                    "The encrypted store master key is supplied by the process environment.",
                    FileEncryptionKeyProvider.MasterKeyEnvironmentVariable);
            }
            catch (InvalidOperationException exception)
            {
                return new DoctorCheck(
                    "secret-master-key",
                    "invalid",
                    false,
                    true,
                    exception.Message,
                    FileEncryptionKeyProvider.MasterKeyEnvironmentVariable);
            }
        }

        var configuredKeyPath = FirstNonEmpty(
            options.SecureStorageKeyFilePath,
            Environment.GetEnvironmentVariable(FileEncryptionKeyProvider.KeyFileEnvironmentVariable));
        if (configuredKeyPath is not null)
        {
            return CheckSecretKeyFile(
                Path.GetFullPath(Environment.ExpandEnvironmentVariables(configuredKeyPath)),
                "configured-key-file",
                "The encrypted store master key is supplied by a configured key file.");
        }

        var credentialsDirectory = Environment.GetEnvironmentVariable(
            FileEncryptionKeyProvider.SystemdCredentialsDirectoryEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(credentialsDirectory))
        {
            var credentialPath = Path.Combine(
                credentialsDirectory.Trim(),
                FileEncryptionKeyProvider.SystemdCredentialFileName);
            if (File.Exists(credentialPath))
            {
                return CheckSecretKeyFile(
                    credentialPath,
                    "systemd-credential",
                    "The encrypted store master key is supplied by a systemd credential.");
            }
        }

        return new DoctorCheck(
            "secret-master-key",
            "unconfigured",
            false,
            true,
            "The protected file store requires an externally managed master key; local sibling-key generation is disabled.",
            null);
    }

    private static DoctorCheck CheckPlatformVault(string status, string message)
        => new(
            "credential-vault",
            status,
            true,
            true,
            message,
            null);

    private static DoctorCheck CheckSecretKeyFile(
        string path,
        string successStatus,
        string successMessage)
    {
        if (!File.Exists(path))
        {
            return new DoctorCheck(
                "secret-master-key",
                "missing",
                false,
                true,
                "The configured secret master-key file does not exist.",
                path);
        }

        try
        {
            var key = FileEncryptionKeyProvider.DecodeKey(
                File.ReadAllText(path),
                $"Secret key file '{path}'");
            CryptographicOperations.ZeroMemory(key);
            if (!OperatingSystem.IsWindows())
            {
                var mode = File.GetUnixFileMode(path);
                var unsafeBits = mode & (UnixFileMode.GroupRead
                                         | UnixFileMode.GroupWrite
                                         | UnixFileMode.GroupExecute
                                         | UnixFileMode.OtherRead
                                         | UnixFileMode.OtherWrite
                                         | UnixFileMode.OtherExecute);
                if (unsafeBits != 0)
                {
                    return new DoctorCheck(
                        "secret-master-key",
                        "insecure-permissions",
                        false,
                        true,
                        $"The secret master-key file has unsafe Unix permissions: {mode}.",
                        path);
                }
            }

            return new DoctorCheck(
                "secret-master-key",
                successStatus,
                true,
                true,
                successMessage,
                path);
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or InvalidOperationException
                                           or PlatformNotSupportedException)
        {
            return new DoctorCheck(
                "secret-master-key",
                "invalid",
                false,
                true,
                exception.Message,
                path);
        }
    }

    private static DoctorCheck CheckExecutable(string executableName, bool required)
    {
        var path = FindExecutable(executableName);
        return new DoctorCheck(
            $"tool.{executableName}",
            path is null ? "unavailable" : "available",
            path is not null,
            required,
            path is null
                ? $"'{executableName}' was not found on PATH."
                : $"'{executableName}' is available.",
            path);
    }

    internal static IReadOnlyList<DoctorCheck> CheckDotNetTools()
    {
        return
        [
            CheckExecutable("dotnet-trace", required: false),
            CheckExecutable("dotnet-dsrouter", required: false)
        ];
    }

    private static DoctorCheck CheckSkiaSharp()
    {
        try
        {
            using var data = SKData.CreateCopy([0]);
            return new DoctorCheck(
                "native.skia",
                "available",
                true,
                false,
                "The SkiaSharp native runtime is available for screenshot evidence and comparison.",
                "libSkiaSharp");
        }
        catch (Exception exception)
        {
            var cause = exception.GetBaseException().Message;
            return new DoctorCheck(
                "native.skia",
                "unavailable",
                false,
                false,
                $"The SkiaSharp native runtime is unavailable. Screenshot-backed UI actions will fail. {cause}",
                null);
        }
    }

    internal static DoctorCheck CreateScrcpyCheck(ScrcpyToolResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        return new DoctorCheck(
            "tool.scrcpy",
            resolution.IsFound ? "available" : "unavailable",
            resolution.IsFound,
            false,
            resolution.Message,
            resolution.IsFound ? resolution.ExecutablePath : null);
    }

    private static string? FindExecutable(string executableName)
    {
        var effectiveName = OperatingSystem.IsWindows() && !executableName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? executableName + ".exe"
            : executableName;
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (var directory in path.Split(
                     Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(directory.Trim('"'), effectiveName);
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    internal static string Render(DoctorResult result)
    {
        var lines = new List<string>
        {
            $"{GetTrafficLight(result.Signal)} Ansight doctor: {result.Signal.ToUpperInvariant()} — {GetSignalSummary(result.Signal)}",
            $"Platform: {result.OperatingSystem} ({result.Architecture})",
            $"Data: {result.DataDirectory}"
        };
        lines.AddRange(result.Checks.Select(check =>
            $"{GetTrafficLight(check.Signal)} "
            + $"{(check.IsSuccess ? "[ok]" : check.IsRequired ? "[error]" : "[warn]")} "
            + $"{check.Name}: {check.Message}"));
        return string.Join(Environment.NewLine, lines);
    }

    private static string GetTrafficLight(string signal)
        => signal switch
        {
            "green" => "🟢",
            "amber" => "🟡",
            "red" => "🔴",
            _ => "⚪"
        };

    private static string GetSignalSummary(string signal)
        => signal switch
        {
            "green" => "all checks passed",
            "amber" => "optional checks need attention",
            "red" => "required checks failed",
            _ => "check status is unknown"
        };

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string BuildHelp()
        => """
           Diagnose whether this machine can run the headless Ansight host

           Usage:
             ansight doctor [options]

           Checks:
             data directory       Exists or can be created and is writable
             credential vault     macOS Keychain, Linux Secret Service, Windows DPAPI, or protected file
             secret-store safety  Encryption schema, master-key availability, and Unix permissions
             .NET profiling       Optional dotnet-trace and dotnet-dsrouter; ADB or Xcode per target
             repository runtime   Node.js availability
             image runtime        Native SkiaSharp availability for screenshot evidence
             screenshot OCR       Tesseract path and version for local PII redaction
             device backends      Xcode/simctl/devicectl and Android SDK/ADB capabilities
             simulator input      Native SimulatorKit HID compatibility for the selected Xcode
             simulator audio      BlackHole registration and Ansight process Accessibility permission
             Android video        scrcpy executable, version, and matching server availability
             physical iOS input   Appium CLI, XCUITest driver, and Appium server status

           Options:
             --data-dir <path>            Inspect a non-default Ansight state directory
             --credentials-only          Check data directory and credential readiness only
             --target android-device     Require ADB and a connected, authorized external target
             --target android-emulator   Require emulator, AVD image, and usable acceleration
             --secret-store-file <path>   Inspect an encrypted file store instead of the OS vault
             --secret-key-file <path>     Validate its externally protected AES key file
             --adb-path <path>            Explicit ADB executable or Android SDK directory
             --xcode-path <path>          Explicit Xcode app or Developer directory
             --require <check-name>       Make a named optional check release-blocking; repeatable
             --json                       Emit every check as versioned structured output
             --full                       Include machine, tools, apps, skills and agents
             --output <report.json>       Save the full report as JSON
             --include-secret-metadata    Opt in to aliases and revisions; requires --full

           Secret metadata is not collected by default, including with --full. Values and
           secret hashes are never included. No report is uploaded by this command.

           Traffic lights:
             GREEN   Every check passed
             AMBER   Required checks passed, but optional capabilities need attention
             RED     One or more required checks failed

           Required failures return the capability-unavailable exit code. Optional tool failures are
           warnings so Linux headless hosts can remain healthy without Apple-only capabilities.
           """;

}
