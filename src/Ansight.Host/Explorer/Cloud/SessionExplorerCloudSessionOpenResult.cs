
namespace Ansight.Host.Replay;

public readonly record struct SessionExplorerCloudSessionOpenResult(
    bool IsSuccess,
    string Message,
    string? ImportedSessionId)
{
    public static SessionExplorerCloudSessionOpenResult Success(
        string message,
        string importedSessionId)
        => new(true, message, importedSessionId);

    public static SessionExplorerCloudSessionOpenResult Failure(string message)
        => new(false, message, null);
}
