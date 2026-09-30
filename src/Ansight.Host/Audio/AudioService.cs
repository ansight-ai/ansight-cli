using System.Text.Json.Nodes;

namespace Ansight.Host.Audio;

/// <summary>Session-bound audio operations shared by CLI and repository tasks.</summary>
public sealed class AudioService
{
    private readonly Func<string, JsonObject?, string?, Task<RequestResult>> executeOperation;

    internal AudioService(Func<string, JsonObject?, string?, Task<RequestResult>> executeOperation)
        => this.executeOperation = executeOperation;

    public async Task<AudioOperationResult> ExecuteAsync(AudioOperation operation, JsonObject? arguments, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var scope = ToolExecutionCancellation.Push(cancellationToken);
        var name = operation switch
        {
            AudioOperation.Capabilities => "ansight_get_audio_capabilities",
            AudioOperation.Inject => "ansight_inject_audio",
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
        // Await provider cleanup as well as delivery; cancelling only the await would leave audio running.
        var result = await executeOperation(name, arguments?.DeepClone().AsObject(), $"audio-{Guid.NewGuid():N}").ConfigureAwait(false);
        var payload = (result.Payload?["structuredContent"] as JsonObject)?.DeepClone().AsObject() ?? new JsonObject();
        var failed = result.IsError || result.Payload?["isError"]?.GetValue<bool>() == true;
        var message = payload["message"]?.GetValue<string>() ?? result.ErrorMessage ?? (failed ? "Audio operation failed." : "Audio operation completed.");
        return new AudioOperationResult(operation, !failed, message, payload);
    }
}
