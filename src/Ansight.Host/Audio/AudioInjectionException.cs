using System.Text.Json.Nodes;

namespace Ansight.Host.Audio;

internal sealed class AudioInjectionException : Exception
{
    public AudioInjectionException(string code, string message, JsonObject? diagnostics = null) : base(message)
    {
        this.Code = code;
        this.Diagnostics = diagnostics;
    }

    public string Code { get; }
    public JsonObject? Diagnostics { get; }
}
