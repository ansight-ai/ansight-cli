using System.Text.Json.Nodes;

namespace Ansight.Cli.Tests.Commands.Doctor;

public sealed class BlackHoleDoctorTests
{
    [Fact]
    public void MissingDriverIsOptionalAmberWithExplicitInstallAndRouteGuidance()
    {
        var check = BlackHoleDoctor.FromDevices([]);
        Assert.False(check.IsRequired);
        Assert.False(check.IsSuccess);
        Assert.Equal("amber", check.Signal);
        Assert.Contains("brew install --cask blackhole-2ch", check.InstallInstructions);
        Assert.Contains("Audio Input", check.InstallInstructions);
        Assert.Contains("administrator", check.InstallInstructions);
    }

    [Fact]
    public void DriverMustBeRegisteredWithExactUidAndDuplexChannels()
    {
        var check = BlackHoleDoctor.FromDevices(JsonNode.Parse("""
            [{"name":"BlackHole 2ch","uid":"unrelated-device","inputChannels":2,"outputChannels":2},
             {"name":"BlackHole 2ch","uid":"BlackHole2ch_UID","inputChannels":2,"outputChannels":0}]
            """)!.AsArray());
        Assert.False(check.IsSuccess);
        Assert.Equal("not-registered", check.Status);
    }

    [Fact]
    public void RegisteredDriverIsHealthyWithoutClaimingSimulatorSelection()
    {
        var check = BlackHoleDoctor.FromDevices(JsonNode.Parse("""
            [{"name":"BlackHole 2ch","uid":"BlackHole2ch_UID","inputChannels":2,"outputChannels":2}]
            """)!.AsArray());
        Assert.True(check.IsSuccess);
        Assert.False(check.IsRequired);
        Assert.Equal("green", check.Signal);
        Assert.Equal("registered", check.Status);
        Assert.Contains("registration alone does not verify", check.Message);
    }
}
