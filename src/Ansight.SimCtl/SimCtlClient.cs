using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ansight.SimCtl;

public sealed partial class SimCtlClient
{
    private readonly SimCtlToolResolution toolResolution;
    private readonly ISimCtlCommandRunner commandRunner;
    private readonly ISimCtlProcessLauncher processLauncher;

    public SimCtlClient(
        SimCtlToolResolution toolResolution,
        ISimCtlCommandRunner? commandRunner = null,
        ISimCtlProcessLauncher? processLauncher = null)
    {
        ArgumentNullException.ThrowIfNull(toolResolution);
        if (!toolResolution.IsFound)
        {
            throw new ArgumentException("A resolved SimCtl installation is required.", nameof(toolResolution));
        }

        this.toolResolution = toolResolution;
        this.commandRunner = commandRunner ?? new DotNetSimCtlCommandRunner();
        this.processLauncher = processLauncher ?? SimCtlProcessLauncher.Create();
    }

    public string DeveloperDirectory => toolResolution.DeveloperDirectory;

    public Task<SimCtlCommandResult> GetVersionAsync(CancellationToken cancellationToken = default)
        => RunXcrunAsync(["simctl", "help"], cancellationToken);

    public async Task<IReadOnlyList<SimCtlDevice>> GetDevicesAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunXcrunAsync(["simctl", "list", "--json", "devices"], cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException(BuildFailureMessage("enumerate simulators", result));
        }

        using var document = JsonDocument.Parse(result.StandardOutput);
        if (!document.RootElement.TryGetProperty("devices", out var runtimes)
            || runtimes.ValueKind != JsonValueKind.Object)
        {
            return Array.Empty<SimCtlDevice>();
        }

        var devices = new List<SimCtlDevice>();
        foreach (var runtime in runtimes.EnumerateObject())
        {
            if (runtime.Value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var value in runtime.Value.EnumerateArray())
            {
                var udid = GetString(value, "udid");
                if (string.IsNullOrWhiteSpace(udid))
                {
                    continue;
                }

                var lastBootedValue = GetString(value, "lastBootedAt");
                devices.Add(new SimCtlDevice(
                    udid,
                    GetString(value, "name"),
                    GetString(value, "state"),
                    GetBoolean(value, "isAvailable", defaultValue: true),
                    runtime.Name,
                    DateTimeOffset.TryParse(lastBootedValue, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var lastBooted)
                        ? lastBooted.ToUniversalTime()
                        : null,
                    GetString(value, "dataPath")));
            }
        }

