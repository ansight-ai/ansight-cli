using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Ansight.Host.Runtime.Tasks;

namespace Ansight.Host.Runtime.Operations.Tools.RepositoryTasks;

internal sealed record RepositoryTaskSearchMatch(
    RepositoryTaskDefinition Task,
    double Score,
    double Coverage,
    IReadOnlyList<string> MatchedQueryTerms,
    IReadOnlyList<string> UnmatchedQueryTerms,
    IReadOnlyList<RepositoryTaskSearchEvidence> Evidence,
    IReadOnlyList<string> BehavioralSynonyms)
{
    public string? ExclusionReason { get; init; }
}
