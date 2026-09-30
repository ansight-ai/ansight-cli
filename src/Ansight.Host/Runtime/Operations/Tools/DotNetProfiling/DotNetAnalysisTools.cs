using System.Text.Json.Nodes;
using Ansight.Host.Runtime.DotNetProfiling;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.DotNetProfiling;

internal abstract class DotNetAnalysisOperation : DotNetOperation
{
    protected DotNetAnalysisOperation(DotNetOperationServices services)
        : base(services)
    {
    }

    protected bool TryAnalyze(
        JsonObject? arguments,
        out ParsedTraceAnalysisResult? result,
        out RequestResult? errorResult,
        int hotspotLimit = 100,
        int callTreeDepth = 64,
        int callTreeChildLimit = 100)
    {
        result = null;
        errorResult = null;
        if (!DotNetOperationArguments.TryReadSelection(
                arguments,
                out var captureId,
                out var selection,
                out var error))
        {
            errorResult = ToolError(error!);
            return false;
        }

        try
        {
            result = Services.Analysis.Analyze(
                captureId,
                selection,
                hotspotLimit,
                callTreeDepth,
                callTreeChildLimit);
            return true;
        }
        catch (Exception ex) when (ex is KeyNotFoundException
                                   or InvalidOperationException
                                   or IOException
                                   or UnauthorizedAccessException)
        {
            errorResult = ToolError(ex.Message);
            return false;
        }
    }

    protected static Dictionary<string, ToolSchema> SelectionProperties()
        => DotNetOperationArguments.CaptureSelectionProperties();
}

internal sealed class DotNetGetTraceOverviewTool : DotNetAnalysisOperation
{
    public DotNetGetTraceOverviewTool(DotNetOperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_dotnet_get_trace_overview";

    protected override string Title => "Get .NET Trace Overview";

    protected override string Description => "Summarize selected NetTrace duration, events, CPU samples, processes, threads, GC pauses, JIT work, exceptions, providers, and symbolication quality.";

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

        var overview = JsonSerializer.SerializeToNode(result!.Analysis.Overview, JsonUtil.Compact)?.AsObject()
                       ?? new JsonObject();
        overview["providerEventCounts"] = JsonSerializer.SerializeToNode(
            result.Analysis.ProviderEventCounts,
            JsonUtil.Compact);
        overview["eventNameCounts"] = JsonSerializer.SerializeToNode(
            result.Analysis.EventNameCounts,
            JsonUtil.Compact);
        return Completed(ToolSuccess(DotNetOperationPayloads.AnalysisEnvelope(result, "overview", overview)));
    }
}

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

internal sealed class DotNetGetCpuHotspotsTool : DotNetAnalysisOperation
{
    public DotNetGetCpuHotspotsTool(DotNetOperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_dotnet_get_cpu_hotspots";

    protected override string Title => "Get .NET CPU Hotspots";

    protected override string Description => "Rank symbolicated .NET methods by exclusive and inclusive sampled-thread counts within an optional trace range, process, or thread.";

    protected override JsonObject InputSchema
    {
        get
        {
            var properties = SelectionProperties();
            properties["limit"] = ToolSchema.Integer("Maximum methods to return. Defaults to 50, maximum 500.", nullable: true);
            return ToolSchema.Object(properties: properties, required: ["captureId"], additionalProperties: false).ToJson();
        }
    }

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        if (!ArgumentReader.TryReadPositiveLimit(arguments, "limit", 50, 500, out var limit, out var limitError))
        {
            return Completed(ToolError(limitError!));
        }

        if (!TryAnalyze(arguments, out var result, out var error, hotspotLimit: limit))
        {
            return Completed(error!);
        }

        var payload = new JsonObject
        {
            ["sampleCount"] = result!.Analysis.Overview.CpuSampleCount,
            ["hotspotCount"] = result.Analysis.CpuHotspots.Count,
            ["hotspots"] = JsonSerializer.SerializeToNode(result.Analysis.CpuHotspots, JsonUtil.Compact)
        };
        return Completed(ToolSuccess(DotNetOperationPayloads.AnalysisEnvelope(result, "cpu", payload)));
    }
}

