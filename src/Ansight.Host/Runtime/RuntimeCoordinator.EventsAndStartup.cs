using System.ComponentModel.Composition;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ansight.Infrastructure.Security;
using Ansight.Infrastructure.Logging;
using Ansight.Tools;
using Ansight.Host.Runtime.Automation;

namespace Ansight.Host.Runtime;

public sealed partial class RuntimeCoordinator
{
    private void HandleLogAdded(object? sender, LogEntry entry)
    {
        LogReceived?.Invoke(
            this,
            new RuntimeLogEntry(
                entry.TimestampUtc,
                entry.Message,
                entry.Source ?? "Host",
                entry.Tag,
                null));
    }

    private void HandleRuntimeEventOccurred(object? sender, RuntimeEvent runtimeEvent)
    {
        try
        {
            if (AutomationEventNormalizer.TryGetTriggerIndex(runtimeEvent, out var triggerIndex)
                && RepositoryAutomations.HasCandidates(triggerIndex.EventKind, triggerIndex.AppId)
                && AutomationEventNormalizer.TryNormalize(runtimeEvent, out var automationEvent)
                && automationEvent is not null)
            {
                RepositoryAutomations.Publish(automationEvent);
            }

            RaiseRuntimeEvent(runtimeEvent);
        }
        finally
        {
            if (runtimeEvent is RuntimeSessionCaptureEvent
                {
                    Kind: RuntimeSessionCaptureEventKind.Finalized
                } finalizedSession)
            {
                Trends.QueueRegisteredSessionEvaluation(finalizedSession.SessionId);
            }
        }
    }

    private void HandleSessionUpdated(object? sender, AppSessionSnapshot snapshot)
    {
        SessionUpdated?.Invoke(this, snapshot);
    }

    private void HandleSessionLogsAdded(object? sender, SessionLogBatchEventArgs batch)
    {
        if (RepositoryAutomations.HasCandidates(
                AutomationEventNormalizer.SessionLogReceivedKind,
                batch.AppId))
        {
            foreach (var entry in batch.Entries)
            {
                RepositoryAutomations.Publish(AutomationEventNormalizer.NormalizeLog(batch, entry));
            }
        }

        SessionLogsAdded?.Invoke(this, batch);
    }

    private void HandleRepositoryAutomationRunCompleted(
        object? sender,
        AutomationRunCompletedEvent completedEvent)
    {
        log.Info(
            $"repository_automation_completed runId={completedEvent.RunId} attempt={completedEvent.AttemptNumber}/{completedEvent.MaximumAttempts} triggerId={completedEvent.TriggerId} automationId={completedEvent.AutomationId} eventId={completedEvent.EventId} status={completedEvent.Status} willRetry={completedEvent.WillRetry} exitCode={completedEvent.ExitCode}");
        AutomationRunCompleted?.Invoke(this, completedEvent);
        Analytics.RecordUsage("automation",
            outcome: completedEvent.Status switch
            {
                AutomationRunStatus.Succeeded => "succeeded", AutomationRunStatus.Cancelled => "cancelled",
                AutomationRunStatus.Rejected => "blocked", _ => "failed"
            }, durationSeconds: Math.Max(0, (completedEvent.CompletedAtUtc - completedEvent.StartedAtUtc).TotalSeconds));
    }

    private void HandleSessionDeleted(object? sender, string sessionId)
    {
        SessionDeleted?.Invoke(this, sessionId);
    }

    private void HandlePairingServerStatusChanged(object? sender, string status)
    {
        StatusChanged?.Invoke(this, status);
    }

