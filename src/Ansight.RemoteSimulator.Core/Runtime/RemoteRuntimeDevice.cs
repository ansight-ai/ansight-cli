using System.Text.Json.Serialization;

namespace Ansight.RemoteSimulator.Core.Runtime;

public sealed record RemoteRuntimeDevice(
    [property: JsonPropertyName("udid")] string Identifier,
    string Name,
    string State,
    bool IsBooted,
    string RuntimeIdentifier,
    string Platform,
    int? DisplayWidth = null,
    int? DisplayHeight = null,
    DateTimeOffset? LastBootedUtc = null,
    string? BootIdentifier = null);
