using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Ansight.Host.AppGraphs;

public sealed record LocalAppGraphRecordingCompleteTransitionRequest(
    string SemanticMeaning,
    string? AutomationId = null,
    string? ExistingDestinationId = null,
    LocalAppGraphRecordingDestinationRequest? Destination = null);
