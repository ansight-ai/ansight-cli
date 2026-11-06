using Ansight.Host.Cloud.AppGraphs;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ansight.Host;
using Ansight.Host.Workspaces;

namespace Ansight.Cli.Commands.AppGraph;

internal sealed record AppGraphExploreOutput(
    string Schema,
    Guid? AppGraphId,
    Guid? AppGraphVersionId,
    bool CreatedGraph,
    string SessionId,
    string AppId,
    string AuditRunId,
    Guid? ObservationId,
    int DestinationCount,
    int ElementActionCount,
    decimal Confidence,
    string Summary,
    JsonObject CandidateDefinition,
    JsonObject Evidence,
    bool WasSaved,
    string SaveMessage);
