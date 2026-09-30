using Ansight.Host.Identity;

namespace Ansight.Host.Tests.Unit.Identity;

public sealed class HardwareMachineFingerprintProviderTests
{
    [Fact]
    public void Create_NormalizesInputAndDoesNotExposeRawHardwareIdentifier()
    {
        var first = HardwareMachineFingerprintProvider.Create(
            " macOS.IOPlatformUUID ",
            " 00112233-4455-6677-8899-AABBCCDDEEFF ");
        var second = HardwareMachineFingerprintProvider.Create(
            "macos.ioplatformuuid",
            "00112233-4455-6677-8899-aabbccddeeff");

        Assert.Equal("hardware-sha256-v1", first.Version);
        Assert.Equal("macos.ioplatformuuid", first.Source);
        Assert.Equal(first.Value, second.Value);
        Assert.Matches("^[0-9a-f]{64}$", first.Value);
        Assert.DoesNotContain("00112233", first.Value, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Create_SeparatesFingerprintSources()
    {
        var mac = HardwareMachineFingerprintProvider.Create("macos.ioplatformuuid", "same-value");
        var windows = HardwareMachineFingerprintProvider.Create("windows.machineguid", "same-value");

        Assert.NotEqual(mac.Value, windows.Value);
    }
}
