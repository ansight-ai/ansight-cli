namespace Ansight.Host.Runtime.SimulatorCrashReports;



internal sealed record SimulatorCrashReport(
    string FilePath,
    string IncidentId,
    string BundleId,
    int ProcessId,
    DateTimeOffset CapturedAtUtc,
    string ProcessName,
    string? AppVersion,
    string? BuildVersion,
    string? SimulatorUdid);
