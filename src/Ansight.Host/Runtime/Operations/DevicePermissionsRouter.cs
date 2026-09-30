namespace Ansight.Host.Runtime.Operations;

internal sealed class DevicePermissionsRouter
{
    private NativePermissionService? service;

    public void Configure(NativePermissionService value) => Volatile.Write(ref service, value);

    public Task<PermissionResult> ExecuteAsync(string action, string permission, string? platform,
        string deviceId, string bundleIdentifier, CancellationToken cancellationToken)
        => (Volatile.Read(ref service) ?? throw new InvalidOperationException("The host's native permission service is not configured."))
            .ExecuteAsync(action, permission, platform, deviceId, bundleIdentifier, cancellationToken);
}
