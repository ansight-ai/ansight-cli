using System.Text.Json.Nodes;

namespace Ansight.Host.Audio;

internal sealed class AudioInjectionException(string code, string message, JsonObject? diagnostics = null) : Exception(message)
{
    public string Code { get; } = code;
    public JsonObject? Diagnostics { get; } = diagnostics;
}
