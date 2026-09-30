using System.Text.RegularExpressions;

namespace Ansight.Host.Runtime.Permissions;

internal sealed partial class NativePermissionService(
    Func<CancellationToken, Task<DeviceInventory>> inventory,
    IDeviceCommandRunner commands,
    RuntimeOptions options,
    string? simctlPathOverride = null)
{
    public async Task<PermissionResult> ExecuteAsync(string action, string permission, string? requestedPlatform,
        string deviceId, string bundleIdentifier, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (action is not ("grant" or "revoke" or "query" or "reset")) throw new ArgumentException("Unknown permission action.");
        if (!Regex.IsMatch(bundleIdentifier, @"^[A-Za-z0-9_]+(?:\.[A-Za-z0-9_-]+)+$"))
            throw new ArgumentException("An exact application identifier is required.");
        var devices = await inventory(cancellationToken).ConfigureAwait(false);
        var matches = devices.Devices.Where(device => string.Equals(device.Identifier, deviceId, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1 || !matches[0].IsAvailable || !matches[0].IsBooted)
            throw new InvalidOperationException("Permissions require a uniquely identified, connected device or booted simulator.");
        var target = matches[0];
        if (requestedPlatform is not null && requestedPlatform != target.Platform)
            throw new ArgumentException($"The requested {requestedPlatform} API does not match the session's {target.Platform} device.");
        if (requestedPlatform is null && !PermissionCatalog.Ios.ContainsKey(permission))
            throw new ArgumentException($"Unknown shared permission '{permission}'.");
        if (target.Platform == DevicePlatforms.Android)
            return await AndroidAsync(target, bundleIdentifier, action, permission, requestedPlatform is null, cancellationToken).ConfigureAwait(false);
        if (target.Platform == DevicePlatforms.Ios && target.IsVirtual)
            return await IosAsync(target, bundleIdentifier, action, permission, requestedPlatform is null, cancellationToken).ConfigureAwait(false);
        return Unsupported(action, permission, target, bundleIdentifier, "Native permission control supports Android devices and iOS Simulators. Physical iOS devices are unsupported.");
    }

    private static PermissionResult Unsupported(string action, string permission, DeviceDescriptor target, string app, string message)
        => new(action, permission, target.Platform, target.Identifier, app, false, action == "query", "unsupported", "none", message, []);

    internal static string Aggregate(IReadOnlyList<NativePermissionState> states)
    {
        if (states.Count == 0 || states.Any(state => state.Status == "unknown")) return "unknown";
        var distinct = states.Select(state => state.Status).Distinct().ToArray();
        return distinct.Length == 1 ? distinct[0] : "limited";
    }
}
