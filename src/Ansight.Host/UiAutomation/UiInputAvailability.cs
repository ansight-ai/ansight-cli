namespace Ansight.Host.UiAutomation;

public sealed record UiInputAvailability(
    bool IsAvailable,
    string Backend,
    string Message)
{
    public static UiInputAvailability Unavailable(string message)
        => new(false, string.Empty, message);
}
