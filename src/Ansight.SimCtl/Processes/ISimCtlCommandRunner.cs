namespace Ansight.SimCtl;

public interface ISimCtlCommandRunner
{
    Task<SimCtlCommandResult> RunAsync(
        SimCtlToolResolution toolResolution,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default);
}
