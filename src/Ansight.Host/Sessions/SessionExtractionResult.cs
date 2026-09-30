namespace Ansight.Host.Sessions;

public readonly record struct SessionExtractionResult(bool IsSuccess, string Message, AppSessionSnapshot? ExtractedSession)
{
    public static SessionExtractionResult Success(string message, AppSessionSnapshot extractedSession) => new(true, message, extractedSession);

    public static SessionExtractionResult Failure(string message) => new(false, message, null);
}
