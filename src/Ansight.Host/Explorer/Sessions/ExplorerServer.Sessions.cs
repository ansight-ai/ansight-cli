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
    public void SelectInitialSession(string sessionId)
    {
        if (!isExplorer)
        {
            throw new InvalidOperationException("Only the session explorer can change its initial session.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        Volatile.Write(ref initialSessionId, sessionId.Trim());
    }

    private async Task<bool> TryHandleSessionsGetAsync(string route, HttpListenerRequest request, HttpListenerResponse response, bool isHead, CancellationToken cancellationToken)
    {
        switch (route)
        {
            case "api/bootstrap":
                await WriteJsonAsync(response, new SessionExplorerBootstrap("ansight.local-session-explorer/v1", isExplorer ? "explorer" : "session", InitialSessionId, true, true, true, isExplorer, isExplorer, isExplorer, isExplorer, isExplorer, isExplorer, isExplorer, runtime.Extensions.IsAvailable(), isExplorer, isExplorer, isExplorer && runtime.Extensions.IsAvailable(), isExplorer, isExplorer, isExplorer, BuildConfiguration.MapboxAccessToken) { ExtensionUi = runtime.Extensions.ReadUiEntryPoints() }, HttpStatusCode.OK, isHead, cancellationToken).ConfigureAwait(false);
                return true;
            case "api/sessions":
                await WriteJsonAsync(response, CreateSessionSummaries(), HttpStatusCode.OK, isHead, cancellationToken).ConfigureAwait(false);
                return true;
            case "api/session-events" when isExplorer:
                await StreamSessionEventsAsync(response, isHead, cancellationToken).ConfigureAwait(false);
                return true;
            case "api/trends/history" when isExplorer:
                await WriteJsonAsync(response, runtime.Trends.ListHistory(request.QueryString["appId"], request.QueryString["metric"], request.QueryString["decision"], ReadBoundedPositiveQueryInteger(request, "limit", 500, 10_000), request.QueryString["spanGroup"], limitPerSeries: true), HttpStatusCode.OK, isHead, cancellationToken).ConfigureAwait(false);
                return true;
            case "api/session":
                await WriteInitialSessionAsync(response, isHead, cancellationToken).ConfigureAwait(false);
                return true;
            case "api/session-cache" when isExplorer:
                await WriteJsonAsync(response, CreateSessionCachePlan(runtime.UserPreferences.SessionAutoCleanupRetentionDays, runtime.UserPreferences.SessionAutoCleanupMaximumCacheBytes), HttpStatusCode.OK, isHead, cancellationToken).ConfigureAwait(false);
                return true;
        }

        return false;
    }

    private async Task<bool> TryHandleSessionsPostAsync(string route, HttpListenerRequest request, HttpListenerResponse response, CancellationToken cancellationToken)
    {
        switch (route)
        {
            case "api/sessions/import" when isExplorer:
                await ImportSessionArchiveAsync(request, response, cancellationToken).ConfigureAwait(false);
                return true;
            case "api/sessions/bulk" when isExplorer:
                {
                    var body = await ReadJsonAsync<LocalSessionBulkOperationRequest>(request, cancellationToken).ConfigureAwait(false);
                    var result = ApplyBulkSessionOperation(body);
                    await WriteJsonAsync(response, result, result.IsSuccess ? HttpStatusCode.OK : HttpStatusCode.Conflict, false, cancellationToken).ConfigureAwait(false);
                    return true;
                }

            case "api/session-cache/plan" when isExplorer:
                {
                    var body = await ReadJsonAsync<LocalSessionCachePlanRequest>(request, cancellationToken).ConfigureAwait(false);
                    await WriteJsonAsync(response, CreateSessionCachePlan(body.RetentionDays, body.MaximumCacheSizeBytes), HttpStatusCode.OK, false, cancellationToken).ConfigureAwait(false);
                    return true;
                }

            case "api/session-cache/apply" when isExplorer:
                {
                    var body = await ReadJsonAsync<LocalSessionCacheApplyRequest>(request, cancellationToken).ConfigureAwait(false);
                    var result = ApplySessionCacheCleanup(body.SessionIds);
                    await WriteJsonAsync(response, result, result.IsSuccess ? HttpStatusCode.OK : HttpStatusCode.Conflict, false, cancellationToken).ConfigureAwait(false);
                    return true;
                }

            case "api/session-cache/compact" when isExplorer:
                {
                    var compacted = runtime.SessionCache.CompactSessionCache(runtime.UserPreferences.SessionAutoCompactionAgeDays);
                    await WriteJsonAsync(response, OperationResult.Success($"Compacted {compacted:N0} session cache(s)."), HttpStatusCode.OK, false, cancellationToken).ConfigureAwait(false);
                    return true;
                }

            case "api/trends/rebuild" when isExplorer:
                {
                    var body = await ReadJsonAsync<TrendsRebuildRequest>(request, cancellationToken).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(body.AppId))
                    {
                        await WriteJsonAsync(response, OperationResult.Failure("App ID is required."), HttpStatusCode.BadRequest, false, cancellationToken).ConfigureAwait(false);
                        return true;
                    }

                    var result = runtime.Trends.RebuildHistory(new WorkspaceTrendsHistoryRebuildRequest(AppId: body.AppId.Trim()), cancellationToken);
                    await WriteJsonAsync(response, result, result.Apps.Count > 0 ? HttpStatusCode.OK : HttpStatusCode.Conflict, false, cancellationToken).ConfigureAwait(false);
                    return true;
                }
        }

        return false;
    }

    private SessionCacheCleanupPlan CreateSessionCachePlan(int retentionDays, long maximumCacheSizeBytes)
    {
        EnsureInRange(retentionDays, SessionCleanupPreferenceDefaults.MinimumRetentionDays, SessionCleanupPreferenceDefaults.MaximumRetentionDays, "Session retention days");
        EnsureInRange(maximumCacheSizeBytes, SessionCleanupPreferenceDefaults.MinimumMaximumCacheBytes, SessionCleanupPreferenceDefaults.MaximumMaximumCacheBytes, "Session cache limit");
        var lastCleanup = runtime.LastSessionCacheCleanup;
        return runtime.SessionCache.CreateSessionCacheCleanupPlan(retentionDays, maximumCacheSizeBytes) with
        {
            AutoCleanupEnabled = runtime.UserPreferences.SessionAutoCleanupEnabled,
            LastAutoCleanupUtc = lastCleanup?.DeletedSessionCount > 0 ? lastCleanup.CompletedUtc : null,
            LastAutoCleanupDeletedCount = lastCleanup?.DeletedSessionCount ?? 0
        };
    }

    private LocalSessionCacheApplyResult ApplySessionCacheCleanup(IReadOnlyList<string>? requestedSessionIds)
    {
        var requestedIds = (requestedSessionIds ?? []).Where(static sessionId => !string.IsNullOrWhiteSpace(sessionId)).Select(static sessionId => sessionId.Trim()).Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        var plan = CreateSessionCachePlan(runtime.UserPreferences.SessionAutoCleanupRetentionDays, runtime.UserPreferences.SessionAutoCleanupMaximumCacheBytes);
        var eligibleIds = plan.Items.Select(static item => item.SessionId).ToHashSet(StringComparer.Ordinal);
        var items = new List<LocalSessionOperationItem>();
        foreach (var sessionId in requestedIds)
        {
            if (!eligibleIds.Contains(sessionId))
            {
                items.Add(new LocalSessionOperationItem(sessionId, false, "The session is not in the current cleanup plan."));
                continue;
            }

            var current = runtime.Sessions.GetSummaries().FirstOrDefault(session => string.Equals(session.SessionId, sessionId, StringComparison.Ordinal));
            if (current is null)
            {
                items.Add(new LocalSessionOperationItem(sessionId, false, "Session no longer exists."));
                continue;
            }

            if (current.IsPinned || runtime.IsSessionLive(sessionId))
            {
                items.Add(new LocalSessionOperationItem(sessionId, false, "Session became pinned or live after the cleanup plan was created."));
                continue;
            }

            var result = runtime.SessionEditing.Delete(sessionId);
            items.Add(new LocalSessionOperationItem(sessionId, result.IsSuccess, result.Message));
        }

        var deletedCount = items.Count(static item => item.IsSuccess);
        var failedCount = items.Count - deletedCount;
        return new LocalSessionCacheApplyResult(failedCount == 0, failedCount == 0 ? $"Deleted {deletedCount:N0} session(s) from the local cache." : $"Deleted {deletedCount:N0} session(s); {failedCount:N0} could not be deleted.", deletedCount, failedCount, items);
    }

    private LocalSessionBulkOperationResult ApplyBulkSessionOperation(LocalSessionBulkOperationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var operation = request.Operation?.Trim().ToLowerInvariant();
        if (operation is not ("delete" or "pin" or "unpin" or "disconnect"))
        {
            throw new InvalidDataException("Bulk session operation must be delete, pin, unpin, or disconnect.");
        }

        var items = new List<LocalSessionOperationItem>();
        foreach (var sessionId in request.SessionIds.Where(static sessionId => !string.IsNullOrWhiteSpace(sessionId)).Select(static sessionId => sessionId.Trim()).Distinct(StringComparer.Ordinal))
        {
            OperationResult result;
            if (operation == "delete")
            {
                result = runtime.SessionEditing.Delete(sessionId);
            }
            else if (operation == "disconnect")
            {
                result = runtime.AppTools.ForceDisconnect(sessionId);
            }
            else
            {
                var snapshot = runtime.Sessions.GetSummaries().FirstOrDefault(session => string.Equals(session.SessionId, sessionId, StringComparison.Ordinal));
                result = snapshot is null ? OperationResult.Failure($"Session '{sessionId}' was not found.") : runtime.SessionEditing.UpdateMetadata(sessionId, operation == "pin", snapshot.Tags, snapshot.Notes, snapshot.Name);
            }

            items.Add(new LocalSessionOperationItem(sessionId, result.IsSuccess, result.Message));
        }

        var succeededCount = items.Count(static item => item.IsSuccess);
        var failedCount = items.Count - succeededCount;
        return new LocalSessionBulkOperationResult(failedCount == 0, failedCount == 0 ? $"Applied '{operation}' to {succeededCount:N0} session(s)." : $"Applied '{operation}' to {succeededCount:N0} session(s); {failedCount:N0} failed.", succeededCount, failedCount, items);
    }

    internal async Task ImportSessionArchiveAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken cancellationToken)
    {
        var temporaryPath = Path.Combine(Path.GetTempPath(), $"ansight-local-import-{Guid.NewGuid():N}.ansight-session.zip");
        try
        {
            await CopyArchiveRequestToFileAsync(request, temporaryPath, cancellationToken).ConfigureAwait(false);
            var result = await runtime.SessionArchives.ImportSessionArchiveAsync(temporaryPath, cancellationToken, new SessionReplaySource { Kind = "local-web-import", DisplayName = "Local web import", Detail = "Imported through the local host web player" }).ConfigureAwait(false);
            await WriteJsonAsync(response, result, result.IsSuccess ? HttpStatusCode.Created : HttpStatusCode.BadRequest, false, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TryDeleteTemporaryFile(temporaryPath);
        }
    }

    private async Task ExportSessionArchiveAsync(HttpListenerResponse response, string sessionId, bool isHead, CancellationToken cancellationToken)
    {
        var snapshot = runtime.Sessions.GetSummaries().FirstOrDefault(session => string.Equals(session.SessionId, sessionId, StringComparison.Ordinal));
        if (snapshot is null)
        {
            await WriteJsonAsync(response, OperationResult.Failure($"Session '{sessionId}' was not found."), HttpStatusCode.NotFound, isHead, cancellationToken).ConfigureAwait(false);
            return;
        }

        var fileName = $"{FileNameUtil.Sanitize(snapshot.Name ?? snapshot.SessionId)}.ansight-session.zip";
        var temporaryPath = Path.Combine(Path.GetTempPath(), $"ansight-local-export-{Guid.NewGuid():N}-{fileName}");
        try
        {
            var result = await runtime.SessionArchives.ExportSessionArchiveAsync(sessionId, temporaryPath, cancellationToken).ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                await WriteJsonAsync(response, result, HttpStatusCode.Conflict, isHead, cancellationToken).ConfigureAwait(false);
                return;
            }

            await WriteDownloadFileAsync(response, temporaryPath, fileName, "application/zip", isHead, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TryDeleteTemporaryFile(temporaryPath);
        }
    }

    private static async Task CopyArchiveRequestToFileAsync(HttpListenerRequest request, string path, CancellationToken cancellationToken)
    {
        if (request.ContentLength64 > MaximumSessionArchiveUploadBytes)
        {
            throw new InvalidDataException("The session archive is too large.");
        }

        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true);
        var buffer = new byte[128 * 1024];
        long written = 0;
        while (true)
        {
            var count = await request.InputStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }

            written += count;
            if (written > MaximumSessionArchiveUploadBytes)
            {
                throw new InvalidDataException("The session archive is too large.");
            }

            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
        }

        if (written == 0)
        {
            throw new InvalidDataException("The session archive is empty.");
        }
    }

    private static bool TryParseTimelineTrimMode(string? value, out SessionTimelineTrimMode mode)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "cutselection":
            case "cut_selection":
            case "remove":
                mode = SessionTimelineTrimMode.CutSelection;
                return true;
            case "keepselectiononly":
            case "keep_selection_only":
            case "keep":
                mode = SessionTimelineTrimMode.KeepSelectionOnly;
                return true;
            default:
                mode = default;
                return false;
        }
    }

    private IReadOnlyList<SessionExplorerSummary> CreateSessionSummaries()
    {
        var registeredAppByAppId = runtime.Apps.GetDefinitions().ToDictionary(static app => app.AppId, StringComparer.Ordinal);
        return runtime.Sessions.GetSummaries().OrderByDescending(static session => session.LastUpdatedUtc).Select(session =>
        {
            var runtimeDeviceIdentifier = ResolveRuntimeDeviceIdentifier(session);
            registeredAppByAppId.TryGetValue(session.AppId, out var registeredApp);
            var sessionIconPath = ResolveSessionIconPath(session, registeredApp?.IconImagePath);
            var appName = string.IsNullOrWhiteSpace(registeredApp?.Name) ? session.ClientName : registeredApp.Name;
            var isVirtualDevice = session.DeviceProfile?.Device?.IsVirtual == true || session.DeviceProfile?.Device?.IsEmulator == true;
            string? runtimePlatform = null;
            if (!string.IsNullOrWhiteSpace(runtimeDeviceIdentifier) && runtime.Devices.TryGetKnownDevice(runtimeDeviceIdentifier, out var knownPlatform, out var knownKind))
            {
                runtimePlatform = knownPlatform;
                isVirtualDevice |= DeviceKinds.IsVirtual(knownKind);
            }
            else if (runtimeDeviceIdentifier?.StartsWith("emulator-", StringComparison.OrdinalIgnoreCase) == true)
            {
                runtimePlatform = DevicePlatforms.Android;
                isVirtualDevice = true;
            }

            if (runtimePlatform is null)
            {
                var capturedPlatform = VisualTreeContract.NormalizeRuntimePlatform(session.DeviceProfile?.Device?.OsName);
                runtimePlatform = capturedPlatform == VisualTreeContract.UnknownRuntimePlatform ? null : capturedPlatform;
            }

            var technologyEvidence = string.Join(" ", session.Tags
                .Concat(session.VisualTreeSnapshots.Select(static tree => tree.VisualTreeKind))
                .Concat(session.VisualTreeSnapshots.Select(static tree => tree.VisualTreeFormat))
                .Concat(session.VisualTreeSnapshots.Select(static tree => tree.Source))
                .Append(session.DeviceProfile?.Sdk?.Name)
                .Append(session.DeviceProfile?.Sdk?.PackageId)
                .Append(session.DeviceProfile?.Sdk?.Language)
                .Append(session.DeviceProfile?.Runtime?.Engine?.Name)
                .Append(session.CustomProperties?.ToJsonString())
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase));
            var technology = technologyEvidence.Contains("react-native", StringComparison.OrdinalIgnoreCase) || technologyEvidence.Contains("react native", StringComparison.OrdinalIgnoreCase) || technologyEvidence.Contains("reactNative", StringComparison.OrdinalIgnoreCase) ? "react-native"
                : technologyEvidence.Contains("flutter", StringComparison.OrdinalIgnoreCase) ? "flutter"
                : technologyEvidence.Contains("maui", StringComparison.OrdinalIgnoreCase) ? "dotnet-maui"
                : technologyEvidence.Contains("swiftui", StringComparison.OrdinalIgnoreCase) ? "swiftui"
                : technologyEvidence.Contains("compose", StringComparison.OrdinalIgnoreCase) ? "jetpack-compose"
                : technologyEvidence.Contains("dotnet", StringComparison.OrdinalIgnoreCase) || technologyEvidence.Contains(".net", StringComparison.OrdinalIgnoreCase) ? "dotnet"
                : technologyEvidence.Contains("kotlin", StringComparison.OrdinalIgnoreCase) ? "kotlin"
                : technologyEvidence.Contains("swift", StringComparison.OrdinalIgnoreCase) ? "swift"
                : null;

            return new SessionExplorerSummary(session.SessionId, session.AppId, appName, sessionIconPath is null ? null : $"api/sessions/{Uri.EscapeDataString(session.SessionId)}/icon", session.Name, session.ClientName, session.Status, runtime.IsSessionLive(session.SessionId), isVirtualDevice, runtimeDeviceIdentifier, runtimePlatform, technology, session.IsHistorical, session.IsPinned, session.CreatedUtc, session.LastUpdatedUtc, session.TotalLogCount, session.TotalImageCount, session.VisualTreeSnapshots.Count, session.ArtifactSnapshots.Count, session.Tags)
            {
                CaptureSource = session.CaptureSource
            };
        }).ToArray();
    }

    private async Task StreamSessionEventsAsync(HttpListenerResponse response, bool isHead, CancellationToken cancellationToken)
    {
        response.StatusCode = (int)HttpStatusCode.OK;
        response.ContentType = "text/event-stream; charset=utf-8";
        response.Headers["Cache-Control"] = "no-cache";
        response.Headers["X-Accel-Buffering"] = "no";
        response.KeepAlive = true;
        response.SendChunked = true;
        if (isHead)
        {
            response.Close();
            return;
        }

        var subscriberId = Interlocked.Increment(ref nextSessionEventSubscriberId);
        var events = Channel.CreateBounded<SessionCatalogEvent>(new BoundedChannelOptions(SessionEventBufferCapacity) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false });
        sessionEventSubscribers[subscriberId] = events;
        try
        {
            await WriteServerSentEventAsync(response, "event: ready\nretry: 3000\ndata: {}\n\n", cancellationToken).ConfigureAwait(false);
            while (!cancellationToken.IsCancellationRequested)
            {
                SessionCatalogEvent? catalogEvent = null;
                using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                readTimeout.CancelAfter(SessionEventHeartbeatInterval);
                try
                {
                    catalogEvent = await events.Reader.ReadAsync(readTimeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                }

                if (catalogEvent is null)
                {
                    await WriteServerSentEventAsync(response, ": keepalive\n\n", cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var payload = JsonSerializer.Serialize(catalogEvent, jsonOptions);
                await WriteServerSentEventAsync(response, $"id: {catalogEvent.Sequence}\nevent: sessions-changed\ndata: {payload}\n\n", cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or HttpListenerException or ObjectDisposedException)
        {
        }
        finally
        {
            sessionEventSubscribers.TryRemove(subscriberId, out _);
            events.Writer.TryComplete();
            try
            {
                response.Close();
            }
            catch
            {
            }
        }
    }

    private void SubscribeToSessionCatalogEvents()
    {
        if (!isExplorer || sessionCatalogEventsSubscribed)
        {
            return;
        }

        runtime.SessionUpdated += RuntimeOnSessionUpdated;
        runtime.SessionDeleted += RuntimeOnSessionDeleted;
        runtime.AppConnectionsChanged += RuntimeOnAppConnectionsChanged;
        runtime.Apps.Changed += RuntimeAppsOnChanged;
        sessionCatalogEventsSubscribed = true;
    }

    private void UnsubscribeFromSessionCatalogEvents()
    {
        if (!sessionCatalogEventsSubscribed)
        {
            return;
        }

        runtime.SessionUpdated -= RuntimeOnSessionUpdated;
        runtime.SessionDeleted -= RuntimeOnSessionDeleted;
        runtime.AppConnectionsChanged -= RuntimeOnAppConnectionsChanged;
        runtime.Apps.Changed -= RuntimeAppsOnChanged;
        sessionCatalogEventsSubscribed = false;
    }

    private void RuntimeOnSessionUpdated(object? sender, AppSessionSnapshot snapshot)
    {
        var fingerprint = CreateSessionCatalogFingerprint(snapshot);
        lock (sessionCatalogGate)
        {
            if (sessionCatalogFingerprintById.TryGetValue(snapshot.SessionId, out var previous) && previous == fingerprint)
            {
                return;
            }

            sessionCatalogFingerprintById[snapshot.SessionId] = fingerprint;
        }

        PublishSessionCatalogEvent("session-updated", snapshot.SessionId);
    }

    private void RuntimeOnSessionDeleted(object? sender, string sessionId)
    {
        lock (sessionCatalogGate)
        {
            sessionCatalogFingerprintById.Remove(sessionId);
        }

        PublishSessionCatalogEvent("session-deleted", sessionId);
    }

    private SessionCatalogFingerprint CreateSessionCatalogFingerprint(AppSessionSnapshot session)
    {
        var isConnected = runtime.IsSessionLive(session.SessionId);
        var appIconKey = session.AppIcon is null ? string.Empty : $"{session.AppIcon.FileName}:{session.AppIcon.Format}:{session.AppIcon.ByteCount}";
        return new SessionCatalogFingerprint(session.AppId, session.Name, session.ClientName, session.Status, isConnected, session.IsHistorical, session.IsPinned, ResolveRuntimeDeviceIdentifier(session), session.DeviceProfile?.Device?.OsName, session.DeviceProfile?.Device?.IsVirtual == true, session.DeviceProfile?.Device?.IsEmulator == true, appIconKey, string.Join('\u001f', session.Tags), isConnected ? null : session.LastUpdatedUtc, isConnected ? null : session.TotalLogCount, isConnected ? null : session.TotalImageCount, isConnected ? null : session.VisualTreeSnapshots.Count, isConnected ? null : session.ArtifactSnapshots.Count);
    }

    private void PublishSessionCatalogEvent(string reason, string? sessionId)
    {
        var catalogEvent = new SessionCatalogEvent(Interlocked.Increment(ref nextSessionEventId), reason, sessionId, DateTimeOffset.UtcNow);
        foreach (var events in sessionEventSubscribers.Values)
        {
            events.Writer.TryWrite(catalogEvent);
        }
    }

    private async Task WriteSessionIconAsync(HttpListenerResponse response, string sessionId, bool isHead, CancellationToken cancellationToken)
    {
        var snapshot = runtime.Sessions.GetSummaries().FirstOrDefault(session => string.Equals(session.SessionId, sessionId, StringComparison.Ordinal));
        var iconPath = snapshot is null ? null : ResolveSessionIconPath(snapshot);
        if (iconPath is null && snapshot?.AppIcon is not null && runtime.SessionCache.ExpandSessionCache(sessionId))
        {
            iconPath = ResolveSessionIconPath(snapshot);
        }

        if (iconPath is null)
        {
            await WriteTextAsync(response, "App icon not found.", HttpStatusCode.NotFound, isHead, cancellationToken).ConfigureAwait(false);
            return;
        }

        await WriteImageFileAsync(response, iconPath, isHead, cancellationToken).ConfigureAwait(false);
    }

    private string? ResolveSessionIconPath(AppSessionSnapshot session) => ResolveSessionIconPath(session, runtime.Apps.Get(session.AppId)?.IconImagePath);
    private string? ResolveSessionIconPath(AppSessionSnapshot session, string? registeredIconPath)
    {
        if (session.AppIcon is not null)
        {
            var capturesRootPath = SessionAppIconArtifactPath.ResolveSessionCapturesRootPath(runtime.ApplicationPaths);
            var capturedIconPath = SessionAppIconArtifactPath.ResolveSessionIconPath(capturesRootPath, session.AppId, session.SessionId, session.AppIcon);
            if (File.Exists(capturedIconPath))
            {
                return capturedIconPath;
            }
        }

        return !string.IsNullOrWhiteSpace(registeredIconPath) && File.Exists(registeredIconPath) ? registeredIconPath : null;
    }

    private async Task WriteInitialSessionAsync(HttpListenerResponse response, bool isHead, CancellationToken cancellationToken)
    {
        if (InitialSessionId is null)
        {
            await WriteTextAsync(response, "No initial session was selected.", HttpStatusCode.NotFound, isHead, cancellationToken).ConfigureAwait(false);
            return;
        }

        await WriteSessionAsync(response, InitialSessionId, isHead, cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteSessionAsync(HttpListenerResponse response, string sessionId, bool isHead, CancellationToken cancellationToken)
    {
        var snapshot = await LoadReplaySnapshotAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            await WriteTextAsync(response, "Session not found.", HttpStatusCode.NotFound, isHead, cancellationToken).ConfigureAwait(false);
            return;
        }

        await WriteJsonAsync(response, CreateSessionPresentation(snapshot), HttpStatusCode.OK, isHead, cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteSessionUpdateAsync(HttpListenerRequest request, HttpListenerResponse response, string sessionId, bool isHead, CancellationToken cancellationToken)
    {
        var cursor = new SessionLiveUpdateCursor(ReadNonNegativeQueryInteger(request, "logIndex"), ReadNonNegativeQueryInteger(request, "imageIndex"), ReadNonNegativeQueryInteger(request, "touchIndex"), ReadNonNegativeQueryInteger(request, "networkRequestIndex"), ReadNonNegativeQueryInteger(request, "visualTreeIndex"), ReadNonNegativeQueryInteger(request, "artifactIndex"), ReadNonNegativeQueryInteger(request, "metricIndex"), ReadNonNegativeQueryInteger(request, "metricChannelCount"), ReadNonNegativeQueryInteger(request, "annotationCount"), ReadNonNegativeQueryInteger(request, "analysisCount"));
        if (!runtime.Sessions.TryGetLiveUpdate(sessionId, cursor, out var update) || update is null)
        {
            await WriteTextAsync(response, "Session not found.", HttpStatusCode.NotFound, isHead, cancellationToken).ConfigureAwait(false);
            return;
        }

        await WriteJsonAsync(response, CreateSessionPresentation(update), HttpStatusCode.OK, isHead, cancellationToken).ConfigureAwait(false);
    }

    private static JsonNode CreateSessionPresentation<T>(T value)
    {
        var presentation = JsonSerializer.SerializeToNode(value, jsonOptions) ?? new JsonObject();
        VisualTreePresentationNormalizer.NormalizeSerializedSession(presentation);
        return presentation;
    }

    private async Task WriteSessionStorageBreakdownAsync(HttpListenerResponse response, string sessionId, bool isHead, CancellationToken cancellationToken)
    {
        var snapshot = await LoadReplaySnapshotAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            await WriteTextAsync(response, "Session not found.", HttpStatusCode.NotFound, isHead, cancellationToken).ConfigureAwait(false);
            return;
        }

        var capturesRootPath = SessionImageArtifactPath.ResolveSessionCapturesRootPath(runtime.ApplicationPaths);
        var sessionDirectoryPath = SessionImageArtifactPath.ResolveSessionDirectoryPath(capturesRootPath, snapshot.AppId, snapshot.SessionId);
        var breakdown = CreateSessionStorageBreakdown(sessionDirectoryPath);
        if (breakdown.TotalSizeBytes <= 0 && runtime.SessionCache.TryGetSizeBytes(snapshot.SessionId, out var cacheSizeBytes) && cacheSizeBytes > 0)
        {
            breakdown = new SessionStorageBreakdown(cacheSizeBytes, 1, [new SessionStorageBreakdownItem("Other files", cacheSizeBytes)]);
        }

        await WriteJsonAsync(response, breakdown, HttpStatusCode.OK, isHead, cancellationToken).ConfigureAwait(false);
    }

    private static SessionStorageBreakdown CreateSessionStorageBreakdown(string sessionDirectoryPath)
    {
        if (!Directory.Exists(sessionDirectoryPath))
        {
            return new SessionStorageBreakdown(0, 0, []);
        }

        var entries = new List<SessionStorageBreakdownItem>();
        var knownPaths = new HashSet<string>(StringComparer.Ordinal);
        AddStorageBreakdownFile(entries, knownPaths, "Session summary", Path.Combine(sessionDirectoryPath, "session.json"));
        AddStorageBreakdownFile(entries, knownPaths, "Logs", Path.Combine(sessionDirectoryPath, "logs.json"));
        AddStorageBreakdownFile(entries, knownPaths, "Logs", Path.Combine(sessionDirectoryPath, "logs.jsonl"));
        AddStorageBreakdownFile(entries, knownPaths, "Logs", Path.Combine(sessionDirectoryPath, "log-streams.json"));
        AddStorageBreakdownDirectory(entries, knownPaths, "Logs", Path.Combine(sessionDirectoryPath, "log-segments"));
        AddStorageBreakdownFile(entries, knownPaths, "Device profile", Path.Combine(sessionDirectoryPath, "device-profile.json"));
        AddStorageBreakdownFile(entries, knownPaths, "Analyses", Path.Combine(sessionDirectoryPath, "analyses.json"));
        AddStorageBreakdownFile(entries, knownPaths, "Annotations", Path.Combine(sessionDirectoryPath, "annotations.json"));
        AddStorageBreakdownFile(entries, knownPaths, "Agent task links", Path.Combine(sessionDirectoryPath, "agent-tasks.json"));
        AddStorageBreakdownFile(entries, knownPaths, "Image index", Path.Combine(sessionDirectoryPath, "images.json"));
        AddStorageBreakdownFile(entries, knownPaths, "Image index", Path.Combine(sessionDirectoryPath, "images.jsonl"));
        AddStorageBreakdownFile(entries, knownPaths, "Touch input", Path.Combine(sessionDirectoryPath, "touches.json"));
        AddStorageBreakdownFile(entries, knownPaths, "Touch input", Path.Combine(sessionDirectoryPath, "touches.jsonl"));
        AddStorageBreakdownFile(entries, knownPaths, "Application events", Path.Combine(sessionDirectoryPath, "application-events.json"));
        AddStorageBreakdownDirectory(entries, knownPaths, "Network requests", Path.Combine(sessionDirectoryPath, "network", "requests"));
        AddStorageBreakdownFile(entries, knownPaths, "Trends results", Path.Combine(sessionDirectoryPath, "trends-results.json"));
        AddStorageBreakdownFile(entries, knownPaths, "Metric channels", Path.Combine(sessionDirectoryPath, "metric-channels.json"));
        AddStorageBreakdownDirectory(entries, knownPaths, "Telemetry", Path.Combine(sessionDirectoryPath, "telemetry"));
        AddStorageBreakdownDirectory(entries, knownPaths, "Screenshots", Path.Combine(sessionDirectoryPath, "images"));
        AddStorageBreakdownDirectory(entries, knownPaths, "Visual trees", Path.Combine(sessionDirectoryPath, "visual-trees"));
        AddStorageBreakdownDirectory(entries, knownPaths, "Artifacts", Path.Combine(sessionDirectoryPath, "artifacts"));
        AddStorageBreakdownFile(entries, knownPaths, "Visual trees", Path.Combine(sessionDirectoryPath, "visual-trees.json"));
        AddStorageBreakdownFiles(entries, knownPaths, "App assets", EnumerateStorageFiles(sessionDirectoryPath, "app-icon.*", SearchOption.TopDirectoryOnly));
        var directoryFiles = EnumerateStorageFiles(sessionDirectoryPath, "*", SearchOption.AllDirectories).ToArray();
        var actualTotalBytes = directoryFiles.Sum(static filePath => GetStorageFileSizeBytes(filePath));
        var knownBytes = entries.Sum(static entry => entry.SizeBytes);
        var otherBytes = Math.Max(0, actualTotalBytes - knownBytes);
        if (otherBytes > 0)
        {
            entries.Add(new SessionStorageBreakdownItem("Other files", otherBytes));
        }

        var items = entries.Where(static entry => entry.SizeBytes > 0).GroupBy(static entry => entry.Label, StringComparer.Ordinal).Select(static group => new SessionStorageBreakdownItem(group.Key, group.Sum(static entry => entry.SizeBytes))).OrderByDescending(static entry => entry.SizeBytes).ToArray();
        return new SessionStorageBreakdown(Math.Max(actualTotalBytes, items.Sum(static entry => entry.SizeBytes)), directoryFiles.Length, items);
    }

    private static void AddStorageBreakdownFile(ICollection<SessionStorageBreakdownItem> entries, ISet<string> knownPaths, string label, string filePath) => AddStorageBreakdownFiles(entries, knownPaths, label, [filePath]);
    private static void AddStorageBreakdownDirectory(ICollection<SessionStorageBreakdownItem> entries, ISet<string> knownPaths, string label, string directoryPath) => AddStorageBreakdownFiles(entries, knownPaths, label, EnumerateStorageFiles(directoryPath, "*", SearchOption.AllDirectories));
    private static void AddStorageBreakdownFiles(ICollection<SessionStorageBreakdownItem> entries, ISet<string> knownPaths, string label, IEnumerable<string> filePaths)
    {
        var sizeBytes = filePaths.Sum(filePath => GetStorageFileSizeBytes(filePath, knownPaths));
        if (sizeBytes > 0)
        {
            entries.Add(new SessionStorageBreakdownItem(label, sizeBytes));
        }
    }

    private static IEnumerable<string> EnumerateStorageFiles(string directoryPath, string searchPattern, SearchOption searchOption)
    {
        if (!Directory.Exists(directoryPath))
        {
            return [];
        }

        try
        {
            return Directory.EnumerateFiles(directoryPath, searchPattern, searchOption).ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static long GetStorageFileSizeBytes(string filePath, ISet<string>? knownPaths = null)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return 0;
            }

            var normalizedPath = Path.GetFullPath(filePath);
            if (knownPaths is not null && !knownPaths.Add(normalizedPath))
            {
                return 0;
            }

            return Math.Max(0, new FileInfo(normalizedPath).Length);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private async Task WriteFrameAsync(HttpListenerResponse response, string sessionId, string frameId, bool isHead, CancellationToken cancellationToken)
    {
        var snapshot = await LoadReplaySnapshotAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var frame = snapshot?.Images.FirstOrDefault(candidate => string.Equals(candidate.FrameId, frameId, StringComparison.Ordinal));
        if (snapshot is null || frame is null)
        {
            await WriteTextAsync(response, "Frame not found.", HttpStatusCode.NotFound, isHead, cancellationToken).ConfigureAwait(false);
            return;
        }

        var capturesRootPath = SessionImageArtifactPath.ResolveSessionCapturesRootPath(runtime.ApplicationPaths);
        var framePath = SessionImageArtifactPath.ResolveCapturedImagePath(capturesRootPath, snapshot.AppId, snapshot.SessionId, frame);
        if (!File.Exists(framePath))
        {
            runtime.SessionCache.ExpandSessionCache(sessionId);
        }

        if (!File.Exists(framePath))
        {
            await WriteTextAsync(response, "Frame not found.", HttpStatusCode.NotFound, isHead, cancellationToken).ConfigureAwait(false);
            return;
        }

        var file = new FileInfo(framePath);
        response.StatusCode = (int)HttpStatusCode.OK;
        response.ContentType = ResolveImageContentType(file.Extension);
        response.ContentLength64 = file.Length;
        if (!isHead)
        {
            await using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 128 * 1024, useAsync: true);
            await stream.CopyToAsync(response.OutputStream, cancellationToken).ConfigureAwait(false);
        }

        response.Close();
    }

    private static void EnsureArchiveRequest(HttpListenerRequest request)
    {
        if (request.ContentType?.StartsWith("application/zip", StringComparison.OrdinalIgnoreCase) == true || request.ContentType?.StartsWith("application/octet-stream", StringComparison.OrdinalIgnoreCase) == true)
        {
            return;
        }

        throw new InvalidDataException("Session archive uploads must use application/zip or application/octet-stream.");
    }
}
