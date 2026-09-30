using System.Globalization;
using System.Text.RegularExpressions;
using Ansight.Adb.Devices;

namespace Ansight.Adb;

public sealed partial class AdbClient
{
    private readonly IAdbProcessLauncher processLauncher;

    public AdbClient(string adbPath)
        : this(adbPath, new DotNetAdbProcessLauncher())
    {
    }

    public AdbClient(string adbPath, IAdbProcessLauncher processLauncher)
    {
        if (string.IsNullOrWhiteSpace(adbPath))
        {
            throw new ArgumentException("An ADB executable path is required.", nameof(adbPath));
        }

        AdbPath = Path.GetFullPath(adbPath);
        this.processLauncher = processLauncher ?? throw new ArgumentNullException(nameof(processLauncher));
    }

    public string AdbPath { get; }

    public Task<AdbCommandResult> GetVersionAsync(CancellationToken cancellationToken = default)
        => RunAsync(["version"], cancellationToken);

    public Task<AdbCommandResult> CollapseSystemUiPanelsAsync(
        string deviceSerial,
        CancellationToken cancellationToken = default)
    {
        ValidateDeviceSerial(deviceSerial);
        return RunAsync(
            ["-s", deviceSerial, "shell", "cmd", "statusbar", "collapse"],
            cancellationToken);
    }

    public async Task<bool?> GetSoftwareKeyboardVisibilityAsync(
        string deviceSerial,
        CancellationToken cancellationToken = default)
    {
        ValidateDeviceSerial(deviceSerial);
        var result = await RunAsync(
            [
                "-s",
                deviceSerial,
                "shell",
                "dumpsys input_method | grep '^[[:space:]]*mInputShown='"
            ],
            cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
               && TryParseSoftwareKeyboardVisibility(result.StandardOutput, out var isVisible)
            ? isVisible
            : null;
    }

    internal static bool TryParseSoftwareKeyboardVisibility(
        string output,
        out bool isVisible)
    {
        const string statePrefix = "mInputShown=";
        foreach (var line in output.Split(
                     '\n',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith(statePrefix, StringComparison.Ordinal)
                && bool.TryParse(line[statePrefix.Length..].Trim(), out isVisible))
            {
                return true;
            }
        }

        isVisible = false;
        return false;
    }

    public async Task<IReadOnlyList<AdbDevice>> GetDevicesAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(["devices", "-l"], cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException(BuildFailureMessage("enumerate devices", result));
        }

        var devices = new List<AdbDevice>();
        foreach (var line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith('*'))
            {
                continue;
            }

            var parts = WhitespacePattern().Split(line.Trim());
            if (parts.Length < 2)
            {
                continue;
            }

            var properties = parts
                .Skip(2)
                .Select(value => value.Split(':', 2))
                .Where(pair => pair.Length == 2)
                .ToDictionary(pair => pair[0], pair => pair[1], StringComparer.OrdinalIgnoreCase);
            devices.Add(new AdbDevice(
                parts[0],
                parts[1],
                GetValue(properties, "product"),
                GetValue(properties, "model")?.Replace('_', ' '),
                GetValue(properties, "device"),
                GetValue(properties, "transport_id")));
        }

