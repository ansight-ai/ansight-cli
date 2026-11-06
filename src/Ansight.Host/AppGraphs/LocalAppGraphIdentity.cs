using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Ansight.Host.AppGraphs;

public sealed record LocalAppGraphIdentity(
    Guid GraphId,
    string AppId,
    string AppName);
