namespace Ansight.Host.Runtime.NativeProfiling;

internal sealed record NativeProfileToolchain(
    string Platform,
    string Engine,
    bool IsAvailable,
    string? CaptureToolPath,
    string? CaptureToolVersion,
    string? SupportingToolVersion,
    string Message);
