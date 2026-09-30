namespace Ansight.Host.Sessions;

public readonly record struct SessionImportResult(bool IsSuccess, string Message, AppSessionSnapshot? ImportedSession)
{
    public SessionImportFailureReason FailureReason { get; init; }

    public static SessionImportResult Success(string message, AppSessionSnapshot importedSession) => new(true, message, importedSession);

    public static SessionImportResult Failure(
        string message,
        SessionImportFailureReason failureReason = SessionImportFailureReason.InvalidArchive)
    {
        return new SessionImportResult(false, message, null)
        {
            FailureReason = failureReason
        };
    }
}
