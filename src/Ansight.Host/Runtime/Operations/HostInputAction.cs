using System.Text.Json;

namespace Ansight.Host.Runtime.Operations;

/// <summary>Host delivery evidence, separate from the SDK's observed touch stream.</summary>
internal sealed record HostInputAction(
    string ActionId,
    string Kind,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    string Status,
    string? EvidenceActionId = null,
    double? NormalizedX = null,
    double? NormalizedY = null,
    int? DurationMilliseconds = null)
{
    internal const string EventType = "ansight.host-input";
    private static readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web);

    internal SessionApplicationEvent ToEvent() => new(
        ActionId, $"Host input: {Kind}", EventType, JsonSerializer.Serialize(this, jsonOptions), CompletedAtUtc, 0);

    internal static HostInputAction? Read(SessionApplicationEvent item)
    {
        if (item.EventType != EventType) return null;
        try
        {
            var action = JsonSerializer.Deserialize<HostInputAction>(item.Details ?? "", jsonOptions);
            return action is { ActionId.Length: > 0, Kind: "tap" or "longPress" or "type" or "keyboard.press" or "keyboard.submit" }
                   && action.CompletedAtUtc >= action.StartedAtUtc
                   && action.Status is "succeeded" or "failed" or "interrupted"
                ? action : null;
        }
        catch (JsonException) { return null; }
    }
}
