using System.Diagnostics;
using System.Text.Json;
using Ansight.SimCtl;

namespace Ansight.Host.Devices;

internal sealed class CoreDeviceClient
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(45);
    private readonly SimCtlToolResolution toolResolution;

    public CoreDeviceClient(SimCtlToolResolution toolResolution)
    {
        ArgumentNullException.ThrowIfNull(toolResolution);
        if (!toolResolution.IsFound)
        {
            throw new ArgumentException("A resolved Xcode command-line toolchain is required.", nameof(toolResolution));
        }

        this.toolResolution = toolResolution;
    }

    public async Task<IReadOnlyList<CoreDeviceDescriptor>> GetDevicesAsync(
        CancellationToken cancellationToken = default)
    {
        using var document = await RunJsonAsync(
            ["list", "devices"],
            "enumerate physical Apple devices",
            cancellationToken).ConfigureAwait(false);
        return ParseDevices(document.RootElement);
    }

    internal static IReadOnlyList<CoreDeviceDescriptor> ParseDevices(JsonElement root)
    {
        if (!TryFindArray(root, "devices", out var devices))
        {
            return [];
        }

        var results = new List<CoreDeviceDescriptor>();
        foreach (var device in devices.EnumerateArray())
        {
            var identifier = FirstNonEmpty(
                FindString(device, "udid"),
                FindString(device, "identifier"),
                FindString(device, "serialNumber"));
            if (identifier is null)
            {
                continue;
            }

            var pairingState = FindString(device, "pairingState");
            var tunnelState = FindString(device, "tunnelState");
            var developerMode = FindString(device, "developerModeStatus");
            var isPaired = string.IsNullOrWhiteSpace(pairingState)
                           || string.Equals(pairingState, "paired", StringComparison.OrdinalIgnoreCase);
            var isConnected = string.IsNullOrWhiteSpace(tunnelState)
                              || string.Equals(tunnelState, "connected", StringComparison.OrdinalIgnoreCase);
            var isDeveloperModeEnabled = string.IsNullOrWhiteSpace(developerMode)
                                         || string.Equals(developerMode, "enabled", StringComparison.OrdinalIgnoreCase);
            var state = !isPaired
                ? "unpaired"
                : !isConnected
                    ? tunnelState ?? "disconnected"
                    : !isDeveloperModeEnabled
                        ? "developer-mode-disabled"
                        : "connected";
            results.Add(new CoreDeviceDescriptor(
                identifier,
                FirstNonEmpty(FindDirectString(device, "deviceProperties", "name"),
                    FindString(device, "name"), identifier)!,
                FindString(device, "platform") ?? string.Empty,
                FindString(device, "productType") ?? string.Empty,
                FirstNonEmpty(
                    FindString(device, "osVersionNumber"),
                    FindString(device, "operatingSystemVersion")) ?? string.Empty,
                state,
                isPaired && isConnected && isDeveloperModeEnabled));
        }

        return results
            .DistinctBy(static device => device.Identifier, StringComparer.OrdinalIgnoreCase)
            .OrderBy(static device => device.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<IReadOnlyList<InstalledApplication>> GetInstalledApplicationsAsync(
        string deviceIdentifier,
        CancellationToken cancellationToken = default)
    {
        ValidateDeviceIdentifier(deviceIdentifier);
        using var document = await RunJsonAsync(
            ["device", "info", "apps", "--device", deviceIdentifier, "--include-removable-apps", "--include-default-apps"],
            "list physical-device applications",
            cancellationToken).ConfigureAwait(false);
        return ParseApplications(document.RootElement);
    }

    public async Task<string?> GetRunningApplicationProcessIdentityAsync(
        string deviceIdentifier, string bundleIdentifier, string bundlePath,
        CancellationToken cancellationToken = default)
    {
        ValidateDeviceIdentifier(deviceIdentifier);
        ValidateBundleIdentifier(bundleIdentifier);
        ArgumentNullException.ThrowIfNull(bundlePath);
        using var document = await RunJsonAsync(
            ["device", "info", "processes", "--device", deviceIdentifier],
            "list physical-device processes", cancellationToken).ConfigureAwait(false);
        return ParseRunningApplicationProcessIdentity(document.RootElement, bundleIdentifier, bundlePath);
    }

    public Task<string?> GetRunningApplicationProcessIdentityAsync(
        string deviceIdentifier, string bundleIdentifier, CancellationToken cancellationToken = default)
        => GetRunningApplicationProcessIdentityAsync(
            deviceIdentifier, bundleIdentifier, string.Empty, cancellationToken);

    internal static string? ParseRunningApplicationProcessIdentity(
        JsonElement root, string bundleIdentifier, string bundlePath)
    {
        if (!TryFindArray(root, "runningProcesses", out var processes)
            && !TryFindArray(root, "processes", out processes)) return null;
        var normalizedBundlePath = NormalizeDevicePath(bundlePath).TrimEnd('/');
        var matches = processes.EnumerateArray().Where(process =>
        {
            var reportedBundle = FirstNonEmpty(
                FindString(process, "bundleIdentifier"),
                FindString(process, "executableObjectIdentifier"));
            var executable = FindString(process, "executable")
                             ?? FindString(process, "url")
                             ?? FindString(process, "path");
            if (executable is not null && normalizedBundlePath.Length > 0)
            {
                var normalizedExecutable = NormalizeDevicePath(executable);
                if (normalizedExecutable.StartsWith(normalizedBundlePath + "/", StringComparison.Ordinal))
                {
                    var relativeExecutable = normalizedExecutable[(normalizedBundlePath.Length + 1)..];
                    return relativeExecutable.Length > 0 && !relativeExecutable.Contains('/');
                }
            }
            return string.Equals(reportedBundle, bundleIdentifier, StringComparison.Ordinal);
        }).ToArray();
        if (matches.Length == 0) return null;
        if (matches.Length != 1) throw new IOException("The physical iOS app process could not be identified unambiguously.");
        var pid = FindInteger(matches[0], "processIdentifier") ?? FindInteger(matches[0], "pid");
        if (pid is null or <= 0) throw new IOException("The physical iOS app process has no valid PID.");
        var started = FirstNonEmpty(FindString(matches[0], "startDate"),
            FindString(matches[0], "startTime"));
        return started is null ? pid.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : pid.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + started;
    }

    private static string NormalizeDevicePath(string path)
    {
        var normalized = path.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            ? Uri.UnescapeDataString(path["file:".Length..]) : path;
        return normalized.StartsWith("/", StringComparison.Ordinal)
            ? "/" + normalized.TrimStart('/') : normalized;
    }

    internal static IReadOnlyList<InstalledApplication> ParseApplications(JsonElement root)
    {
        if (!TryFindArray(root, "apps", out var apps))
        {
            return [];
        }

        return apps.EnumerateArray()
            .Select(static app => new InstalledApplication(
                FirstNonEmpty(
                    FindString(app, "bundleIdentifier"),
                    FindString(app, "bundleID")) ?? string.Empty,
                FirstNonEmpty(
                    FindString(app, "name"),
                    FindString(app, "displayName"),
                    FindString(app, "bundleIdentifier")) ?? string.Empty,
                BundlePath: FirstNonEmpty(
                    FindString(app, "bundlePath"),
                    FindString(app, "bundleURL"),
                    FindString(app, "installationURL"),
                    FindString(app, "url"),
                    FindString(app, "path")),
                Version: FirstNonEmpty(
                    FindString(app, "version"),
                    FindString(app, "shortVersion"),
                    FindString(app, "bundleShortVersion"),
                    FindString(app, "CFBundleShortVersionString")),
                BuildVersion: FirstNonEmpty(
                    FindString(app, "bundleVersion"),
                    FindString(app, "buildVersion"),
                    FindString(app, "CFBundleVersion")),
                InstalledAtUtc: FindTimestamp(
                    app,
                    "installedDate",
                    "installationDate",
                    "installDate"),
                LastUpdatedAtUtc: FindTimestamp(
                    app,
                    "lastUpdatedDate",
                    "lastModifiedDate",
                    "updateDate")))
            .Where(static app => !string.IsNullOrWhiteSpace(app.Identifier))
            .DistinctBy(static app => app.Identifier, StringComparer.OrdinalIgnoreCase)
            .OrderBy(static app => app.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task InstallApplicationAsync(
        string deviceIdentifier,
        string applicationBundlePath,
        CancellationToken cancellationToken = default)
    {
        ValidateDeviceIdentifier(deviceIdentifier);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationBundlePath);
        var fullPath = Path.GetFullPath(applicationBundlePath);
        if (!Directory.Exists(fullPath)
            || !string.Equals(Path.GetExtension(fullPath), ".app", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("An extracted, signed .app bundle is required.", nameof(applicationBundlePath));
        }

        using var document = await RunJsonAsync(
            ["device", "install", "app", "--device", deviceIdentifier, fullPath],
            "install the physical-device app",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<int?> LaunchApplicationAsync(
        string deviceIdentifier,
        string bundleIdentifier,
        CancellationToken cancellationToken = default)
        => await LaunchApplicationAsync(
            deviceIdentifier,
            bundleIdentifier,
            environmentVariables: null,
            cancellationToken: cancellationToken).ConfigureAwait(false);

    public async Task<int?> LaunchApplicationAsync(
        string deviceIdentifier,
        string bundleIdentifier,
        IReadOnlyDictionary<string, string>? environmentVariables,
        CancellationToken cancellationToken = default)
    {
        ValidateDeviceIdentifier(deviceIdentifier);
        ValidateBundleIdentifier(bundleIdentifier);
        using var document = await RunJsonAsync(
            [
                "device", "process", "launch", "--device", deviceIdentifier,
                "--terminate-existing", bundleIdentifier
            ],
            "launch the physical-device app",
            cancellationToken,
            environmentVariables).ConfigureAwait(false);
        return FindInteger(document.RootElement, "processIdentifier")
               ?? FindInteger(document.RootElement, "pid");
    }

    public async Task TerminateApplicationAsync(
        string deviceIdentifier,
        string bundleIdentifier,
        int? knownProcessId = null,
        CancellationToken cancellationToken = default)
    {
        ValidateDeviceIdentifier(deviceIdentifier);
        ValidateBundleIdentifier(bundleIdentifier);
        var processId = knownProcessId ?? await FindProcessIdAsync(
                deviceIdentifier,
                bundleIdentifier,
                cancellationToken)
            .ConfigureAwait(false);
        if (!processId.HasValue)
        {
            return;
        }

        using var document = await RunJsonAsync(
            [
                "device", "process", "terminate", "--device", deviceIdentifier,
                "--pid", processId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
            ],
            "terminate the physical-device app",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<int?> FindProcessIdAsync(
        string deviceIdentifier,
        string bundleIdentifier,
        CancellationToken cancellationToken)
    {
        using var document = await RunJsonAsync(
            ["device", "info", "processes", "--device", deviceIdentifier],
            "list physical-device processes",
            cancellationToken).ConfigureAwait(false);
        if (!TryFindArray(document.RootElement, "processes", out var processes))
        {
            return null;
        }

        foreach (var process in processes.EnumerateArray())
        {
            var processBundleIdentifier = FirstNonEmpty(
                FindString(process, "bundleIdentifier"),
                FindString(process, "executableObjectIdentifier"));
            if (string.Equals(processBundleIdentifier, bundleIdentifier, StringComparison.Ordinal))
            {
                return FindInteger(process, "processIdentifier") ?? FindInteger(process, "pid");
            }
        }

        return null;
    }

    private async Task<JsonDocument> RunJsonAsync(
        IReadOnlyList<string> arguments,
        string operation,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? childEnvironmentVariables = null)
    {
        var jsonOutputPath = Path.Combine(
            Path.GetTempPath(),
            $"ansight-devicectl-{Guid.NewGuid():N}.json");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(DefaultTimeout);
            var startInfo = CreateProcessStartInfo(
                toolResolution,
                arguments,
                jsonOutputPath,
                childEnvironmentVariables);

            using var process = Process.Start(startInfo)
                                ?? throw new InvalidOperationException("Could not start xcrun devicectl.");
            var standardOutputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var standardErrorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var standardOutput = await standardOutputTask.ConfigureAwait(false);
            var standardError = await standardErrorTask.ConfigureAwait(false);
            if (process.ExitCode != 0 || !File.Exists(jsonOutputPath))
            {
                var detail = FirstNonEmpty(standardError, standardOutput);
                throw new InvalidOperationException(
                    detail is null
                        ? $"xcrun devicectl could not {operation} (exit code {process.ExitCode})."
                        : $"xcrun devicectl could not {operation}: {detail.Trim()}");
            }

            await using var stream = File.OpenRead(jsonOutputPath);
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"xcrun devicectl timed out while attempting to {operation}.");
        }
        finally
        {
            try
            {
                File.Delete(jsonOutputPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    internal static ProcessStartInfo CreateProcessStartInfo(
        SimCtlToolResolution resolution,
        IReadOnlyList<string> arguments,
        string jsonOutputPath,
        IReadOnlyDictionary<string, string>? childEnvironmentVariables = null)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(jsonOutputPath);
        var startInfo = new ProcessStartInfo
        {
            FileName = resolution.XcrunPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.Environment["DEVELOPER_DIR"] = resolution.DeveloperDirectory;
        if (childEnvironmentVariables is not null)
        {
            foreach (var variable in childEnvironmentVariables)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(variable.Key);
                startInfo.Environment[$"DEVICECTL_CHILD_{variable.Key.Trim()}"] = variable.Value;
            }
        }

        startInfo.ArgumentList.Add("devicectl");
        startInfo.ArgumentList.Add("--quiet");
        startInfo.ArgumentList.Add("--timeout");
        startInfo.ArgumentList.Add(((int)DefaultTimeout.TotalSeconds).ToString(
            System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--json-output");
        startInfo.ArgumentList.Add(jsonOutputPath);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static bool TryFindArray(JsonElement element, string propertyName, out JsonElement result)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.Array)
                {
                    result = property.Value;
                    return true;
                }

                if (TryFindArray(property.Value, propertyName, out result))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (TryFindArray(item, propertyName, out result))
                {
                    return true;
                }
            }
        }

        result = default;
        return false;
    }

    private static string? FindString(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.String)
                {
                    return property.Value.GetString();
                }

                var nested = FindString(property.Value, propertyName);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindString(item, propertyName);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }

        return null;
    }

    private static string? FindDirectString(JsonElement element, string parentProperty, string propertyName)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(parentProperty, out var parent)
           && parent.ValueKind == JsonValueKind.Object
           && parent.TryGetProperty(propertyName, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DateTimeOffset? FindTimestamp(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            var value = FindString(element, propertyName);
            if (DateTimeOffset.TryParse(
                    value,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind,
                    out var parsed))
            {
                return parsed.ToUniversalTime();
            }
        }

        return null;
    }

    private static int? FindInteger(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.Number
                    && property.Value.TryGetInt32(out var value))
                {
                    return value;
                }

                var nested = FindInteger(property.Value, propertyName);
                if (nested.HasValue)
                {
                    return nested;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindInteger(item, propertyName);
                if (nested.HasValue)
                {
                    return nested;
                }
            }
        }

        return null;
    }

    private static void ValidateDeviceIdentifier(string value)
        => ArgumentException.ThrowIfNullOrWhiteSpace(value);

    private static void ValidateBundleIdentifier(string value)
        => ArgumentException.ThrowIfNullOrWhiteSpace(value);

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim();
}