internal sealed class DotNetGetCallTreeTool : DotNetAnalysisOperation
{
    public DotNetGetCallTreeTool(DotNetOperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_dotnet_get_call_tree";

    protected override string Title => "Get .NET Call Tree";

    protected override string Description => "Return a bounded root-to-leaf .NET sampled call tree for the selected trace range, process, or thread.";

    protected override JsonObject InputSchema
    {
        get
        {
            var properties = SelectionProperties();
            properties["maxDepth"] = ToolSchema.Integer("Maximum returned stack depth. Defaults to 32, maximum 128.", nullable: true);
            properties["maxChildren"] = ToolSchema.Integer("Maximum children per call-tree node. Defaults to 50, maximum 200.", nullable: true);
            return ToolSchema.Object(properties: properties, required: ["captureId"], additionalProperties: false).ToJson();
        }
    }

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        if (!ArgumentReader.TryReadPositiveLimit(arguments, "maxDepth", 32, 128, out var maxDepth, out var depthError))
        {
            return Completed(ToolError(depthError!));
        }

        if (!ArgumentReader.TryReadPositiveLimit(arguments, "maxChildren", 50, 200, out var maxChildren, out var childError))
        {
            return Completed(ToolError(childError!));
        }

        if (!TryAnalyze(
                arguments,
                out var result,
                out var error,
                callTreeDepth: maxDepth,
                callTreeChildLimit: maxChildren))
        {
            return Completed(error!);
        }

        return Completed(ToolSuccess(DotNetOperationPayloads.AnalysisEnvelope(
            result!,
            "callTree",
            JsonSerializer.SerializeToNode(result!.Analysis.CallTree, JsonUtil.Compact))));
    }
}

internal sealed class DotNetGetThreadActivityTool : DotNetAnalysisOperation
{
    public DotNetGetThreadActivityTool(DotNetOperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_dotnet_get_thread_activity";

    protected override string Title => "Get .NET Thread Activity";

    protected override string Description => "Rank processes and threads by captured sampled-thread activity for the selected NetTrace range.";

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

        return Completed(ToolSuccess(DotNetOperationPayloads.AnalysisEnvelope(
            result!,
            "threads",
            JsonSerializer.SerializeToNode(result!.Analysis.Threads, JsonUtil.Compact))));
    }
}

internal sealed class DotNetGetGcSummaryTool : DotNetAnalysisOperation
{
    public DotNetGetGcSummaryTool(DotNetOperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_dotnet_get_gc_summary";

    protected override string Title => "Get .NET GC Summary";

    protected override string Description => "Summarize captured .NET garbage collections and return the longest GC intervals in the selected trace range.";

    protected override JsonObject InputSchema
    {
        get
        {
            var properties = SelectionProperties();
            properties["limit"] = ToolSchema.Integer("Maximum GC intervals to return. Defaults to 100, maximum 1000.", nullable: true);
            return ToolSchema.Object(properties: properties, required: ["captureId"], additionalProperties: false).ToJson();
        }
    }

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        if (!ArgumentReader.TryReadPositiveLimit(arguments, "limit", 100, 1000, out var limit, out var limitError))
        {
            return Completed(ToolError(limitError!));
        }

        if (!TryAnalyze(arguments, out var result, out var error))
        {
            return Completed(error!);
        }

        var pauses = result!.Analysis.GcPauses
            .OrderByDescending(pause => pause.DurationMilliseconds)
            .Take(limit)
            .ToArray();
        var payload = new JsonObject
        {
            ["collectionCount"] = result.Analysis.GcPauses.Count,
            ["totalIntervalMilliseconds"] = result.Analysis.GcPauses.Sum(pause => pause.DurationMilliseconds),
            ["longestIntervalMilliseconds"] = result.Analysis.GcPauses.Count == 0
                ? 0
                : result.Analysis.GcPauses.Max(pause => pause.DurationMilliseconds),
            ["generationCounts"] = JsonSerializer.SerializeToNode(
                result.Analysis.GcPauses
                    .GroupBy(pause => pause.Generation)
                    .OrderBy(group => group.Key)
                    .ToDictionary(group => $"generation-{group.Key}", group => group.Count()),
                JsonUtil.Compact),
            ["intervals"] = JsonSerializer.SerializeToNode(pauses, JsonUtil.Compact)
        };
        return Completed(ToolSuccess(DotNetOperationPayloads.AnalysisEnvelope(result, "gc", payload)));
    }
}

