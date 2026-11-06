using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Ansight.Host.AppGraphs;

public readonly record struct LocalAppGraphRecordingOperationResult(
    bool IsSuccess,
    string Message,
    LocalAppGraphRecording? Recording,
    Guid? GraphId = null,
    Guid? VersionId = null)
{
    public static LocalAppGraphRecordingOperationResult Success(
        LocalAppGraphRecording recording,
        string message,
        Guid? graphId = null,
        Guid? versionId = null)
        => new(true, message, recording, graphId, versionId);

    public static LocalAppGraphRecordingOperationResult Failure(string message)
        => new(false, message, null);
}
