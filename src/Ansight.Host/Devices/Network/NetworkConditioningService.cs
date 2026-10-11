using System.Text.Json.Nodes;

namespace Ansight.Host.Devices.Network;

/// <summary>Resident-host operations used by the CLI. The TypeScript task API uses the same tools.</summary>
public sealed class NetworkConditioningService
{
    private readonly Func<string, JsonObject?, string?, Task<RequestResult>> execute;
    internal NetworkConditioningService(Func<string, JsonObject?, string?, Task<RequestResult>> execute) => this.execute = execute;

    public async Task<NetworkConditioningResult> ExecuteAsync(string action, JsonObject arguments, CancellationToken token = default)
    {
        if (action is not ("capabilities" or "get" or "set" or "reset" or "scenario")) throw new ArgumentException("Unknown network conditioning action.");
        using var scope = ToolExecutionCancellation.Push(token);
        var result = await execute("ansight_network_conditioning_" + action, arguments, "network-" + Guid.NewGuid().ToString("N")).ConfigureAwait(false);
        var payload = (result.Payload?["structuredContent"] as JsonObject)?.DeepClone().AsObject() ?? new JsonObject();
        return new NetworkConditioningResult(!result.IsError && result.Payload?["isError"]?.GetValue<bool>() != true, payload);
    }
}

public sealed record NetworkConditioningResult(bool IsSuccess, JsonObject Payload);
