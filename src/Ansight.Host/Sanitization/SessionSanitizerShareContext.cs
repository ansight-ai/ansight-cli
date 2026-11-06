using System.Text.Json.Serialization;

namespace Ansight.Host.Sanitization;

public sealed record SessionSanitizerShareContext
{
    public required SessionSanitizerShareAudience Audience { get; init; }
    public required Guid TeamId { get; init; }
}
