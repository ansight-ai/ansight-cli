using Ansight.Host;
using System.Globalization;
using System.IO.Enumeration;

namespace Ansight.Cli.Commands.Device;

internal static class DeviceCommands
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

        var runtimeOptions = CliRuntime.ResolveOptions(arguments);
        var service = new DeviceService(CliRuntime.CreateHostOptions(runtimeOptions));
        if (IsImplicitListInvocation(arguments))
        {
            var exitCode = await ListAsync(service, arguments, output, cancellationToken);
            if (!output.IsJson)
            {
                output.WriteText(
                    $"Run 'ansight {arguments.Positionals[0]} help' to see available device actions and options.{Environment.NewLine}");
            }
            return exitCode;
        }

        var subcommand = arguments.RequirePositional(1, "device subcommand").ToLowerInvariant();
        return subcommand switch
        {
            "list" => await ListAsync(service, arguments, output, cancellationToken),
            "start" or "boot" => await RunOperationAsync(
                service.StartAsync(
                    arguments.RequirePositional(2, "platform"),
                    arguments.RequirePositional(3, "device identifier"),
                    new DeviceStartOptions(Headless: arguments.HasFlag("headless")),
                    cancellationToken),
                output),
            "shutdown" or "stop" => await RunOperationAsync(
                service.ShutdownAsync(
                    arguments.RequirePositional(2, "platform"),
                    arguments.RequirePositional(3, "device identifier"),
                    cancellationToken),
                output),
            "apps" => await ListApplicationsAsync(service, arguments, output, cancellationToken),
            "install" => await RunOperationAsync(
                service.InstallApplicationAsync(
                    arguments.RequirePositional(2, "platform"),
                    arguments.RequirePositional(3, "device identifier"),
                    arguments.RequirePositional(4, "application path"),
                    cancellationToken),
                output),
            "launch" => await RunOperationAsync(
                service.LaunchApplicationAsync(
                    arguments.RequirePositional(2, "platform"),
                    arguments.RequirePositional(3, "device identifier"),
                    arguments.RequirePositional(4, "application identifier"),
                    cancellationToken),
                output),
            "terminate" => await RunOperationAsync(
                service.TerminateApplicationAsync(
                    arguments.RequirePositional(2, "platform"),
                    arguments.RequirePositional(3, "device identifier"),
                    arguments.RequirePositional(4, "application identifier"),
                    cancellationToken),
                output),
            "location" => await RunLocationAsync(service, runtimeOptions, arguments, output, cancellationToken),
            "screenshot" => await RunScreenshotAsync(service, arguments, output, cancellationToken),
            _ => throw new CliUsageException(
                $"Unknown device subcommand '{subcommand}'. Expected list, start, shutdown, apps, install, launch, terminate, location, or screenshot.")
        };
    }

    private static bool IsImplicitListInvocation(CliArguments arguments)
        => arguments.Positionals.Count == 1;

    private static async Task<int> ListAsync(
        DeviceService service,
        CliArguments arguments,
        CliOutput output,
        CancellationToken cancellationToken)
    {
        var inventory = await service.ListAsync(cancellationToken).ConfigureAwait(false);
        var includeOffline = IncludesOfflineDevices(arguments);
        var applicationIdentifier = ResolveApplicationIdentifier(arguments);
        var applicationVersion = ResolveApplicationVersionFilter(arguments, applicationIdentifier);
        var applicationBuildVersion = ResolveApplicationMetadataFilter(
            arguments,
            "app-build",
            applicationIdentifier);
        var filteredDevices = FilterDevices(inventory.Devices, arguments);
        var applicationFilter = applicationIdentifier is null
            ? new DeviceApplicationFilterResult(filteredDevices, [], [], [])
            : await FilterDevicesByApplicationAsync(
                    filteredDevices,
                    applicationIdentifier,
                    async (device, token) =>
                    {
                        var application = await service.GetInstalledApplicationAsync(
                                device.Platform,
                                device.Identifier,
                                applicationIdentifier,
                                token)
                            .ConfigureAwait(false);
                        return application is null
                            ? Array.Empty<InstalledApplication>()
                            : [application];
                    },
                    cancellationToken,
                    applicationVersion,
                    applicationBuildVersion)
                .ConfigureAwait(false);
        if (arguments.IsVerbose)
        {
            WriteApplicationInspectionDiagnostics(output, applicationFilter.Failures);
        }

        var filteredInventory = inventory with
        {
            Devices = applicationFilter.Devices,
            Warnings = inventory.Warnings.Concat(applicationFilter.Warnings).ToArray()
        };
        output.Write(
            new DeviceInventoryOutput(
                "ansight.devices/v1",
                filteredInventory.Capabilities,
                filteredInventory.Devices,
                filteredInventory.Warnings,
                applicationIdentifier,
                applicationVersion,
                applicationBuildVersion,
                applicationIdentifier is null ? null : applicationFilter.Matches),
            () => Environment.NewLine + RenderInventory(
                filteredInventory,
                includeOffline,
                applicationIdentifier,
                applicationFilter.Matches) + Environment.NewLine);
        return CliExitCodes.Success;
    }

    internal static IReadOnlyList<DeviceDescriptor> FilterDevices(
        IReadOnlyList<DeviceDescriptor> devices,
        CliArguments arguments)
    {
        var includeAndroid = arguments.HasFlag("android");
        var includeIos = arguments.HasFlag("ios");
        var hasPlatformFilter = includeAndroid || includeIos;

        var includeEmulators = HasAnyFlag(arguments, "emulator", "emulators");
        var includeSimulators = HasAnyFlag(arguments, "simulator", "simulators");
        var includePhysicalDevices = HasAnyFlag(arguments, "physical", "physical-devices");
        var hasKindFilter = includeEmulators || includeSimulators || includePhysicalDevices;
        var includePhones = HasAnyFlag(arguments, "phone", "phones");
        var includeTablets = HasAnyFlag(arguments, "tablet", "tablets");
        var hasFormFactorFilter = includePhones || includeTablets;
        var includeOffline = IncludesOfflineDevices(arguments);

        return devices
            .Where(device => includeOffline || device.IsBooted)
            .Where(device => !hasPlatformFilter
                             || (includeAndroid && IsPlatform(device, DevicePlatforms.Android))
                             || (includeIos && IsPlatform(device, DevicePlatforms.Ios)))
            .Where(device => !hasKindFilter
                             || (includeEmulators && IsAndroidVirtualDevice(device))
                             || (includeSimulators && IsIosVirtualDevice(device))
                             || (includePhysicalDevices && device.IsPhysical))
            .Where(device => !hasFormFactorFilter
                             || (includePhones && DeviceFormFactors.IsPhone(device.FormFactor))
                             || (includeTablets && DeviceFormFactors.IsTablet(device.FormFactor)))
            .ToArray();
    }

    internal static async Task<DeviceApplicationFilterResult> FilterDevicesByApplicationAsync(
        IReadOnlyList<DeviceDescriptor> devices,
        string applicationIdentifier,
        Func<DeviceDescriptor, CancellationToken, Task<IReadOnlyList<InstalledApplication>>>
            listApplicationsAsync,
        CancellationToken cancellationToken,
        string? applicationVersion = null,
        string? applicationBuildVersion = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationIdentifier);
        ArgumentNullException.ThrowIfNull(listApplicationsAsync);

        var matches = new bool[devices.Count];
        var matchedApplications = new InstalledApplication?[devices.Count];
        var failures = new DeviceApplicationInspectionFailure?[devices.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, devices.Count),
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = 4
            },
            async (index, token) =>
            {
                var device = devices[index];
                var unavailableReason = GetApplicationInspectionUnavailableReason(device);
                if (unavailableReason is not null)
                {
                    failures[index] = CreateApplicationInspectionFailure(device, unavailableReason);
                    return;
                }

                try
                {
                    var applications = await listApplicationsAsync(device, token).ConfigureAwait(false);
                    var application = applications.FirstOrDefault(application => string.Equals(
                        application.Identifier,
                        applicationIdentifier,
                        StringComparison.OrdinalIgnoreCase));
                    if (application is not null
                        && MatchesApplicationMetadata(
                            application,
                            applicationVersion,
                            applicationBuildVersion))
                    {
                        matches[index] = true;
                        matchedApplications[index] = application;
                    }
                }
                catch (Exception exception) when (IsApplicationInspectionFailure(exception))
                {
                    failures[index] = CreateApplicationInspectionFailure(
                        device,
                        NormalizeWarningMessage(exception.Message));
                }
            }).ConfigureAwait(false);

        var matchingDevices = Enumerable.Range(0, devices.Count)
            .Where(index => matches[index])
            .Select(index => devices[index])
            .ToArray();
        var applicationMatches = Enumerable.Range(0, devices.Count)
            .Where(index => matches[index] && matchedApplications[index] is not null)
            .Select(index => new DeviceApplicationMatchOutput(
                devices[index].Identifier,
                matchedApplications[index]!))
            .ToArray();
        var inspectionFailures = failures
            .OfType<DeviceApplicationInspectionFailure>()
            .ToArray();
        if (inspectionFailures.Length == 0)
        {
            return new DeviceApplicationFilterResult(matchingDevices, [], [], applicationMatches);
        }

        var deviceLabel = inspectionFailures.Length == 1 ? "device" : "devices";
        var devicePronoun = inspectionFailures.Length == 1 ? "it" : "them";
        return new DeviceApplicationFilterResult(
            matchingDevices,
            [
                $"Application search skipped {inspectionFailures.Length} {deviceLabel} that could not be inspected. "
                + $"Results may be incomplete; start or reconnect {devicePronoun} and retry. "
                + "Pass --verbose for details."
            ],
            inspectionFailures,
            applicationMatches);
    }

    private static string? GetApplicationInspectionUnavailableReason(DeviceDescriptor device)
    {
        if (device.IsBooted || IsIosVirtualDevice(device))
        {
            return null;
        }

        return device.IsPhysical
            ? "Device is not connected. Reconnect it to inspect installed applications."
            : "Device is not running. Start it to inspect installed applications.";
    }

    private static DeviceApplicationInspectionFailure CreateApplicationInspectionFailure(
        DeviceDescriptor device,
        string message)
        => new(device.Name, device.Identifier, message);

    private static void WriteApplicationInspectionDiagnostics(
        CliOutput output,
        IReadOnlyList<DeviceApplicationInspectionFailure> failures)
    {
        foreach (var failure in failures)
        {
            output.WriteProgress(
                $"[device.apps] {failure.DeviceName} ({failure.DeviceIdentifier}): {failure.Message}");
        }
    }

    private static bool HasAnyFlag(CliArguments arguments, params string[] names)
        => names.Any(arguments.HasFlag);

    private static bool IncludesOfflineDevices(CliArguments arguments)
        => HasAnyFlag(arguments, "offline", "all");

    private static string? ResolveApplicationIdentifier(CliArguments arguments)
    {
        if (!arguments.HasFlag("app-id"))
        {
            return null;
        }

        var applicationIdentifier = arguments.RequireOption("app-id").Trim();
        if (applicationIdentifier.Length == 0)
        {
            throw new CliUsageException("--app-id must be a non-empty bundle or package identifier.");
        }

        return applicationIdentifier;
    }

    private static string? ResolveApplicationMetadataFilter(
        CliArguments arguments,
        string optionName,
        string? applicationIdentifier)
    {
        if (!arguments.HasFlag(optionName))
        {
            return null;
        }

        if (applicationIdentifier is null)
        {
            throw new CliUsageException($"--{optionName} requires --app-id <id>.");
        }

        var value = arguments.RequireOption(optionName).Trim();
        return value.Length == 0
            ? throw new CliUsageException($"--{optionName} must be non-empty.")
            : value;
    }

    internal static string? ResolveApplicationVersionFilter(
        CliArguments arguments,
        string? applicationIdentifier)
    {
        var hasCanonicalOption = arguments.HasFlag("app-version");
        var hasAlias = arguments.HasFlag("version");
        if (hasCanonicalOption && hasAlias)
        {
            throw new CliUsageException(
                "Use either --app-version or its --version alias, not both.");
        }

        return ResolveApplicationMetadataFilter(
            arguments,
            hasAlias ? "version" : "app-version",
            applicationIdentifier);
    }

    private static bool MatchesApplicationMetadata(
        InstalledApplication application,
        string? applicationVersion,
        string? applicationBuildVersion)
        => (applicationVersion is null
            || MatchesApplicationMetadataPattern(application.Version, applicationVersion))
           && (applicationBuildVersion is null
               || MatchesApplicationMetadataPattern(
                   application.BuildVersion,
                   applicationBuildVersion));

    private static bool MatchesApplicationMetadataPattern(string? value, string pattern)
    {
        if (value is null)
        {
            return false;
        }

        return pattern.Contains('*') || pattern.Contains('?')
            ? FileSystemName.MatchesSimpleExpression(pattern, value, ignoreCase: true)
            : string.Equals(value, pattern, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsApplicationInspectionFailure(Exception exception)
        => exception is ArgumentException
            or IOException
            or InvalidOperationException
            or PlatformNotSupportedException
            or TimeoutException
            or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception;

    private static string NormalizeWarningMessage(string message)
        => string.Join(' ', message.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static bool IsAndroidVirtualDevice(DeviceDescriptor device)
        => IsPlatform(device, DevicePlatforms.Android) && device.IsVirtual;

    private static bool IsIosVirtualDevice(DeviceDescriptor device)
        => IsPlatform(device, DevicePlatforms.Ios) && device.IsVirtual;

    private static bool IsPlatform(DeviceDescriptor device, string platform)
        => string.Equals(device.Platform, platform, StringComparison.OrdinalIgnoreCase);

    private static async Task<int> ListApplicationsAsync(
        DeviceService service,
        CliArguments arguments,
        CliOutput output,
        CancellationToken cancellationToken)
    {
        var platform = arguments.RequirePositional(2, "platform");
        var deviceIdentifier = arguments.RequirePositional(3, "device identifier");
        var applications = await service.ListApplicationsAsync(
            platform,
            deviceIdentifier,
            cancellationToken).ConfigureAwait(false);
        output.Write(
            new DeviceApplicationsOutput(
                "ansight.device-applications/v1",
                platform,
                deviceIdentifier,
                applications),
            () => applications.Count == 0
                ? "No user applications found."
                : string.Join(
                    Environment.NewLine,
                    applications.Select(application =>
                        $"{application.Identifier}\t{application.Name}"
                        + RenderApplicationMetadata(application))));
        return CliExitCodes.Success;
    }

    private static async Task<int> RunLocationAsync(
        DeviceService service,
        CliRuntimeOptions runtimeOptions,
        CliArguments arguments,
        CliOutput output,
        CancellationToken cancellationToken)
    {
        var action = arguments.RequirePositional(2, "location action").ToLowerInvariant();
        if (action is "play" or "replay")
        {
            return await PlayLocationRouteAsync(runtimeOptions, arguments, output, cancellationToken)
                .ConfigureAwait(false);
        }

        if (action is "status")
        {
            arguments.EnsurePositionalCount(3, "ansight device location status");
            await using var lease = await CliRuntimeLease.CreateAsync(
                runtimeOptions,
                start: false,
                cancellationToken).ConfigureAwait(false);
            var snapshot = lease.Runtime.DeviceLocationPlayback.GetSnapshot();
            return WriteLocationPlayback(snapshot, output);
        }

        if (action is "stop")
        {
            arguments.EnsurePositionalCount(3, "ansight device location stop");
            await using var lease = await CliRuntimeLease.CreateAsync(
                runtimeOptions,
                start: false,
                cancellationToken).ConfigureAwait(false);
            var result = await lease.Runtime.DeviceLocationPlayback.StopAsync(cancellationToken)
                .ConfigureAwait(false);
            output.Write(
                new DeviceLocationPlaybackOperationOutput(
                    "ansight.device-location-playback-operation/v1",
                    result,
                    lease.Runtime.DeviceLocationPlayback.GetSnapshot()),
                () => result.Message);
            return result.IsSuccess ? CliExitCodes.Success : CliExitCodes.Failure;
        }

        var platform = arguments.RequirePositional(3, "platform");
        var deviceIdentifier = arguments.RequirePositional(4, "device identifier");
        return action switch
        {
            "set" => await RunOperationAsync(
                service.SetLocationAsync(
                    platform,
                    deviceIdentifier,
                    arguments.GetDoubleOption("latitude"),
                    arguments.GetDoubleOption("longitude"),
                    cancellationToken),
                output).ConfigureAwait(false),
            "clear" => await RunOperationAsync(
                service.ClearLocationAsync(platform, deviceIdentifier, cancellationToken),
                output).ConfigureAwait(false),
            _ => throw new CliUsageException(
                $"Unknown location action '{action}'. Expected set, clear, play, status, or stop.")
        };
    }

    private static async Task<int> PlayLocationRouteAsync(
        CliRuntimeOptions runtimeOptions,
        CliArguments arguments,
        CliOutput output,
        CancellationToken cancellationToken)
    {
        arguments.EnsurePositionalCount(
            6,
            "ansight device location play <ios|android> <device-id> <route.gpx|route.kml> [options]");
        var routePath = Path.GetFullPath(arguments.RequirePositional(5, "route file"));
        if (!File.Exists(routePath))
        {
            throw new CliUsageException($"Route file '{routePath}' was not found.");
        }

        var mode = arguments.GetOption("mode")?.Trim().ToLowerInvariant() switch
        {
            null or "" or "recorded" or "recorded-timing" => DeviceLocationPlaybackMode.RecordedTiming,
            "fixed" or "fixed-speed" => DeviceLocationPlaybackMode.FixedSpeed,
            _ => throw new CliUsageException("--mode must be recorded or fixed-speed.")
        };
        var speedMultiplier = ParsePositiveDouble(arguments.GetOption("speed"), 1d, "speed");
        var fixedSpeedKph = ParsePositiveDouble(
            arguments.GetOption("fixed-speed-kph") ?? arguments.GetOption("speed-kph"),
            30d,
            "fixed-speed-kph");
        var hasResidentRuntime = CliCommandContext.Current is not null;
        await using var lease = await CliRuntimeLease.CreateAsync(
            runtimeOptions,
            start: false,
            cancellationToken).ConfigureAwait(false);
        var startResult = await lease.Runtime.DeviceLocationPlayback.StartAsync(
            new DeviceLocationPlaybackRequest(
                arguments.RequirePositional(3, "platform"),
                arguments.RequirePositional(4, "device identifier"),
                Path.GetFileName(routePath),
                await File.ReadAllTextAsync(routePath, cancellationToken).ConfigureAwait(false),
                mode,
                speedMultiplier,
                fixedSpeedKph,
                arguments.HasFlag("loop")),
            cancellationToken).ConfigureAwait(false);
        if (!startResult.IsSuccess)
        {
            output.Write(
                new DeviceLocationPlaybackStartOutput(
                    "ansight.device-location-playback-start/v1",
                    startResult),
                () => startResult.Message);
            return CliExitCodes.Failure;
        }

        if (hasResidentRuntime && !arguments.HasFlag("wait"))
        {
            output.Write(
                new DeviceLocationPlaybackStartOutput(
                    "ansight.device-location-playback-start/v1",
                    startResult),
                () => $"{startResult.Playback.Message} Run: {startResult.Playback.RunId}");
            return CliExitCodes.Success;
        }

        var lastPointIndex = -1;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = lease.Runtime.DeviceLocationPlayback.GetSnapshot();
            if (arguments.IsVerbose && snapshot.CurrentPointIndex != lastPointIndex)
            {
                lastPointIndex = snapshot.CurrentPointIndex;
                output.WriteProgress(
                    $"[{snapshot.CurrentPointIndex + 1}/{snapshot.PointCount}] {snapshot.Message}");
            }

            if (!snapshot.IsPlaying)
            {
                return WriteLocationPlayback(snapshot, output);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
        }
    }

    private static int WriteLocationPlayback(
        DeviceLocationPlaybackSnapshot snapshot,
        CliOutput output)
    {
        output.Write(
            new DeviceLocationPlaybackOutput(
                "ansight.device-location-playback/v1",
                snapshot),
            () => $"{snapshot.Status}: {snapshot.Message}"
                  + (string.IsNullOrWhiteSpace(snapshot.RunId)
                      ? string.Empty
                      : $"{Environment.NewLine}Run: {snapshot.RunId}"
                        + $"{Environment.NewLine}Point: {snapshot.CurrentPointIndex + 1}/{snapshot.PointCount}"));
        return string.Equals(snapshot.Status, "failed", StringComparison.OrdinalIgnoreCase)
            ? CliExitCodes.Failure
            : CliExitCodes.Success;
    }

    private static double ParsePositiveDouble(string? value, double defaultValue, string optionName)
    {
        if (value is null)
        {
            return defaultValue;
        }

        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            || !double.IsFinite(parsed)
            || parsed <= 0)
        {
            throw new CliUsageException($"--{optionName} must be a positive finite number.");
        }

        return parsed;
    }

    private static Task<int> RunScreenshotAsync(
        DeviceService service,
        CliArguments arguments,
        CliOutput output,
        CancellationToken cancellationToken)
    {
        var platform = arguments.RequirePositional(2, "platform");
        var deviceIdentifier = arguments.RequirePositional(3, "device identifier");
        var defaultExtension = platform.Equals(DevicePlatforms.Android, StringComparison.OrdinalIgnoreCase)
            ? ".png"
            : ".jpg";
        var outputPath = arguments.GetOption("output")
                         ?? Path.Combine(
                             Environment.CurrentDirectory,
                             $"ansight-{deviceIdentifier}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}{defaultExtension}");
        return RunOperationAsync(
            service.CaptureScreenshotAsync(
                platform,
                deviceIdentifier,
                outputPath,
                cancellationToken),
            output);
    }

    internal static async Task<int> RunOperationAsync(
        Task<DeviceOperationResult> operationTask,
        CliOutput output)
    {
        var result = await operationTask.ConfigureAwait(false);
        output.Write(
            new DeviceOperationOutput("ansight.device-operation/v1", result),
            () => result.Message);
        return result.IsSuccess ? CliExitCodes.Success : CliExitCodes.Failure;
    }

    internal static string RenderInventory(
        DeviceInventory inventory,
        bool includesOfflineDevices,
        string? applicationIdentifier,
        IReadOnlyList<DeviceApplicationMatchOutput> applicationMatches)
    {
        var lines = new List<string>();
        if (inventory.Devices.Count == 0)
        {
            lines.Add(applicationIdentifier is null
                ? "No local devices found."
                : $"No devices found with application '{applicationIdentifier}'.");
        }
        else
        {
            var matchesByDeviceIdentifier = applicationMatches.ToDictionary(
                static match => match.DeviceIdentifier,
                StringComparer.OrdinalIgnoreCase);
            var rows = inventory.Devices.Select(device =>
            {
                var columns = new List<string>
                {
                    device.Platform,
                    device.Kind,
                    device.Identifier,
                    device.State,
                    device.Name
                };
                if (matchesByDeviceIdentifier.TryGetValue(device.Identifier, out var match))
                {
                    columns.AddRange(RenderApplicationMetadataColumns(
                        match.Application,
                        includeUnknownVersion: true));
                }
                return columns;
            }).ToArray();
            lines.AddRange(RenderColumns(rows));
        }

        if (inventory.Warnings.Count > 0)
        {
            lines.Add(string.Empty);
            lines.AddRange(inventory.Warnings.Select(warning => $"Warning: {warning}"));
        }
        if (!includesOfflineDevices)
        {
            lines.Add(string.Empty);
            lines.Add(
                "By default, only live devices are shown. Pass --all (alias: --offline) to include "
                + "offline, shut down, unauthorized, and unavailable devices.");
        }
        return string.Join(Environment.NewLine, lines);
    }

    private static IReadOnlyList<string> RenderApplicationMetadataColumns(
        InstalledApplication application,
        bool includeUnknownVersion = false)
    {
        var version = !string.IsNullOrWhiteSpace(application.Version)
            ? $"version={application.Version}"
            : includeUnknownVersion
                ? "version=unknown"
                : string.Empty;
        var buildVersion = !string.IsNullOrWhiteSpace(application.BuildVersion)
            ? $"build={application.BuildVersion}"
            : string.Empty;
        var installedAt = application.InstalledAtUtc.HasValue
            ? $"installed={application.InstalledAtUtc.Value:O}"
            : string.Empty;
        var updatedAt = application.LastUpdatedAtUtc.HasValue
            ? $"updated={application.LastUpdatedAtUtc.Value:O}"
            : string.Empty;

        return [version, buildVersion, installedAt, updatedAt];
    }

    private static string RenderApplicationMetadata(InstalledApplication application)
    {
        var values = RenderApplicationMetadataColumns(application)
            .Where(static value => value.Length > 0)
            .ToArray();
        return values.Length == 0 ? string.Empty : $"\t{string.Join('\t', values)}";
    }

    private static IReadOnlyList<string> RenderColumns(IReadOnlyList<List<string>> rows)
    {
        var columnCount = rows.Max(static row => row.Count);
        var columnWidths = new int[columnCount];
        foreach (var row in rows)
        {
            for (var index = 0; index < row.Count; index++)
            {
                columnWidths[index] = Math.Max(columnWidths[index], row[index].Length);
            }
        }

        return rows.Select(row => string.Join(
                "  ",
                row.Select((value, index) => index == row.Count - 1
                    ? value
                    : value.PadRight(columnWidths[index]))).TrimEnd())
            .ToArray();
    }

    private static string BuildHelp()
        => """
           Discover and control local simulators, emulators, and physical devices

           By default, device listings show only live devices: booted virtual targets and
           connected physical devices. Pass --all or --offline to include every discovered target.

           Usage:
             ansight devices [--app-id <id>] [--version <pattern>] [--ios|--android] [--simulator|--emulator|--physical] [--phone|--tablet] [--all|--offline]
             ansight device list [--app-id <id>] [--version <pattern>] [--ios|--android] [--simulator|--emulator|--physical] [--phone|--tablet] [--all|--offline]
             ansight device start <ios|android> <device-id>
             ansight device shutdown <ios|android> <device-id>
             ansight device apps <ios|android> <device-id>
             ansight device install <ios|android> <device-id> <app-path>
             ansight device launch <ios|android> <device-id> <app-id>
             ansight device terminate <ios|android> <device-id> <app-id>
             ansight device location set <ios|android> <device-id> --latitude <value> --longitude <value>
             ansight device location clear <ios|android> <device-id>
             ansight device location play <ios|android> <device-id> <route.gpx|route.kml> [options]
             ansight device location status
             ansight device location stop
             ansight device screenshot <ios|android> <device-id> [--output <path>]

           Commands:
             list          List live local virtual and physical devices by default
             start         Boot a virtual device or verify that a physical iOS device is ready; alias: boot
             shutdown      Shut down a virtual device; alias: stop
             apps          List user-installed applications on a target
             install       Install an iOS .app or Android .apk supported by the target
             launch        Launch an installed application by bundle/package ID
             terminate     Stop an application by bundle/package ID
             location      Set/clear GPS coordinates or replay GPX/KML routes
             screenshot    Capture an Android or iOS Simulator display to a local image file

           Options:
             --headless            Start/boot without a native window; shown by default
             --ios                 Show only iOS devices
             --android             Show only Android devices
             --simulator           Show only iOS simulators; alias: --simulators
             --emulator            Show only Android emulators; alias: --emulators
             --physical            Show only physical devices; alias: --physical-devices
             --phone               Show only phones; alias: --phones
             --tablet              Show only tablets; alias: --tablets
             --app-id <id>         Show only devices where this bundle/package ID is installed.
                                   Offline iOS simulators are inspected from disk; stopped Android
                                   and disconnected physical targets are skipped with a short warning.
             --app-version <value> With --app-id, match an app version; supports * and ? wildcards
             --version <value>     Alias for --app-version in device listings
             --app-build <value>   With --app-id, match a build/version code; supports * and ? wildcards
             --offline             Also show offline, shut down, unauthorized, and unavailable devices
             --all                 Alias for --offline; show every discovered device
             --latitude <number>   Latitude for `location set`
             --longitude <number>  Longitude for `location set`
             --mode <mode>         Route timing: recorded (default) or fixed-speed
             --speed <number>      Recorded-timing multiplier; default: 1
             --fixed-speed-kph <n> Fixed route speed in km/h; default: 30
             --loop                Repeat the route until stopped
             --wait                Wait for a resident-host route replay to finish
             --output <path>       Screenshot destination; otherwise a timestamped path is used
             --adb-path <path>     Explicit ADB executable or Android SDK directory
             --xcode-path <path>   Explicit Xcode app or Developer directory
             --json                Emit versioned machine-readable output

           Finding identifiers:
             ansight device list
             ansight device apps <ios|android> <device-id>

           Examples:
             ansight device list --json
             ansight devices --app-id com.example.app
             ansight devices --app-id com.example.app --app-version 1.2.3
             ansight devices --app-id com.example.app --version '2.10.*' --ios --phone --all
             ansight devices --app-id com.example.app --all
             ansight devices --all
             ansight devices --phone
             ansight devices --tablet --all
             ansight devices list --android
             ansight devices list --ios --simulator --offline
             ansight devices list --physical
             ansight device start ios 3CED9C5A-33E7-438D-90B2-DBE09009CDB1
             ansight device launch ios 3CED9C5A-33E7-438D-90B2-DBE09009CDB1 com.example.app
           """;

}
