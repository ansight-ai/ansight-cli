using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Ansight.Host.AppGraphs;

public sealed record LocalAppGraphRecording(
    Guid Id,
    string SessionId,
    string AppId,
    string AppName,
    Guid? AppGraphId,
    Guid? BaseVersionId,
    Guid? CommittedVersionId,
    string GraphName,
    string Intent,
    string Status,
    string? CurrentDestinationId,
    JsonObject Definition,
    JsonObject Evidence,
    JsonObject? PendingTransition,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? StoppedAtUtc,
    IReadOnlyList<string> Gaps);