        return devices;
    }

    public async Task<string?> GetEmulatorAvdNameAsync(
        string deviceSerial,
        CancellationToken cancellationToken = default)
    {
        ValidateDeviceSerial(deviceSerial);
        var result = await RunAsync(
            ["-s", deviceSerial, "emu", "avd", "name"],
            cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return null;
        }

        return result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(static line =>
                !string.Equals(line, "OK", StringComparison.OrdinalIgnoreCase)
                && !line.StartsWith("KO", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(line, "unknown", StringComparison.OrdinalIgnoreCase));
    }

    public async Task<AndroidDeviceFormFactor> GetDeviceFormFactorAsync(
        string deviceSerial,
        CancellationToken cancellationToken = default)
    {
        ValidateDeviceSerial(deviceSerial);
        var characteristicsResult = await RunAsync(
            ["-s", deviceSerial, "shell", "getprop", "ro.build.characteristics"],
            cancellationToken).ConfigureAwait(false);
        var sizeResult = await RunAsync(
            ["-s", deviceSerial, "shell", "wm", "size"],
            cancellationToken).ConfigureAwait(false);
        var densityResult = await RunAsync(
            ["-s", deviceSerial, "shell", "wm", "density"],
            cancellationToken).ConfigureAwait(false);
        return FormFactorDetector.FromConnectedDevice(
            characteristicsResult.IsSuccess ? characteristicsResult.StandardOutput : string.Empty,
            sizeResult.IsSuccess ? sizeResult.StandardOutput : string.Empty,
            densityResult.IsSuccess ? densityResult.StandardOutput : string.Empty);
    }

    public async Task<IReadOnlyList<int>> GetProcessIdsAsync(
        string deviceSerial,
        string packageIdentifier,
        CancellationToken cancellationToken = default)
    {
        ValidateDeviceSerial(deviceSerial);
        ValidatePackageIdentifier(packageIdentifier);
        var result = await RunAsync(
            ["-s", deviceSerial, "shell", "pidof", packageIdentifier],
            cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return Array.Empty<int>();
        }

        return result.StandardOutput
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var processId) ? processId : 0)
            .Where(processId => processId > 0)
            .Distinct()
            .Order()
            .ToArray();
    }

    public async Task<AdbInstalledApplication?> GetInstalledApplicationAsync(
        string deviceSerial,
        string packageIdentifier,
        CancellationToken cancellationToken = default)
    {
        ValidateDeviceSerial(deviceSerial);
        ValidatePackageIdentifier(packageIdentifier);
        var pathResult = await RunAsync(
            ["-s", deviceSerial, "shell", "pm", "path", packageIdentifier],
            cancellationToken).ConfigureAwait(false);
        if (!pathResult.IsSuccess)
        {
            return null;
        }

        var packagePath = pathResult.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(static line => line.StartsWith("package:", StringComparison.Ordinal))?
            ["package:".Length..]
            .Trim();
        if (string.IsNullOrWhiteSpace(packagePath))
        {
            return null;
        }

        var packageResult = await RunAsync(
            ["-s", deviceSerial, "shell", "dumpsys", "package", packageIdentifier],
            cancellationToken).ConfigureAwait(false);
        var timeZoneResult = await RunAsync(
            ["-s", deviceSerial, "shell", "getprop", "persist.sys.timezone"],
            cancellationToken).ConfigureAwait(false);
        return ParseInstalledApplication(
            packageIdentifier,
            packagePath,
            packageResult.IsSuccess ? packageResult.StandardOutput : string.Empty,
            timeZoneResult.IsSuccess ? timeZoneResult.StandardOutput : string.Empty);
    }

    internal static AdbInstalledApplication ParseInstalledApplication(
        string packageIdentifier,
        string packagePath,
        string packageDump,
        string timeZoneId)
    {
        var version = ReadPackageValue(packageDump, "versionName");
        var buildVersion = ReadPackageValue(packageDump, "versionCode")?
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        var installedAtUtc = ParseDeviceTimestamp(
            ReadPackageValue(packageDump, "firstInstallTime"),
            timeZoneId);
        var lastUpdatedAtUtc = ParseDeviceTimestamp(
            ReadPackageValue(packageDump, "lastUpdateTime"),
            timeZoneId);
        return new AdbInstalledApplication(
            packageIdentifier,
            packagePath,
            string.Equals(version, "null", StringComparison.OrdinalIgnoreCase) ? null : version,
            buildVersion,
            installedAtUtc,
            lastUpdatedAtUtc);
    }

    private static string? ReadPackageValue(string output, string key)
    {
        var prefix = key + "=";
        return output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(line => line.StartsWith(prefix, StringComparison.Ordinal))?
            [prefix.Length..]
            .Trim();
    }

    private static DateTimeOffset? ParseDeviceTimestamp(string? value, string timeZoneId)
    {
        if (!DateTime.TryParseExact(
                value,
                "yyyy-MM-dd HH:mm:ss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var localTime)
            || string.IsNullOrWhiteSpace(timeZoneId))
        {
            return null;
        }

        try
        {
            var timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId.Trim());
            var unspecifiedTime = DateTime.SpecifyKind(localTime, DateTimeKind.Unspecified);
            return new DateTimeOffset(unspecifiedTime, timeZone.GetUtcOffset(unspecifiedTime))
                .ToUniversalTime();
        }
        catch (TimeZoneNotFoundException)
        {
            return null;
        }
        catch (InvalidTimeZoneException)
        {
            return null;
        }
    }

    public async Task<string?> GetInstalledPackageSha256Async(
        string deviceSerial,
        string packageIdentifier,
        CancellationToken cancellationToken = default)
    {
        ValidateDeviceSerial(deviceSerial);
        ValidatePackageIdentifier(packageIdentifier);
        var pathResult = await RunAsync(
            ["-s", deviceSerial, "shell", "pm", "path", packageIdentifier],
            cancellationToken).ConfigureAwait(false);
        if (!pathResult.IsSuccess)
        {
            return null;
        }

        var packagePaths = pathResult.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static line => line.StartsWith("package:", StringComparison.Ordinal))
            .Select(static line => line["package:".Length..].Trim())
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .ToArray();
        var basePackagePath = packagePaths.FirstOrDefault(static path =>
                                  string.Equals(Path.GetFileName(path), "base.apk", StringComparison.OrdinalIgnoreCase))
                              ?? (packagePaths.Length == 1 ? packagePaths[0] : null);
        if (basePackagePath is null)
        {
            return null;
        }

        var checksumResult = await RunAsync(
            ["-s", deviceSerial, "shell", "sha256sum", basePackagePath],
            cancellationToken).ConfigureAwait(false);
        if (!checksumResult.IsSuccess)
        {
            return null;
        }

        var checksum = checksumResult.StandardOutput
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        return checksum is { Length: 64 } && checksum.All(Uri.IsHexDigit)
            ? checksum.ToLowerInvariant()
            : null;
    }

    public async Task SetEmulatorLocationAsync(
        string deviceSerial,
        double latitude,
        double longitude,
        CancellationToken cancellationToken = default)
    {
        ValidateDeviceSerial(deviceSerial);
        ValidateCoordinate(latitude, longitude);
        var result = await RunAsync(
            [
                "-s",
                deviceSerial,
                "emu",
                "geo",
                "fix",
                longitude.ToString("R", CultureInfo.InvariantCulture),
                latitude.ToString("R", CultureInfo.InvariantCulture)
            ],
            cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException(BuildFailureMessage("set the emulator location", result));
        }
    }

    public async Task LaunchApplicationAsync(
        string deviceSerial,
        string packageIdentifier,
        CancellationToken cancellationToken = default)
    {
        ValidateDeviceSerial(deviceSerial);
        ValidatePackageIdentifier(packageIdentifier);
        var component = await ResolveLaunchComponentAsync(
            deviceSerial, packageIdentifier, cancellationToken).ConfigureAwait(false);
        var result = await RunAsync(
            ["-s", deviceSerial, "shell", "am", "start", "-W",
                "-a", "android.intent.action.MAIN", "-c", "android.intent.category.LAUNCHER",
                "-n", component],
            cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess || ReportsActivityStartFailure(result))
        {
            throw new InvalidOperationException(BuildFailureMessage("launch the Android app", result));
        }
    }

    public async Task LaunchApplicationForProfilingAsync(
        string deviceSerial,
        string packageIdentifier,
        CancellationToken cancellationToken = default)
    {
        ValidateDeviceSerial(deviceSerial);
        ValidatePackageIdentifier(packageIdentifier);
        var launchComponent = await ResolveLaunchComponentAsync(
            deviceSerial,
            packageIdentifier,
            cancellationToken).ConfigureAwait(false);
        var result = await RunAsync(
            [
                "-s",
                deviceSerial,
                "shell",
                "am",
                "start",
                "-W",
                "-S",
                "-a",
                "android.intent.action.MAIN",
                "-c",
                "android.intent.category.LAUNCHER",
                "-n",
                launchComponent
            ],
            cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess || ReportsActivityStartFailure(result))
        {
            throw new InvalidOperationException(BuildFailureMessage("launch the Android app for profiling", result));
        }
    }

    public async Task LaunchApplicationAsync(
        string deviceSerial,
        string packageIdentifier,
        string intentStringExtraName,
        string intentStringExtraValue,
        CancellationToken cancellationToken = default)
    {
        ValidateDeviceSerial(deviceSerial);
        ValidatePackageIdentifier(packageIdentifier);
        if (string.IsNullOrWhiteSpace(intentStringExtraName)
            || !SafeIdentifierPattern().IsMatch(intentStringExtraName))
        {
            throw new ArgumentException("The Android Intent extra name is invalid.", nameof(intentStringExtraName));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(intentStringExtraValue);
        var launchComponent = await ResolveLaunchComponentAsync(
            deviceSerial,
            packageIdentifier,
            cancellationToken).ConfigureAwait(false);
        var command = string.Join(
            ' ',
            "am start -W -S -a android.intent.action.MAIN -c android.intent.category.LAUNCHER -n",
            QuoteShellArgument(launchComponent),
            "--es",
            QuoteShellArgument(intentStringExtraName),
            QuoteShellArgument(intentStringExtraValue));
        var result = await RunWithStandardInputAsync(
            ["-s", deviceSerial, "shell"],
            command + "\n",
            cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess || ReportsActivityStartFailure(result))
        {
            throw new InvalidOperationException(BuildFailureMessage("launch the Android app", result));
        }
    }

    private async Task<string> ResolveLaunchComponentAsync(
        string deviceSerial,
        string packageIdentifier,
        CancellationToken cancellationToken)
    {
        var result = await RunAsync(
            [
                "-s",
                deviceSerial,
                "shell",
                "cmd",
                "package",
                "resolve-activity",
                "--brief",
                "--components",
                "-a",
                "android.intent.action.MAIN",
                "-c",
                "android.intent.category.LAUNCHER",
                "-p",
                packageIdentifier
            ],
            cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException(BuildFailureMessage("resolve the Android app launcher", result));
        }

        var launchComponents = result.StandardOutput.Split(
            '\n',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (launchComponents.Length != 1
            || !IsValidLaunchComponent(launchComponents[0], packageIdentifier))
        {
            throw new InvalidOperationException(
                $"ADB could not resolve a launcher activity for Android app '{packageIdentifier}'.");
        }

        return launchComponents[0];
    }

    public async Task InstallPackageAsync(
        string deviceSerial,
        string packagePath,
        CancellationToken cancellationToken = default)
    {
        ValidateDeviceSerial(deviceSerial);
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        var fullPackagePath = Path.GetFullPath(packagePath);
        if (!File.Exists(fullPackagePath)
            || !Path.GetExtension(fullPackagePath).Equals(".apk", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("An existing .apk package is required.", nameof(packagePath));
        }

        var result = await RunAsync(
            ["-s", deviceSerial, "install", "-r", fullPackagePath],
            cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess
            || !result.StandardOutput.Contains("Success", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(BuildFailureMessage("install the Android app", result));
        }
    }

    public async Task TerminateApplicationAsync(
        string deviceSerial,
        string packageIdentifier,
        CancellationToken cancellationToken = default)
    {
        ValidateDeviceSerial(deviceSerial);
        ValidatePackageIdentifier(packageIdentifier);
        var result = await RunAsync(
            ["-s", deviceSerial, "shell", "am", "force-stop", packageIdentifier],
            cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException(BuildFailureMessage("terminate the Android app", result));
        }
    }

    public Task<AdbLogcatStream> StartLogcatAsync(
        AdbLogcatRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateDeviceSerial(request.DeviceSerial);
        if (request.ProcessId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "A positive process ID is required.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var arguments = new List<string>
        {
            "-s",
            request.DeviceSerial,
            "logcat",
            "-v",
            "threadtime",
            "-v",
            "year",
            "-v",
            "UTC",
            "-v",
            "usec",
            $"--pid={request.ProcessId}"
        };
        var buffers = request.Buffers is { Count: > 0 }
            ? request.Buffers
            : ["main", "system", "crash"];
        arguments.Add("-b");
        arguments.Add(string.Join(',', buffers.Where(IsValidBufferName)));

        return Task.FromResult(new AdbLogcatStream(StartProcess(arguments)));
    }

    public async Task<byte[]> CaptureScreenshotPngAsync(
        string deviceSerial,
        CancellationToken cancellationToken = default)
    {
        ValidateDeviceSerial(deviceSerial);
        await using var process = StartProcess(
            ["-s", deviceSerial, "exec-out", "screencap", "-p"]);
        process.StandardInput.Dispose();

        try
        {
            using var output = new MemoryStream();
            var outputTask = process.StandardOutput.CopyToAsync(output, cancellationToken);
            using var errorReader = new StreamReader(process.StandardError);
            var errorTask = errorReader.ReadToEndAsync(cancellationToken);
            var exitCode = await process.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
            if (exitCode != 0 || output.Length == 0)
            {
                var detail = errorTask.Result.Trim();
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(detail)
                        ? $"ADB failed to capture a screenshot with exit code {exitCode}."
                        : $"ADB failed to capture a screenshot: {detail}");
            }

            return output.ToArray();
        }
        catch (OperationCanceledException)
        {
            process.Terminate(force: true);
            throw;
        }
    }

    public async Task<AdbCommandResult> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
        => await RunProcessAsync(arguments, standardInput: null, cancellationToken).ConfigureAwait(false);

    public async Task<AdbCommandResult> RunWithStandardInputAsync(
        IReadOnlyList<string> arguments,
        string standardInput,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(standardInput);
        return await RunProcessAsync(arguments, standardInput, cancellationToken).ConfigureAwait(false);
    }

    private async Task<AdbCommandResult> RunProcessAsync(
        IReadOnlyList<string> arguments,
        string? standardInput,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        await using var process = StartProcess(arguments);

        try
        {
            using var outputReader = new StreamReader(process.StandardOutput);
            using var errorReader = new StreamReader(process.StandardError);
            var outputTask = outputReader.ReadToEndAsync(cancellationToken);
            var errorTask = errorReader.ReadToEndAsync(cancellationToken);
            if (standardInput is null)
            {
                process.StandardInput.Dispose();
            }
            else
            {
                await using var inputWriter = new StreamWriter(
                    process.StandardInput,
                    new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                await inputWriter.WriteAsync(standardInput.AsMemory(), cancellationToken).ConfigureAwait(false);
            }
            var exitCode = await process.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
            return new AdbCommandResult(exitCode, outputTask.Result, errorTask.Result);
        }
        catch (OperationCanceledException)
        {
            process.Terminate(force: true);
            throw;
        }
    }

    public IAdbProcess StartProcess(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return processLauncher.Start(new AdbProcessStartRequest(AdbPath, arguments));
    }

    private static string? GetValue(IReadOnlyDictionary<string, string> values, string key)
        => values.TryGetValue(key, out var value) ? value : null;

    private static void ValidateDeviceSerial(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !SafeIdentifierPattern().IsMatch(value))
        {
            throw new ArgumentException("The ADB device serial is invalid.", nameof(value));
        }
    }

    private static void ValidatePackageIdentifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !SafeIdentifierPattern().IsMatch(value))
        {
            throw new ArgumentException("The Android package identifier is invalid.", nameof(value));
        }
    }

    private static void ValidateCoordinate(double latitude, double longitude)
    {
        if (!double.IsFinite(latitude) || latitude is < -90d or > 90d)
        {
            throw new ArgumentOutOfRangeException(nameof(latitude), "Latitude must be between -90 and 90.");
        }

        if (!double.IsFinite(longitude) || longitude is < -180d or > 180d)
        {
            throw new ArgumentOutOfRangeException(nameof(longitude), "Longitude must be between -180 and 180.");
        }
    }

    private static bool IsValidBufferName(string value)
        => !string.IsNullOrWhiteSpace(value) && SafeIdentifierPattern().IsMatch(value);



    private static bool ReportsActivityStartFailure(AdbCommandResult result)
        => result.StandardOutput.Contains("Error:", StringComparison.OrdinalIgnoreCase)
           || result.StandardOutput.Contains("unable to resolve Intent", StringComparison.OrdinalIgnoreCase)
           || result.StandardError.Contains("Error:", StringComparison.OrdinalIgnoreCase)
           || result.StandardError.Contains("unable to resolve Intent", StringComparison.OrdinalIgnoreCase);

    private static bool IsValidLaunchComponent(string value, string packageIdentifier)
    {
        var separatorIndex = value.IndexOf('/');
        return separatorIndex == packageIdentifier.Length
               && value.StartsWith(packageIdentifier, StringComparison.Ordinal)
               && separatorIndex < value.Length - 1
               && SafeActivityNamePattern().IsMatch(value[(separatorIndex + 1)..]);
    }

    private static string QuoteShellArgument(string value)
        => $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";

    private static string BuildFailureMessage(string operation, AdbCommandResult result)
    {
        var detail = string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardOutput.Trim()
            : result.StandardError.Trim();
        return string.IsNullOrWhiteSpace(detail)
            ? $"ADB failed to {operation} with exit code {result.ExitCode}."
            : $"ADB failed to {operation}: {detail}";
    }

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespacePattern();

    [GeneratedRegex(@"^[A-Za-z0-9._:\-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeIdentifierPattern();

    [GeneratedRegex(@"^[A-Za-z0-9._$]+$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeActivityNamePattern();
}
