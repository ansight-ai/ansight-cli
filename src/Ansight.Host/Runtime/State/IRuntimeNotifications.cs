namespace Ansight.Host.Runtime.State;

using Ansight.Host;

internal interface IRuntimeNotifications
{
    event EventHandler<LogEntry>? LogAdded;
    event EventHandler<RuntimeEvent>? RuntimeEventOccurred;
    event EventHandler<AppSessionSnapshot>? SessionUpdated;
    event EventHandler<SessionLogBatchEventArgs>? SessionLogsAdded;
    event EventHandler<string>? SessionDeleted;
    event EventHandler<string>? ServerStatusChanged;

    bool IsServerRunning { get; }
    string ServerStatusText { get; }

    void SetServerStatus(bool isRunning, string message);
    void LogHost(string message);
    void PublishRuntimeEvent(RuntimeEvent runtimeEvent);
}
