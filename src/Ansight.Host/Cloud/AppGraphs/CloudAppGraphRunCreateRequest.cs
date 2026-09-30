using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Ansight.Host.Cloud;

public sealed record CloudAppGraphRunCreateRequest(
    Guid TeamId,
    Guid AppGraphId,
    Guid AppGraphVersionId,
    string RequestedIntent,
    JsonObject Parameters,
    string TargetSessionId);
