using System.Text.Json.Nodes;
using SkiaSharp;

namespace Ansight.Host.Runtime.Operations.Tools.UiAutomation;

internal sealed record LiveUiOcrSearchResult(
    bool Available,
    string? Provider,
    string? Message,
    IReadOnlyList<JsonObject> Matches,
    JsonObject? TraceEvidence)
{
    public static LiveUiOcrSearchResult NotAttempted { get; } = new(false, null, null, [], null);

    public bool Attempted => !ReferenceEquals(this, NotAttempted);

    public static LiveUiOcrSearchResult Unavailable(string message, JsonObject? traceEvidence = null)
        => new(false, null, message, [], traceEvidence);

    public bool TryGetUniqueTapPoint(out LiveUiScreenPoint point, out JsonObject? match)
        => TryGetTapPoint(index: 0, indexSpecified: false, out point, out match);

    public bool TryGetTapPoint(
        int index,
        bool indexSpecified,
        out LiveUiScreenPoint point,
        out JsonObject? match)
    {
        match = indexSpecified
            ? Matches.ElementAtOrDefault(index)
            : Matches.Count == 1 ? Matches[0] : null;

        if (match?["tapHint"]?["selector"] is not JsonObject selector
            || selector["normalizedX"] is not JsonValue normalizedXValue
            || !normalizedXValue.TryGetValue<double>(out var normalizedX)
            || selector["normalizedY"] is not JsonValue normalizedYValue
            || !normalizedYValue.TryGetValue<double>(out var normalizedY))
        {
            point = default;

            return false;
        }

        point = new LiveUiScreenPoint(normalizedX, normalizedY);

        return true;
    }

    public JsonObject ToJson()
        => new()
        {
            ["attempted"] = Attempted,
            ["available"] = Available,
            ["provider"] = Provider,
            ["message"] = Message,
            ["matchCount"] = Matches.Count
        };
}
