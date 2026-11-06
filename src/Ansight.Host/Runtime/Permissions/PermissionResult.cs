namespace Ansight.Host.Runtime.Permissions;

internal sealed record PermissionResult(
    string Operation, string Permission, string Platform, string DeviceId, string BundleIdentifier,
    bool Supported, bool IsSuccess, string Status, string Backend, string? Message,
    IReadOnlyList<NativePermissionState> NativePermissions);
