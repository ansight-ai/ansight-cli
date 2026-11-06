using System.Net;

namespace Ansight.Host.Explorer;

internal sealed record LocalAppWatchRequest(string AppId, string? Platform = null, string? DeviceId = null)
{
    public string[] CaptureFiles { get; init; } = [];
    public bool CaptureInstruments { get; init; }
    public int? ScreenshotIntervalMilliseconds { get; init; }
}
