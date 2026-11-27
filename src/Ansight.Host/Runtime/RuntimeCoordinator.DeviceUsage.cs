namespace Ansight.Host.Runtime;

public sealed partial class RuntimeCoordinator
{
    private readonly Lock deviceUsageGate = new();
    private readonly Dictionary<string, DateTimeOffset> deviceUsageRecordedAt = new(StringComparer.Ordinal);

    private void RecordLiveDeviceUsage(AppSessionSnapshot snapshot)
    {
        var profile = snapshot.DeviceProfile?.Device;
        var identifier = profile?.NativeDeviceId;
        var platform = profile?.OsName?.Trim().ToLowerInvariant();
        var hasSupportedDevice = !string.IsNullOrWhiteSpace(identifier)
            && platform is DevicePlatforms.Ios or DevicePlatforms.Android;
        var isLiveCapture = !snapshot.IsHistorical
            && hasSupportedDevice
            && IsSessionLive(snapshot.SessionId);

        if (!isLiveCapture)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var key = platform + ":" + identifier;

        lock (deviceUsageGate)
        {
            if (deviceUsageRecordedAt.TryGetValue(key, out var previous)
                && now - previous < TimeSpan.FromSeconds(5))
            {
                return;
            }

            deviceUsageRecordedAt[key] = now;
        }

        _ = RecordLiveDeviceUsageAsync(platform!, identifier!);
    }

    private async Task RecordLiveDeviceUsageAsync(string platform, string identifier)
    {
        try
        {
            await Devices.RecordUsedAsync(platform, identifier).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            log.Warning($"Could not record live device usage: {exception.Message}");
        }
    }
}
