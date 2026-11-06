using System.ComponentModel.Composition;

namespace Ansight.Host.Pairing;

internal sealed record UnattendedEnrollmentIssueResult(
    bool IsSuccess,
    string Message,
    string? InviteId,
    string? Payload,
    DateTimeOffset? ExpiresAtUtc)
{
    public static UnattendedEnrollmentIssueResult Success(
        string inviteId,
        string payload,
        DateTimeOffset expiresAtUtc)
        => new(
            true,
            "Issued a one-use enrollment payload for the physical test target.",
            inviteId,
            payload,
            expiresAtUtc);

    public static UnattendedEnrollmentIssueResult Failure(string message)
        => new(false, message, null, null, null);
}
