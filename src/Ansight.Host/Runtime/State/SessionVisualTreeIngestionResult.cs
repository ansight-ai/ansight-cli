namespace Ansight.Host.Runtime.State;

using Ansight.Host;

internal readonly record struct SessionVisualTreeIngestionResult(
    OperationResult Result,
    bool WasSaved,
    bool WasDiscarded)
{
    public static SessionVisualTreeIngestionResult Saved(string message)
        => new(OperationResult.Success(message), WasSaved: true, WasDiscarded: false);

    public static SessionVisualTreeIngestionResult Discarded(string message)
        => new(OperationResult.Success(message), WasSaved: false, WasDiscarded: true);

    public static SessionVisualTreeIngestionResult Failure(string message)
        => new(OperationResult.Failure(message), WasSaved: false, WasDiscarded: false);
}
