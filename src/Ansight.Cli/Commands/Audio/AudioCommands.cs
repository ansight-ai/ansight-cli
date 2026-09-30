using System.Text.Json;
using System.Text.Json.Nodes;
using Ansight.Host.Audio;

namespace Ansight.Cli.Commands.Audio;

internal static class AudioCommands
{
    private static readonly string[] commonFlagOptions =
    ["help", "beta", "json", "silent", "verbose", "diagnostic", "audit"];

    private static readonly string[] commonValueOptions =
    ["session", "session-id", "data-dir"];

    internal delegate Task<AudioOperationResult> ExecuteAudioOperation(
        AudioOperation operation,
        JsonObject arguments,
        CancellationToken cancellationToken);

    public static Task<int> RunAsync(
        CliArguments arguments,
        CliOutput output,
        CancellationToken cancellationToken)
    {
        if (CliCommandHelp.IsRequested(arguments))
        {
            return Task.FromResult(CliCommandHelp.Write(output, Help));
        }

        return RunAsync(arguments, output, (operation, operationArguments, token) =>
        {
            var context = CliCommandContext.Current
                ?? throw new CliHostUnavailableException(
                    "Audio injection requires a resident host and an existing connected session. Run 'ansight host run' and select a session with 'ansight session list --connected --json'.");
            return context.Runtime.Audio.ExecuteAsync(operation, operationArguments, token);
        }, cancellationToken);
    }

    internal static async Task<int> RunAsync(
        CliArguments arguments,
        CliOutput output,
        ExecuteAudioOperation execute,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(execute);
        var action = arguments.RequirePositional(1, "audio action").ToLowerInvariant();
        arguments.EnsurePositionalCount(2, $"ansight audio {action} [options]");
        var operation = action switch
        {
            "capabilities" => AudioOperation.Capabilities,
            "inject" => AudioOperation.Inject,
            _ => throw new CliUsageException(
                $"Unknown audio action '{action}'. Expected capabilities or inject.")
        };
        arguments.EnsureOptionContract(
            $"ansight audio {action}",
            commonFlagOptions,
            operation == AudioOperation.Inject
                ? [.. commonValueOptions, "file", "timeout-ms", "wait-for-microphone-ms"]
                : commonValueOptions);

        var session = arguments.GetOption("session");
        var sessionAlias = arguments.GetOption("session-id");
        if (session is not null && sessionAlias is not null)
        {
            throw new CliUsageException("Use only one of --session or --session-id.");
        }
        var sessionId = session ?? sessionAlias;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new CliUsageException(
                "audio requires --session <id> or --session-id <id>. Choose an existing connected session with 'ansight session list --connected --json'.");
        }

        var operationArguments = new JsonObject { ["sessionId"] = sessionId.Trim() };
        if (operation == AudioOperation.Inject)
        {
            operationArguments["file"] = Path.GetFullPath(arguments.RequireOption("file"));
            operationArguments["timeoutMs"] = arguments.HasFlag("timeout-ms")
                ? arguments.GetRequiredIntOption("timeout-ms", 100, 60_000)
                : 30_000;
            operationArguments["waitForMicrophoneMs"] = arguments.HasFlag("wait-for-microphone-ms")
                ? arguments.GetRequiredIntOption("wait-for-microphone-ms", 0, 10_000)
                : 0;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var result = await execute(operation, operationArguments, cancellationToken).ConfigureAwait(false);
        output.Write(result.Payload, () => !result.IsSuccess || result.Payload.Count == 0
            ? result.Message
            : result.Payload.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        if (result.IsSuccess) return CliExitCodes.Success;
        return result.Payload["code"]?.GetValue<string>() switch
        {
            "cancelled" => CliExitCodes.Cancelled,
            "unsupported-device" or "audio-route-unavailable" or "audio-route-unverified"
                or "input-route-unverified" or "simulator-audio-route-stale" or "audio-device-missing" or "audio-helper-missing"
                or "audio-helper-unavailable" or "accessibility-permission-required" or "audio-route-ambiguous"
                or "unsupported-audio-device" or "xcode-unavailable" or "platform-unavailable" or "adb-unavailable"
                or "endpoint-unavailable" or "endpoint-ambiguous" or "endpoint-authentication-unavailable"
                => CliExitCodes.CapabilityUnavailable,
            _ => CliExitCodes.Failure
        };
    }

    private const string Help = """
        Deliver an audio file to an existing virtual device's microphone input.

        Usage:
          ansight audio capabilities --session <id> [--json]
          ansight audio inject --session <id> --file <path.wav> [--json]
              [--timeout-ms <100-60000>] [--wait-for-microphone-ms <0-10000>]

        --session-id is an alias for --session. A connected session is required.
        Start recording in the app before injecting. This command does not launch
        the app, press Record, or grant permissions. Calls block until delivery and
        cleanup finish. Ctrl+C requests cancellation and waits up to 10 seconds for
        cleanup acknowledgement; a lost acknowledgement reports uncertain cleanup.
        Failed delivery is never retried automatically.

        WAV input must be PCM16, mono, 16 kHz, at most 15 seconds. Relative --file
        paths use the calling CLI's working directory. Timeout defaults to 30000ms;
        provider preflight does not wait unless a bounded readiness wait is set.

        iOS requires an installed loopback device and verified Simulator input route.
        Android requires supported emulator gRPC and healthy active microphone input.
        Use capabilities for readiness diagnostics; it does not play a test sound.
        A completed delivery is not proof of app capture or transcription. Assert
        the app's transcript or recording separately. Physical devices are unsupported.
        """;
}
