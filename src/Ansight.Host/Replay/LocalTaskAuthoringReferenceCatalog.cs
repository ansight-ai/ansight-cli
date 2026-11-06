using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ansight.Host.Runtime.RepositoryContracts;
using Ansight.Host.Runtime.Operations.Tools.SessionEvidence;
using Ansight.Host.Runtime.Sanitization;
using Ansight.Host.Runtime.Tasks;
using Ansight.Host.SimulatorAgent;
using Ansight.Host.Workspaces.Execution;
using Ansight.Tools;

namespace Ansight.Host.Replay;

public sealed record LocalTaskAuthoringReferenceCatalog(
    string Schema,
    string SessionId,
    bool IsLive,
    DateTimeOffset? CapturedAtUtc,
    IReadOnlyList<LocalTaskAuthoringReference> References,
    IReadOnlyList<LocalTaskSupportModule> SupportModules,
    string Message)
{
    public static LocalTaskAuthoringReferenceCatalog NotFound(string sessionId)
        => new(
            "ansight.local-task-authoring-references/v1",
            sessionId,
            false,
            null,
            [],
            [],
            $"Session '{sessionId}' was not found.");
}
