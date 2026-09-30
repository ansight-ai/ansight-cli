namespace Ansight.Host.Runtime.State;

using AppLifecycleState = global::Ansight.AppLifecycleState;
using System.Text;
using System.Text.Json.Nodes;
using Ansight.Host;
using Ansight.Pairing.Models;
using Ansight.Infrastructure.Logging;
using static RuntimeSnapshotNormalizer;

internal sealed partial class RuntimeState
{
    private async Task PersistSessionImageAsync(
        string sessionId,
        string appId,
        DateTimeOffset capturedAtUtc,
        string format,
        int width,
        int height,
        int quality,
        ReadOnlyMemory<byte> bytes)
    {
        var frame = await sessionCaptureStore.SaveSessionImageAsync(
            appId,
            sessionId,
            capturedAtUtc,
            format,
            width,
            height,
            quality,
            bytes);

        if (frame is null)
        {
            return;
        }

        UpdateSession(sessionId, session =>
        {
            session.Images.Add(frame);
            session.MarkImagesChanged();
        }, frame.CapturedAtUtc);
    }

    private SessionAppIcon? PersistSessionAppIcon(string sessionId, DeviceApplicationIconProfile? icon)
    {
        if (icon is null || string.IsNullOrWhiteSpace(icon.DataBase64))
        {
            return null;
        }

        string appId;
        lock (gate)
        {
            if (!sessionsById.TryGetValue(sessionId, out var session))
            {
                return null;
            }

            appId = session.AppId;
        }

        try
        {
            return sessionCaptureStore.SaveSessionAppIcon(appId, sessionId, icon);
        }
        catch (Exception ex)
        {
            log.Warning($"Failed to persist the app icon for session {sessionId}.");
            log.Exception(ex);
            return null;
        }
    }

}


internal sealed partial class RuntimeState
{
    private static void TrimRetainedTail<T>(List<T> entries, int maximumCount, int trimCount)
    {
        if (entries.Count <= maximumCount)
        {
            return;
        }

        var targetCount = Math.Max(0, maximumCount - trimCount);
        entries.RemoveRange(0, entries.Count - targetCount);
    }

}


internal sealed partial class RuntimeState
{
    private static bool IsSessionLive(SessionState state)
    {
        if (state.IsHistorical)
        {
            return false;
        }

        return IsSessionStatusLive(state.Status);
    }

    private static bool IsSessionStatusLive(string? status)
    {
        return !IsTerminalSessionStatus(status);
    }

    private static bool IsTerminalSessionStatus(string? status)
    {
        var normalizedStatus = status ?? string.Empty;
        return normalizedStatus.Contains("complete", StringComparison.OrdinalIgnoreCase)
               || normalizedStatus.Contains("error", StringComparison.OrdinalIgnoreCase)
               || normalizedStatus.Contains("closed", StringComparison.OrdinalIgnoreCase)
               || normalizedStatus.Contains("timeout", StringComparison.OrdinalIgnoreCase)
               || normalizedStatus.Contains("sign in required", StringComparison.OrdinalIgnoreCase)
               || normalizedStatus.Contains("rejected", StringComparison.OrdinalIgnoreCase);
    }

    internal static int ParseSessionNumber(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return 0;
        }

        var separatorIndex = sessionId.LastIndexOf('-');
        if (separatorIndex < 0 || separatorIndex == sessionId.Length - 1)
        {
            return 0;
        }

