namespace Ansight.Host.Tests.Unit.Utilities;

using System.Net;

public sealed class HostAddressDiscoveryTests
{
    [Fact]
    public void ParseMacWifiNetworkName_ReturnsSsid()
    {
        var wifiName = AddressDiscovery.ParseMacWifiNetworkName(
            "Current Wi-Fi Network: LegacyDesktop Network");

        Assert.Equal("LegacyDesktop Network", wifiName);
    }

    [Fact]
    public void ApplyAdditionalHostAddresses_PrependsValidUniqueAddresses()
    {
        var addresses = AddressDiscovery.ApplyAdditionalHostAddresses(
            ["10.211.55.4", "fdb2:2c26:f4e4::1"],
            "192.168.0.147; 10.211.55.4; not-an-ip; 169.254.1.2");

        Assert.Equal(
            ["192.168.0.147", "10.211.55.4", "fdb2:2c26:f4e4::1"],
            addresses);
    }

    [Fact]
    public void ApplyAdditionalHostAddresses_WhenAdditionalAddressesAreBlank_ReturnsDiscoveredAddresses()
    {
        var addresses = AddressDiscovery.ApplyAdditionalHostAddresses(
            ["10.211.55.4"],
            " ");

        Assert.Equal(["10.211.55.4"], addresses);
    }

    [Theory]
    [InlineData("192.0.0.2", 32, false)]
    [InlineData("192.168.0.147", 24, true)]
    [InlineData("2405:6e00:c38:9be7::2", 64, true)]
    [InlineData("fd1b:3f34:65ee::2", 64, true)]
    [InlineData("fd1b:3f34:65ee::2", 128, false)]
    public void IsEligibleDiscoveredHostAddress_RejectsHostOnlyInterfaceAddresses(
        string address,
        int prefixLength,
        bool expected)
    {
        var result = AddressDiscovery.IsEligibleDiscoveredHostAddress(
            IPAddress.Parse(address),
            prefixLength);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void ParseWindowsWifiNetworkName_ReturnsSsidForMatchingInterface()
    {
        const string output = """
                              There are 2 interfaces on the system:

                                  Name                   : Wi-Fi
                                  Description            : Wireless Adapter
                                  State                  : connected
                                  SSID                   : Office Wifi
                                  BSSID                  : 00:11:22:33:44:55

                                  Name                   : Backup Wi-Fi
                                  Description            : Wireless Adapter
                                  State                  : connected
                                  SSID                   : Guest Wifi
                                  BSSID                  : 66:77:88:99:aa:bb
                              """;

        var wifiName = AddressDiscovery.ParseWindowsWifiNetworkName(output, "Wi-Fi");

        Assert.Equal("Office Wifi", wifiName);
    }

    [Fact]
    public void ParseWindowsWifiNetworkName_WhenInterfaceDoesNotMatch_ReturnsFirstSsid()
    {
        const string output = """
                              Name                   : Wi-Fi
                              Description            : Wireless Adapter
                              State                  : connected
                              SSID                   : Office Wifi
                              BSSID                  : 00:11:22:33:44:55
                              """;

        var wifiName = AddressDiscovery.ParseWindowsWifiNetworkName(output, "Other Wi-Fi");

        Assert.Equal("Office Wifi", wifiName);
    }
}
