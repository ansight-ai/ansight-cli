using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ansight.Host.Runtime.Operations;

namespace Ansight.Host.Runtime.Tasks;

internal sealed record TimelineExtractedAction(
    string Kind,
    JsonObject? Selector,
    long DurationMilliseconds,
    string Description,
    DateTimeOffset CapturedAtUtc,
    string? InputValue,
    double? StartNormalizedX,
    double? StartNormalizedY,
    double? EndNormalizedX,
    double? EndNormalizedY);
