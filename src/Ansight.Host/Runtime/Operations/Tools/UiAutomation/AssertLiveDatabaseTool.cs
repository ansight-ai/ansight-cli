using System.Diagnostics;
using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.UiAutomation;

internal sealed class AssertLiveDatabaseTool : RemoteAppOperation
{
    private const string QueryToolId = "data.query";

    public AssertLiveDatabaseTool(OperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_assert_database";

    protected override string Title => "Assert Live Database";

    protected override string Description => "Run a constrained read-only query until its row count or first-row scalar value matches the expected database state.";

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: new Dictionary<string, ToolSchema>
        {
            ["sessionId"] = ToolSchema.String("Specific live session id to target.", nullable: true),
            ["appId"] = ToolSchema.String("App id to target when exactly one live session exists for that app.", nullable: true),
            ["path"] = ToolSchema.String("SQLite database path inside the app sandbox."),
            ["sql"] = ToolSchema.String("Read-only SELECT, PRAGMA, WITH, or EXPLAIN query."),
            ["expectedRowCount"] = ToolSchema.Integer("Exact expected result row count.", nullable: true),
            ["scalarColumn"] = ToolSchema.String("Column to read from the first row. Defaults to the first column.", nullable: true),
            ["expectedScalar"] = ToolSchema.String("Expected string representation of the first-row scalar value.", nullable: true),
            ["timeoutMs"] = ToolSchema.Integer("Timeout from zero through 60000 milliseconds. Defaults to zero for one assertion.", nullable: true),
            ["pollIntervalMs"] = ToolSchema.Integer("Polling interval from 100 through 5000 milliseconds. Defaults to 500.", nullable: true),
            ["maxRows"] = ToolSchema.Integer("Maximum rows to retrieve from 1 through 1000. Defaults to 100.", nullable: true),
            ["actionId"] = ToolSchema.String("Optional UI action id to correlate this database assertion with.", nullable: true)
        },
        required: ["path", "sql"],
        additionalProperties: false).ToJson();

    public override async Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        if (!sessionResolver.TryResolveLiveSession(arguments, out var snapshot, out var resolutionError))
        {
            return ToolError(resolutionError);
        }

        var path = LiveUiToolSchemas.ReadString(arguments, "path");
        var sql = LiveUiToolSchemas.ReadString(arguments, "sql");
        if (path is null || sql is null)
        {
            return ToolError("path and sql are required.");
        }

        var expectedRowCount = ReadNullableInteger(arguments, "expectedRowCount");
        var expectedScalar = ReadOptionalScalar(arguments, "expectedScalar");
        if (!expectedRowCount.HasValue && expectedScalar is null)
        {
            return ToolError("expectedRowCount or expectedScalar is required.");
        }

        var catalogResponse = await appToolBridge.QueryToolsAsync(
            snapshot!.SessionId,
            CancellationToken.None,
            new AppToolBridgeRequestContext("host-ui", Name, correlationId));
        if (!catalogResponse.Success
            || catalogResponse.Envelope?.Payload is not { } catalog
            || !RemoteAppToolCatalog.HasTool(catalog, QueryToolId))
        {
            return ToolError("The live app does not expose the read-only data.query tool.");
        }

        var timeoutMs = Math.Clamp(LiveUiToolSchemas.ReadInteger(arguments, "timeoutMs", 0), 0, 60_000);
        var pollIntervalMs = Math.Clamp(LiveUiToolSchemas.ReadInteger(arguments, "pollIntervalMs", 500), 100, 5_000);
        var maxRows = Math.Clamp(LiveUiToolSchemas.ReadInteger(arguments, "maxRows", 100), 1, 1_000);
        var scalarColumn = LiveUiToolSchemas.ReadString(arguments, "scalarColumn");
        var actionId = LiveUiToolSchemas.ReadString(arguments, "actionId");
        var stopwatch = Stopwatch.StartNew();
        var attempts = 0;
        DatabaseAssertionSample? latest = null;
        string? latestError = null;

        do
        {
            attempts++;
            var response = await appToolBridge.CallToolWithCatalogRecoveryAsync(
                snapshot.SessionId,
                QueryToolId,
                new JsonObject
                {
                    ["path"] = path,
                    ["database"] = path,
                    ["sql"] = sql,
                    ["maxRows"] = maxRows,
                    ["limit"] = maxRows
                },
                after: null,
                CancellationToken.None,
                new AppToolBridgeRequestContext("host-ui", Name, correlationId));
            if (response.Success
                && response.Envelope is not null
                && !string.Equals(response.Envelope.Type, ToolProtocolMessageTypes.ErrorType, StringComparison.Ordinal)
                && TryReadSample(response.Envelope.Payload, scalarColumn, out latest)
                && latest is not null)
            {
                latestError = null;
                if ((!expectedRowCount.HasValue || latest.RowCount == expectedRowCount.Value)
                    && (expectedScalar is null || string.Equals(latest.Scalar, expectedScalar, StringComparison.Ordinal)))
                {
                    return BuildResult(
                        snapshot,
                        path,
                        sql,
                        expectedRowCount,
                        expectedScalar,
                        latest,
                        attempts,
                        stopwatch.ElapsedMilliseconds,
                        passed: true,
                        message: "Database assertion passed.",
                        actionId: actionId);
                }
            }
            else
            {
                latestError = response.Message;
            }

            var remainingMs = timeoutMs - stopwatch.ElapsedMilliseconds;
            if (remainingMs <= 0)
            {
                break;
            }

            await Task.Delay((int)Math.Min(pollIntervalMs, remainingMs));
        }
        while (stopwatch.ElapsedMilliseconds <= timeoutMs);

