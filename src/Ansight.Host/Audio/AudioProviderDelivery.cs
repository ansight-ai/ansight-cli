using System.Text.Json.Nodes;

namespace Ansight.Host.Audio;

internal sealed record AudioProviderDelivery(string CompletionKind, long SubmittedFrames, JsonObject Diagnostics);
