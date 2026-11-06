using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Ansight.Host.Cloud;

public sealed record CloudAppGraphCreateRequest(
    Guid TeamId,
    Guid TeamAppId,
    string Name,
    string Intent,
    JsonObject Definition,
    IReadOnlyList<CloudAppGraphBinding>? Bindings = null);
