namespace Ansight.Host.Apps;

internal interface IAppWatchCapture
{
    string SessionId { get; }
    bool IsActive { get; }

    void SetAppState(global::Ansight.AppLifecycleState state, DateTimeOffset observedAtUtc);

    void SetScreenshotInterval(int intervalMilliseconds);

    Task StopAsync(string reason, IReadOnlyList<string> captureFiles);
}
