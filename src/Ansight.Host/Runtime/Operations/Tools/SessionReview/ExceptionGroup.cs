using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Operations.Tools.SessionReview;

internal sealed record ExceptionGroup(JsonObject Payload, int Count, LogPriority MaximumPriority, DateTimeOffset FirstUtc);
