namespace Ansight.Cli;

internal static class CliExitCodes
{
    public const int Success = 0;
    public const int Failure = 1;
    public const int Usage = 2;
    public const int Configuration = 3;
    public const int HostUnavailable = 4;
    public const int CapabilityUnavailable = 5;
    public const int AccessDenied = 6;
    public const int TestFailed = 10;
    public const int TelemetryAnalysisDetected = 11;
    public const int ArtifactDifferenceDetected = 12;
    public const int Cancelled = 130;
}
