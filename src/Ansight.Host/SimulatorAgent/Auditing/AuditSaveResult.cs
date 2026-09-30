using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ansight.Host.SimulatorAgent.Auditing;

internal sealed record AuditSaveResult(string? FilePath, string? ErrorMessage);
