using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ansight.Host.SimulatorAgent.Auditing;

internal interface IAuditStore
{
    AuditSaveResult Save(SimulatorAgentRunAudit audit);

    IReadOnlyList<SimulatorAgentRunHistoryEntry> List();
}
