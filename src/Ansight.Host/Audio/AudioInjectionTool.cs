using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Audio;

internal sealed class AudioInjectionTool : Operation
{
    private readonly AudioInjectionRouter router;
    private readonly bool inject;

    public AudioInjectionTool(OperationServices services, bool inject) : base(services)
    {
        router = services.AudioInjectionRouter;
        this.inject = inject;
    }

    public override string Name => inject ? "ansight_inject_audio" : "ansight_get_audio_capabilities";
    protected override string Title => inject ? "Inject microphone audio" : "Inspect microphone audio capabilities";
    protected override string Description => inject
        ? "Deliver a WAV fixture to the virtual microphone of an existing connected session. Completion proves delivery only; verify app capture or transcription separately."
        : "Read microphone injection support and readiness for an existing connected session.";

    protected override JsonObject InputSchema
    {
        get
        {
            var properties = new Dictionary<string, ToolSchema>
            {
                ["sessionId"] = ToolSchema.String("Exact connected Ansight session id."),
                ["appId"] = ToolSchema.String("Optional owning app id; must match the selected session.", nullable: true)
            };
            if (inject)
            {
                properties["file"] = ToolSchema.String("PCM16 mono 16 kHz WAV, at most 15 seconds. Tasks require a repository-relative path.");
                properties["timeoutMs"] = ToolSchema.Integer("Total deadline, 100–60000 milliseconds; default 30000.");
                properties["waitForMicrophoneMs"] = ToolSchema.Integer("Readiness wait, 0–10000 milliseconds; default 0.");
            }
            return ToolSchema.Object(properties: properties, required: inject ? ["sessionId", "file"] : ["sessionId"], additionalProperties: false).ToJson();
        }
    }

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
        => ExecuteAsync(arguments, correlationId, null);

    public override async Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId, OperationExecutionContext? context)
    {
        var rejection = AudioExecutionPolicy.RejectParallelCall(Name, context);
        if (rejection is not null) return rejection;
        try
        {
            var sessionId = NormalizeOptionalString(arguments?["sessionId"]?.GetValue<string>());
            if (sessionId is null) throw new AudioInjectionException("invalid-arguments", "Provide an explicit connected sessionId.");
            var appId = NormalizeOptionalString(arguments?["appId"]?.GetValue<string>());
            if (appId is not null && (!runtimeState.TryGetSessionSnapshot(sessionId, out var session) || session?.AppId != appId))
                throw new AudioInjectionException("session-mismatch", "The selected session does not belong to the requested app.");
            var token = ToolExecutionCancellation.Current;
            var payload = inject
                ? await router.Engine.InjectAsync(sessionId,
                    arguments?["file"]?.GetValue<string>() ?? string.Empty,
                    arguments?["timeoutMs"]?.GetValue<int>() ?? 30000,
                    arguments?["waitForMicrophoneMs"]?.GetValue<int>() ?? 0, token).ConfigureAwait(false)
                : await router.Engine.GetCapabilitiesAsync(sessionId, token).ConfigureAwait(false);
            return RequestResult.ToolResult(payload, inject && payload["status"]?.GetValue<string>() != "completed");
        }
        catch (Exception exception) when (exception is AudioInjectionException or OperationCanceledException or InvalidOperationException or ArgumentException)
        {
            var audioError = exception as AudioInjectionException;
            return RequestResult.ToolResult(new JsonObject
            {
                ["schema"] = inject ? "ansight.audio-injection/v1" : "ansight.audio-capabilities/v1",
                ["status"] = exception is OperationCanceledException ? "cancelled" : "failed",
                ["available"] = false,
                ["code"] = audioError?.Code ?? (exception is OperationCanceledException ? "cancelled" : "invalid-arguments"),
                ["message"] = audioError?.Message ?? (exception is OperationCanceledException ? "Audio operation cancelled or timed out." : "Invalid audio operation arguments."),
                ["diagnostics"] = audioError?.Diagnostics?.DeepClone()
            }, isError: true);
        }
    }
}
