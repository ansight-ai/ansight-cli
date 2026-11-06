using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Ansight.Host.Cloud;

public sealed record CloudAppGraphRunStepWriteRequest(
    Guid TeamId,
    Guid RunId,
    int StepIndex,
    string EdgeId,
    Guid? BindingId,
    string Status,
    JsonObject Request,
    JsonObject Response,
    JsonObject Evidence,
    string? ErrorMessage,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt);
