namespace Ansight.Host.Workspaces.Execution;

internal sealed record TestVideoRecordingCompletion(bool IsComplete, string? Error = null, TimeSpan? CapturedDuration = null);
