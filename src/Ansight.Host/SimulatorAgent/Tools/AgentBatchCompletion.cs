namespace Ansight.Host.SimulatorAgent.Tools;

internal sealed record AgentBatchCompletion(OpenAiFunctionCall Call, DateTimeOffset StartedUtc, long DurationMilliseconds, ToolCallResult Result);
