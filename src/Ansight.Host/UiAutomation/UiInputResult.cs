namespace Ansight.Host.UiAutomation;

public sealed record UiInputResult(
    bool IsSuccess,
    string Backend,
    string Message)
{
    public static UiInputResult Failure(string message)
        => new(false, string.Empty, message);
}
