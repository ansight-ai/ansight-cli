
namespace Ansight.Host.Replay;

public sealed record LocalWorkspaceTestExecutionRequest(
    string WorkspacePath,
    IReadOnlyList<string>? TestIds = null,
    string? SessionId = null,
    string? Platform = null,
    string? DeviceIdentifier = null,
    string? ApplicationPath = null,
    string? DeviceKind = null,
    string Model = "",
    int MaximumTurnsPerInstruction = 64,
    int MaximumRoundTrips = 64,
    int MaximumToolCalls = 512,
    bool ContinueAfterFailure = true,
    bool CaptureTrace = false,
    bool EnableWorkspaceTools = true,
    Guid? TeamId = null)
{
    public string Reasoning { get; init; } = AgentReasoningModes.Fast;

    [System.Text.Json.Serialization.JsonIgnore]
    internal string? DraftSource { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    internal string? DraftTaskRootPath { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    internal IReadOnlyList<string> PreferredTaskIds { get; init; } = [];
}
