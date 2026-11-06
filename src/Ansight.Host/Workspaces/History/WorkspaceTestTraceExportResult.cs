using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ansight.Host.Workspaces.History;

public sealed record WorkspaceTestTraceExportResult(
    bool IsSuccess,
    string Message,
    string RunId,
    string OutputPath,
    int TraceCount,
    int SessionCount);