        return BuildResult(
            snapshot,
            path,
            sql,
            expectedRowCount,
            expectedScalar,
            latest,
            attempts,
            stopwatch.ElapsedMilliseconds,
            passed: false,
            message: latestError ?? "Database values did not match the expected state before the timeout.",
            actionId: actionId);
    }

    private RequestResult BuildResult(
        AppSessionSnapshot snapshot,
        string path,
        string sql,
        int? expectedRowCount,
        string? expectedScalar,
        DatabaseAssertionSample? sample,
        int attempts,
        long elapsedMs,
        bool passed,
        string message,
        string? actionId)
    {
        if (actionId is not null)
        {
            runtimeState.AddSessionLog(
                snapshot.SessionId,
                new LogEntry(DateTimeOffset.UtcNow, message)
                {
                    Source = "Ansight Database Assertion",
                    Tag = "database.assert",
                    EventId = actionId,
                    Priority = passed ? LogPriority.Information : LogPriority.Error
                });
        }

        return RequestResult.ToolResult(
            new JsonObject
            {
                ["capability"] = "database.assert",
                ["passed"] = passed,
                ["sessionId"] = snapshot.SessionId,
                ["appId"] = snapshot.AppId,
                ["toolId"] = QueryToolId,
                ["actionId"] = actionId,
                ["path"] = path,
                ["sql"] = sql,
                ["expectedRowCount"] = expectedRowCount,
                ["actualRowCount"] = sample?.RowCount,
                ["expectedScalar"] = expectedScalar,
                ["actualScalar"] = sample?.Scalar,
                ["capturedAtUtc"] = sample?.CapturedAtUtc,
                ["attempts"] = attempts,
                ["elapsedMs"] = elapsedMs,
                ["message"] = message
            },
            isError: !passed);
    }

    private static bool TryReadSample(
        JsonNode? envelopePayload,
        string? scalarColumn,
        out DatabaseAssertionSample? sample)
    {
        sample = null;
        if (envelopePayload is not JsonObject payload
            || payload["result"] is not JsonObject rawResult)
        {
            return false;
        }

        var result = rawResult["query"] as JsonObject ?? rawResult;
        if (result["rows"] is not JsonArray rows)
        {
            return false;
        }

        var firstRow = rows.OfType<JsonObject>().FirstOrDefault();
        JsonNode? scalar = null;
        if (firstRow is not null)
        {
            if (scalarColumn is not null)
            {
                scalar = firstRow[scalarColumn];
            }
            else
            {
                scalar = firstRow.FirstOrDefault().Value;
            }
        }

        sample = new DatabaseAssertionSample(
            rows.Count,
            ReadScalar(scalar),
            ReadCapturedAtUtc(rawResult));
        return true;
    }

    private static string? ReadScalar(JsonNode? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value is JsonValue scalar)
        {
            if (scalar.TryGetValue<string>(out var text))
            {
                return text;
            }

            if (scalar.TryGetValue<long>(out var integer))
            {
                return integer.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            if (scalar.TryGetValue<double>(out var number))
            {
                return number.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            if (scalar.TryGetValue<bool>(out var boolean))
            {
                return boolean.ToString().ToLowerInvariant();
            }
        }

        return value.ToJsonString();
    }

    private static DateTimeOffset ReadCapturedAtUtc(JsonObject result)
        => DateTimeOffset.TryParse(LiveUiNodeQuery.ReadString(result, "capturedAtUtc"), out var capturedAtUtc)
            ? capturedAtUtc.ToUniversalTime()
            : DateTimeOffset.UtcNow;

    private static string? ReadOptionalScalar(JsonObject? arguments, string propertyName)
        => arguments?[propertyName] is null
            ? null
            : arguments[propertyName] is JsonValue value && value.TryGetValue<string>(out var text)
                ? text
                : arguments[propertyName]?.ToJsonString();

    private static int? ReadNullableInteger(JsonObject? arguments, string propertyName)
        => arguments?[propertyName] is JsonValue value && value.TryGetValue<int>(out var result)
            ? result
            : null;

    private sealed record DatabaseAssertionSample(
        int RowCount,
        string? Scalar,
        DateTimeOffset CapturedAtUtc);
}
