using Ansight.Host;
using Ansight.Host.Explorer.TaskExtraction;

namespace Ansight.Cli.Commands.App.Learning;

internal sealed class AppLearnAuthor(RuntimeCoordinator runtime, CliOutput output) : IAppLearnAuthor, IDisposable
{
    private readonly TaskDraftAuthor author = new(runtime);

    public async Task<LocalTaskExtractionSnapshot> AuthorAsync(LocalTaskExtractionStartRequest request, CancellationToken cancellationToken,
        Action<LocalTaskExtractionSnapshot> checkpoint)
    {
        var snapshot = await author.AuthorAsync(request,
            new CliProgress<string>(message => output.WriteProgress($"[learn.author] {message}")), cancellationToken, checkpoint)
            .ConfigureAwait(false);
        if (snapshot.Status != "ready" || snapshot.Draft is null) return snapshot;
        var catalog = runtime.InspectRepositoryTasks(snapshot.AppId, snapshot.Draft.DraftRootPath);
        var task = catalog.Tasks.Count == 1 ? catalog.Tasks[0] : null;
        if (catalog.Warnings.Count > 0 || task is null || !task.Enabled || string.IsNullOrWhiteSpace(task.StartingState)
            || string.IsNullOrWhiteSpace(task.SuccessState) || task.InputSchema["required"] is System.Text.Json.Nodes.JsonArray { Count: > 0 })
        {
            const string message = "Learned tasks must be enabled, declare starting/success states, and require no runtime inputs.";
            return snapshot with { Status = "needsReview", Message = message,
                Draft = snapshot.Draft with { ValidationWarnings = [.. snapshot.Draft.ValidationWarnings, message] } };
        }
        return snapshot;
    }

    public Task<LocalTaskExtractionSnapshot> TestAsync(string extractionId, string sessionId, CancellationToken cancellationToken)
        => author.TestAsync(extractionId, sessionId, cancellationToken);

    public LocalTaskExtractionSnapshot Save(string extractionId) => author.Save(extractionId);

    public void Dispose() => author.Dispose();
}
