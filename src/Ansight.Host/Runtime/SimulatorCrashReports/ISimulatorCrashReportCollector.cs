namespace Ansight.Host.Runtime.SimulatorCrashReports;

internal interface ISimulatorCrashReportCollector
{
    void SearchAndAttach(string sessionId, DateTimeOffset disconnectedAtUtc);
}
