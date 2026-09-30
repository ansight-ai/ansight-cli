namespace Ansight.Host.Runtime.DotNetProfiling;

internal sealed record DotNetTraceProcessRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    TimeSpan? Timeout = null,
    IReadOnlyDictionary<string, string?>? EnvironmentVariables = null);
