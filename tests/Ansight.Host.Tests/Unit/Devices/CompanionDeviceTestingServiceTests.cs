using Ansight.Host.Devices.Network;
using Ansight.RemoteSimulator.Core.Devices;

namespace Ansight.Host.Tests.Unit.Devices;

public sealed class CompanionDeviceTestingServiceTests
{
    [Fact]
    public async Task FollowPhone_BlocksOnHeartbeatLossRecoversAndIgnoresOldUpdates()
    {
        var clock = new TestClock();
        var profiles = new List<bool>();
        var service = new CompanionDeviceTestingService(
            (_, _) => throw new InvalidOperationException("Unexpected shake."),
            new NetworkConditioningService((name, arguments, _) =>
            {
                if (name.EndsWith("_set", StringComparison.Ordinal))
                {
                    profiles.Add(arguments!["profile"]!["blocked"]!.GetValue<bool>());
                }

                return Task.FromResult(RequestResult.ToolResult(new JsonObject
                {
                    ["leaseId"] = "follow-lease",
                    ["expiresAtUtc"] = clock.GetUtcNow().AddMinutes(2)
                }, isError: false));
            }), clock);

        var started = await service.ExecuteAsync(new RemoteDeviceTestingRequest(
            "sim", "network-follow", "app", Online: true));
        Assert.True(started.IsSuccess);
        clock.Advance(TimeSpan.FromSeconds(9));
        await service.CheckPhoneTimeoutAsync();
        Assert.Equal(new[] { false, true }, profiles);

        await service.ExecuteAsync(new RemoteDeviceTestingRequest(
            "sim", "network-heartbeat", "app", LeaseId: "follow-lease", Online: true, Sequence: 2));
        await service.ExecuteAsync(new RemoteDeviceTestingRequest(
            "sim", "network-heartbeat", "app", LeaseId: "follow-lease", Online: false, Sequence: 1));
        Assert.Equal(new[] { false, true, false }, profiles);

        await service.ExecuteAsync(new RemoteDeviceTestingRequest(
            "sim", "network-reset", "app", LeaseId: "follow-lease"));
        clock.Advance(TimeSpan.FromSeconds(9));
        await service.CheckPhoneTimeoutAsync();
        Assert.Equal(3, profiles.Count);
    }

    [Fact]
    public async Task NetworkPreset_IsBoundedAndCannotResetAnUnrelatedLease()
    {
        var calls = new List<JsonObject>();
        var network = new NetworkConditioningService((_, arguments, _) =>
        {
            calls.Add(arguments!.DeepClone().AsObject());

            return Task.FromResult(RequestResult.ToolResult(
                new JsonObject { ["leaseId"] = "companion-lease" }, isError: false));
        });
        var service = new CompanionDeviceTestingService(
            (_, _) => throw new InvalidOperationException("Unexpected shake."), network);

        var applied = await service.ExecuteAsync(
            new RemoteDeviceTestingRequest("sim-1", "network-set", "app.one", "slow"));
        var otherLease = await service.ExecuteAsync(
            new RemoteDeviceTestingRequest("sim-1", "network-reset", "app.one", LeaseId: "task-lease"));
        var otherTarget = await service.ExecuteAsync(
            new RemoteDeviceTestingRequest("sim-2", "network-reset", "app.one", LeaseId: "companion-lease"));
        var otherApp = await service.ExecuteAsync(
            new RemoteDeviceTestingRequest("sim-1", "network-set", "app.two", "blocked", "companion-lease"));

        Assert.True(applied.IsSuccess);
        Assert.False(otherLease.IsSuccess);
        Assert.False(otherTarget.IsSuccess);
        Assert.False(otherApp.IsSuccess);

        var arguments = Assert.Single(calls);
        Assert.Equal(120_000, arguments["durationMs"]!.GetValue<int>());
        Assert.Equal("sim-1", arguments["deviceId"]!.GetValue<string>());
        Assert.Equal("app.one", arguments["appId"]!.GetValue<string>());

        var reset = await service.ExecuteAsync(
            new RemoteDeviceTestingRequest("sim-1", "network-reset", "app.one", LeaseId: "companion-lease"));
        Assert.True(reset.IsSuccess);

        var retriedReset = await service.ExecuteAsync(
            new RemoteDeviceTestingRequest("sim-1", "network-reset", "app.one", LeaseId: "companion-lease"));
        Assert.True(retriedReset.IsSuccess);
        Assert.Equal(2, calls.Count);
    }

    [Theory]
    [InlineData("network-set", "app.one", "packet-loss")]
    [InlineData("network-set", "", "slow")]
    [InlineData("arbitrary-tool", "app.one", "slow")]
    public async Task InvalidNetworkRequests_DoNotReachTheBackend(string action, string appId, string preset)
    {
        var service = new CompanionDeviceTestingService(
            (_, _) => throw new InvalidOperationException("Unexpected shake."),
            new NetworkConditioningService((_, _, _) => throw new InvalidOperationException("Unexpected network call.")));

        var result = await service.ExecuteAsync(new RemoteDeviceTestingRequest("sim", action, appId, preset));

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task Shake_ReportsBackendFailureWithoutClaimingSuccess()
    {
        var service = new CompanionDeviceTestingService(
            (_, _) => Task.FromResult(RequestResult.ToolResult(
                new JsonObject { ["message"] = "Simulator is unavailable." }, isError: true)),
            new NetworkConditioningService((_, _, _) => throw new InvalidOperationException("Unexpected network call.")));

        var result = await service.ExecuteAsync(new RemoteDeviceTestingRequest("sim", "shake"));

        Assert.False(result.IsSuccess);
        Assert.Equal("Simulator is unavailable.", result.Message);
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan duration)
        {
            now += duration;
        }
    }
}
