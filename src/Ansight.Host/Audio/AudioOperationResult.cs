using System.Text.Json.Nodes;

namespace Ansight.Host.Audio;

public sealed record AudioOperationResult(AudioOperation Operation, bool IsSuccess, string Message, JsonObject Payload);
