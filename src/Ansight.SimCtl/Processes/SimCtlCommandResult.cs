namespace Ansight.SimCtl;

public sealed record SimCtlCommandResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool IsSuccess => ExitCode == 0;
}
