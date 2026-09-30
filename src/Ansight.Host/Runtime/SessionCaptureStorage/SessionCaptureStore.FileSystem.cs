namespace Ansight.Host.Runtime.SessionCaptureStorage;

using AppLifecycleState = global::Ansight.AppLifecycleState;
using Ansight.Host;
using Ansight.Pairing.Models;
using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Threading.Channels;
using static SessionCaptureFileSystem;


internal sealed partial class SessionCaptureStore
{
    private SessionPathLayout GetSessionLayout(string appId, string sessionId)
    {
        var appDirectoryPath = Path.Combine(
            capturesRootPath,
            FileNameUtil.Sanitize(appId));
        var sessionDirectoryPath = Path.Combine(appDirectoryPath, FileNameUtil.Sanitize(sessionId));
        return new SessionPathLayout(
            appDirectoryPath,
            sessionDirectoryPath,
            Path.Combine(sessionDirectoryPath, SessionSummaryFileName),
            Path.Combine(sessionDirectoryPath, LogsBlobFileName),
            Path.Combine(sessionDirectoryPath, LogStreamsBlobFileName),
            Path.Combine(sessionDirectoryPath, LogSegmentsDirectoryName),
            Path.Combine(sessionDirectoryPath, DeviceProfileBlobFileName),
            Path.Combine(sessionDirectoryPath, AppToolCatalogBlobFileName),
            Path.Combine(sessionDirectoryPath, AnalysesBlobFileName),
            Path.Combine(sessionDirectoryPath, AnnotationsBlobFileName),
            Path.Combine(sessionDirectoryPath, AgentTaskLinksBlobFileName),
            Path.Combine(sessionDirectoryPath, ImagesBlobFileName),
            Path.Combine(sessionDirectoryPath, TouchesBlobFileName),
            Path.Combine(sessionDirectoryPath, ApplicationEventsBlobFileName),
            Path.Combine(sessionDirectoryPath, NetworkDirectoryName, NetworkRequestsDirectoryName),
            Path.Combine(sessionDirectoryPath, VisualTreesDirectoryName),
            Path.Combine(sessionDirectoryPath, ArtifactsDirectoryName),
            Path.Combine(sessionDirectoryPath, MetricChannelsBlobFileName),
            Path.Combine(sessionDirectoryPath, TelemetryDirectoryName),
            Path.Combine(sessionDirectoryPath, ImagesDirectoryName));
    }

    internal static string GetTelemetryBlobFilePath(SessionPathLayout layout, byte channelId)
    {
        return Path.Combine(layout.TelemetryDirectoryPath, $"channel-{channelId:D3}.json");
    }

    internal static string GetTelemetryAppendFilePath(SessionPathLayout layout)
        => Path.Combine(layout.TelemetryDirectoryPath, TelemetryAppendFileName);

    internal static string GetImagesAppendFilePath(SessionPathLayout layout)
        => Path.Combine(layout.SessionDirectoryPath, ImagesAppendFileName);

    internal static string GetTouchesAppendFilePath(SessionPathLayout layout)
        => Path.Combine(layout.SessionDirectoryPath, TouchesAppendFileName);

    internal static string GetLogsAppendFilePath(SessionPathLayout layout)
        => Path.Combine(layout.SessionDirectoryPath, LogsAppendFileName);

    private void RunWithSessionPersistenceLock(string sessionId, Action action)
        => RunWithSessionPersistenceLocks([sessionId], action);

    private T RunWithSessionPersistenceLock<T>(string sessionId, Func<T> action)
        => RunWithSessionPersistenceLocks([sessionId], action);

    private void RunWithSessionPersistenceLocks(IReadOnlyList<string> sessionIds, Action action)
        => RunWithSessionPersistenceLocks(
            sessionIds,
            () =>
            {
                action();
                return true;
            });

    private T RunWithSessionPersistenceLocks<T>(IReadOnlyList<string> sessionIds, Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(sessionIds);
        ArgumentNullException.ThrowIfNull(action);

        var normalizedSessionIds = sessionIds
            .Where(sessionId => !string.IsNullOrWhiteSpace(sessionId))
            .Select(sessionId => sessionId.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(sessionId => sessionId, StringComparer.Ordinal)
            .ToArray();
        var lockStates = new SessionPersistenceLockState[normalizedSessionIds.Length];

        lock (sessionPersistenceLocksGate)
        {
            for (var index = 0; index < normalizedSessionIds.Length; index++)
            {
                var sessionId = normalizedSessionIds[index];
                if (!persistenceLockStateBySessionId.TryGetValue(sessionId, out var lockState))
                {
                    lockState = new SessionPersistenceLockState();
                    persistenceLockStateBySessionId[sessionId] = lockState;
                }

                lockState.ReferenceCount++;
                lockStates[index] = lockState;
            }
        }

        try
        {
            return RunLocked(0);
        }
        finally
        {
            lock (sessionPersistenceLocksGate)
            {
                for (var index = normalizedSessionIds.Length - 1; index >= 0; index--)
                {
                    var lockState = lockStates[index];
                    lockState.ReferenceCount--;
                    if (lockState.ReferenceCount == 0
                        && persistenceLockStateBySessionId.TryGetValue(normalizedSessionIds[index], out var registeredState)
                        && ReferenceEquals(lockState, registeredState))
                    {
                        persistenceLockStateBySessionId.Remove(normalizedSessionIds[index]);
                    }
                }
            }
        }

        T RunLocked(int index)
        {
            if (index >= lockStates.Length)
            {
                return action();
            }

            lock (lockStates[index].Gate)
            {
                return RunLocked(index + 1);
            }
        }
    }

    internal static string ResolveTelemetryType(byte channelId, SessionMetricChannel? channel)
        => MetricChannelClassification.ResolveTelemetryType(channelId, channel);
}
