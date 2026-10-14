using System.Text.Json.Nodes;

namespace Ansight.Host.SimulatorAgent.Tools;

internal sealed record AgentBatchStep(string ToolName, JsonObject Arguments, bool UsePreviousTarget = false, int CooldownMilliseconds = 0);
