using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Ansight.Host.AppGraphs;

public sealed record LocalAppGraphRecordingTabGroupRequest(
    string ParentDestinationId,
    string SelectedDestinationId,
    IReadOnlyList<string> TabDestinationIds,
    string? TabGroupId = null,
    AppGraphNavigationTechnologyDescriptor? Technology = null);
