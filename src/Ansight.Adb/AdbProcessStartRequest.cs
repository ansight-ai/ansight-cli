namespace Ansight.Adb;

public sealed record AdbProcessStartRequest(
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null);
