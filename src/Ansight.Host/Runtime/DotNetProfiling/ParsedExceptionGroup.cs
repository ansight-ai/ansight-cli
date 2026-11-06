namespace Ansight.Host.Runtime.DotNetProfiling;

internal sealed record ParsedExceptionGroup(
    string Type,
    string? Message,
    int Count,
    double FirstTimestampMilliseconds,
    double LastTimestampMilliseconds);
