using System.Diagnostics;
using Ansight.Host.Runtime.Tasks;

namespace Ansight.Host.UiAutomation;

/// <summary>
/// Per-connection state for direct interaction with an existing app session.
/// Serializes actions and retains automatic evidence. Does not own a transport,
/// create a recording session, or change the app/host lifetime.
/// </summary>
internal sealed class AppInteractionContext : IAppInteractionContext
{
    private readonly IAppInteractionBackend backend;
    private readonly SemaphoreSlim actionGate = new(1, 1);
    private AppInteractionScreenshot? latestScreenshot;

    internal AppInteractionContext(IAppInteractionBackend backend)
    {
        this.backend = backend;
    }

    public string SessionId => backend.SessionId;

    public async Task<AppInteractionResult> ExecuteAsync(
        AppInteractionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        if (request.Command is "batch" or "exit")
            throw new ArgumentException("batch and exit are connection commands, not individual app actions.");
        await actionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            backend.EnsureConnected();
            var timer = Stopwatch.StartNew();
            if (request.Command == "tasks")
            {
                var catalog = backend.ListTasks();
                return new("ansight.app-interaction/v1", request.Id, request.Command, SessionId,
                    true, null, null, "Tasks available to the connected app.", null, latestScreenshot,
                    new(0, 0, timer.Elapsed.TotalMilliseconds), TaskCatalog: catalog, Ui: backend.Ui);
            }
            // A failed capture never permits another blind action. Recover automatically.
            if ((latestScreenshot is null || latestScreenshot.Settling?.Status == "timed_out") && request.Command != "snapshot")
            {
                try
                {
                    latestScreenshot = await backend.CaptureAsync(
                        $"interaction-{Guid.NewGuid():N}-initial", cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    return Finish(request, null, null, null, "evidence_unavailable",
                        $"Automatic screenshot capture failed; no input was sent. {exception.Message}", 0, timer);
                }
                if (latestScreenshot is null || latestScreenshot.Settling?.Status == "timed_out")
                {
                    return Finish(request, null, null, latestScreenshot,
                        latestScreenshot is null ? "evidence_unavailable" : "ui_unsettled",
                        "Automatic screenshot capture did not settle; no input was sent.", 0, timer);
                }
            }

            var previous = latestScreenshot;
            UiInputResult? input = null;
            RepositoryTaskRunResult? task = null;
            string? executionError = null;
            var inputMs = 0d;
            if (request.Command != "snapshot")
            {
                var inputTimer = Stopwatch.StartNew();
                try
                {
                    if (request.Command == "task")
                        task = await backend.RunTaskAsync(request.TaskId!, request.Input, cancellationToken).ConfigureAwait(false);
                    else
                        input = await backend.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // Input may have partially completed. Still capture its resulting state.
                    if (request.Command == "task") executionError = exception.Message;
                    else input = UiInputResult.Failure(exception.Message);
                }
                inputMs = inputTimer.Elapsed.TotalMilliseconds;
            }

            latestScreenshot = null;
            var captureTimer = Stopwatch.StartNew();
            string? captureError = null;
            try
            {
                latestScreenshot = await backend.CaptureAsync(
                    $"interaction-{Guid.NewGuid():N}", cancellationToken, afterInput: request.Command != "snapshot").ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                captureError = exception.Message;
            }

            return Finish(request, input, previous, latestScreenshot,
                latestScreenshot is null ? "evidence_unavailable"
                    : executionError is not null || task is { Status: not RepositoryTaskRunStatus.Passed } ? "task_failed"
                    : input?.IsSuccess == false ? "input_failed"
                    : latestScreenshot.Settling?.Status == "timed_out" ? "ui_unsettled" : null,
                latestScreenshot is null
                    ? $"Automatic post-action screenshot capture failed. The command may have completed; do not blindly retry. {captureError}".Trim()
                    : latestScreenshot.Settling?.Status == "timed_out"
                        ? "The command may have completed, but the UI did not become visually quiet within the settling window. Latest screenshot retained; inspect it before continuing. Do not replay the action."
                        : executionError ?? task?.Message ?? input?.Message ?? "Fresh screenshot captured and retained in the session.",
                inputMs, timer, captureTimer.Elapsed.TotalMilliseconds, task);
        }
        catch (OperationCanceledException)
        {
            latestScreenshot = null;
            throw;
        }
        finally
        {
            actionGate.Release();
        }
    }

    private AppInteractionResult Finish(
        AppInteractionRequest request,
        UiInputResult? input,
        AppInteractionScreenshot? previous,
        AppInteractionScreenshot? screenshot,
        string? error,
        string message,
        double inputMs,
        Stopwatch timer,
        double? captureMs = null,
        RepositoryTaskRunResult? task = null)
    {
        var result = new AppInteractionResult(
            "ansight.app-interaction/v1", request.Id, request.Command, SessionId,
            error is null, input?.IsSuccess, error, message, previous, screenshot,
            new AppInteractionTiming(inputMs, captureMs ?? timer.Elapsed.TotalMilliseconds, timer.Elapsed.TotalMilliseconds), task,
            Ui: screenshot is null ? null : backend.Ui);
        backend.Record(result);
        return result;
    }

}
