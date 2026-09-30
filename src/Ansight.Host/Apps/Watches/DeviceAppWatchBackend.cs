using Ansight.Adb;

namespace Ansight.Host.Apps;

internal sealed class DeviceAppWatchBackend(
    IDeviceService devices,
    Func<string?> adbPath,
    Func<AppWatchDefinition, AppWatchObservation, IDisposable, CancellationToken, Task<IAppWatchCapture>> startCapture,
    IDeviceCommandRunner? commandRunner = null,
    Func<string, string, CancellationToken, Task<bool>>? physicalIosForeground = null) : IAppWatchBackend
{
    private readonly IDeviceCommandRunner commands = commandRunner ?? new DeviceCommandRunner();
    private readonly Dictionary<string, string> physicalIosBundlePaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> physicalIosForegroundApps = new(StringComparer.OrdinalIgnoreCase);
    private DeviceInventory? inventory;
    private DateTimeOffset inventoryExpires;

    public async Task<AppWatchDiscovery> ObserveAsync(AppWatchDefinition watch, CancellationToken cancellationToken)
    {
        if (inventory is null || DateTimeOffset.UtcNow >= inventoryExpires)
        {
            inventory = await devices.ListAsync(cancellationToken).ConfigureAwait(false);
            inventoryExpires = DateTimeOffset.UtcNow.AddSeconds(5);
        }
        var matches = SelectDevices(watch, inventory.Devices);
        var observations = new List<AppWatchObservation>();
        foreach (var device in matches)
        {
            try
            {
                observations.Add(await ReadObservationAsync(watch, device, cancellationToken).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) { observations.Add(new AppWatchObservation(device, null, exception.Message)); }
        }
        return new AppWatchDiscovery(observations, inventory.Warnings.Count == 0 ? null : string.Join("; ", inventory.Warnings));
    }

    internal static IReadOnlyList<DeviceDescriptor> SelectDevices(AppWatchDefinition watch, IReadOnlyList<DeviceDescriptor> devices)
    {
        var matches = devices.Where(device => device.Platform is DevicePlatforms.Ios or DevicePlatforms.Android
            && (watch.Platform is null || device.Platform == watch.Platform)
            && (watch.DeviceId is null || device.Identifier.Equals(watch.DeviceId, StringComparison.OrdinalIgnoreCase)
                || (device.Platform == DevicePlatforms.Android && device.Name == watch.DeviceId))).ToArray();
        return matches.Where(device => (device.IsVirtual || (watch.DeviceId is not null
                && watch.Platform == device.Platform && device.IsPhysical
                && (device.Platform == DevicePlatforms.Ios
                    || device.Platform == DevicePlatforms.Android
                        && device.Identifier.Equals(watch.DeviceId, StringComparison.OrdinalIgnoreCase))))
            && device.IsBooted && device.IsAvailable)
            .DistinctBy(device => device.Platform + ":" + device.Identifier).ToArray();
    }

    public async Task<IAppWatchCapture?> TryStartAsync(AppWatchDefinition watch, AppWatchObservation observation,
        CancellationToken cancellationToken)
    {
        var claim = DeviceExecutionClaim.TryAcquire(observation.Device!);
        if (claim is null) return null;
        try { return await startCapture(watch, observation, claim, cancellationToken).ConfigureAwait(false); }
        catch { claim.Dispose(); throw; }
    }

    internal async Task<string?> ReadIdentityAsync(AppWatchDefinition watch, DeviceDescriptor device,
        CancellationToken cancellationToken)
        => (await ReadObservationAsync(watch, device, cancellationToken).ConfigureAwait(false)).ProcessIdentity;

    internal async Task<AppWatchObservation> ReadObservationAsync(AppWatchDefinition watch, DeviceDescriptor device,
        CancellationToken cancellationToken)
    {
        if (device.Platform == DevicePlatforms.Ios && device.IsPhysical)
        {
            var key = device.Identifier + ":" + watch.AppId;
            if (!physicalIosBundlePaths.TryGetValue(key, out var bundlePath))
            {
                var installed = (await devices.ListApplicationsAsync(DevicePlatforms.Ios, device.Identifier,
                    cancellationToken).ConfigureAwait(false)).SingleOrDefault(app =>
                    string.Equals(app.Identifier, watch.AppId, StringComparison.Ordinal));
                if (installed is null) throw new IOException($"'{watch.AppId}' is not installed on the selected iPhone.");
                bundlePath = installed.BundlePath;
                if (string.IsNullOrWhiteSpace(bundlePath))
                    throw new IOException("CoreDevice did not expose the installed app bundle path needed for process matching.");
                physicalIosBundlePaths[key] = bundlePath;
            }
            var identity = await devices.GetPhysicalIosProcessIdentityAsync(device.Identifier, watch.AppId,
                bundlePath, cancellationToken).ConfigureAwait(false);
            if (identity is null && !physicalIosForegroundApps.Contains(key))
                return new AppWatchObservation(device, null);
            if (physicalIosForeground is null)
                throw new InvalidOperationException("Physical iOS foreground detection is unavailable.");
            var foreground = await physicalIosForeground(device.Identifier, watch.AppId, cancellationToken)
                .ConfigureAwait(false);
            if (foreground && identity is not null) physicalIosForegroundApps.Add(key);
            else if (!foreground) physicalIosForegroundApps.Remove(key);
            return new AppWatchObservation(device, foreground ? identity : null,
                AppState: !foreground ? global::Ansight.AppLifecycleState.Background
                    : identity is not null ? global::Ansight.AppLifecycleState.Foreground : global::Ansight.AppLifecycleState.Unknown,
                ObservedAtUtc: DateTimeOffset.UtcNow);
        }
        if (device.Platform == DevicePlatforms.Android)
        {
            var adb = AdbToolLocator.Resolve(adbPath());
            if (!adb.IsFound) throw new IOException(adb.Message);
            if (device.IsPhysical && !await IsAndroidAppForegroundAsync(adb.AdbPath!, device.Identifier,
                    watch.AppId, cancellationToken).ConfigureAwait(false))
                return new AppWatchObservation(device, null, AppState: global::Ansight.AppLifecycleState.Background,
                    ObservedAtUtc: DateTimeOffset.UtcNow);
            var observedAtUtc = DateTimeOffset.UtcNow;
            var pid = await commands.RunAsync(adb.AdbPath!, ["-s", device.Identifier, "shell",
                "pidof " + DeviceCommandRunner.Quote(watch.AppId)], cancellationToken).ConfigureAwait(false);
            if (pid.ExitCode == 1 && pid.Text.Length == 0 && string.IsNullOrWhiteSpace(pid.Error))
                return new AppWatchObservation(device, null);
            var text = pid.RequireText();
            if (!int.TryParse(text, out var processId) || processId <= 0)
                throw new IOException("The app's main process could not be identified unambiguously.");
            var stat = await commands.RunAsync(adb.AdbPath!, ["-s", device.Identifier, "shell",
                $"cat /proc/{processId}/stat"], cancellationToken).ConfigureAwait(false);
            var identity = DeviceProcessCounters.ParseAndroid(processId, stat.RequireText(), "", 0).Identity;
            if (identity.EndsWith(":unknown", StringComparison.Ordinal)) throw new IOException("Process start time is unavailable.");
            return new AppWatchObservation(device, identity,
                AppState: device.IsPhysical ? global::Ansight.AppLifecycleState.Foreground : global::Ansight.AppLifecycleState.Unknown,
                ObservedAtUtc: observedAtUtc);
        }
        var services = await commands.RunAsync("/usr/bin/xcrun",
            ["simctl", "spawn", device.Identifier, "launchctl", "list"], cancellationToken).ConfigureAwait(false);
        var matching = services.RequireText().Split('\n')
            .Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length >= 3 && parts[2].StartsWith("UIKitApplication:" + watch.AppId + "[", StringComparison.Ordinal))
            .ToArray();
        if (matching.Length == 0 || (matching.Length == 1 && matching[0][0] == "-")) return new AppWatchObservation(device, null);
        if (matching.Length != 1 || !int.TryParse(matching[0][0], out var simulatorPid) || simulatorPid <= 0)
            throw new IOException("The app's simulator process could not be identified unambiguously.");
        var container = await commands.RunAsync("/usr/bin/xcrun",
            ["simctl", "get_app_container", device.Identifier, watch.AppId, "app"], cancellationToken).ConfigureAwait(false);
        return new AppWatchObservation(device, DeviceProcessCounters.ReadMac(simulatorPid, container.RequireText()).Identity);
    }

    private async Task<bool> IsAndroidAppForegroundAsync(string adb, string deviceId, string appId,
        CancellationToken cancellationToken)
    {
        var result = await commands.RunAsync(adb, ["-s", deviceId, "shell", "dumpsys activity activities"],
            cancellationToken).ConfigureAwait(false);
        return AndroidForegroundActivity.IsForeground(result.RequireText(), appId);
    }
}