        return int.TryParse(sessionId[(separatorIndex + 1)..], out var value) ? value : 0;
    }

    internal static string NormalizeSessionAppHint(string? appId)
    {
        if (string.IsNullOrWhiteSpace(appId))
        {
            return DefaultSessionAppHint;
        }

        var builder = new StringBuilder();
        var previousWasSeparator = false;
        foreach (var rawCharacter in appId.Trim())
        {
            var character = char.ToLowerInvariant(rawCharacter);
            if (IsAsciiAlphaNumeric(character))
            {
                if (builder.Length >= MaximumSessionAppHintLength)
                {
                    break;
                }

                builder.Append(character);
                previousWasSeparator = false;
                continue;
            }

            if (builder.Length == 0 || previousWasSeparator || builder.Length >= MaximumSessionAppHintLength)
            {
                continue;
            }

            builder.Append('-');
            previousWasSeparator = true;
        }

        var appHint = builder.ToString().Trim('-');
        return string.IsNullOrWhiteSpace(appHint) ? DefaultSessionAppHint : appHint;
    }

    private string AllocateSessionIdCandidate(string appId)
    {
        lock (gate)
        {
            var sessionId = $"{NormalizeSessionAppHint(appId)}-{nextSessionNumber:D3}";
            nextSessionNumber++;
            return sessionId;
        }
    }

    private string AllocateAvailableSessionId(string appId)
    {
        while (true)
        {
            var candidateSessionId = AllocateSessionIdCandidate(appId);
            if (!SessionIdExists(candidateSessionId))
            {
                return candidateSessionId;
            }
        }
    }

    private static bool IsAsciiAlphaNumeric(char character)
    {
        return (character >= 'a' && character <= 'z')
               || (character >= '0' && character <= '9');
    }

    private bool TryResolveExistingSessionId(string appId, string processSessionId, out string sessionId)
    {
        lock (gate)
        {
            var loadedMatch = sessionsById.Values
                .Where(session => string.Equals(session.AppId, appId, StringComparison.Ordinal)
                                  && string.Equals(session.ProcessSessionId, processSessionId, StringComparison.Ordinal))
                .OrderByDescending(session => session.LastUpdatedUtc)
                .FirstOrDefault();
            if (loadedMatch is not null)
            {
                sessionId = loadedMatch.SessionId;
                return true;
            }
        }

        return sessionCaptureStore.TryFindSessionId(appId, processSessionId, out sessionId);
    }

    private string ResolveImportedSessionId(string preferredSessionId, string appId)
    {
        if (!SessionIdExists(preferredSessionId))
        {
            return preferredSessionId;
        }

        EnsureNextSessionNumberInitialized();

        return AllocateAvailableSessionId(appId);
    }

    private bool SessionIdExists(string sessionId)
    {
        lock (gate)
        {
            if (sessionsById.ContainsKey(sessionId))
            {
                return true;
            }
        }

        return sessionCaptureStore.IsSessionIdRetired(sessionId)
               || sessionCaptureStore.TryLoad(sessionId, out _);
    }

    private static AppSessionSnapshot CreateImportedSnapshot(
        AppSessionSnapshot snapshot,
        string resolvedSessionId,
        SessionReplaySource? replaySource)
    {
        var logStreams = SessionLogStreams.Normalize(snapshot.LogStreams, snapshot.Logs);
        return new AppSessionSnapshot
        {
            SessionId = resolvedSessionId,
            AppId = snapshot.AppId.Trim(),
            ClientName = snapshot.ClientName.Trim(),
            RemoteAddress = snapshot.RemoteAddress.Trim(),
            CreatedUtc = snapshot.CreatedUtc.ToUniversalTime(),
            ConfigId = string.IsNullOrWhiteSpace(snapshot.ConfigId) ? null : snapshot.ConfigId.Trim(),
            ProcessSessionId = string.IsNullOrWhiteSpace(snapshot.ProcessSessionId) ? null : snapshot.ProcessSessionId.Trim(),
            Status = string.IsNullOrWhiteSpace(snapshot.Status) ? "Imported" : snapshot.Status.Trim(),
            LastUpdatedUtc = SessionStateMapper.ResolveSessionTimelineEnd(snapshot),
            IsHistorical = true,
            CacheSizeBytes = snapshot.CacheSizeBytes,
            IsPinned = snapshot.IsPinned,
            Author = snapshot.Author,
            ReplaySource = replaySource ?? snapshot.ReplaySource,
            CaptureSource = snapshot.CaptureSource,
            SdkVersion = SessionSdkVersion.Resolve(snapshot.SdkVersion, snapshot.DeviceProfile, snapshot.DeviceProfileJson),
            Name = NormalizeSessionName(snapshot.Name),
            Tags = NormalizeTags(snapshot.Tags),
            Notes = NormalizeNotes(snapshot.Notes),
            CustomProperties = SessionSnapshotCloner.CloneCustomProperties(snapshot.CustomProperties),
            AppState = snapshot.AppState,
            AppStateChangedUtc = snapshot.AppStateChangedUtc,
            DeviceProfile = snapshot.DeviceProfile,
            DeviceProfileJson = snapshot.DeviceProfileJson,
            AppIcon = snapshot.AppIcon,
            AppToolCatalog = SessionSnapshotCloner.CloneAppToolCatalog(snapshot.AppToolCatalog),
            Analyses = snapshot.Analyses.ToArray(),
            Annotations = NormalizeAnnotations(snapshot.Annotations),
            AgentTaskLinks = snapshot.AgentTaskLinks
                .Select(taskLink => SessionSnapshotCloner.CloneAgentTaskLink(taskLink, resolvedSessionId))
                .ToArray(),
            Images = snapshot.Images.ToArray(),
            Touches = NormalizeTouches(snapshot.Touches),
            NetworkRequests = snapshot.NetworkRequests
                .Select(SessionNetworkRequestSanitizer.Normalize)
                .Where(static request => request is not null)
                .Cast<SessionNetworkRequest>()
                .OrderBy(static request => request.StartedAtUtc)
                .ThenBy(static request => request.Id, StringComparer.Ordinal)
                .ToArray(),
            VisualTreeSnapshots = NormalizeVisualTreeSnapshots(snapshot.VisualTreeSnapshots),
            ArtifactSnapshots = NormalizeArtifactSnapshots(snapshot.ArtifactSnapshots),
            ApplicationEvents = snapshot.ApplicationEvents
                .OrderBy(static appEvent => appEvent.CapturedAtUtc)
                .ThenBy(static appEvent => appEvent.EventId, StringComparer.Ordinal)
                .ToArray(),
            LogStreams = logStreams,
            Logs = SessionLogStreams.Flatten(logStreams),
            TotalLogCount = Math.Max(snapshot.TotalLogCount, snapshot.Logs.Count),
            RetainedLogStartIndex = snapshot.RetainedLogStartIndex,
            MetricChannels = snapshot.MetricChannels
                .OrderBy(channel => channel.ChannelId)
                .ToArray(),
            Metrics = snapshot.Metrics
                .OrderBy(metric => metric.CapturedAtUtc)
                .ThenBy(metric => metric.ChannelId)
                .ToArray(),
            TotalApplicationEventCount = Math.Max(snapshot.TotalApplicationEventCount, snapshot.ApplicationEvents.Count),
            TotalNetworkRequestCount = Math.Max(snapshot.TotalNetworkRequestCount, snapshot.NetworkRequests.Count)
        };
    }

    private static int ResolveNextTelemetrySegmentId(IReadOnlyList<SessionMetricSample> metrics)
    {
        if (metrics.Count == 0)
        {
            return 1;
        }

        var highestSegmentId = metrics.Max(metric => metric.SegmentId);
        return Math.Max(1, highestSegmentId + 1);
    }

    private static string? NormalizeProcessSessionId(string? processSessionId)
    {
        return string.IsNullOrWhiteSpace(processSessionId)
            ? null
            : processSessionId.Trim();
    }

    internal static string? NormalizeNotes(string? notes)
    {
        return string.IsNullOrWhiteSpace(notes)
            ? null
            : notes.Trim();
    }

    private static string? NormalizeSessionName(string? name)
    {
        return string.IsNullOrWhiteSpace(name)
            ? null
            : name.Trim();
    }

    private SessionCaptureAuthorMetadata? ResolveCurrentAuthor()
    { return null; }

    private static string? NormalizeAccountValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();
    }
}
