using Ansight.Host;
using Ansight.Host.Workspaces;

namespace Ansight.Cli.Commands.Test;

internal sealed record TestMeteringRecoveryOutput(
    string Schema,
    string AuditFilePath,
    string RunId,
    int ModelPassCount,
    SimulatorAgentTokenUsage Tokens,
    SimulatorAgentRunCost? Cost);
