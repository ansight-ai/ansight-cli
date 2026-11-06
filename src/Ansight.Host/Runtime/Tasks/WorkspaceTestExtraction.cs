using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ansight.Host.Models.Session;
using Ansight.Host.Workspaces.Catalog;
using Ansight.Host.Explorer.TaskExtraction;

namespace Ansight.Host.Runtime.Tasks;

/// <summary>A reviewable Ansight workspace test derived from a recorded session interval.</summary>
public sealed record WorkspaceTestExtraction(
    string SuggestedName,
    string Source,
    int GeneratedActionCount,
    IReadOnlyList<string> Diagnostics)
{
    public string? TestName { get; init; }
    public IReadOnlyList<string> AutomationIds { get; init; } = [];
}
