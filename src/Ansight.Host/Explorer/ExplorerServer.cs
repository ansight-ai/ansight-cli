using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Ansight.Host.AppGraphs;
using Ansight.Host.Files;
using Ansight.Host.Trends;
using Ansight.Host.Runtime.BinaryTransfers;
using Ansight.Host.Runtime.Operations;
using Ansight.Host.Runtime.Operations.Tools.UiAutomation;
using Ansight.Infrastructure.Preferences;

namespace Ansight.Host.Explorer;

internal sealed partial class ExplorerServer : IAsyncDisposable
{
    private sealed record LocalDoctorRequest(bool IncludeSecretMetadata = false);
    private const int AutomaticPortAttempts = 10;
    private const int LiveFileDownloadChunkBytes = 512 * 1024;
    private const int MaximumLiveFileSearchDepth = 16;
    private const int MaximumLiveFileSearchEntries = 1000;
    private const int MaximumRequestBodyBytes = 32 * 1024 * 1024;
    private const long MaximumSessionArchiveUploadBytes = 2L * 1024L * 1024L * 1024L;
    private const int MaximumHostLogReadBytes = 2 * 1024 * 1024;
    private const int SessionEventBufferCapacity = 1;
    private const string ResourcePrefix = "Ansight.Host.Replay.Assets.";
    private static readonly TimeSpan SessionEventHeartbeatInterval = TimeSpan.FromSeconds(15);
    internal static readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = JsonUtil.MaximumDepth
    };
    private static readonly Lazy<CachedReplayAsset> htmlAsset = new(() => LoadCachedAsset("session-replay.html"));
    private static readonly Lazy<CachedReplayAsset> cssAsset = new(() => LoadCachedAsset("session-replay.css"));
    private static readonly Lazy<CachedReplayAsset> javascriptAsset = new(() => LoadCachedAsset("session-replay.js"));
    private static readonly ConcurrentDictionary<string, CachedReplayAsset> additionalAssets = new(StringComparer.Ordinal);
    private static readonly HttpClient simulatorHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };
    internal readonly RuntimeCoordinator runtime;
    private readonly LocalWebAnalytics analytics;
    private readonly LocalTestExecutionCoordinator testExecutions;
    private readonly LocalTaskExtractionCoordinator taskExtractions;
    private readonly WorkspaceTestDraftStore workspaceTestDrafts;
    internal readonly bool isExplorer;
    private readonly HttpListener listener;
    private readonly CancellationTokenSource shutdown;
    private readonly CancellationTokenRegistration featureLifetimeRegistration;
    private readonly Lock requestGate = new();
    private readonly HashSet<Task> activeRequests = [];
    private readonly ConcurrentDictionary<long, Channel<SessionCatalogEvent>> sessionEventSubscribers = [];
    private readonly Lock sessionCatalogGate = new();
    private readonly Dictionary<string, SessionCatalogFingerprint> sessionCatalogFingerprintById = new(StringComparer.Ordinal);
    private string? initialSessionId;
    private Task? runTask;
    private long nextSessionEventId;
    private long nextSessionEventSubscriberId;
    private bool sessionCatalogEventsSubscribed;
    private int stopping;
    private ExplorerServer(RuntimeCoordinator runtime, string serverKey, string? initialSessionId, bool isExplorer, int port, string? requestedPath)
    {
        this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        shutdown = CancellationTokenSource.CreateLinkedTokenSource(runtime.FeatureLifetime);
        analytics = new LocalWebAnalytics(runtime.Analytics);
        testExecutions = new LocalTestExecutionCoordinator(runtime);
        taskExtractions = new LocalTaskExtractionCoordinator(runtime);
        workspaceTestDrafts = new WorkspaceTestDraftStore(runtime.ApplicationPaths.ApplicationDataPath);
        this.isExplorer = isExplorer;
        ServerKey = serverKey;
        this.initialSessionId = initialSessionId;
        Port = port;
        ReplayUrl = new Uri($"http://127.0.0.1:{port}{ResolveReplayPath(requestedPath)}");
        listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        featureLifetimeRegistration = runtime.FeatureLifetime.Register(StopListening);
    }

    public string ServerKey { get; }
    public string SessionId => InitialSessionId ?? string.Empty;
    public string? InitialSessionId => Volatile.Read(ref initialSessionId);
    public int Port { get; }
    public Uri ReplayUrl { get; }
    public Task Completion => runTask ?? Task.CompletedTask;

    public static ExplorerServer Start(RuntimeCoordinator runtime, string serverKey, string? initialSessionId, bool isExplorer, int requestedPort, string? requestedPath = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        runtime.FeatureLifetime.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(serverKey);
        Exception? lastFailure = null;
        var attempts = requestedPort == 0 ? AutomaticPortAttempts : 1;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            var port = requestedPort == 0 ? FindAvailablePort() : requestedPort;
            var server = new ExplorerServer(runtime, serverKey, initialSessionId, isExplorer, port, requestedPath);
            try
            {
                server.listener.Start();
                server.SubscribeToSessionCatalogEvents();
                server.runTask = server.RunAsync();
                return server;
            }
            catch (HttpListenerException exception)
            {
                lastFailure = exception;
                server.featureLifetimeRegistration.Dispose();
                server.listener.Close();
                server.shutdown.Dispose();
            }
        }

        throw new InvalidOperationException(requestedPort == 0 ? "No loopback port was available for the local session server." : $"Loopback port {requestedPort} is unavailable.", lastFailure);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        StopListening();
        if (runTask is not null)
        {
            await runTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        await WaitForActiveRequestsAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        StopListening();
        if (runTask is not null)
        {
            try
            {
                await runTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        await WaitForActiveRequestsAsync(CancellationToken.None).ConfigureAwait(false);
        listener.Close();
        featureLifetimeRegistration.Dispose();
        testExecutions.Dispose();
        taskExtractions.Dispose();
        shutdown.Dispose();
    }

    private async Task RunAsync()
    {
        while (!shutdown.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().WaitAsync(shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
            {
                return;
            }
            catch (HttpListenerException) when (shutdown.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException) when (shutdown.IsCancellationRequested)
            {
                return;
            }

            var requestStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var requestTask = HandleRequestAfterTrackingAsync(context, requestStart.Task, shutdown.Token);
            lock (requestGate)
            {
                activeRequests.Add(requestTask);
            }

            requestStart.SetResult();
            _ = ObserveRequestAsync(requestTask);
        }
    }

    private async Task HandleRequestAfterTrackingAsync(HttpListenerContext context, Task requestStart, CancellationToken cancellationToken)
    {
        await requestStart.ConfigureAwait(false);
        await HandleRequestSafelyAsync(context, cancellationToken).ConfigureAwait(false);
    }

    private async Task ObserveRequestAsync(Task requestTask)
    {
        try
        {
            await requestTask.ConfigureAwait(false);
        }
        finally
        {
            lock (requestGate)
            {
                activeRequests.Remove(requestTask);
            }
        }
    }

    private async Task WaitForActiveRequestsAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task[] pending;
            lock (requestGate)
            {
                pending = activeRequests.ToArray();
            }

            if (pending.Length == 0)
            {
                return;
            }

            await Task.WhenAll(pending).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    internal async Task WaitForActiveRequestSnapshotAsync(CancellationToken cancellationToken = default)
    {
        Task[] pending;
        lock (requestGate)
        {
            pending = activeRequests.ToArray();
        }

        if (pending.Length > 0)
        {
            await Task.WhenAll(pending).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleRequestSafelyAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        var path = context.Request.Url?.AbsolutePath ?? "";
        var prefix = ReplayUrl.AbsolutePath;
        using var usage = new LocalRequestUsage(runtime.Analytics,
            path.StartsWith(prefix, StringComparison.Ordinal) ? path[prefix.Length..] : "",
            context.Request.HttpMethod, context.Response);
        try
        {
            await HandleRequestAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            TryClose(context.Response, HttpStatusCode.ServiceUnavailable);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or ArgumentException)
        {
            await TryWriteErrorAsync(context.Response, exception.Message, HttpStatusCode.BadRequest, cancellationToken).ConfigureAwait(false);
        }
        catch (Ansight.Infrastructure.Extensions.OptionalExtensionUnavailableException exception)
        {
            await TryWriteErrorAsync(context.Response, exception.Message, HttpStatusCode.Conflict, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await TryWriteErrorAsync(context.Response, exception.GetBaseException().Message, HttpStatusCode.InternalServerError, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        var request = context.Request;
        var response = context.Response;
        ApplySecurityHeaders(response);
        if (runtime.FeatureLifetime.IsCancellationRequested)
        {
            await WriteTextAsync(response, "product_access_required", HttpStatusCode.Forbidden, false, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var absolutePath = request.Url?.AbsolutePath ?? string.Empty;
        var replayPath = ReplayUrl.AbsolutePath;
        if (!absolutePath.StartsWith(replayPath, StringComparison.Ordinal))
        {
            await WriteTextAsync(response, "Not found.", HttpStatusCode.NotFound, false, cancellationToken).ConfigureAwait(false);
            return;
        }

        var route = absolutePath[replayPath.Length..];
        var isGet = string.Equals(request.HttpMethod, "GET", StringComparison.Ordinal);
        var isHead = string.Equals(request.HttpMethod, "HEAD", StringComparison.Ordinal);
        if (isGet || isHead)
        {
            if (await TryHandleGetAsync(route, request, response, isHead, cancellationToken).ConfigureAwait(false))
            {
                return;
            }
        }
        else if (string.Equals(request.HttpMethod, "POST", StringComparison.Ordinal))
        {
            if (string.Equals(route, "api/sessions/import", StringComparison.Ordinal))
            {
                EnsureArchiveRequest(request);
            }
            else
            {
                EnsureJsonRequest(request);
            }

            if (await TryHandlePostAsync(route, request, response, cancellationToken).ConfigureAwait(false))
            {
                return;
            }
        }
        else
        {
            response.Headers["Allow"] = "GET, HEAD, POST";
            await WriteTextAsync(response, "Method not allowed.", HttpStatusCode.MethodNotAllowed, false, cancellationToken).ConfigureAwait(false);
            return;
        }

        await WriteTextAsync(response, "Not found.", HttpStatusCode.NotFound, isHead, cancellationToken).ConfigureAwait(false);
    }

    internal static string RequireValue(string? value, string message) => string.IsNullOrWhiteSpace(value) ? throw new InvalidDataException(message) : value.Trim();
    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    internal static bool AcceptsServerSentEvents(HttpListenerRequest request) => request.AcceptTypes?.Any(static value => value.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase)) == true;
    private static IReadOnlyList<WorkspaceSanitizerDefinition> DiscoverWorkspaceSanitizers(string workspacePath)
    {
        var sanitizersPath = Path.Combine(Path.GetFullPath(workspacePath), "ansight", "sanitizers");
        if (!Directory.Exists(sanitizersPath))
        {
            return [];
        }

        return Directory.EnumerateFiles(sanitizersPath, "*.ts", SearchOption.AllDirectories).Where(static path => !path.EndsWith(".d.ts", StringComparison.OrdinalIgnoreCase)).OrderBy(static path => path, StringComparer.OrdinalIgnoreCase).Take(512).Select(path =>
        {
            var relativePath = Path.GetRelativePath(sanitizersPath, path);
            var sanitizerId = relativePath[..^Path.GetExtension(relativePath).Length].Replace(Path.DirectorySeparatorChar, '.').Replace(Path.AltDirectorySeparatorChar, '.');
            return new WorkspaceSanitizerDefinition(sanitizerId, Path.GetFullPath(path));
        }).ToArray();
    }

    private void RuntimeOnAppConnectionsChanged(object? sender, EventArgs eventArgs) => PublishSessionCatalogEvent("connections-changed", null);
    private void RuntimeAppsOnChanged(object? sender, EventArgs eventArgs) => PublishSessionCatalogEvent("apps-changed", null);
    private static void EnsureInRange(long value, long minimum, long maximum, string name)
    {
        if (value < minimum || value > maximum)
        {
            throw new InvalidDataException($"{name} must be between {minimum:N0} and {maximum:N0}.");
        }
    }

    private sealed record LiveSimulatorTarget(string DeviceIdentifier, string RemoteControlBaseUrl)
    {
        public static LiveSimulatorTarget Empty { get; } = new(string.Empty, string.Empty);
    }

    private sealed record SessionStorageBreakdown(long TotalSizeBytes, int FileCount, IReadOnlyList<SessionStorageBreakdownItem> Items);
    private sealed record SessionStorageBreakdownItem(string Label, long SizeBytes);
    internal async Task<AppSessionSnapshot?> LoadReplaySnapshotAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (runtime.AppTools.IsConnected(sessionId) && runtime.Sessions.TryGetReplaySnapshot(sessionId, out var liveSnapshot) && liveSnapshot is not null)
        {
            return liveSnapshot;
        }

        return await runtime.Sessions.LoadSnapshotAsync(sessionId, null, cancellationToken).ConfigureAwait(false);
    }

    private static async Task TryWriteErrorAsync(HttpListenerResponse response, string message, HttpStatusCode statusCode, CancellationToken cancellationToken)
    {
        try
        {
            await WriteJsonAsync(response, new OperationResult(false, message), statusCode, false, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            TryClose(response, statusCode);
        }
    }

    private readonly record struct CachedReplayAsset(byte[] Content, string EntityTag);
    private sealed record SessionCatalogEvent(long Sequence, string Reason, string? SessionId, DateTimeOffset OccurredUtc);
    private sealed record SessionCatalogFingerprint(string AppId, string? Name, string ClientName, string Status, bool IsConnected, bool IsHistorical, bool IsPinned, string? RuntimeDeviceIdentifier, string? DeviceOsName, bool IsVirtualDevice, bool IsEmulator, string AppIconKey, string TagsKey, DateTimeOffset? LastUpdatedUtc, int? LogCount, int? ImageCount, int? VisualTreeSnapshotCount, int? ArtifactSnapshotCount);
    private static int FindAvailablePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try
        {
            return ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        finally
        {
            probe.Stop();
        }
    }

    private static void TryClose(HttpListenerResponse response, HttpStatusCode statusCode)
    {
        try
        {
            response.StatusCode = (int)statusCode;
            response.Close();
        }
        catch
        {
        }
    }

    private void StopListening()
    {
        if (Interlocked.Exchange(ref stopping, 1) != 0)
        {
            return;
        }

        UnsubscribeFromSessionCatalogEvents();
        shutdown.Cancel();
        listener.Close();
    }
}
