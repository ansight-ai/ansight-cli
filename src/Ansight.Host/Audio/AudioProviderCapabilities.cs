using System.Text.Json.Nodes;

namespace Ansight.Host.Audio;

internal sealed record AudioProviderCapabilities(bool Available, string Code, string Message, JsonObject Diagnostics);
