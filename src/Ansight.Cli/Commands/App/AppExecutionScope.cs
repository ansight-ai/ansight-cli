namespace Ansight.Cli.Commands.App;

/// <summary>Keeps the session reserved until optional app cleanup has finished.</summary>
internal sealed class AppExecutionScope(
    bool closeAppOnCompletion,
    Func<string, string, string, CancellationToken, Task<DeviceOperationResult>> terminateApplication,
    CliOutput output,
    TimeSpan? cleanupTimeout = null) : IAsyncDisposable
{
    private readonly TimeSpan timeout = cleanupTimeout ?? TimeSpan.FromSeconds(30);
    private WorkspaceTestTarget? target;
    private bool disposed;

    public bool CloseAppOnCompletion { get; } = closeAppOnCompletion;

    public IDisposable? SessionClaim { get; set; }

    public IAsyncDisposable? DeviceSession { get; set; }

    public void SetTarget(WorkspaceTestTarget value) => target = value;

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        try
        {
            if (!CloseAppOnCompletion || target is null)
            {
                return;
            }

            // Cleanup must still run when the execution's cancellation token has been cancelled.
            using var cleanupCancellation = new CancellationTokenSource(timeout);
            output.WriteProgress(
                $"[app.close] Closing '{target.ApplicationIdentifier}' on '{target.DeviceIdentifier}'.");
            var result = await terminateApplication(
                    target.Platform,
                    target.DeviceIdentifier,
                    target.ApplicationIdentifier,
                    cleanupCancellation.Token)
                .WaitAsync(cleanupCancellation.Token)
                .ConfigureAwait(false);
            output.WriteProgress(result.IsSuccess
                ? $"[app.close] Closed '{target.ApplicationIdentifier}'; the device remains running."
                : $"[app.close.warning] Could not close '{target.ApplicationIdentifier}': {result.Message}");
        }
        catch (OperationCanceledException)
        {
            output.WriteProgress("[app.close.warning] Closing the app timed out; it may still be running.");
        }
        catch (Exception exception)
        {
            // Do not mask an execution failure or cancellation with a secondary cleanup error.
            output.WriteProgress($"[app.close.warning] Could not close the app: {exception.Message}");
        }
        finally
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
    }

    internal static WorkspaceTestTarget ResolveSessionTarget(
        AppSessionSnapshot snapshot,
        string? requestedDeviceIdentifier,
        DeviceInventory inventory)
    {
        var nativeIdentifier = DeviceLifecycleTool.ResolveNativeDeviceIdentifier(snapshot);
        var requestedIdentifier = string.IsNullOrWhiteSpace(requestedDeviceIdentifier)
            ? null
            : requestedDeviceIdentifier.Trim();
        var identifier = requestedIdentifier ?? nativeIdentifier;
        var device = ResolveDevice(identifier);
        if (device is null)
        {
            throw new CliUsageException(
                "--close-app-on-completion requires a known host device for this session. "
                + "Pass --device-id <id> for the device running the app.");
        }

        if (nativeIdentifier is not null
            && requestedIdentifier is not null
            && !string.Equals(
                device.Identifier,
                ResolveDevice(nativeIdentifier)?.Identifier,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new CliUsageException("--device-id does not match the session's device; refusing to close another app instance.");
        }

        return new WorkspaceTestTarget(
            device.Platform,
            device.Identifier,
            device.Name,
            snapshot.AppId,
            DeviceStarted: false,
            ApplicationInstalled: false,
            ApplicationLaunched: false)
        {
            DeviceKind = device.Kind
        };

        DeviceDescriptor? ResolveDevice(string? value)
            => inventory.Devices.FirstOrDefault(candidate =>
                   string.Equals(candidate.Identifier, value, StringComparison.OrdinalIgnoreCase))
               ?? inventory.Devices.FirstOrDefault(candidate =>
                   candidate.IsBooted && candidate.IsVirtual && candidate.Platform == DevicePlatforms.Android
                   && string.Equals(candidate.Name, value, StringComparison.OrdinalIgnoreCase));
    }
}
