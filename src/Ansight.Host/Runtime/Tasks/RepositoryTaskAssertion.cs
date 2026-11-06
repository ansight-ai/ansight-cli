using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Ansight.Host.Runtime.Tasks;

public sealed record RepositoryTaskAssertion(
    string AssertionId,
    bool Passed,
    string Message,
    JsonNode? Expected,
    JsonNode? Actual)
{
    public string? Matcher { get; init; }
    public DateTimeOffset? CompletedAtUtc { get; init; }
}
