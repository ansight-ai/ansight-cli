using Ansight.MacSimulatorHid;
using Ansight.SimCtl;

namespace Ansight.Host.Tests.Unit.Devices;

public sealed class IosSimulatorHidCapabilityTests
{
    [Fact]
    public void CapabilityIsIndependentFromSimCtlLifecycleAvailability()
    {
        var simCtl = SimCtlToolResolution.Found(
            "/Applications/Xcode.app/Contents/Developer",
            "/usr/bin/xcrun",
            "/Applications/Xcode.app/Contents/Developer/usr/bin/simctl",
            "test");

        var capability = DeviceService.CreateIosSimulatorHidCapability(
            simCtl,
            static developerDirectory => new MacSimulatorHidCompatibility(
                false,
                "xcode-incompatible",
                "The selected Xcode changed its private SimulatorKit contract.",
                developerDirectory));

        Assert.Equal(DeviceService.IosSimulatorHidCapabilityName, capability.Platform);
        Assert.False(capability.IsAvailable);
        Assert.Equal("xcode-incompatible", capability.Status);
        Assert.Contains("private SimulatorKit contract", capability.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CapabilityIsNotCheckedWhenSimCtlIsUnavailable()
    {
        var probeCalled = false;

        var capability = DeviceService.CreateIosSimulatorHidCapability(
            SimCtlToolResolution.NotFound("Xcode is missing."),
            developerDirectory =>
            {
                probeCalled = true;
                return new MacSimulatorHidCompatibility(
                    true,
                    "available",
                    "Unexpected.",
                    developerDirectory);
            });

        Assert.False(capability.IsAvailable);
        Assert.Equal("not-checked", capability.Status);
        Assert.False(probeCalled);
    }

    [Fact]
    public void RuntimeClassifiesNativeCompatibilityFailuresAsSimulatorHidFailures()
    {
        Assert.True(HeadlessHostDeviceDriver.IsSimulatorHidFailure(
            new MacSimulatorHidException("SimulatorKit changed.")));
        Assert.True(HeadlessHostDeviceDriver.IsSimulatorHidFailure(
            new DllNotFoundException("Bridge missing.")));
        Assert.False(HeadlessHostDeviceDriver.IsSimulatorHidFailure(
            new InvalidOperationException("Unrelated device failure.")));
    }

    [Fact]
    public void RuntimeAvailabilityUsesTheObservedNativeCapability()
    {
        var devices = new DeviceService(
            new RuntimeOptions(),
            new DeviceCapability(
                DeviceService.IosSimulatorHidCapabilityName,
                false,
                "/Applications/Xcode-beta.app/Contents/Developer",
                "The native bridge is incompatible with this Xcode.",
                "xcode-incompatible"));
        using var driver = new HeadlessHostDeviceDriver(devices, new RuntimeOptions());

        var availability = driver.GetAvailability("simulator-udid");

        Assert.False(availability.IsAvailable);
        Assert.Equal(string.Empty, availability.Backend);
        Assert.Contains("incompatible", availability.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeViewportUsesNativeScaleAndCurrentScreenshotOrientation()
    {
        var portrait = HeadlessHostDeviceDriver.CreateIosSimulatorViewport(
            new MacSimulatorDisplayMetrics(1170, 2532, 3),
            new HeadlessIosScreenshotSize(1170, 2532));
        var landscape = HeadlessHostDeviceDriver.CreateIosSimulatorViewport(
            new MacSimulatorDisplayMetrics(1170, 2532, 3),
            new HeadlessIosScreenshotSize(2532, 1170));

        Assert.Equal(390, portrait.Width);
        Assert.Equal(844, portrait.Height);
        Assert.Equal("logical-points", portrait.CoordinateUnit);
        Assert.Equal(844, landscape.Width);
        Assert.Equal(390, landscape.Height);
    }
}
