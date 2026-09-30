using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Ansight.Host.Cloud;

public sealed record CloudAppGraphDetail(
    CloudAppGraphSummary Graph,
    CloudAppGraphVersion Version,
    IReadOnlyList<CloudAppGraphBinding> Bindings);
