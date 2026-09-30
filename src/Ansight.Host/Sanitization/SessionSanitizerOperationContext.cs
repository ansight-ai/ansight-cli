using System.Text.Json.Serialization;

namespace Ansight.Host.Sanitization;

public sealed record SessionSanitizerOperationContext
{
    public string Kind { get; init; } = "export";

    public SessionSanitizerShareContext? Share { get; init; }

    public static SessionSanitizerOperationContext Export { get; } = new();
}

public sealed record SessionSanitizerShareContext
{
    public required SessionSanitizerShareAudience Audience { get; init; }

    public required Guid TeamId { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<SessionSanitizerShareAudience>))]
public enum SessionSanitizerShareAudience
{
    [JsonStringEnumMemberName("team")]
    Team,

    [JsonStringEnumMemberName("public_with_auth")]
    PublicWithSignIn,

    [JsonStringEnumMemberName("public_no_auth")]
    Public
}
