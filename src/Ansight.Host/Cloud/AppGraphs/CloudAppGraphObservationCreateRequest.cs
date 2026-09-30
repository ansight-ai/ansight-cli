using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Ansight.Host.Cloud;

public sealed record CloudAppGraphObservationCreateRequest(
    Guid TeamId,
    Guid AppGraphId,
    string SourceLocalSessionId,
    string SourceAppId,
    string ExplorationAuditRunId,
    JsonObject CandidateDefinition,
    JsonObject Evidence,
    decimal Confidence,
    string? Notes,
    Guid? MergedIntoVersionId = null);
