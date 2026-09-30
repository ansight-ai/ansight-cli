using Ansight.RemoteSimulator.Core.Input;

namespace Ansight.Host.Tests.Unit.RemoteSimulator;

public sealed class RemotePointerEventTests
{
    [Fact]
    public void NormalizeAndValidate_PreservesDesktopMouseInput()
    {
        var pointerEvent = new RemotePointerEvent(
            " mac-process-42 ",
            " SCROLL ",
            1.2,
            -0.25,
            0,
            123,
            MouseButton: " NONE ",
            ScrollDeltaX: 2.5,
            ScrollDeltaY: -18.75);

        var normalized = pointerEvent.NormalizeAndValidate();

        Assert.Equal("mac-process-42", normalized.DeviceUdid);
        Assert.Equal("scroll", normalized.Phase);
        Assert.Equal(1, normalized.NormalizedX);
        Assert.Equal(0, normalized.NormalizedY);
        Assert.Equal("none", normalized.MouseButton);
        Assert.Equal(2.5, normalized.ScrollDeltaX);
        Assert.Equal(-18.75, normalized.ScrollDeltaY);
    }

    [Fact]
    public void NormalizeAndValidate_DefaultsLegacyClickToLeftButton()
    {
        var pointerEvent = new RemotePointerEvent("device", "down", 0.5, 0.5, 1, 123);

        var normalized = pointerEvent.NormalizeAndValidate();

        Assert.Equal("left", normalized.MouseButton);
    }

    [Fact]
    public void NormalizeAndValidate_RejectsNonFiniteScrollDeltas()
    {
        var pointerEvent = new RemotePointerEvent(
            "device",
            "scroll",
            0.5,
            0.5,
            0,
            123,
            ScrollDeltaY: double.NaN);

        Assert.Throws<ArgumentException>(() => pointerEvent.NormalizeAndValidate());
    }
}