internal sealed class DotNetGetJitSummaryTool : DotNetAnalysisOperation
{
    public DotNetGetJitSummaryTool(DotNetOperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_dotnet_get_jit_summary";

    protected override string Title => "Get .NET JIT Summary";

    protected override string Description => "Summarize captured .NET JIT activity and rank methods by IL size within the selected trace range.";

    protected override JsonObject InputSchema
    {
        get
        {
            var properties = SelectionProperties();
            properties["limit"] = ToolSchema.Integer("Maximum JIT methods to return. Defaults to 100, maximum 1000.", nullable: true);
            return ToolSchema.Object(properties: properties, required: ["captureId"], additionalProperties: false).ToJson();
        }
    }

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        if (!ArgumentReader.TryReadPositiveLimit(arguments, "limit", 100, 1000, out var limit, out var limitError))
        {
            return Completed(ToolError(limitError!));
        }

        if (!TryAnalyze(arguments, out var result, out var error))
        {
            return Completed(error!);
        }

        var methods = result!.Analysis.JittedMethods
            .OrderByDescending(method => method.IlSize)
            .ThenBy(method => method.TimestampMilliseconds)
            .Take(limit)
            .ToArray();
        var payload = new JsonObject
        {
            ["methodCount"] = result.Analysis.JittedMethods.Count,
            ["totalIlBytes"] = result.Analysis.JittedMethods.Sum(method => (long)method.IlSize),
            ["methods"] = JsonSerializer.SerializeToNode(methods, JsonUtil.Compact)
        };
        return Completed(ToolSuccess(DotNetOperationPayloads.AnalysisEnvelope(result, "jit", payload)));
    }
}

internal sealed class DotNetGetExceptionsTool : DotNetAnalysisOperation
{
    public DotNetGetExceptionsTool(DotNetOperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_dotnet_get_exceptions";

    protected override string Title => "Get .NET Exceptions";

    protected override string Description => "Group thrown .NET exceptions by type and message within the selected trace range, including counts and first/last timestamps.";

    protected override JsonObject InputSchema
    {
        get
        {
            var properties = SelectionProperties();
            properties["limit"] = ToolSchema.Integer("Maximum exception groups to return. Defaults to 100, maximum 1000.", nullable: true);
            return ToolSchema.Object(properties: properties, required: ["captureId"], additionalProperties: false).ToJson();
        }
    }

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        if (!ArgumentReader.TryReadPositiveLimit(arguments, "limit", 100, 1000, out var limit, out var limitError))
        {
            return Completed(ToolError(limitError!));
        }

        if (!TryAnalyze(arguments, out var result, out var error))
        {
            return Completed(error!);
        }

        var groups = result!.Analysis.Exceptions.Take(limit).ToArray();
        var payload = new JsonObject
        {
            ["throwCount"] = result.Analysis.Exceptions.Sum(group => group.Count),
            ["groupCount"] = result.Analysis.Exceptions.Count,
            ["groups"] = JsonSerializer.SerializeToNode(groups, JsonUtil.Compact)
        };
        return Completed(ToolSuccess(DotNetOperationPayloads.AnalysisEnvelope(result, "exceptions", payload)));
    }
}