        return devices;
    }

    public async Task<IReadOnlyList<SimCtlInstalledApplication>> GetInstalledApplicationsAsync(
        string deviceUdid,
        CancellationToken cancellationToken = default)
    {
        ValidateUdid(deviceUdid);
        var result = await RunXcrunAsync(
            ["simctl", "listapps", deviceUdid],
            cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException(BuildFailureMessage("list installed simulator apps", result));
        }

        return ParseInstalledApplications(result.StandardOutput);
    }

    public async Task BootAsync(
        string deviceUdid,
        CancellationToken cancellationToken = default)
    {
        ValidateUdid(deviceUdid);
        var result = await RunXcrunAsync(["simctl", "boot", deviceUdid], cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException(BuildFailureMessage("start the simulator", result));
        }
    }

    public async Task WaitForBootAsync(
        string deviceUdid,
        CancellationToken cancellationToken = default)
    {
        ValidateUdid(deviceUdid);
        var result = await RunXcrunAsync(
            ["simctl", "bootstatus", deviceUdid, "-b"],
            cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException(
                BuildFailureMessage("wait for the simulator to finish booting", result));
        }
    }

    public async Task ShowSimulatorAsync(
        string deviceUdid,
        CancellationToken cancellationToken = default)
    {
        ValidateUdid(deviceUdid);
        cancellationToken.ThrowIfCancellationRequested();
        var simulatorApplicationPath = Path.Combine(
            toolResolution.DeveloperDirectory,
            "Applications",
            "Simulator.app");
        await using var process = processLauncher.Start(new SimCtlProcessStartRequest(
            "/usr/bin/open",
            toolResolution.DeveloperDirectory,
            [
                "-a",
                simulatorApplicationPath,
                "--args",
                "-CurrentDeviceUDID",
                deviceUdid
            ]));
        process.StandardInput.Dispose();
        using var outputReader = new StreamReader(process.StandardOutput);
        using var errorReader = new StreamReader(process.StandardError);
        var outputTask = outputReader.ReadToEndAsync(cancellationToken);
        var errorTask = errorReader.ReadToEndAsync(cancellationToken);
        var exitCode = await process.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        if (exitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(error) ? output.Trim() : error.Trim();
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(detail)
                    ? $"Simulator.app could not be shown (exit code {exitCode})."
                    : $"Simulator.app could not be shown: {detail}");
        }
    }

    public async Task ShutdownAsync(
        string deviceUdid,
        CancellationToken cancellationToken = default)
    {
        ValidateUdid(deviceUdid);
        var result = await RunXcrunAsync(["simctl", "shutdown", deviceUdid], cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException(BuildFailureMessage("stop the simulator", result));
        }
    }

    public async Task LaunchApplicationAsync(
        string deviceUdid,
        string bundleIdentifier,
        CancellationToken cancellationToken = default)
    {
        ValidateUdid(deviceUdid);
        ValidateBundleIdentifier(bundleIdentifier);
        var result = await RunXcrunAsync(
            ["simctl", "launch", deviceUdid, bundleIdentifier],
            cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException(BuildFailureMessage("launch the simulator app", result));
        }
    }

    public async Task InstallApplicationAsync(
        string deviceUdid,
        string applicationBundlePath,
        CancellationToken cancellationToken = default)
    {
        ValidateUdid(deviceUdid);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationBundlePath);
        var fullBundlePath = Path.GetFullPath(applicationBundlePath);
        if (!Directory.Exists(fullBundlePath)
            || !Path.GetExtension(fullBundlePath).Equals(".app", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("An existing .app bundle is required.", nameof(applicationBundlePath));
        }

        var result = await RunXcrunAsync(
            ["simctl", "install", deviceUdid, fullBundlePath],
            cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException(
                BuildFailureMessage("install the simulator app", result));
        }
    }

    public async Task TerminateApplicationAsync(
        string deviceUdid,
        string bundleIdentifier,
        CancellationToken cancellationToken = default)
    {
        ValidateUdid(deviceUdid);
        ValidateBundleIdentifier(bundleIdentifier);
        var result = await RunXcrunAsync(
            ["simctl", "terminate", deviceUdid, bundleIdentifier],
            cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess && !ReportsApplicationNotRunning(result))
        {
            throw new InvalidOperationException(BuildFailureMessage("terminate the simulator app", result));
        }
    }

    internal static bool ReportsApplicationNotRunning(SimCtlCommandResult result)
    {
        var output = string.Concat(result.StandardOutput, "\n", result.StandardError);
        return output.Contains("is not running", StringComparison.OrdinalIgnoreCase)
               || output.Contains("nothing to terminate", StringComparison.OrdinalIgnoreCase)
               || output.Contains("no such process", StringComparison.OrdinalIgnoreCase);
    }

    public async Task SetLocationAsync(
        string deviceUdid,
        double latitude,
        double longitude,
        CancellationToken cancellationToken = default)
    {
        ValidateUdid(deviceUdid);
        ValidateCoordinate(latitude, longitude);
        var coordinate = string.Create(
            CultureInfo.InvariantCulture,
            $"{latitude:R},{longitude:R}");
        var result = await RunXcrunAsync(
            ["simctl", "location", deviceUdid, "set", coordinate],
            cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException(
                BuildFailureMessage("set the simulator location", result));
        }
    }

    public async Task ClearLocationAsync(
        string deviceUdid,
        CancellationToken cancellationToken = default)
    {
        ValidateUdid(deviceUdid);
        var result = await RunXcrunAsync(
            ["simctl", "location", deviceUdid, "clear"],
            cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException(
                BuildFailureMessage("clear the simulator location", result));
        }
    }

    public async Task<bool> ProcessExistsAsync(
        string deviceUdid,
        int processId,
        CancellationToken cancellationToken = default)
    {
        ValidateUdid(deviceUdid);
        if (processId <= 0)
        {
            return false;
        }

        var result = await RunXcrunAsync(
            ["simctl", "spawn", deviceUdid, "launchctl", "procinfo", processId.ToString(CultureInfo.InvariantCulture)],
            cancellationToken).ConfigureAwait(false);
        return IsProcessInfoAvailable(result, deviceUdid);
    }

    public async Task<byte[]> CaptureScreenshotJpegAsync(
        string deviceUdid,
        CancellationToken cancellationToken = default)
    {
        ValidateUdid(deviceUdid);
        var filePath = Path.Combine(
            Path.GetTempPath(),
            $"ansight-simctl-{Guid.NewGuid():N}.jpg");
        try
        {
            var result = await RunXcrunAsync(
                ["simctl", "io", deviceUdid, "screenshot", "--type=jpeg", filePath],
                cancellationToken).ConfigureAwait(false);
            if (!result.IsSuccess || !File.Exists(filePath))
            {
                throw new InvalidOperationException(BuildFailureMessage("capture a simulator screenshot", result));
            }

            return await File.ReadAllBytesAsync(filePath, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                File.Delete(filePath);
            }
            catch (IOException)
            {
                // This exception is an expected fallback for the best-effort operation.
            }
            catch (UnauthorizedAccessException)
            {
                // This exception is an expected fallback for the best-effort operation.
            }
        }
    }

    internal static bool IsProcessInfoAvailable(SimCtlCommandResult result, string deviceUdid)
    {
        var output = string.Concat(result.StandardOutput, "\n", result.StandardError);
        return result.IsSuccess
               && !output.Contains("No such process", StringComparison.OrdinalIgnoreCase)
               && !output.Contains("could not resolve path", StringComparison.OrdinalIgnoreCase)
               && output.Contains("program path =", StringComparison.OrdinalIgnoreCase)
               && (output.Contains($"SIMULATOR_UDID => {deviceUdid}", StringComparison.OrdinalIgnoreCase)
                   || output.Contains($"/CoreSimulator/Devices/{deviceUdid}/", StringComparison.OrdinalIgnoreCase));
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

    private static void ValidateBundleIdentifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !SafeIdentifierPattern().IsMatch(value))
        {
            throw new ArgumentException("The app bundle identifier is invalid.", nameof(value));
        }
    }

    internal static IReadOnlyList<SimCtlInstalledApplication> ParseInstalledApplications(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        var applications = new List<SimCtlInstalledApplication>();
        string? entryBundleIdentifier = null;
        string bundleIdentifier = string.Empty;
        string displayName = string.Empty;
        string bundleName = string.Empty;
        string bundlePath = string.Empty;
        string version = string.Empty;
        string buildVersion = string.Empty;
        string applicationType = string.Empty;
        var dictionaryDepth = 0;

        foreach (var line in output.Split('\n'))
        {
            if (entryBundleIdentifier is null)
            {
                var entryMatch = AppEntryPattern().Match(line);
                if (!entryMatch.Success)
                {
                    continue;
                }

                entryBundleIdentifier = UnquoteOpenStepValue(entryMatch.Groups["key"].Value);
                bundleIdentifier = string.Empty;
                displayName = string.Empty;
                bundleName = string.Empty;
                bundlePath = string.Empty;
                version = string.Empty;
                buildVersion = string.Empty;
                applicationType = string.Empty;
                dictionaryDepth = 1;
                continue;
            }

            if (dictionaryDepth == 1)
            {
                var propertyMatch = AppPropertyPattern().Match(line);
                if (propertyMatch.Success)
                {
                    var value = UnquoteOpenStepValue(propertyMatch.Groups["value"].Value);
                    switch (propertyMatch.Groups["key"].Value)
                    {
                        case "ApplicationType":
                            applicationType = value;
                            break;
                        case "CFBundleDisplayName":
                            displayName = value;
                            break;
                        case "CFBundleIdentifier":
                            bundleIdentifier = value;
                            break;
                        case "CFBundleName":
                            bundleName = value;
                            break;
                        case "Bundle":
                        case "Path":
                            bundlePath = value;
                            break;
                        case "CFBundleShortVersionString":
                            version = value;
                            break;
                        case "CFBundleVersion":
                            buildVersion = value;
                            break;
                    }
                }
            }

            dictionaryDepth += CountDictionaryDepthDelta(line);
            if (dictionaryDepth > 0)
            {
                continue;
            }

            if (string.Equals(applicationType, "User", StringComparison.OrdinalIgnoreCase))
            {
                applications.Add(new SimCtlInstalledApplication(
                    string.IsNullOrWhiteSpace(bundleIdentifier)
                        ? entryBundleIdentifier
                        : bundleIdentifier,
                    displayName,
                    bundleName,
                    string.IsNullOrWhiteSpace(bundlePath) ? null : bundlePath,
                    string.IsNullOrWhiteSpace(version) ? null : version,
                    string.IsNullOrWhiteSpace(buildVersion) ? null : buildVersion));
            }

            entryBundleIdentifier = null;
        }

        return applications
            .DistinctBy(static application => application.BundleIdentifier, StringComparer.OrdinalIgnoreCase)
            .OrderBy(static application => application.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public Task<SimCtlLogStream> StartLogStreamAsync(
        SimCtlLogStreamRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateUdid(request.DeviceUdid);
        if (request.ProcessId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "A positive process ID is required.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var level = request.Level.Trim().ToLowerInvariant();
        if (level is not ("default" or "info" or "debug"))
        {
            throw new ArgumentException("The SimCtl log level must be default, info, or debug.", nameof(request));
        }

        var process = processLauncher.Start(new SimCtlProcessStartRequest(
            toolResolution.XcrunPath,
            toolResolution.DeveloperDirectory,
            [
                "simctl",
                "spawn",
                request.DeviceUdid,
                "log",
                "stream",
                "--style",
                "ndjson",
                "--color",
                "none",
                "--level",
                level,
                "--process",
                request.ProcessId.ToString(CultureInfo.InvariantCulture),
                "--type",
                "log"
            ]));
        process.StandardInput.Dispose();

        return Task.FromResult(new SimCtlLogStream(process));
    }

    public async Task<SimCtlCommandResult> RunXcrunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return await commandRunner.RunAsync(toolResolution, arguments, cancellationToken).ConfigureAwait(false);
    }

    private static string GetString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;

    private static bool GetBoolean(JsonElement element, string propertyName, bool defaultValue)
        => element.TryGetProperty(propertyName, out var property) && property.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? property.GetBoolean()
            : defaultValue;

    private static string UnquoteOpenStepValue(string value)
    {
        var trimmedValue = value.Trim();
        if (trimmedValue.Length < 2 || trimmedValue[0] != '"' || trimmedValue[^1] != '"')
        {
            return trimmedValue;
        }

        return trimmedValue[1..^1]
            .Replace("\\\"", "\"", StringComparison.Ordinal)
            .Replace("\\\\", "\\", StringComparison.Ordinal);
    }

    private static int CountDictionaryDepthDelta(string line)
    {
        var depthDelta = 0;
        var isQuoted = false;
        var isEscaped = false;
        foreach (var character in line)
        {
            if (isEscaped)
            {
                isEscaped = false;
                continue;
            }
            if (isQuoted && character == '\\')
            {
                isEscaped = true;
                continue;
            }
            if (character == '"')
            {
                isQuoted = !isQuoted;
                continue;
            }
            if (isQuoted)
            {
                continue;
            }

            depthDelta += character switch
            {
                '{' => 1,
                '}' => -1,
                _ => 0
            };
        }

        return depthDelta;
    }

    private static void ValidateUdid(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !UdidPattern().IsMatch(value))
        {
            throw new ArgumentException("The simulator UDID is invalid.", nameof(value));
        }
    }

    private static string BuildFailureMessage(string operation, SimCtlCommandResult result)
    {
        var detail = string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardOutput.Trim()
            : result.StandardError.Trim();
        return string.IsNullOrWhiteSpace(detail)
            ? $"SimCtl failed to {operation} with exit code {result.ExitCode}."
            : $"SimCtl failed to {operation}: {detail}";
    }

    [GeneratedRegex(@"^[A-Fa-f0-9]{8}-[A-Fa-f0-9]{4}-[A-Fa-f0-9]{4}-[A-Fa-f0-9]{4}-[A-Fa-f0-9]{12}$", RegexOptions.CultureInvariant)]
    private static partial Regex UdidPattern();

    [GeneratedRegex(@"^[A-Za-z0-9._\-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeIdentifierPattern();

    [GeneratedRegex(@"^\s*(?<key>""(?:\\.|[^""])*""|[^\s=]+)\s*=\s*\{\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex AppEntryPattern();

    [GeneratedRegex(@"^\s*(?<key>[A-Za-z][A-Za-z0-9]*)\s*=\s*(?<value>.*);\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex AppPropertyPattern();
}
