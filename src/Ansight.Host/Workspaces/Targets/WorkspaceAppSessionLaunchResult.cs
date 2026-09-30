namespace Ansight.Host.Workspaces.Targets;

using System.Text.Json.Serialization;

public sealed record WorkspaceAppSessionLaunchResult(
    bool IsSuccess,
    string Message,
    AppSessionSnapshot? Session = null,
    WorkspaceTestTarget? Target = null) : IAsyncDisposable
{
    public IReadOnlyList<SimulatorAgentStartupStep> StartupSteps { get; init; } = [];

    [JsonIgnore]
    internal IDisposable? SessionClaim { get; init; }

    [JsonIgnore]
    internal DeviceRunSession? DeviceSession { get; init; }

    /// <summary>Ends captures owned by this launch and releases its execution reservation. Borrowed monitor captures continue.</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (DeviceSession is not null) await DeviceSession.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            SessionClaim?.Dispose();
        }
    }

    public static WorkspaceAppSessionLaunchResult Success(
        AppSessionSnapshot session,
        WorkspaceTestTarget target)
        => new(
            true,
            $"Connected Ansight session '{session.SessionId}' after launching '{target.ApplicationIdentifier}' on '{target.DeviceName}'.",
            session,
            target);

    public static WorkspaceAppSessionLaunchResult Failure(
        string message,
        WorkspaceTestTarget? target = null)
        => new(false, message, Target: target);
}
