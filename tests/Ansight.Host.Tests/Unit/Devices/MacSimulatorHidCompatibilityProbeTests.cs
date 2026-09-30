using Ansight.MacSimulatorHid;

namespace Ansight.Host.Tests.Unit.Devices;

public sealed class MacSimulatorHidCompatibilityProbeTests
{
    [Fact]
    public void CheckReportsAvailableWhenTheNativeSessionLoads()
    {
        var result = MacSimulatorHidCompatibilityProbe.Check(
            "/Applications/Xcode.app/Contents/Developer",
            isMacOS: true,
            static _ => null);

        Assert.True(result.IsAvailable);
        Assert.Equal("available", result.Status);
        Assert.Contains("loaded successfully", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckReportsTheBridgeAsMissingWhenTheDylibCannotLoad()
    {
        var result = MacSimulatorHidCompatibilityProbe.Check(
            "/Applications/Xcode.app/Contents/Developer",
            isMacOS: true,
            static _ => throw new DllNotFoundException("libAnsightSimulatorHid.dylib"));

        Assert.False(result.IsAvailable);
        Assert.Equal("bridge-missing", result.Status);
        Assert.Contains("libAnsightSimulatorHid.dylib", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckReportsAnIncompatibleXcodeWhenPrivateSymbolsChange()
    {
        var result = MacSimulatorHidCompatibilityProbe.Check(
            "/Applications/Xcode-beta.app/Contents/Developer",
            isMacOS: true,
            static _ => "SimulatorKit does not expose the required HID message functions.");

        Assert.False(result.IsAvailable);
        Assert.Equal("xcode-incompatible", result.Status);
        Assert.Contains("required HID message functions", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckSeparatesCoreSimulatorRuntimeFailuresFromXcodeCompatibility()
    {
        var result = MacSimulatorHidCompatibilityProbe.Check(
            "/Applications/Xcode.app/Contents/Developer",
            isMacOS: true,
            static _ => "Failed to initialize simulator device set.");

        Assert.False(result.IsAvailable);
        Assert.Equal("runtime-unavailable", result.Status);
    }

    [Fact]
    public void CheckDoesNotLoadTheBridgeOnUnsupportedPlatforms()
    {
        var factoryCalled = false;

        var result = MacSimulatorHidCompatibilityProbe.Check(
            "/Applications/Xcode.app/Contents/Developer",
            isMacOS: false,
            _ =>
            {
                factoryCalled = true;
                return null;
            });

        Assert.False(result.IsAvailable);
        Assert.Equal("unsupported-platform", result.Status);
        Assert.False(factoryCalled);
    }

}
