using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Operations.Tools.TouchReview;

internal readonly record struct DeadTouchCandidate(bool IsDead, JsonObject Payload);
