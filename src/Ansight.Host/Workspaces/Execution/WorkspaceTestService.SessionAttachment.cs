using Ansight.Host.Runtime.Operations;

namespace Ansight.Host.Workspaces.Execution;

public sealed partial class WorkspaceTestService
{
    internal IDisposable ReserveSession(AppSessionSnapshot session)
    {
        var claim = new WorkspaceTestSessionClaim(this);
        try
        {
            if (!runtime.IsSessionLive(session.SessionId))
                throw new InvalidOperationException($"Session '{session.SessionId}' is no longer live.");
            if (claim.Select([session]) is null)
                throw new InvalidOperationException($"Session '{session.SessionId}' is already reserved by another execution.");
            if (!runtime.IsSessionLive(session.SessionId))
                throw new InvalidOperationException($"Session '{session.SessionId}' is no longer live.");
            return claim;
        }
        catch
        {
            claim.Dispose();
            throw;
        }
    }

    internal async Task<string?> ValidateAttachedDeviceTargetAsync(AppSessionSnapshot session,
        WorkspaceTestTargetRequest? target, CancellationToken cancellationToken)
    {
        if (target is null) return null;
        if (!string.IsNullOrWhiteSpace(target.ApplicationPath))
            return "An application artifact cannot be installed when attaching to an existing session.";
        if (DeviceKinds.IsPhysical(target.DeviceKind))
            return "External session execution supports simulators and emulators only.";
        if (!string.IsNullOrWhiteSpace(target.Platform)
            && !MatchesSessionPlatform(target.Platform, session.DeviceProfile?.Device?.OsName))
            return "The requested platform does not match the selected session.";
        if (string.IsNullOrWhiteSpace(target.DeviceIdentifier)) return null;

        var nativeId = DeviceLifecycleTool.ResolveNativeDeviceIdentifier(session);
        if (string.Equals(nativeId, target.DeviceIdentifier, StringComparison.OrdinalIgnoreCase)) return null;
        var inventory = await runtime.Devices.ListAsync(cancellationToken).ConfigureAwait(false);
        // Android callers may select the emulator by AVD name instead of its boot-time serial.
        if (inventory.Devices.Any(device => device.Platform == DevicePlatforms.Android
            && device.IsVirtual && device.IsBooted
            && string.Equals(device.Identifier, nativeId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(device.Name, target.DeviceIdentifier, StringComparison.OrdinalIgnoreCase))) return null;
        return "The requested device does not match the selected session.";
    }
}
