using System.Text.Json.Nodes;
using Ansight.Host.Runtime.DotNetProfiling;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.DotNetProfiling;

internal sealed class DotNetGetStartupTimelineTool : DotNetAnalysisOperation
{
    public DotNetGetStartupTimelineTool(DotNetOperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_dotnet_get_startup_timeline";

    protected override string Title => "Get .NET Startup Timeline";

    protected override string Description => "Build an evidence-backed startup timeline from trace boundaries, Ansight's application-ready marker when present, and earliest JIT, GC, and exception activity.";

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: SelectionProperties(),
        required: ["captureId"],
        additionalProperties: false).ToJson();

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        if (!TryAnalyze(arguments, out var result, out var error))
        {
            return Completed(error!);
        }

        var analysis = result!.Analysis;
        var milestones = new List<JsonObject>
        {
            Milestone("trace-first-event", analysis.Overview.StartMilliseconds, "First captured EventPipe event in the selected range")
        };

        if (analysis.JittedMethods.Count > 0)
        {
            milestones.Add(Milestone(
                "first-jit",
                analysis.JittedMethods.Min(method => method.TimestampMilliseconds),
                "First captured method JIT event"));
        }

        if (analysis.GcPauses.Count > 0)
        {
            milestones.Add(Milestone(
                "first-gc",
                analysis.GcPauses.Min(pause => pause.StartMilliseconds),
                "First captured garbage collection"));
        }

        if (analysis.Exceptions.Count > 0)
        {
            milestones.Add(Milestone(
                "first-exception",
                analysis.Exceptions.Min(exception => exception.FirstTimestampMilliseconds),
                "First captured thrown exception"));
        }

        foreach (var startupMilestone in analysis.StartupMilestones)
        {
            var isApplicationReady = string.Equals(
                startupMilestone.EventName,
                DotNetStartupMarker.EventName,
                StringComparison.Ordinal);
            milestones.Add(Milestone(
                isApplicationReady ? "application-ready" : "managed-module-initialized",
                startupMilestone.TimestampMilliseconds,
                $"{startupMilestone.Provider}/{startupMilestone.EventName}"));
        }

        milestones.Add(Milestone(
            "trace-last-event",
            analysis.Overview.EndMilliseconds,
            "Last captured EventPipe event in the selected range"));

        var timeline = new JsonObject
        {
            ["durationMilliseconds"] = analysis.Overview.DurationMilliseconds,
            ["milestones"] = PayloadJson.CreateJsonArray(
                milestones
                    .OrderBy(milestone => milestone["timestampMilliseconds"]!.GetValue<double>())
                    .Select(milestone => (JsonNode?)milestone)),
            ["applicationReadyMarkerObserved"] = analysis.StartupMilestones.Any(milestone => string.Equals(
                milestone.EventName,
                DotNetStartupMarker.EventName,
                StringComparison.Ordinal)),
            ["limitations"] = analysis.StartupMilestones.Any(milestone => string.Equals(
                milestone.EventName,
                DotNetStartupMarker.EventName,
                StringComparison.Ordinal))
                ? new JsonArray(
                    "The application-ready marker means the first MAUI page handler was observed; it is not a first-frame rendering measurement.",
                    "Diagnostic startup duration includes EventPipe and launch instrumentation overhead.")
                : new JsonArray(
                    "No Ansight application-ready marker was captured, so trace boundaries are the startup proxy.",
                    "Diagnostic startup duration includes EventPipe and launch instrumentation overhead.")
        };

        return Completed(ToolSuccess(DotNetOperationPayloads.AnalysisEnvelope(result, "startupTimeline", timeline)));
    }

    private static JsonObject Milestone(string id, double timestampMilliseconds, string description)
        => new()
        {
            ["id"] = id,
            ["timestampMilliseconds"] = timestampMilliseconds,
            ["description"] = description
        };
}
