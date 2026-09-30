namespace Ansight.Host.Runtime.NativeLogs;

using Ansight.Pairing.Models;

internal interface INativeSessionLogCaptureManager
{
    Task AttachAsync(string sessionId, DeviceAppProfile? profile, string? profileJson);

    Task StopAsync(string sessionId, string reason);
}
