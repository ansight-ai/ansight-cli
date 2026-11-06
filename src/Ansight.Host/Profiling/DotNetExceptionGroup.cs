namespace Ansight.Host.Profiling;

public sealed record DotNetExceptionGroup(
    string Type,
    string? Message,
    int Count,
    double FirstTimestampMilliseconds,
    double LastTimestampMilliseconds);
