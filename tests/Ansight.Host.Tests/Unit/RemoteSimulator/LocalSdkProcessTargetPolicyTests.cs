using Ansight.RemoteSimulator.Core.Runtime;

namespace Ansight.Host.Tests.Unit.RemoteSimulator;

public sealed class LocalSdkProcessTargetPolicyTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    public void IsEligible_AcceptsRunningMacOsProcessFromLoopback(string remoteAddress)
        => Assert.True(LocalSdkProcessTargetPolicy.IsEligible(
            "macos",
            false,
            remoteAddress,
            Environment.ProcessId));

    [Fact]
    public void IsEligible_AcceptsMacCatalystProcessReportedAsPhysicalIos()
        => Assert.True(LocalSdkProcessTargetPolicy.IsEligible(
            "ios",
            false,
            "127.0.0.1",
            Environment.ProcessId));

    [Theory]
    [InlineData(true)]
    [InlineData(null)]
    public void IsEligible_RejectsIosSessionThatIsNotKnownToBeMacCatalyst(bool? isVirtual)
        => Assert.False(LocalSdkProcessTargetPolicy.IsEligible(
            "ios",
            isVirtual,
            "127.0.0.1",
            Environment.ProcessId));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public void IsEligible_RejectsInvalidOrMissingProcess(int processIdentifier)
        => Assert.False(LocalSdkProcessTargetPolicy.IsEligible(
            "macos",
            false,
            "127.0.0.1",
            processIdentifier));

    [Theory]
    [InlineData("")]
    [InlineData("not-an-address")]
    [InlineData("203.0.113.10")]
    public void IsEligible_RejectsNonLocalSessionAddress(string remoteAddress)
        => Assert.False(LocalSdkProcessTargetPolicy.IsEligible(
            "macos",
            false,
            remoteAddress,
            Environment.ProcessId));
}
