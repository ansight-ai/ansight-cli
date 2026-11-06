using Ansight.Host.Trends;

namespace Ansight.Host.Cloud;

public sealed record CloudRunnerKeyOperationResult(
    bool IsSuccess,
    string Message,
    Guid KeyId,
    DateTimeOffset? RevokedAt)
{
    public static CloudRunnerKeyOperationResult Success(Guid keyId, DateTimeOffset revokedAt)
        => new(true, $"Revoked runner API key {keyId:D}.", keyId, revokedAt);

    public static CloudRunnerKeyOperationResult Failure(Guid keyId, string message)
        => new(false, message, keyId, null);
}
