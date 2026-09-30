namespace Ansight.Host.Runtime.DotNetProfiling;

internal interface IDotNetTraceProcessRunner
{
    Task<DotNetTraceProcessResult> RunAsync(
        DotNetTraceProcessRequest request,
        Action<string>? outputReceived,
        CancellationToken cancellationToken);
}
