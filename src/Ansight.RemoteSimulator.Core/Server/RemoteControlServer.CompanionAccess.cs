using Ansight.RemoteSimulator.Core.Companion;
using System.Text.Json;

namespace Ansight.RemoteSimulator.Core.Server;

public sealed partial class RemoteControlServer
{
    private const string CompanionAccessSessionPrefix = "companion:";
    private readonly object companionAccessGate = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, CompanionAccessRegistration> companionAccess = new();

    public void TrackCompanionAccess(string leaseId, DateTimeOffset expiresAtUtc, JsonElement? presence)
    {
        var identifier = Guid.Parse(leaseId);
        var device = presence?.Deserialize<CompanionPresenceMessage>(jsonOptions)?.Normalize();
        bool changed;

        lock (companionAccessGate)
        {
            if (disconnectedCompanionLeases.ContainsKey(identifier))
            {
                throw new UnauthorizedAccessException("The host disconnected this companion. Connect again to start a new session.");
            }

            companionAccess.TryGetValue(identifier, out var existing);
            device ??= existing?.Device ?? new RemoteCompanionDevice(
                identifier.ToString("D"), "Companion app", string.Empty, string.Empty, string.Empty, string.Empty);
            changed = existing is null || existing.Device != device;
            companionAccess[identifier] = new CompanionAccessRegistration(identifier, device,
                existing?.ConnectedAtUtc ?? DateTimeOffset.UtcNow, expiresAtUtc);
        }

        if (changed)
        {
            CompanionConnectionsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool TryReadCompanionAccessSession(string sessionId, out Guid leaseId)
    {
        leaseId = Guid.Empty;

        return sessionId.StartsWith(CompanionAccessSessionPrefix, StringComparison.Ordinal)
            && Guid.TryParse(sessionId[CompanionAccessSessionPrefix.Length..], out leaseId);
    }

    private string GetCompanionTargetDevice(Guid leaseId)
    {
        return webRtcSessions.Values
            .Where(session => session.AuthorizationLeaseId == leaseId.ToString("D")
                && session.AuthorizationExpiresAtUtc > DateTimeOffset.UtcNow)
            .OrderByDescending(session => session.ConnectedAtUtc)
            .FirstOrDefault()?.Session.DeviceUdid ?? string.Empty;
    }

    private void DisconnectCompanionLease(Guid leaseId)
    {
        lock (companionAccessGate)
        {
            disconnectedCompanionLeases.TryAdd(leaseId, 0);
            companionAccess.TryRemove(leaseId, out _);
        }
    }

    private async Task<bool> DisconnectCompanionAccessAsync(Guid leaseId)
    {
        if (!companionAccess.TryGetValue(leaseId, out var selected))
        {
            return false;
        }

        var leases = companionAccess.Values
            .Where(connection => connection.Device.Identifier == selected.Device.Identifier)
            .Select(connection => connection.LeaseId)
            .Append(leaseId)
            .ToHashSet();

        foreach (var identifier in leases)
        {
            DisconnectCompanionLease(identifier);
        }

        CompanionConnectionsChanged?.Invoke(this, EventArgs.Empty);

        foreach (var session in webRtcSessions.ToArray())
        {
            if (Guid.TryParse(session.Value.AuthorizationLeaseId, out var identifier) && leases.Contains(identifier))
            {
                await CloseWebRtcSessionAsync(session.Key).ConfigureAwait(false);
            }
        }

        return true;
    }

    private void RemoveExpiredCompanionAccess()
    {
        var changed = false;

        foreach (var entry in companionAccess)
        {
            if (entry.Value.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            {
                changed |= companionAccess.TryRemove(entry);
            }
        }

        if (changed)
        {
            CompanionConnectionsChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
