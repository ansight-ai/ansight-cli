using Ansight.Host.Runtime.Operations.Tools.DeviceLifecycle;

namespace Ansight.Host.Workspaces.Execution;

public sealed partial class WorkspaceTestService
{
    internal async Task<TestVideoCapture?> StartRunVideoAsync(TestVideoOptions? options,
        AppSessionSnapshot session, WorkspaceTestTarget? target, string runId,
        IProgress<WorkspaceTestRunProgress>? progress, CancellationToken cancellationToken)
    {
        if (options?.IsEnabled != true) return null;
        progress?.Report(new("video.starting", "Starting video recording."));
        var device = session.DeviceProfile?.Device;
        return await TestVideoCapture.StartAsync(runId, session.SessionId,
            target?.DeviceIdentifier ?? DeviceLifecycleTool.ResolveNativeDeviceIdentifier(session),
            target?.Platform ?? device?.OsName?.ToLowerInvariant() ?? string.Empty,
            DeviceKinds.IsVirtual(target?.DeviceKind) || device?.IsVirtual == true || device?.IsEmulator == true,
            options, videoRecorder,
            (recording, path) => runtime.SessionEditing.AddVideoRecording(session.SessionId, recording, path),
            cancellationToken, runtime.SessionVideoTranscoder).ConfigureAwait(false);
    }

    private async Task<WorkspaceTestRunResult> CompleteTestVideoAsync(TestVideoCapture? capture,
        WorkspaceTestRunResult result, IProgress<WorkspaceTestRunProgress>? progress)
    {
        if (capture is null) return result;
        progress?.Report(new("video.finalizing", "Finalizing test video recording."));
        var video = await capture.CompleteAsync(result.IsSuccess).ConfigureAwait(false);
        progress?.Report(new("video." + video.Status, video.Message));
        result = result with { Video = video };
        if (result.RunAudit is not { } audit) return result;
        var updated = audit with { Video = video };
        var saved = simulatorAgent.PersistRunAudit(updated);
        if (result.AgentResult is { } agent)
            return result with { AgentResult = agent with { Audit = updated,
                AuditFilePath = saved.FilePath ?? agent.AuditFilePath,
                AuditPersistenceError = saved.ErrorMessage ?? agent.AuditPersistenceError } };
        return result with { TaskAudit = updated, TaskAuditFilePath = saved.FilePath ?? result.TaskAuditFilePath,
            TaskAuditPersistenceError = saved.ErrorMessage ?? result.TaskAuditPersistenceError };
    }
}
