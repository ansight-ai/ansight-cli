namespace Ansight.RemoteSimulator.Core.Access;

public sealed record RemoteAccessAuthorization(bool IsAuthorized, string FailureMessage)
{
    public static RemoteAccessAuthorization Allow() => new(true, string.Empty);

    public static RemoteAccessAuthorization Deny(string message) => new(
        false,
        string.IsNullOrWhiteSpace(message)
            ? "Sign in to an authorized Ansight account to access this developer machine."
            : message.Trim());
}