    private void HandleAppToolBridgeConnectionsChanged(object? sender, EventArgs e)
    {
        AppConnectionsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RaiseRuntimeEvent(RuntimeEvent runtimeEvent)
    {
        ArgumentNullException.ThrowIfNull(runtimeEvent);

        RuntimeEventLogger.Log(runtimeEvent);
        EventOccurred?.Invoke(this, runtimeEvent);

        switch (runtimeEvent)
        {
            case RuntimeLifecycleEvent lifecycleEvent:
                LifecycleEventOccurred?.Invoke(this, lifecycleEvent);
                break;
            case RuntimePairingEvent pairingEvent:
                PairingEventOccurred?.Invoke(this, pairingEvent);
                if (pairingEvent.Kind is RuntimePairingEventKind.PairingAccepted or RuntimePairingEventKind.PairingRejected)
                    Analytics.RecordUsage("pairing",
                        outcome: pairingEvent.Kind == RuntimePairingEventKind.PairingAccepted ? "succeeded" : "blocked");
                break;
            case RuntimeClientAppStateChangedEvent clientAppStateChangedEvent:
                ClientAppStateChanged?.Invoke(this, clientAppStateChangedEvent);
                break;
            case RuntimeSessionCaptureEvent sessionCaptureEvent:
                SessionCaptureEventOccurred?.Invoke(this, sessionCaptureEvent);
                break;
            case RuntimeSessionTransferEvent sessionTransferEvent:
                SessionTransferEventOccurred?.Invoke(this, sessionTransferEvent);
                break;
            case RuntimeAppEvent appEvent:
                AppEventOccurred?.Invoke(this, appEvent);
                break;
            case RuntimeTrendsEvent trendsEvent:
                TrendsEventOccurred?.Invoke(this, trendsEvent);
                break;
        }
    }

    private static async Task WaitForTasksAsync(Task? pairingTask, CancellationToken cancellationToken)
    {
        if (pairingTask is null)
        {
            return;
        }

        if (!cancellationToken.CanBeCanceled)
        {
            await pairingTask;
            return;
        }

        var cancellationTask = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        var completedTask = await Task.WhenAny(pairingTask, cancellationTask);
        if (completedTask != pairingTask)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        await pairingTask;
    }

    private async Task ResetStartedStateAsync(
        CancellationTokenSource? currentShutdownTokenSource,
        Task? currentPairingTask,
        Task? currentAnalyticsTask)
    {
        lock (gate)
        {
            if (ReferenceEquals(shutdownTokenSource, currentShutdownTokenSource))
            {
                started = false;
                startupWarnings = [];
                pairingTask = null;
                analyticsTask = null;
                shutdownTokenSource = null;
            }
        }

        currentShutdownTokenSource?.Cancel();
        await ObserveAndIgnoreTaskFailuresAsync(currentPairingTask, currentAnalyticsTask);
        currentShutdownTokenSource?.Dispose();
    }

    private static async Task ObserveAndIgnoreTaskFailuresAsync(params Task?[] tasks)
    {
        foreach (var task in tasks)
        {
            if (task is null) continue;
            try
            {
                await task;
            }
            catch (Exception suppressedException)
            {
                System.Diagnostics.Trace.TraceWarning(suppressedException.ToString());
            }
        }
    }

    private static Exception? GetTaskFailure(Task? currentPairingTask)
    {
        return currentPairingTask?.IsFaulted == true
            ? currentPairingTask.Exception?.GetBaseException()
            : null;
    }

    private async Task WaitForStartupStateAsync(
        Task? currentPairingTask,
        CancellationToken cancellationToken)
    {
        var timeoutAtUtc = DateTimeOffset.UtcNow.AddSeconds(5);

        while (DateTimeOffset.UtcNow < timeoutAtUtc)
        {
            if (GetTaskFailure(currentPairingTask) is not null)
            {
                return;
            }

            var pairingReady = runtimeState.IsServerRunning
                               || currentPairingTask?.IsCompleted == true
                               || !string.Equals(runtimeState.ServerStatusText, "UDP pairing listener not started.", StringComparison.Ordinal);
            if (pairingReady)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
        }
    }

    private string[] CaptureStartupWarnings()
    {
        var warnings = new List<string>();
        if (!runtimeState.IsServerRunning
            && !string.IsNullOrWhiteSpace(runtimeState.ServerStatusText)
            && !string.Equals(runtimeState.ServerStatusText, "UDP pairing listener not started.", StringComparison.Ordinal))
        {
            warnings.Add(runtimeState.ServerStatusText.Trim());
        }

        return warnings
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private void StoreStartupWarnings(IReadOnlyList<string> warnings)
    {
        lock (gate)
        {
            startupWarnings = warnings.Count == 0
                ? []
                : warnings.ToArray();
        }
    }

    private string CreateStartupStatusMessage(IReadOnlyList<string> warnings)
    {
        if (warnings.Count == 0)
        {
            return "Host runtime started.";
        }

        return warnings.Count == 1
            ? warnings[0]
            : string.Join(Environment.NewLine, warnings);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

}
