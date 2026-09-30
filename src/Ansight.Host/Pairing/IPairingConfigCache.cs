namespace Ansight.Host.Pairing;

internal interface IPairingConfigCache
{
    void Add(PairingConfig config);
    void Add(CachedPairingConfig config);

    IReadOnlyList<CachedPairingConfig> GetSnapshot();

    CachedPairingConfig? Find(string configId);

    bool Remove(string configId);

    DeviceConnectionAuthorization AuthorizeConnection(DeviceConnectionAttempt attempt, RuntimeIdentity hostIdentity)
        => DeviceConnectionAuthorization.Reject("EnrollmentUnavailable", "Device enrollment is unavailable.");

    DeviceConnectionAuthorization AuthorizeLocalConnection(DeviceConnectionAttempt attempt, RuntimeIdentity hostIdentity)
        => DeviceConnectionAuthorization.Reject("LocalEnrollmentUnavailable", "Local developer enrollment is unavailable.");

    bool HasActiveGrant(string configId, string appId) => false;

    bool RevokeGrant(string grantId, string reason) => false;
}
