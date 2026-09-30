using Ansight.Host;

namespace Ansight.Cli.Tests.Commands.Device;

public sealed class DeviceInputCommandsTests
{
    [Fact]
    public async Task RunAsync_IosTapDispatchesThroughInputRuntime()
    {
        var runtime = new RecordingDeviceInputCommandRuntime();
        using var standardOutput = new StringWriter();

        var exitCode = await DeviceInputCommands.RunAsync(
            CliArguments.Parse([
                "input",
                "tap",
                "ios",
                "simulator-udid",
                "--x",
                "158",
                "--y",
                "826"
            ]),
            new CliOutput(false, standardOutput, TextWriter.Null),
            runtime,
            CancellationToken.None);

        var invocation = Assert.Single(runtime.Invocations);
        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Equal("tap", invocation.Action);
        Assert.Equal("ios", invocation.Platform);
        Assert.Equal("simulator-udid", invocation.DeviceIdentifier);
        Assert.Equal([158, 826], invocation.Coordinates);
        Assert.Contains("delivered", standardOutput.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_IosPinchDispatchesBothContactSpans()
    {
        var runtime = new RecordingDeviceInputCommandRuntime();

        var exitCode = await DeviceInputCommands.RunAsync(
            CliArguments.Parse([
                "input",
                "pinch",
                "ios",
                "simulator-udid",
                "--center-x",
                "195",
                "--center-y",
                "420",
                "--start-span",
                "60",
                "--end-span",
                "180",
                "--duration-ms",
                "450"
            ]),
            new CliOutput(false, TextWriter.Null, TextWriter.Null),
            runtime,
            CancellationToken.None);

        var invocation = Assert.Single(runtime.Invocations);
        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Equal("pinch", invocation.Action);
        Assert.Equal([195, 420, 60, 180, 450], invocation.Coordinates);
    }

    [Fact]
    public void CreateTapRequest_NormalizesIosLogicalScreenPoints()
    {
        var request = DeviceInputCommandRuntime.CreateTapRequest(
            "simulator-udid",
            new UiViewport(DevicePlatforms.Ios, 390, 844, "logical-points"),
            158,
            826);

        Assert.Equal(158d / 389d, request.NormalizedX, 10);
        Assert.Equal(826d / 843d, request.NormalizedY, 10);
    }

    [Fact]
    public void CreatePinchRequest_NormalizesBothContacts()
    {
        var request = DeviceInputCommandRuntime.CreatePinchRequest(
            "simulator-udid",
            new UiViewport(DevicePlatforms.Ios, 390, 844, "logical-points"),
            centerX: 195,
            centerY: 420,
            startSpan: 60,
            endSpan: 180,
            durationMilliseconds: 450);

        Assert.Equal(165d / 389d, request.PrimaryStartNormalizedX, 10);
        Assert.Equal(225d / 389d, request.SecondaryStartNormalizedX, 10);
        Assert.Equal(105d / 389d, request.PrimaryEndNormalizedX, 10);
        Assert.Equal(285d / 389d, request.SecondaryEndNormalizedX, 10);
        Assert.Equal(420d / 843d, request.PrimaryStartNormalizedY, 10);
        Assert.Equal(450, request.DurationMilliseconds);
    }

    [Fact]
    public void CreateTapRequest_RejectsCoordinateOutsideTargetViewport()
    {
        var exception = Assert.Throws<CliUsageException>(() =>
            DeviceInputCommandRuntime.CreateTapRequest(
                "simulator-udid",
                new UiViewport(DevicePlatforms.Ios, 390, 844, "logical-points"),
                390,
                100));

        Assert.Contains("--x must be between 0 and 389", exception.Message, StringComparison.Ordinal);
    }

    private sealed class RecordingDeviceInputCommandRuntime : IDeviceInputCommandRuntime
    {
        public List<InputInvocation> Invocations { get; } = [];

        public Task<DeviceOperationResult> SendTapAsync(
            string platform,
            string deviceIdentifier,
            int x,
            int y,
            CancellationToken cancellationToken)
            => Record("tap", platform, deviceIdentifier, [x, y]);

        public Task<DeviceOperationResult> SendSwipeAsync(
            string platform,
            string deviceIdentifier,
            int startX,
            int startY,
            int endX,
            int endY,
            int durationMilliseconds,
            CancellationToken cancellationToken)
            => Record(
                "swipe",
                platform,
                deviceIdentifier,
                [startX, startY, endX, endY, durationMilliseconds]);

        public Task<DeviceOperationResult> SendPinchAsync(
            string platform,
            string deviceIdentifier,
            int centerX,
            int centerY,
            int startSpan,
            int endSpan,
            int durationMilliseconds,
            CancellationToken cancellationToken)
            => Record(
                "pinch",
                platform,
                deviceIdentifier,
                [centerX, centerY, startSpan, endSpan, durationMilliseconds]);

        public Task<DeviceOperationResult> SendTextAsync(
            string platform,
            string deviceIdentifier,
            string value,
            CancellationToken cancellationToken)
            => Record("type-text", platform, deviceIdentifier, []);

        public Task<DeviceOperationResult> SendButtonAsync(
            string platform,
            string deviceIdentifier,
            string button,
            CancellationToken cancellationToken)
            => Record("press-button", platform, deviceIdentifier, []);

        private Task<DeviceOperationResult> Record(
            string action,
            string platform,
            string deviceIdentifier,
            int[] coordinates)
        {
            Invocations.Add(new InputInvocation(action, platform, deviceIdentifier, coordinates));
            return Task.FromResult(DeviceOperationResult.Success(
                action,
                platform,
                deviceIdentifier,
                $"{action} delivered."));
        }
    }

    private sealed record InputInvocation(
        string Action,
        string Platform,
        string DeviceIdentifier,
        int[] Coordinates);
}
