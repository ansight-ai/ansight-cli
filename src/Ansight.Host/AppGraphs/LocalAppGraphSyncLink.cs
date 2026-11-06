using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Ansight.Host.AppGraphs;

public sealed record LocalAppGraphSyncLink(
    Guid LocalGraphId,
    Guid RemoteGraphId,
    Guid RemoteTeamId,
    Guid? LocalVersionId,
    Guid? RemoteVersionId,
    DateTimeOffset SyncedAtUtc);
