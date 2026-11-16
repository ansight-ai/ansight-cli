using System.Text.Json;

namespace Ansight.RemoteSimulator.Core.Companion;

public interface ICompanionConnectionTracker
{
    void TrackCompanionAccess(string leaseId, DateTimeOffset expiresAtUtc, JsonElement? presence);

    bool IsCompanionLeaseDisconnected(string leaseId);

    Task<bool> DisconnectCompanionAsync(string sessionId);
}
