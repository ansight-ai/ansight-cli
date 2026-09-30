namespace Ansight.Host.Runtime.State;

internal sealed record RuntimeSessionContext(string AppId, string ClientName, string Status,
    string CaptureSource = WorkspaceExecutionModes.Sdk);
