namespace Ansight.Cli.Commands.App.Learning;

internal interface IAppLearnAuthor
{
    Task<LocalTaskExtractionSnapshot> AuthorAsync(LocalTaskExtractionStartRequest request, CancellationToken cancellationToken,
        Action<LocalTaskExtractionSnapshot> checkpoint);
    Task<LocalTaskExtractionSnapshot> TestAsync(string extractionId, string sessionId, CancellationToken cancellationToken);
    LocalTaskExtractionSnapshot Save(string extractionId);
}
