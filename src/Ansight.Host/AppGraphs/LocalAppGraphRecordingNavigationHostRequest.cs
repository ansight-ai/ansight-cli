using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Ansight.Host.AppGraphs;

public sealed record LocalAppGraphRecordingNavigationHostRequest(
    string Kind,
    string Name,
    string DestinationId,
    string ActiveChildDestinationId,
    IReadOnlyList<string> ChildDestinationIds,
    string Framework,
    string? HostId = null,
    AppGraphNavigationTechnologyDescriptor? Technology = null);
