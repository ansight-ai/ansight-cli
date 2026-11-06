using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Ansight.Host.AppGraphs;

public sealed record LocalAppGraphRecordingDestinationRequest(
    string Kind,
    string Name,
    string Purpose,
    string? DestinationId = null,
    string? ParentDestinationId = null,
    IReadOnlyList<string>? Synonyms = null);
