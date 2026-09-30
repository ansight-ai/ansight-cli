namespace Ansight.Host.Runtime.State;

internal sealed class SessionTouchPackedBatch
{
    public required DateTimeOffset T0 { get; init; }
    public string Space { get; init; } = SessionTouchPacking.WindowSpaceCode;
    public string Unit { get; init; } = SessionTouchPacking.PixelUnitCode;
    public List<double?> Surface { get; init; } = [];
    public List<string> Ids { get; init; } = [];
    public List<List<object?>> Rows { get; init; } = [];
}
