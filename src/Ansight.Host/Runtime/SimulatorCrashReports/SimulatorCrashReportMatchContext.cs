namespace Ansight.Host.Runtime.SimulatorCrashReports;



internal sealed record SimulatorCrashReportMatchContext(
    string BundleId,
    int ProcessId,
    DateTimeOffset SessionStartedAtUtc,
    DateTimeOffset DisconnectedAtUtc,
    string? AppVersion,
    string? BuildVersion);
