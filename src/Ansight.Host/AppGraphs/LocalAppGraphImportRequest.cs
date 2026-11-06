using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Ansight.Host.AppGraphs;

public sealed record LocalAppGraphImportRequest(
    string AppId,
    string AppName,
    CloudAppGraphDetail Detail,
    Guid RemoteTeamId);
