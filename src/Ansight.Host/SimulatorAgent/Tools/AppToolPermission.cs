using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Ansight.Host.Runtime.Operations;
using Ansight.Host.Runtime.Operations.Tools.RepositoryTasks;
using Ansight.Host.Runtime;

namespace Ansight.Host.SimulatorAgent.Tools;

internal sealed record AppToolPermission(
    string ToolId,
    string Policy,
    bool IsExecutable,
    string? DenialReason)
{
    public bool IsAllowedForAutomation =>
        IsExecutable
        && (string.Equals(Policy, "read", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Policy, "write", StringComparison.OrdinalIgnoreCase));
}
