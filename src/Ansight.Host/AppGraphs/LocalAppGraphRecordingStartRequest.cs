using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Ansight.Host.AppGraphs;

public sealed record LocalAppGraphRecordingStartRequest(
    string SessionId,
    string AppId,
    string AppName,
    string GraphName,
    string Intent,
    Guid? AppGraphId = null);
