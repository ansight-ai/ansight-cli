using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.NativeProfiling;

internal sealed class NativeListCapturesTool : NativeProfileOperation
{
    public NativeListCapturesTool(NativeProfileOperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_native_list_captures";

    protected override string Title => "List Native Profile Captures";

    protected override string Description => "List retained iOS Instruments and Android Perfetto captures with their state, preset, and provenance.";

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: new Dictionary<string, ToolSchema>
        {
            ["platform"] = ToolSchema.String("Optional exact platform filter: ios or android.", nullable: true),
            ["limit"] = ToolSchema.Integer("Maximum captures to return. Defaults to 100, maximum 1000.", nullable: true)
        },
        additionalProperties: false).ToJson();

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        if (!ArgumentReader.TryReadPositiveLimit(arguments, "limit", 100, 1_000, out var limit, out var error))
        {
            return Completed(ToolError(error!));
        }

        var platform = arguments?["platform"]?.GetValue<string>()?.Trim().ToLowerInvariant();

        if (platform is not null and not ("ios" or "android"))
        {
            return Completed(ToolError("platform must be ios or android when supplied."));
        }

        var captures = Services.Profiling.ListCaptures()
            .Where(capture => capture.Platform is "ios" or "android")
            .Where(capture => platform is null
                || string.Equals(capture.Platform, platform, StringComparison.Ordinal))
            .OrderByDescending(static capture => capture.CreatedUtc)
            .Take(limit)
            .ToArray();

        return Completed(ToolSuccess(new JsonObject
        {
            ["captureCount"] = captures.Length,
            ["captures"] = Serialize(captures)
        }));
    }
}
