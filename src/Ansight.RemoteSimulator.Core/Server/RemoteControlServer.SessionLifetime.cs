using Ansight.RemoteSimulator.Core.Server.WebRtc;

namespace Ansight.RemoteSimulator.Core.Server;

public sealed partial class RemoteControlServer
{
    private static readonly TimeSpan maximumSessionLease = TimeSpan.FromMinutes(2);

    private async Task ExpireWebRtcSessionsAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await RemoveExpiredWebRtcSessionsAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    internal async Task RemoveExpiredWebRtcSessionsAsync()
    {
        foreach (var entry in webRtcSessions)
        {
            if (entry.Value.AuthorizationExpiresAtUtc <= DateTimeOffset.UtcNow)
            {
                try
                {
                    await CloseWebRtcSessionAsync(entry.Key, entry.Value).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    // One failed native cleanup must not disable expiry for other peers.
                    System.Diagnostics.Trace.TraceWarning(exception.ToString());
                }
            }
        }
    }

    private bool RenewWebRtcSession(WebRtcRenewal renewal)
    {
        var now = DateTimeOffset.UtcNow;
        var invalidExpiry = renewal.AuthorizationExpiresAtUtc <= now
            || renewal.AuthorizationExpiresAtUtc > now.Add(maximumSessionLease);

        if (invalidExpiry || string.IsNullOrWhiteSpace(renewal.AuthorizationLeaseId))
        {
            return false;
        }

        while (webRtcSessions.TryGetValue(renewal.SessionId, out var registration))
        {
            var canRenew = registration.AuthorizationExpiresAtUtc > now
                && registration.AuthorizationLeaseId == renewal.AuthorizationLeaseId;

            if (!canRenew)
            {
                return false;
            }

            var updated = registration with { AuthorizationExpiresAtUtc = renewal.AuthorizationExpiresAtUtc };

            if (webRtcSessions.TryUpdate(renewal.SessionId, updated, registration))
            {
                return true;
            }
        }

        return false;
    }

    private bool KeepLocalSessionAlive(string sessionId, SessionRegistration registration)
    {
        // Remote leases can only be extended by a fresh broker authorization.
        if (registration.AuthorizationLeaseId is not null
            || registration.AuthorizationExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            return false;
        }

        var updated = registration with
        {
            AuthorizationExpiresAtUtc = DateTimeOffset.UtcNow.Add(maximumSessionLease)
        };
        return webRtcSessions.TryUpdate(sessionId, updated, registration);
    }
}
