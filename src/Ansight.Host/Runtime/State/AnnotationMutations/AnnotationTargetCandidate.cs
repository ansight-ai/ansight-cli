using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.State;

internal sealed record AnnotationTargetCandidate(JsonObject Node, int Depth);
