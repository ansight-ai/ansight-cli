namespace Ansight.Host.Replay;

public sealed class SessionReplayService : IAsyncDisposable
{
    private const string ExplorerServerKey = "explorer";
    private const string SessionServerKeyPrefix = "session:";
    private readonly RuntimeCoordinator runtime;
    private readonly Lock serverGate = new();
    private readonly SemaphoreSlim startLock = new(1, 1);
    private readonly Dictionary<string, ExplorerServer> servers = new(StringComparer.Ordinal);
    private bool disposed;

    internal SessionReplayService(RuntimeCoordinator runtime)
    {
        this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        runtime.SessionDeleted += HandleSessionDeleted;
    }

    public async Task<SessionReplayStartResult> StartAsync(
        SessionReplayStartRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var sessionId = request.SessionId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return Failure(sessionId, "Session ID is required.");
        }

        if (request.Port is < 0 or > IPEndPoint.MaxPort)
        {
            return Failure(sessionId, "Port must be zero or a value between 1 and 65535.");
        }

        await startLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var serverKey = CreateSessionServerKey(sessionId);
            lock (serverGate)
            {
                if (servers.TryGetValue(serverKey, out var existing))
                {
                    return Success(existing, wasAlreadyRunning: true);
                }
            }

            var snapshot = await runtime.Sessions.LoadSnapshotAsync(sessionId, null, cancellationToken)
                .ConfigureAwait(false);
            if (snapshot is null)
            {
                return Failure(sessionId, $"Session '{sessionId}' was not found.");
            }

            ExplorerServer server;
            try
            {
                server = ExplorerServer.Start(
                    runtime,
                    serverKey,
                    snapshot.SessionId,
                    isExplorer: false,
                    requestedPort: request.Port);
            }
            catch (Exception exception) when (exception is HttpListenerException
                                               or IOException
                                               or InvalidOperationException)
            {
                return Failure(sessionId, $"Unable to start the local replay server: {exception.Message}");
            }

            lock (serverGate)
            {
                servers.Add(serverKey, server);
            }

