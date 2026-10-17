using Ansight.Cli.Input;
using Ansight.Host;

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
        if (IsTextInput(arguments) && TextInputValue.Parse(arguments).SecretAlias is not null)
        {
            _ = ResolveSecretAppId(arguments, TextInputValue.Parse(arguments));
            await using var lease = await CliRuntimeLease.CreateAsync(runtimeOptions, start: false, cancellationToken)
                .ConfigureAwait(false);
            return await RunAsync(arguments, output, runtime, cancellationToken,
                (appId, alias) => SecretTextInput.ReadValue(lease.Runtime, appId, alias)).ConfigureAwait(false);
        }
        return await RunAsync(arguments, output, runtime, cancellationToken).ConfigureAwait(false);
    }

    internal static Task<int> RunAsync(
        CliArguments arguments,
        CliOutput output,
        IDeviceInputCommandRuntime runtime,
        CancellationToken cancellationToken,
        Func<string, string, string>? readSecret = null)
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
            "text" or "type" => RunTextAsync(arguments, output, runtime, platform, deviceIdentifier,
                readSecret, cancellationToken),
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

    internal static bool IsTextInput(CliArguments arguments)
        => arguments.Positionals.Count > 1
           && arguments.Positionals[1].ToLowerInvariant() is "text" or "type";

    private static async Task<int> RunTextAsync(
        CliArguments arguments, CliOutput output, IDeviceInputCommandRuntime runtime,
        string platform, string deviceIdentifier, Func<string, string, string>? readSecret,
        CancellationToken cancellationToken)
    {
        var input = TextInputValue.Parse(arguments);
        if (input.SecretAlias is not { } alias)
            return await DeviceCommands.RunOperationAsync(runtime.SendTextAsync(
                platform, deviceIdentifier, input.Value!, cancellationToken), output).ConfigureAwait(false);

        var appId = ResolveSecretAppId(arguments, input);
        cancellationToken.ThrowIfCancellationRequested();
        var value = (readSecret ?? throw new CliUsageException("Secret input requires access to the app secret store."))(appId, alias);
        bool succeeded;
        try
        {
            var result = await runtime.SendTextAsync(platform, deviceIdentifier, value, cancellationToken).ConfigureAwait(false);
            succeeded = result.IsSuccess;
        }
        catch (OperationCanceledException)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception)
        {
            succeeded = false;
        }
        var message = succeeded ? $"Secret '{alias}' was typed successfully."
            : $"Could not type secret '{alias}'. Inspect the app before retrying.";
        var safeResult = new DeviceOperationResult(succeeded, "type-text", platform, deviceIdentifier, message);
        return await DeviceCommands.RunOperationAsync(Task.FromResult(safeResult), output).ConfigureAwait(false);
    }

    private static string ResolveSecretAppId(CliArguments arguments, TextInputValue input)
    {
        var requested = arguments.GetOption("app-id");
        if (requested is not null && input.AppId is not null
            && !string.Equals(requested, input.AppId, StringComparison.Ordinal))
            throw new CliUsageException("The secret reference's app ID does not match --app-id.");
        return input.AppId ?? arguments.RequireOption("app-id");
    }

    private static string BuildHelp()
        => """
           Send real input through ADB or the iOS Simulator HID bridge

           Usage:
             ansight input tap <ios|android> <device-id> --x <coordinate> --y <coordinate>
             ansight input swipe <ios|android> <device-id> --start-x <coordinate> --start-y <coordinate> --end-x <coordinate> --end-y <coordinate> [--duration-ms <ms>]
             ansight input pinch ios <device-id> --center-x <points> --center-y <points> --start-span <points> --end-span <points> [--duration-ms <ms>]
             ansight input text <ios|android> <device-id> --value <text>
             ansight input text <ios|android> <device-id> --app-id <app-id> --secret <alias>
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
             Text accepts --value '@secret:<app-id>/<alias>', or --secret <alias> with --app-id.
             The shorter --value '@secret:<alias>' also uses --app-id.
             Focus the intended app field first; --app-id selects the secret's storage scope.
             Use --value '@@secret:<text>' to type a literal @secret: prefix.
             Secret input resolves locally and returns status only; normal capture still applies.

           Examples:
             ansight input tap android emulator-5554 --x 240 --y 520
             ansight input tap ios CD96833E-0000-0000-0000-000000000000 --x 158 --y 826
             ansight input swipe android emulator-5554 --start-x 200 --start-y 700 --end-x 200 --end-y 200
             ansight input pinch ios CD96833E-0000-0000-0000-000000000000 --center-x 195 --center-y 420 --start-span 60 --end-span 180
             ansight input text android emulator-5554 --value "hello world"
           """;
}
