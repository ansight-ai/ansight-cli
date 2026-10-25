using Ansight.Host.Runtime.RepositoryContracts;

namespace Ansight.Host.Explorer.TaskExtraction;

/// <summary>Awaitable task authoring for command workflows using the replay editor's compiler and evidence checks.</summary>
public sealed class TaskDraftAuthor : IDisposable
{
    private readonly LocalTaskExtractionCoordinator coordinator;
    private readonly RuntimeCoordinator runtime;

    public TaskDraftAuthor(RuntimeCoordinator runtime)
    {
        this.runtime = runtime;
        coordinator = new(runtime);
    }

    public static void ValidateEnvironment(RuntimeCoordinator runtime)
    {
        if (LocalTypeScriptTaskCompiler.GetEnvironmentError(runtime.JavaScriptExecutablePath) is { } error)
            throw new InvalidOperationException(error);
    }

    public async Task<LocalTaskExtractionSnapshot> AuthorAsync(
        LocalTaskExtractionStartRequest request,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        Action<LocalTaskExtractionSnapshot>? checkpoint = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = coordinator.Start(request);
        checkpoint?.Invoke(snapshot);
        try
        {
            var lastMessage = string.Empty;
            while (snapshot.Status is "queued" or "running")
            {
                if (snapshot.Message != lastMessage)
                {
                    progress?.Report(snapshot.Message);
                    lastMessage = snapshot.Message;
                }
                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
                snapshot = coordinator.Get(snapshot.ExtractionId)!;
            }
            // Completion includes trace persistence and usage metering after the draft becomes ready.
            await coordinator.WaitForAuthoringAsync(snapshot.ExtractionId, cancellationToken).ConfigureAwait(false);
            snapshot = coordinator.Get(snapshot.ExtractionId)!;
            checkpoint?.Invoke(snapshot);
            return snapshot;
        }
        catch (OperationCanceledException)
        {
            coordinator.Cancel(snapshot.ExtractionId);
            await coordinator.WaitForAuthoringAsync(snapshot.ExtractionId, CancellationToken.None).ConfigureAwait(false);
            checkpoint?.Invoke(coordinator.Get(snapshot.ExtractionId)!);
            throw;
        }
    }

    public async Task<LocalTaskExtractionSnapshot> TestAsync(
        string extractionId, string sessionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var session = runtime.Sessions.GetSummaries().FirstOrDefault(candidate => candidate.SessionId == sessionId)
            ?? throw new InvalidDataException("The verification session is unavailable.");
        using var claim = runtime.WorkspaceTests.ReserveSession(session);
        var snapshot = coordinator.Test(extractionId, new(sessionId))
            ?? throw new InvalidDataException("The learned task draft is unavailable.");
        try
        {
            while (snapshot.TestStatus is "running" or "cancelling")
            {
                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
                snapshot = coordinator.Get(extractionId)!;
            }
            return snapshot;
        }
        catch (OperationCanceledException)
        {
            await coordinator.CancelTestAsync(extractionId).ConfigureAwait(false);
            throw;
        }
    }

    public LocalTaskExtractionSnapshot Save(string extractionId)
    {
        var snapshot = coordinator.Get(extractionId)
            ?? throw new InvalidDataException("The learned task draft is unavailable.");
        if (snapshot.TestResult is not { Status: "passed", Assertions.Count: > 0 } result
            || result.Assertions.Any(assertion => !assertion.Passed))
            throw new InvalidDataException("Only a learned task with passing assertions can be saved.");
        var taskDirectory = Path.Combine(snapshot.WorkspacePath, "ansight", "tasks");
        Directory.CreateDirectory(taskDirectory);
        var declarations = Path.Combine(taskDirectory, "ansight-task.d.ts");
        if (!File.Exists(declarations))
        {
            using var writer = new StreamWriter(new FileStream(declarations, FileMode.CreateNew, FileAccess.Write));
            writer.Write(RepositoryModuleContractArtifacts.GetTaskTypeDefinitions());
        }
        return coordinator.Commit(extractionId)!;
    }

    public void Dispose() => coordinator.Dispose();
}
