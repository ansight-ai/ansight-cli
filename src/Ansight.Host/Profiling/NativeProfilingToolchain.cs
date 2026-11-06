namespace Ansight.Host.Profiling;

public sealed record NativeProfilingToolchain(
    string Platform,
    string Engine,
    bool IsAvailable,
    string? CaptureToolPath,
    string? CaptureToolVersion,
    string? SupportingToolVersion,
    string Message);
