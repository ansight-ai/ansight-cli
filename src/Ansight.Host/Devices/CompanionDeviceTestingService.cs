using System.Text.Json.Nodes;
using Ansight.Host.Devices.Network;
using Ansight.RemoteSimulator.Core.Devices;

namespace Ansight.Host.Devices;

internal sealed partial class CompanionDeviceTestingService : IRemoteDeviceTestingSource
{
    private readonly Func<JsonObject, CancellationToken, Task<RequestResult>> shake;
    private readonly NetworkConditioningService network;
    private readonly SemaphoreSlim gate = new(1, 1);
    private CompanionLease? activeLease;
    private CompanionLease? lastStoppedLease;
    private readonly TimeProvider clock;
    private readonly CompanionRoutePlaybackService? routes;
    private readonly CompanionAppTestingService? apps;

    public CompanionDeviceTestingService(
        Func<JsonObject, CancellationToken, Task<RequestResult>> shake,
        NetworkConditioningService network,
        TimeProvider? clock = null,
        CompanionRoutePlaybackService? routes = null,
        CompanionAppTestingService? apps = null)
    {
        this.shake = shake;
        this.network = network;
        this.clock = clock ?? TimeProvider.System;
        this.routes = routes;
        this.apps = apps;
    }

    public async Task<RemoteDeviceTestingResult> ExecuteAsync(
        RemoteDeviceTestingRequest request,
        CancellationToken cancellationToken = default)
    {
        if (CompanionAppTestingService.SupportsAction(request.Action))
        {
            return apps is null
                ? new RemoteDeviceTestingResult(false, "Update the Mac app to use these testing controls.")
                : await apps.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
        }

        if (request.Action is "route-start" or "route-status" or "route-stop")
        {
            return routes is null
                ? new RemoteDeviceTestingResult(false, "Route playback is unavailable on this host.")
                : await routes.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
        }

        var arguments = new JsonObject
        {
            ["deviceId"] = request.DeviceUdid,
            ["appId"] = request.AppId
        };

        if (request.Action == "shake")
        {
            var result = await shake(arguments, cancellationToken).ConfigureAwait(false);
            var payload = result.Payload?["structuredContent"] as JsonObject;
            var succeeded = !result.IsError && result.Payload?["isError"]?.GetValue<bool>() != true;

            return new RemoteDeviceTestingResult(
                succeeded,
                payload?["message"]?.GetValue<string>() ?? result.ErrorMessage ?? "Shake request completed.");
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return await ExecuteNetworkAsync(request, arguments, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<RemoteDeviceTestingResult> ExecuteNetworkAsync(
        RemoteDeviceTestingRequest request,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        var action = request.Action switch
        {
            "network-capabilities" => "capabilities",
            "network-set" => "set",
            "network-get" => "get",
            "network-reset" => "reset",
            "network-follow" => "set",
            "network-heartbeat" => "set",
            _ => null
        };

        if (action is null)
        {
            return new RemoteDeviceTestingResult(false, "Unknown device testing action.");
        }

        var followsPhone = request.Action is "network-follow" or "network-heartbeat";

        if (followsPhone && request.Online is null)
        {
            return new RemoteDeviceTestingResult(false, "The phone connection state is required.");
        }

        if (request.Action == "network-heartbeat")
        {
            return await ApplyHeartbeatAsync(request, cancellationToken).ConfigureAwait(false);
        }

        if (action == "reset" && MatchesLease(lastStoppedLease, request))
        {
            return new RemoteDeviceTestingResult(true, "Network conditions are already reset.");
        }

        if (action is "get" or "reset" || request.LeaseId is not null)
        {
            var ownsLease = MatchesLease(activeLease, request);

            if (!ownsLease)
            {
                return new RemoteDeviceTestingResult(false, "This network lease does not belong to the selected companion target.");
            }

            arguments["leaseId"] = request.LeaseId;
        }

        if (action is "set" or "capabilities" && string.IsNullOrWhiteSpace(request.AppId))
        {
            return new RemoteDeviceTestingResult(false, "Choose a running app before applying network conditions.");
        }

        if (action == "set")
        {
            var profile = followsPhone
                ? new JsonObject { ["blocked"] = request.Online != true } : CreateProfile(request.Preset);

            if (profile is null)
            {
                return new RemoteDeviceTestingResult(false, "Choose slow, high-latency, stalled, or blocked.");
            }

            arguments["profile"] = profile;
            arguments["durationMs"] = 120_000;
        }

        var result = await network.ExecuteAsync(action, arguments, cancellationToken).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            return new RemoteDeviceTestingResult(
                false,
                result.Payload["message"]?.GetValue<string>() ?? "Network conditioning failed.");
        }

        if (action == "set")
        {
            StopFollowingPhone();
            activeLease = new CompanionLease(
                result.Payload["leaseId"]!.GetValue<string>(),
                request.DeviceUdid,
                request.AppId!);

            if (followsPhone)
            {
                StartFollowingPhone(activeLease, request.Online == true, result.Payload);
            }
        }
        else if (action == "reset")
        {
            StopFollowingPhone();
            lastStoppedLease = activeLease;
            activeLease = null;
        }

        var message = action switch
        {
            "capabilities" => result.Payload["available"]?.GetValue<bool>() == true
                ? "Network conditioning is available."
                : result.Payload["reason"]?.GetValue<string>() ?? "Network conditioning is unavailable.",
            "set" => "Network preset applied. It expires automatically within two minutes.",
            "reset" => "Network conditions reset. Proxied connections were closed.",
            _ => "Network condition status updated."
        };

        return new RemoteDeviceTestingResult(true,
            followsPhone ? "Following phone connectivity for up to two minutes." : message,
            result.Payload);
    }

    private static JsonObject? CreateProfile(string? preset)
    {
        return preset switch
        {
            "slow" => new JsonObject
            {
                ["uploadKbps"] = 256,
                ["downloadKbps"] = 1_000,
                ["latencyMs"] = 300
            },
            "high-latency" => new JsonObject
            {
                ["latencyMs"] = 1_000,
                ["jitterMs"] = 250,
                ["seed"] = 1
            },
            "stalled" => new JsonObject { ["stall"] = true },
            "blocked" => new JsonObject { ["blocked"] = true },
            _ => null
        };
    }

    private static bool MatchesLease(CompanionLease? lease, RemoteDeviceTestingRequest request)
    {
        var matches = lease is not null
            && lease.Id == request.LeaseId
            && lease.DeviceId == request.DeviceUdid
            && lease.AppId == request.AppId;

        return matches;
    }

    private sealed record CompanionLease(string Id, string DeviceId, string AppId);
}
