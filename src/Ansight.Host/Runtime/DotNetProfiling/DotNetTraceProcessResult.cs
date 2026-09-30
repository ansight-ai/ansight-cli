namespace Ansight.Host.Runtime.DotNetProfiling;

internal sealed record DotNetTraceProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut)
{
    public bool IsSuccess => ExitCode == 0 && !TimedOut;
}
