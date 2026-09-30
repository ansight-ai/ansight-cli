namespace Ansight.Host.Apps;

public sealed record AppWatchDefinition(
    string Id, string AppId, string? Platform, string? DeviceId, bool Enabled, string[] CaptureFiles,
    bool CaptureInstruments = false, int ScreenshotIntervalMilliseconds = AppWatchDefinition.DefaultScreenshotIntervalMilliseconds)
{
    public const int DefaultScreenshotIntervalMilliseconds = 2_000;
    public const int MinimumScreenshotIntervalMilliseconds = 100;
    public const int MaximumScreenshotIntervalMilliseconds = 60_000;

    public static void ValidateScreenshotInterval(int intervalMilliseconds)
    {
        if (intervalMilliseconds is < MinimumScreenshotIntervalMilliseconds or > MaximumScreenshotIntervalMilliseconds)
            throw new ArgumentOutOfRangeException(nameof(intervalMilliseconds),
                $"Screenshot interval must be between {MinimumScreenshotIntervalMilliseconds} and {MaximumScreenshotIntervalMilliseconds} milliseconds.");
    }
}

public sealed record AppWatchStatus(
    AppWatchDefinition Watch, string State, string? SessionId = null, string? LastSessionId = null,
    string? ProcessIdentity = null, string? Message = null)
{
    public IReadOnlyList<AppWatchDeviceStatus> Devices { get; init; } = [];
}

public sealed record AppWatchDeviceStatus(
    string Platform, string DeviceId, string DeviceName, string State, string? SessionId,
    string? LastSessionId, string? ProcessIdentity, string? Message);

internal sealed record AppWatchObservation(DeviceDescriptor Device, string? ProcessIdentity, string? Error = null,
    global::Ansight.AppLifecycleState AppState = global::Ansight.AppLifecycleState.Unknown,
    DateTimeOffset? ObservedAtUtc = null);

internal sealed record AppWatchDiscovery(IReadOnlyList<AppWatchObservation> Observations, string? Warning = null);

internal interface IAppWatchCapture
{
    string SessionId { get; }
    bool IsActive { get; }
    void SetAppState(global::Ansight.AppLifecycleState state, DateTimeOffset observedAtUtc);
    void SetScreenshotInterval(int intervalMilliseconds);
    Task StopAsync(string reason, IReadOnlyList<string> captureFiles);
}

internal interface IAppWatchBackend
{
    Task<AppWatchDiscovery> ObserveAsync(AppWatchDefinition watch, CancellationToken cancellationToken);
    Task<IAppWatchCapture?> TryStartAsync(AppWatchDefinition watch, AppWatchObservation observation,
        CancellationToken cancellationToken);
}
