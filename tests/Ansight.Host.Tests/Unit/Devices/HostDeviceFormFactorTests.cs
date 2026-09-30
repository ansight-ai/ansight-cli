using Ansight.Host;

namespace Ansight.Host.Tests.Unit.Devices;

public sealed class HostDeviceFormFactorTests
{
    [Theory]
    [InlineData("iPhone 17 Pro", null, DeviceFormFactors.Phone)]
    [InlineData("Matthew's iPhone", "iPhone17,1", DeviceFormFactors.Phone)]
    [InlineData("iPad Air 11-inch (M4)", null, DeviceFormFactors.Tablet)]
    [InlineData("Matthew's iPad", "iPad14,1", DeviceFormFactors.Tablet)]
    [InlineData("Apple TV", null, null)]
    public void ResolveAppleFormFactor_UsesProductFamily(
        string name,
        string? productType,
        string? expected)
    {
        var formFactor = DeviceService.ResolveAppleFormFactor(name, productType);

        Assert.Equal(expected, formFactor);
    }
}
