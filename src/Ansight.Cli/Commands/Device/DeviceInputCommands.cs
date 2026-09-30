namespace Ansight.Cli.Commands.Device;

internal static class DeviceInputCommands
{
    public static async Task<int> RunAsync(
        CliArguments arguments,
        CliOutput output,
        CancellationToken cancellationToken)
    {
        if (CliCommandHelp.IsRequested(arguments))
        {
            return CliCommandHelp.Write(output, BuildHelp());
        }

        var runtimeOptions = CliRuntime.ResolveOptions(arguments);
        using var runtime = new DeviceInputCommandRuntime(
            CliRuntime.CreateHostOptions(runtimeOptions));
        return await RunAsync(arguments, output, runtime, cancellationToken).ConfigureAwait(false);
    }

    internal static Task<int> RunAsync(
        CliArguments arguments,
        CliOutput output,
        IDeviceInputCommandRuntime runtime,
        CancellationToken cancellationToken)
    {
        var action = arguments.RequirePositional(1, "input action").ToLowerInvariant();
        var platform = arguments.RequirePositional(2, "platform");
        var deviceIdentifier = arguments.RequirePositional(3, "device identifier");
        return action switch
        {
            "tap" => DeviceCommands.RunOperationAsync(
                runtime.SendTapAsync(
                    platform,
                    deviceIdentifier,
                    arguments.GetRequiredIntOption("x", 0, 100_000),
                    arguments.GetRequiredIntOption("y", 0, 100_000),
                    cancellationToken),
                output),
            "swipe" => DeviceCommands.RunOperationAsync(
                runtime.SendSwipeAsync(
                    platform,
                    deviceIdentifier,
                    arguments.GetRequiredIntOption("start-x", 0, 100_000),
                    arguments.GetRequiredIntOption("start-y", 0, 100_000),
                    arguments.GetRequiredIntOption("end-x", 0, 100_000),
                    arguments.GetRequiredIntOption("end-y", 0, 100_000),
                    arguments.GetIntOption("duration-ms", 300, 1, 60_000),
                    cancellationToken),
                output),
            "pinch" => DeviceCommands.RunOperationAsync(
                runtime.SendPinchAsync(
                    platform,
                    deviceIdentifier,
                    arguments.GetRequiredIntOption("center-x", 0, 100_000),
                    arguments.GetRequiredIntOption("center-y", 0, 100_000),
                    arguments.GetRequiredIntOption("start-span", 1, 100_000),
                    arguments.GetRequiredIntOption("end-span", 1, 100_000),
                    arguments.GetIntOption("duration-ms", 300, 1, 60_000),
                    cancellationToken),
                output),
            "text" or "type" => DeviceCommands.RunOperationAsync(
                runtime.SendTextAsync(
                    platform,
                    deviceIdentifier,
                    arguments.RequireOption("value"),
                    cancellationToken),
                output),
            "button" => DeviceCommands.RunOperationAsync(
                runtime.SendButtonAsync(
                    platform,
                    deviceIdentifier,
                    arguments.RequirePositional(4, "button"),
                    cancellationToken),
                output),
            _ => throw new CliUsageException(
                $"Unknown input action '{action}'. Expected tap, swipe, pinch, text, or button.")
        };
    }

    private static string BuildHelp()
        => """
           Send real input through ADB or the iOS Simulator HID bridge

           Usage:
             ansight input tap <ios|android> <device-id> --x <coordinate> --y <coordinate>
             ansight input swipe <ios|android> <device-id> --start-x <coordinate> --start-y <coordinate> --end-x <coordinate> --end-y <coordinate> [--duration-ms <ms>]
             ansight input pinch ios <device-id> --center-x <points> --center-y <points> --start-span <points> --end-span <points> [--duration-ms <ms>]
             ansight input text <ios|android> <device-id> --value <text>
             ansight input button <ios|android> <device-id> <button>

           Commands:
             tap       Tap an absolute screen coordinate
             swipe     Drag between two absolute screen coordinates
             pinch     Move two horizontal contacts between the requested spans
             text      Type text through the platform input bridge; alias: type
             button    Send a platform button such as back, home, enter, or menu

           Notes:
             Android coordinates are display pixels. iOS Simulator coordinates are logical
             screen points and are normalized against the current screenshot orientation.
             Use `ansight doctor` to inspect the packaged Simulator HID capability.

           Examples:
             ansight input tap android emulator-5554 --x 240 --y 520
             ansight input tap ios CD96833E-0000-0000-0000-000000000000 --x 158 --y 826
             ansight input swipe android emulator-5554 --start-x 200 --start-y 700 --end-x 200 --end-y 200
             ansight input pinch ios CD96833E-0000-0000-0000-000000000000 --center-x 195 --center-y 420 --start-span 60 --end-span 180
             ansight input text android emulator-5554 --value "hello world"
           """;
}
