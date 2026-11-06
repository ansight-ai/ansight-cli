using System.Text.Json.Nodes;
using Ansight.RemoteSimulator.Core.Devices;

namespace Ansight.Host.Devices;

internal sealed partial class CompanionDeviceTestingService
{
    private FollowState? follow;
    private CancellationTokenSource? followCancellation;

    private void StartFollowingPhone(CompanionLease lease, bool online, JsonObject payload)
    {
        var expires = payload["expiresAtUtc"]?.GetValue<DateTimeOffset>()
            ?? clock.GetUtcNow().AddMinutes(2);
        follow = new FollowState(lease, clock.GetUtcNow(), expires, online, -1);

        var cancellation = new CancellationTokenSource();
        followCancellation = cancellation;
        _ = MonitorPhoneAsync(cancellation);
    }

    private void StopFollowingPhone()
    {
        follow = null;
        followCancellation?.Cancel();
        followCancellation = null;
    }

    private async Task<RemoteDeviceTestingResult> ApplyHeartbeatAsync(
        RemoteDeviceTestingRequest request,
        CancellationToken cancellationToken)
    {
        var matches = follow is not null
            && follow.Lease.Id == request.LeaseId
            && follow.Lease.DeviceId == request.DeviceUdid
            && follow.Lease.AppId == request.AppId
            && request.Sequence >= 0;

        if (!matches)
        {
            return new RemoteDeviceTestingResult(false, "No matching phone-following lease is active.");
        }

        if (request.Sequence <= follow!.Sequence)
        {
            return new RemoteDeviceTestingResult(true, "Older connectivity update ignored.");
        }

        var online = request.Online == true;

        if (online != follow.Online)
        {
            var result = await SetFollowProfileAsync(follow.Lease, online, cancellationToken).ConfigureAwait(false);

            if (!result.IsSuccess)
            {
                return new RemoteDeviceTestingResult(false, result.Payload["message"]?.GetValue<string>() ?? "Could not update connectivity.");
            }
        }

        follow = follow with
        {
            LastHeartbeat = clock.GetUtcNow(),
            Online = online,
            Sequence = request.Sequence!.Value
        };

        return new RemoteDeviceTestingResult(true, online
            ? "Following phone: online. Normal TCP traffic is enabled."
            : "Following phone: offline. TCP connections are blocked.");
    }

    internal async Task CheckPhoneTimeoutAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (follow is null)
            {
                return;
            }

            var now = clock.GetUtcNow();

            if (now >= follow.ExpiresAt)
            {
                StopFollowingPhone();

                return;
            }

            var missedHeartbeat = follow.Online && now - follow.LastHeartbeat >= TimeSpan.FromSeconds(8);

            if (missedHeartbeat)
            {
                var result = await SetFollowProfileAsync(follow.Lease, false, cancellationToken).ConfigureAwait(false);

                if (result.IsSuccess)
                {
                    follow = follow with
                    {
                        Online = false
                    };
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private Task<Network.NetworkConditioningResult> SetFollowProfileAsync(
        CompanionLease lease,
        bool online,
        CancellationToken cancellationToken)
    {
        return network.ExecuteAsync("set", new JsonObject
        {
            ["deviceId"] = lease.DeviceId,
            ["appId"] = lease.AppId,
            ["leaseId"] = lease.Id,
            ["profile"] = new JsonObject { ["blocked"] = !online }
        }, cancellationToken);
    }

    private async Task MonitorPhoneAsync(CancellationTokenSource cancellation)
    {
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), clock, cancellation.Token).ConfigureAwait(false);
                await CheckPhoneTimeoutAsync(cancellation.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            // The network backend's independent hard expiry remains in force.
            System.Diagnostics.Trace.TraceWarning($"Companion connectivity monitor stopped: {error.Message}");
        }
        finally
        {
            await gate.WaitAsync().ConfigureAwait(false);

            try
            {
                if (ReferenceEquals(followCancellation, cancellation))
                {
                    followCancellation = null;
                    follow = null;
                }
            }
            finally
            {
                gate.Release();
                cancellation.Dispose();
            }
        }
    }

    private sealed record FollowState(
        CompanionLease Lease,
        DateTimeOffset LastHeartbeat,
        DateTimeOffset ExpiresAt,
        bool Online,
        long Sequence);
}