            _ = ObserveCompletionAsync(serverKey, server);
            return Success(server, wasAlreadyRunning: false);
        }
        finally
        {
            startLock.Release();
        }
    }

    public async Task<SessionExplorerStartResult> StartExplorerAsync(
        SessionExplorerStartRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Port is < 0 or > IPEndPoint.MaxPort)
        {
            return ExplorerFailure("Port must be zero or a value between 1 and 65535.");
        }

        string? requestedPath;
        try
        {
            requestedPath = ExplorerServer.NormalizeRequestedPath(request.UrlPath);
        }
        catch (InvalidOperationException exception)
        {
            return ExplorerFailure(exception.Message);
        }

        var initialSessionId = string.IsNullOrWhiteSpace(request.InitialSessionId)
            ? null
            : request.InitialSessionId.Trim();
        if (initialSessionId is not null
            && await runtime.Sessions.LoadSnapshotAsync(initialSessionId, null, cancellationToken).ConfigureAwait(false) is null)
        {
            return ExplorerFailure($"Session '{initialSessionId}' was not found.");
        }

        await startLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            lock (serverGate)
            {
                if (servers.TryGetValue(ExplorerServerKey, out var existing))
                {
                    if (requestedPath is not null
                        && !string.Equals(
                            requestedPath,
                            existing.ReplayUrl.AbsolutePath,
                            StringComparison.Ordinal))
                    {
                        return ExplorerFailure(
                            $"The local session explorer is already running at '{existing.ReplayUrl}'. Restart the host to change its path.");
                    }

                    if (initialSessionId is not null)
                    {
                        existing.SelectInitialSession(initialSessionId);
                    }

                    return ExplorerSuccess(existing, wasAlreadyRunning: true);
                }
            }

            ExplorerServer server;
            try
            {
                server = ExplorerServer.Start(
                    runtime,
                    ExplorerServerKey,
                    initialSessionId,
                    isExplorer: true,
                    requestedPort: request.Port,
                    requestedPath: requestedPath);
            }
            catch (Exception exception) when (exception is HttpListenerException
                                               or IOException
                                               or InvalidOperationException)
            {
                return ExplorerFailure($"Unable to start the local session explorer: {exception.Message}");
            }

            lock (serverGate)
            {
                servers.Add(ExplorerServerKey, server);
            }

            _ = ObserveCompletionAsync(ExplorerServerKey, server);
            return ExplorerSuccess(server, wasAlreadyRunning: false);
        }
        finally
        {
            startLock.Release();
        }
    }

    public async Task<OperationResult> StopAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var normalizedSessionId = sessionId.Trim();
        var serverKey = CreateSessionServerKey(normalizedSessionId);
        ExplorerServer? server;
        lock (serverGate)
        {
            servers.Remove(serverKey, out server);
        }

        if (server is null)
        {
            return OperationResult.Failure($"Session '{normalizedSessionId}' is not being served locally.");
        }

        await server.StopAsync(cancellationToken).ConfigureAwait(false);
        await server.DisposeAsync().ConfigureAwait(false);
        return OperationResult.Success($"Stopped the local replay for session '{normalizedSessionId}'.");
    }

    public Task<OperationResult> StopExplorerAsync(CancellationToken cancellationToken = default)
        => StopServerAsync(
            ExplorerServerKey,
            "The local session explorer is not running.",
            "Stopped the local session explorer.",
            cancellationToken);

    public async Task WaitForStopAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ExplorerServer? server;
        lock (serverGate)
        {
            servers.TryGetValue(CreateSessionServerKey(sessionId.Trim()), out server);
        }

        if (server is not null)
        {
            await server.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public Task WaitForExplorerStopAsync(CancellationToken cancellationToken = default)
        => WaitForServerStopAsync(ExplorerServerKey, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        ExplorerServer[] activeServers;
        lock (serverGate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            activeServers = servers.Values.ToArray();
            servers.Clear();
        }

        runtime.SessionDeleted -= HandleSessionDeleted;
        foreach (var server in activeServers)
        {
            await server.DisposeAsync().ConfigureAwait(false);
        }

        startLock.Dispose();
    }

    private async Task ObserveCompletionAsync(string serverKey, ExplorerServer server)
    {
        try
        {
            await server.Completion.ConfigureAwait(false);
        }
        finally
        {
            lock (serverGate)
            {
                if (servers.TryGetValue(serverKey, out var current)
                    && ReferenceEquals(current, server))
                {
                    servers.Remove(serverKey);
                }
            }
        }
    }

    private void HandleSessionDeleted(object? sender, string sessionId)
    {
        _ = StopDeletedSessionReplayAsync(sessionId);
    }

    private async Task StopDeletedSessionReplayAsync(string sessionId)
    {
        await Task.Yield();
        ExplorerServer? server;
        lock (serverGate)
        {
            servers.TryGetValue(CreateSessionServerKey(sessionId), out server);
        }

        if (server is null)
        {
            return;
        }

        await server.WaitForActiveRequestSnapshotAsync().ConfigureAwait(false);
        await StopAsync(sessionId).ConfigureAwait(false);
    }

    private async Task<OperationResult> StopServerAsync(
        string serverKey,
        string missingMessage,
        string successMessage,
        CancellationToken cancellationToken)
    {
        ExplorerServer? server;
        lock (serverGate)
        {
            servers.Remove(serverKey, out server);
        }

        if (server is null)
        {
            return OperationResult.Failure(missingMessage);
        }

        await server.StopAsync(cancellationToken).ConfigureAwait(false);
        await server.DisposeAsync().ConfigureAwait(false);
        return OperationResult.Success(successMessage);
    }

    private async Task WaitForServerStopAsync(
        string serverKey,
        CancellationToken cancellationToken)
    {
        ExplorerServer? server;
        lock (serverGate)
        {
            servers.TryGetValue(serverKey, out server);
        }

        if (server is not null)
        {
            await server.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    private static SessionReplayStartResult Success(
        ExplorerServer server,
        bool wasAlreadyRunning)
        => new(
            true,
            wasAlreadyRunning
                ? $"Session '{server.SessionId}' is already being served locally."
                : $"Serving session '{server.SessionId}' locally.",
            server.SessionId,
            server.ReplayUrl,
            server.Port,
            wasAlreadyRunning);

    private static SessionExplorerStartResult ExplorerSuccess(
        ExplorerServer server,
        bool wasAlreadyRunning)
        => new(
            true,
            wasAlreadyRunning
                ? "The local session explorer is already running."
                : "Serving the local Ansight session explorer.",
            server.ReplayUrl,
            server.Port,
            wasAlreadyRunning);

    private static SessionReplayStartResult Failure(string sessionId, string message)
        => new(false, message, sessionId, null, null, false);

    private static SessionExplorerStartResult ExplorerFailure(string message)
        => new(false, message, null, null, false);

    private static string CreateSessionServerKey(string sessionId)
        => SessionServerKeyPrefix + sessionId;
}
