using Ansight.RemoteSimulator.Core.Input;
using Ansight.RemoteSimulator.Core.Runtime;
using Ansight.RemoteSimulator.Core.Simulator.Android;

namespace Ansight.Host.Tests.Unit.RemoteSimulator;

public sealed class AndroidEmulatorInputSinkTests
{
    private const string DeviceSerial = "emulator-5554";

    [Fact]
    public async Task SendAsync_ConvertsTapToDisplayCoordinates()
    {
        var client = new FakeAndroidEmulatorClient();
        await using var sink = CreateSink(client);

        await sink.SendAsync(new RemotePointerEvent(DeviceSerial, "down", 0.25, 0.5, 7, 100));
        var result = await sink.SendAsync(new RemotePointerEvent(DeviceSerial, "up", 0.25, 0.5, 7, 150));

        Assert.True(result.IsSuccess);
        Assert.Collection(
            client.Commands,
            command => Assert.Equal(
                ["-s", DeviceSerial, "shell", "input", "tap", "250", "1000"],
                command));
    }

    [Fact]
    public async Task SendAsync_ConvertsCompletedDragToTimedSwipe()
    {
        var client = new FakeAndroidEmulatorClient();
        await using var sink = CreateSink(client);

        await sink.SendAsync(new RemotePointerEvent(DeviceSerial, "down", 0.1, 0.2, 4, 100));
        await sink.SendAsync(new RemotePointerEvent(DeviceSerial, "move", 0.5, 0.6, 4, 250));
        var result = await sink.SendAsync(new RemotePointerEvent(DeviceSerial, "up", 0.9, 0.8, 4, 500));

        Assert.True(result.IsSuccess);
        Assert.Collection(
            client.Commands,
            command => Assert.Equal(
                ["-s", DeviceSerial, "shell", "input", "swipe", "100", "400", "899", "1599", "400"],
                command));
    }

    [Fact]
    public async Task SendAsync_ForwardsLiveMultiTouchThroughEmulatorController()
    {
        var client = new FakeAndroidEmulatorClient();
        var controller = new FakeAndroidEmulatorController();
        await using var sink = CreateSink(client, controller);

        var downResult = await sink.SendAsync(new RemotePointerEvent(
            DeviceSerial,
            "down",
            0.25,
            0.5,
            7,
            100,
            new RemoteTouchContact(0.75, 0.5)));
        var upResult = await sink.SendAsync(new RemotePointerEvent(
            DeviceSerial,
            "up",
            0.2,
            0.45,
            7,
            160,
            new RemoteTouchContact(0.8, 0.55)));

        Assert.True(downResult.IsSuccess);
        Assert.True(upResult.IsSuccess);
        Assert.Empty(client.Commands);
        Assert.Collection(
            controller.TouchEvents,
            touchEvent => Assert.Equal(
                [
                    new AndroidEmulatorTouch(250, 1000, 0, 1024),
                    new AndroidEmulatorTouch(749, 1000, 1, 1024),
                ],
                touchEvent),
            touchEvent => Assert.Equal(
                [
                    new AndroidEmulatorTouch(200, 900, 0, 0),
                    new AndroidEmulatorTouch(799, 1099, 1, 0),
                ],
                touchEvent));
    }

    [Fact]
    public async Task SendButtonAndKeyAsync_UseTypedAndroidKeyCodes()
    {
        var client = new FakeAndroidEmulatorClient();
        await using var sink = CreateSink(client);

        var backResult = await sink.SendButtonAsync(new RemoteButtonEvent(DeviceSerial, "back"));
        var keyDownResult = await sink.SendKeyAsync(new RemoteKeyEvent(DeviceSerial, 4, "down"));
        var keyUpResult = await sink.SendKeyAsync(new RemoteKeyEvent(DeviceSerial, 4, "up"));

        Assert.True(backResult.IsSuccess);
        Assert.True(keyDownResult.IsSuccess);
        Assert.True(keyUpResult.IsSuccess);
        Assert.Collection(
            client.Commands,
            command => Assert.Equal(
                ["-s", DeviceSerial, "shell", "input", "keyevent", "KEYCODE_BACK"],
                command),
            command => Assert.Equal(
                ["-s", DeviceSerial, "shell", "input", "keyevent", "KEYCODE_A"],
                command));
    }

    [Fact]
    public async Task SendKeyAsync_ForwardsModifierChordAsKeyCombination()
    {
        var client = new FakeAndroidEmulatorClient();
        await using var sink = CreateSink(client);

        await sink.SendKeyAsync(new RemoteKeyEvent(DeviceSerial, 225, "down"));
        await sink.SendKeyAsync(new RemoteKeyEvent(DeviceSerial, 4, "down"));
        await sink.SendKeyAsync(new RemoteKeyEvent(DeviceSerial, 4, "up"));
        await sink.SendKeyAsync(new RemoteKeyEvent(DeviceSerial, 225, "up"));

        Assert.Collection(
            client.Commands,
            command => Assert.Equal(
                ["-s", DeviceSerial, "shell", "input", "keycombination", "KEYCODE_SHIFT_LEFT", "KEYCODE_A"],
                command));
    }

    [Fact]
    public async Task SendTextAsync_UsesStandardInputSoTextIsAbsentFromHostProcessArguments()
    {
        var client = new FakeAndroidEmulatorClient();
        await using var sink = CreateSink(client);

        var result = await sink.SendTextAsync(new RemoteTextEvent(DeviceSerial, "a b%s\n"));

        Assert.True(result.IsSuccess);
        Assert.Empty(client.Commands);
        Assert.Collection(
            client.StandardInputCommands,
            command =>
            {
                Assert.Equal(["-s", DeviceSerial, "shell", "-T"], command.Arguments);
                Assert.Equal("input 'text' 'a%sb'\n", command.StandardInput);
            },
            command => Assert.Equal("input 'text' '%'\n", command.StandardInput),
            command => Assert.Equal("input 'text' 's'\n", command.StandardInput),
            command => Assert.Equal("input 'keyevent' 'KEYCODE_ENTER'\n", command.StandardInput));
    }

    private static AndroidEmulatorInputSink CreateSink(
        FakeAndroidEmulatorClient client,
        IAndroidEmulatorController? controller = null)
        => new(
            client,
            serial => string.Equals(serial, DeviceSerial, StringComparison.Ordinal)
                ? new RemoteRuntimeDevice(
                    DeviceSerial,
                    "Pixel 9",
                    "Booted",
                    true,
                    "Android API 35",
                    "android",
                    1000,
                    2000)
                : null,
            controller);

    private sealed class FakeAndroidEmulatorController : IAndroidEmulatorController
    {
        public List<IReadOnlyList<AndroidEmulatorTouch>> TouchEvents { get; } = [];

        public Task<byte[]> CaptureScreenshotPngAsync(
            string deviceSerial,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Array.Empty<byte>());

        public Task SendTouchAsync(
            string deviceSerial,
            IReadOnlyList<AndroidEmulatorTouch> touches,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(DeviceSerial, deviceSerial);
            TouchEvents.Add(touches.ToArray());
            return Task.CompletedTask;
        }
    }
}
