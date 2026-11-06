using System.Text.Json.Serialization;

namespace Ansight.Host.Sanitization;

public sealed record SessionSanitizerOperationContext
{
    public string Kind { get; init; } = "export";

    public SessionSanitizerShareContext? Share { get; init; }
    public static SessionSanitizerOperationContext Export { get; } = new();
}
