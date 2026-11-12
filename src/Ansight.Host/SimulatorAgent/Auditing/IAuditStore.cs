using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ansight.Host.SimulatorAgent.Auditing;

internal interface IAuditStore
{
    AuditSaveResult Save(SimulatorAgentRunAudit audit);

    IReadOnlyList<SimulatorAgentRunHistoryEntry> List();

    IReadOnlyList<SimulatorAgentRunSummary> ListSummaries()
    {
        return List().Select(SimulatorAgentRunSummary.FromEntry).ToArray();
    }

    SimulatorAgentRunHistoryEntry? Load(SimulatorAgentRunSummary summary)
    {
        return List().FirstOrDefault(entry => entry.FilePath == summary.FilePath
            && entry.Audit.RunId == summary.RunId);
    }
}
