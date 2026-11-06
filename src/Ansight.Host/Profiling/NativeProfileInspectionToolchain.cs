namespace Ansight.Host.Profiling;

public sealed record NativeProfileInspectionToolchain(
    bool IsAvailable,
    string? ToolPath,
    string? Version,
    string Message);
