using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.DeviceExecution;

[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed class ExecutionRequirements
{
    public IReadOnlyList<string> Capabilities { get; init; } = [];
    public IReadOnlyList<string> AppTools { get; init; } = [];

    internal void Validate()
    {
        if (Capabilities is null || AppTools is null || Capabilities.Count > 64 || AppTools.Count > 64
            || Capabilities.Concat(AppTools).Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 160 || value.Any(char.IsControl)))
        {
            throw new InvalidDataException("Requirements must contain at most 64 non-empty capability and app-tool identifiers each.");
        }
    }
}
