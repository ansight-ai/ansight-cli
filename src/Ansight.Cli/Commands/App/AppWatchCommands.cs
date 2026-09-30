namespace Ansight.Cli.Commands.App;

internal static class AppWatchCommands
{
    internal const string Help = """
        Automatically record selected apps on virtual devices and explicitly selected physical phones

        Usage:
          ansight app watch add <app-id> [--platform ios|android] [--device-id <id>] [--capture-file <path>] [--instruments] [--screenshot-interval-ms <ms>]
          ansight app watch configure <watch-id> --screenshot-interval-ms <ms>
          ansight app watch list
          ansight app watch enable <watch-id>
          ansight app watch disable <watch-id>
          ansight app watch remove <watch-id>

        Watches persist across resident host restarts. Run `ansight host run` to monitor.
        By default, discover the app on all booted iOS simulators and Android emulators.
        A physical phone requires its platform and exact --device-id. iOS also requires Appium/XCUITest.
        Devices that boot later are discovered automatically. No device registration is needed.
        --platform optionally limits discovery to iOS or Android.
        --device-id optionally limits discovery to one iOS device ID, Android serial, or AVD name.
        Each matching device gets its own recording. Re-adding a selection updates and enables it.
        --capture-file is repeatable (maximum 16); paths are relative to the app data sandbox.
        Physical iPhone watches cannot capture private sandbox files with --capture-file.
        --instruments attaches Activity Monitor to a physical iPhone app and ingests the native trace on stop.
        --screenshot-interval-ms sets the screenshot interval for this monitor (100-60000 ms; default: 2000).
        Configure updates active recordings without restarting them and preserves other monitor settings.
        Capture runs sequentially; slow devices may capture less often than the requested interval.
        Files are best-effort copies on exit, disable, remove, or host shutdown (16 MiB each).
        On an observed restart or device loss, exit copies are skipped to avoid mixing runs.
        Backgrounding ends a physical-device recording; virtual-device recordings follow process lifetime.
        One capture can own a device at a time.
        Use --json for structured state, active session IDs, and errors.
        """;

    public static async Task<int> RunAsync(RuntimeCoordinator runtime, CliArguments arguments,
        CliOutput output, CancellationToken cancellationToken)
    {
        var action = arguments.RequirePositional(2, "watch action").ToLowerInvariant();
        var service = runtime.AppWatches;
        string? affectedId = null;
        switch (action)
        {
            case "list":
                arguments.EnsurePositionalCount(3, "ansight app watch list");
                break;
            case "add":
                arguments.EnsurePositionalCount(4, "ansight app watch add <app-id> [--platform <platform>] [--device-id <id>]");
                ValidateSelectionOptions(arguments);
                affectedId = (await service.AddAsync(arguments.RequirePositional(3, "app identifier"),
                    arguments.GetOption("platform"), arguments.GetOption("device-id"),
                    arguments.GetOptions("capture-file"), cancellationToken,
                    captureInstruments: arguments.HasFlag("instruments"),
                    screenshotIntervalMilliseconds: ReadScreenshotInterval(arguments)).ConfigureAwait(false)).Id;
                break;
            case "configure":
                arguments.EnsurePositionalCount(4, "ansight app watch configure <watch-id> --screenshot-interval-ms <ms>");
                affectedId = arguments.RequirePositional(3, "watch identifier");
                var interval = ReadScreenshotInterval(arguments)
                    ?? throw new CliUsageException("--screenshot-interval-ms is required.");
                await service.SetScreenshotIntervalAsync(affectedId, interval, cancellationToken).ConfigureAwait(false);
                break;
            case "enable":
            case "disable":
                arguments.EnsurePositionalCount(4, $"ansight app watch {action} <watch-id>");
                affectedId = arguments.RequirePositional(3, "watch identifier");
                await service.SetEnabledAsync(affectedId, action == "enable", cancellationToken).ConfigureAwait(false);
                break;
            case "remove":
                arguments.EnsurePositionalCount(4, "ansight app watch remove <watch-id>");
                affectedId = arguments.RequirePositional(3, "watch identifier");
                await service.RemoveAsync(affectedId, cancellationToken).ConfigureAwait(false);
                break;
            default:
                throw new CliUsageException("Expected app watch add, configure, list, enable, disable, or remove.");
        }
        var watches = service.List();
        output.Write(new { Schema = "ansight.app-watches/v1", Operation = action, WatchId = affectedId,
            HostRunning = service.IsRunning, Watches = watches }, () =>
            (service.IsRunning ? "App watcher is running." : "Configuration saved. Start the resident host with: ansight host run")
            + Environment.NewLine + (watches.Count == 0 ? "No app watches configured." : string.Join(Environment.NewLine,
                watches.Select(status => $"{status.Watch.Id}\t{status.Watch.AppId}\t{status.Watch.Platform ?? "ios+android"}:{status.Watch.DeviceId ?? "all virtual devices"}"
                    + $"\t{status.State}"
                    + $"\tscreenshot-interval={status.Watch.ScreenshotIntervalMilliseconds}ms"
                    + (status.Message is null ? "" : $"\t{status.Message}")
                    + string.Concat(status.Devices.Select(device => Environment.NewLine
                        + $"  {device.Platform}:{device.DeviceName}\t{device.State}\tsession={device.SessionId ?? device.LastSessionId ?? "-"}"
                        + (device.Message is null ? "" : $"\t{device.Message}")))))));
        return CliExitCodes.Success;
    }

    internal static void ValidateSelectionOptions(CliArguments arguments)
    {
        foreach (var name in new[] { "platform", "device-id" })
        {
            if (arguments.HasFlag(name) && string.IsNullOrWhiteSpace(arguments.GetOption(name)))
                throw new CliUsageException($"--{name} requires a value when supplied.");
            if (arguments.GetOptions(name).Count > 1)
                throw new CliUsageException($"Use at most one --{name}; omit it to discover all matching virtual devices.");
        }
    }

    internal static int? ReadScreenshotInterval(CliArguments arguments)
    {
        const string option = "screenshot-interval-ms";
        if (!arguments.HasFlag(option)) return null;
        if (arguments.GetOptions(option).Count > 1)
            throw new CliUsageException($"Use at most one --{option}.");
        return arguments.GetRequiredIntOption(option,
            AppWatchDefinition.MinimumScreenshotIntervalMilliseconds,
            AppWatchDefinition.MaximumScreenshotIntervalMilliseconds);
    }
}
