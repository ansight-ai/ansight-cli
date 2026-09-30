using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ansight.Host.SimulatorAgent.Auditing;

internal sealed class NullAuditStore : IAuditStore
{
    public static NullAuditStore Instance { get; } = new();

    public AuditSaveResult Save(SimulatorAgentRunAudit audit)
    {
        ArgumentNullException.ThrowIfNull(audit);
        return new AuditSaveResult(null, null);
    }

    public IReadOnlyList<SimulatorAgentRunHistoryEntry> List()
        => Array.Empty<SimulatorAgentRunHistoryEntry>();
}
