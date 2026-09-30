using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ansight.Cli.HostConnection;

internal static class InteractionProtocol
{
    public const string Schema = "ansight.app-interaction/v1";
    public const int MaximumLineCharacters = 65536;
    public static readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 16,
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task WriteAsync(TextWriter writer, object value, CancellationToken cancellationToken)
    {
        await writer.WriteLineAsync(JsonSerializer.Serialize(value, jsonOptions).AsMemory(), cancellationToken).ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static AppInteractionResult Error(string? id, string command, string sessionId, string code, string message)
        => new(Schema, id, command, sessionId, false, null, code, message, null, null, new(0, 0, 0));
}
