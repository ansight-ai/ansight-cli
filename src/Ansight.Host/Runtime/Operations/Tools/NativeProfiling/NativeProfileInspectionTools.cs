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

internal sealed class NativeGetCaptureManifestTool : NativeProfileOperation
{
    public NativeGetCaptureManifestTool(NativeProfileOperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_native_get_capture_manifest";

    protected override string Title => "Get Native Profile Manifest";

    protected override string Description => "Return the retained native capture manifest, artifact hashes, tool versions, warnings, state, and application provenance.";

    protected override JsonObject InputSchema => CaptureIdSchema();

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        if (!TryReadCaptureId(arguments, out var captureId, out var error))
        {
            return Completed(error!);
        }

        var manifest = Services.Profiling.GetManifest(captureId!);
        return Completed(manifest is null
            ? ToolError($"Native profile capture '{captureId}' was not found.")
            : ToolSuccess(Serialize(manifest)));
    }
}

internal sealed class NativeGetCaptureArtifactTool : NativeProfileOperation
{
    public NativeGetCaptureArtifactTool(NativeProfileOperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_native_get_capture_artifact";

    protected override string Title => "Resolve Native Profile Artifact";

    protected override string Description => "Resolve the absolute local path and immutable evidence metadata for a native capture artifact; defaults to the authoritative trace.";

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: new Dictionary<string, ToolSchema>
        {
            ["captureId"] = ToolSchema.String("Required native profile capture identifier."),
            ["artifactKind"] = ToolSchema.String("Optional exact artifact kind; defaults to the authoritative artifact.", nullable: true)
        },
        required: ["captureId"],
        additionalProperties: false).ToJson();

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        if (!TryReadCaptureId(arguments, out var captureId, out var error))
        {
            return Completed(error!);
        }

        var artifact = Services.Profiling.GetArtifact(
            captureId!,
            arguments?["artifactKind"]?.GetValue<string>());
        return Completed(artifact is null
            ? ToolError($"Native profile capture '{captureId}' or the requested artifact was not found.")
            : ToolSuccess(Serialize(artifact)));
    }
}

internal sealed class NativeGetTraceOverviewTool : NativeProfileOperation
{
    public NativeGetTraceOverviewTool(NativeProfileOperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_native_get_trace_overview";

    protected override string Title => "Get Native Trace Overview";

    protected override string Description => "Summarize native capture provenance and artifacts plus Instruments table inventory or Perfetto configuration and core trace counts.";

    protected override JsonObject InputSchema => CaptureIdSchema();

    public override async Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        if (!TryReadCaptureId(arguments, out var captureId, out var error))
        {
            return error!;
        }

        try
        {
            return ToolSuccess(Serialize(await Services.Profiling.InspectCaptureAsync(captureId!)
                .ConfigureAwait(false)));
        }
        catch (Exception ex) when (ex is KeyNotFoundException
                                   or InvalidOperationException
                                   or IOException
                                   or UnauthorizedAccessException)
        {
            return ToolError(ex.Message);
        }
    }
}

internal sealed class IosGetInstrumentsTocTool : NativeProfileOperation
{
    public IosGetInstrumentsTocTool(NativeProfileOperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_ios_get_instruments_toc";

    protected override string Title => "Inspect Instruments Table of Contents";

    protected override string Description => "Inventory runs, table schemas, and processes from the retained xctrace table-of-contents XML for an iOS capture.";

    protected override JsonObject InputSchema => CaptureIdSchema();

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        if (!TryReadCaptureId(arguments, out var captureId, out var error))
        {
            return Completed(error!);
        }

        try
        {
            return Completed(ToolSuccess(Serialize(Services.Profiling.InspectInstrumentsToc(captureId!))));
        }
        catch (Exception ex) when (ex is KeyNotFoundException
                                   or InvalidOperationException
                                   or IOException
                                   or UnauthorizedAccessException)
        {
            return Completed(ToolError(ex.Message));
        }
    }
}

internal sealed class AndroidQueryPerfettoTool : NativeProfileOperation
{
    public AndroidQueryPerfettoTool(NativeProfileOperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_android_query_perfetto";

    protected override string Title => "Query Android Perfetto Trace";

    protected override string Description => "Run one bounded, read-only SELECT or WITH query against a retained Android trace through the local Perfetto Trace Processor.";

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: new Dictionary<string, ToolSchema>
        {
            ["captureId"] = ToolSchema.String("Required Android native profile capture identifier."),
            ["sql"] = ToolSchema.String("One read-only PerfettoSQL SELECT or WITH statement."),
            ["limit"] = ToolSchema.Integer("Maximum returned rows. Defaults to 1000, maximum 10000.", nullable: true)
        },
        required: ["captureId", "sql"],
        additionalProperties: false).ToJson();

    public override async Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        if (!TryReadCaptureId(arguments, out var captureId, out var captureError))
        {
            return captureError!;
        }

        var sql = arguments?["sql"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(sql))
        {
            return ToolError("sql is required.");
        }

        if (!ArgumentReader.TryReadPositiveLimit(arguments, "limit", 1_000, 10_000, out var limit, out var limitError))
        {
            return ToolError(limitError!);
        }

        try
        {
            return ToolSuccess(Serialize(await Services.Profiling.QueryPerfettoAsync(
                captureId!,
                sql,
                limit).ConfigureAwait(false)));
        }
        catch (Exception ex) when (ex is ArgumentException
                                   or KeyNotFoundException
                                   or InvalidOperationException
                                   or IOException
                                   or UnauthorizedAccessException)
        {
            return ToolError(ex.Message);
        }
    }
}
