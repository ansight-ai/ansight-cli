namespace Ansight.Cli.Commands.App.Learning;

internal static class AppLearnPipeline
{
    public static async Task<AppLearnTaskResult> RunCandidateAsync(
        AppLearnCandidate candidate,
        LocalTaskExtractionStartRequest request,
        bool draftOnly,
        IAppLearnAuthor author,
        Func<AppLearnCandidate, LocalTaskExtractionSnapshot, CancellationToken, Task<bool>> prepare,
        CancellationToken cancellationToken,
        Action<LocalTaskExtractionSnapshot>? checkpoint = null)
    {
        LocalTaskExtractionSnapshot? snapshot = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            snapshot = await author.AuthorAsync(request, cancellationToken, value =>
            {
                snapshot = value;
                checkpoint?.Invoke(value);
            }).ConfigureAwait(false);
            checkpoint?.Invoke(snapshot);
            if (snapshot.Status != "ready" || snapshot.Draft is null || snapshot.Draft.ValidationWarnings.Count > 0)
                return new(candidate, "needs-review", snapshot.Message, snapshot);
            if (snapshot.Draft.TaskId != candidate.TaskId)
                return new(candidate, "needs-review", "Generated task identity differs from the learning plan.", snapshot);
            if (draftOnly)
                return new(candidate, "draft", "Static validation passed; live verification has not run.", snapshot);

            if (!await prepare(candidate, snapshot, cancellationToken).ConfigureAwait(false))
                return new(candidate, "blocked", "Could not establish the task's required starting state. Draft retained.", snapshot);

            snapshot = await author.TestAsync(snapshot.ExtractionId, request.SessionId, cancellationToken).ConfigureAwait(false);
            checkpoint?.Invoke(snapshot);
            if (snapshot.TestResult is not { Status: "passed" } result)
                return new(candidate, "failed", snapshot.TestMessage ?? "Task verification did not pass.", snapshot);
            if (result.SessionId != request.SessionId
                || result.Assertions.Any(assertion => !assertion.Passed)
                || !result.Assertions.Any(assertion => assertion.AssertionId == "starting-state")
                || !result.Assertions.Any(assertion => assertion.AssertionId == "destination-reached"))
                return new(candidate, "failed", "Verification requires passing starting-state and destination-reached assertions from the selected session.", snapshot);

            cancellationToken.ThrowIfCancellationRequested();
            snapshot = author.Save(snapshot.ExtractionId);
            checkpoint?.Invoke(snapshot);
            return new(candidate, "saved", "Verified with passing assertions and saved to the workspace.", snapshot);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or IOException)
        {
            return new(candidate, "failed", exception.Message, snapshot);
        }
    }
}
