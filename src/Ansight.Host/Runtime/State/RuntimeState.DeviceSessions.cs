namespace Ansight.Host.Runtime.State;

using Ansight.Pairing.Models;
using System.Text.Json.Nodes;

internal sealed partial class RuntimeState
{
    // Only this in-memory registry grants live device access. Imported captures never do.
    private readonly HashSet<string> activeDeviceSessionIds = new(StringComparer.Ordinal);

    public string CreateDeviceSession(WorkspaceTestTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Platform is not (DevicePlatforms.Android or DevicePlatforms.Ios)
            || (!DeviceKinds.IsVirtual(target.DeviceKind)
                && !DeviceKinds.IsPhysical(target.DeviceKind)))
            throw new ArgumentException("Device execution requires a supported Android or iOS device kind.");

        EnsureNextSessionNumberInitialized();
        var sessionId = AllocateAvailableSessionId(target.ApplicationIdentifier);
        var profile = new DeviceAppProfile
        {
            Device = new DeviceProfile
            {
                Model = target.DeviceName,
                OsName = target.Platform,
                IsVirtual = DeviceKinds.IsVirtual(target.DeviceKind),
                IsEmulator = target.Platform == DevicePlatforms.Android
                    && DeviceKinds.IsVirtual(target.DeviceKind)
            },
            App = new DeviceApplicationProfile
            {
                AppId = target.ApplicationIdentifier,
                AppName = target.ApplicationIdentifier
            }
        };
        var profileJson = JsonSerializer.SerializeToNode(profile, JsonUtil.Compact)!.AsObject();
        profileJson["device"]!["nativeDeviceId"] = target.DeviceIdentifier;
        lock (gate)
        {
            sessionsById.Add(sessionId, new SessionState
            {
                SessionId = sessionId,
                AppId = target.ApplicationIdentifier,
                ClientName = target.ApplicationIdentifier,
                RemoteAddress = "host-device",
                ConfigId = null,
                CaptureSource = WorkspaceExecutionModes.Device,
                CreatedUtc = DateTimeOffset.UtcNow,
                LastUpdatedUtc = DateTimeOffset.UtcNow,
                Status = "Capturing",
                // No SDK lifecycle is available. The runner observes the device screen.
                AppState = global::Ansight.AppLifecycleState.Unknown,
                DeviceProfile = profile,
                DeviceProfileJson = profileJson.ToJsonString(JsonUtil.Compact),
                Author = ResolveCurrentAuthor()
            });
            activeDeviceSessionIds.Add(sessionId);
        }
        PersistAndBroadcast(sessionId);
        return sessionId;
    }

    public bool IsDeviceSessionActive(string sessionId)
    {
        lock (gate) return activeDeviceSessionIds.Contains(sessionId);
    }

    public IReadOnlyList<string> GetActiveDeviceSessionIds()
    {
        lock (gate) return activeDeviceSessionIds.ToArray();
    }

    public void EndDeviceSession(string sessionId)
    {
        lock (gate)
        {
            if (!activeDeviceSessionIds.Remove(sessionId)) return;
        }
        var endedUtc = DateTimeOffset.UtcNow;
        UpdateSession(sessionId, session =>
        {
            session.Status = "Completed";
            session.IsHistorical = true;
            var properties = session.CustomProperties?.DeepClone().AsObject() ?? new JsonObject();
            properties["captureEndedUtc"] = endedUtc;
            session.CustomProperties = properties;
        }, endedUtc);
        log.Info($"session_status_changed sessionId={sessionId} status=Completed message=Host device capture ended.");
    }
}
