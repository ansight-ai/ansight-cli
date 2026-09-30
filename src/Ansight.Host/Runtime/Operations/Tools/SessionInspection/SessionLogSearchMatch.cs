namespace Ansight.Host.Runtime.Operations.Tools.SessionInspection;

internal sealed record SessionLogSearchMatch(
    AppSessionSnapshot Session,
    LogEntry Log,
    bool IsLive,
    string PlatformKey,
    string OperatingSystemName);
