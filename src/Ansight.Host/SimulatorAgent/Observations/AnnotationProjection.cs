using System.Text.Json.Nodes;

namespace Ansight.Host.SimulatorAgent.Observations;

internal sealed record AnnotationProjection(
    JsonArray Managers,
    int ReturnedAnnotationCount,
    bool WasTruncated);
