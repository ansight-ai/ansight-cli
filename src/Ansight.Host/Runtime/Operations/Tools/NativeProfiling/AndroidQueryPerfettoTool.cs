using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.NativeProfiling;

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
