using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Operations.Tools.Apps;

internal abstract class AppOperation : Operation
{
    protected AppOperation(OperationServices services)
        : base(services)
    {
    }

    protected JsonObject BuildListAppsPayload()
    {
        var apps = hostAppService.List();

        return new JsonObject
        {
            ["knownAppCount"] = apps.Count,
            ["liveSessionCount"] = apps.Sum(static app => app.LiveSessionCount),
            ["apps"] = PayloadJson.CreateJsonArray(
                apps.Select(app => (JsonNode?)BuildAppPayload(app)))
        };
    }

    protected RequestResult BuildGetAppResult(JsonObject? arguments)
    {
        var appId = NormalizeRequiredAppId(arguments);
        if (string.IsNullOrWhiteSpace(appId))
        {
            return ToolError("appId is required.");
        }

        var app = hostAppService.Get(appId);
        if (app is null)
        {
            return RequestResult.ToolResult(
                new JsonObject
                {
                    ["appId"] = appId,
                    ["message"] = $"App '{appId}' was not found."
                },
                isError: true);
        }

        return RequestResult.ToolResult(
            new JsonObject
            {
                ["appId"] = appId,
                ["app"] = BuildAppPayload(app)
            },
            isError: false);
    }

    protected RequestResult BuildRegisterAppResult(JsonObject? arguments)
    {
        var appId = NormalizeRequiredAppId(arguments);
        if (string.IsNullOrWhiteSpace(appId))
        {
            return ToolError("appId is required.");
        }

        var appName = NormalizeOptionalString(arguments?["appName"]?.GetValue<string>()) ?? appId;
        var codebasePath = NormalizeOptionalString(arguments?["codebasePath"]?.GetValue<string>());
        var result = hostAppService.Register(new AppRegistrationRequest(
            appId,
            appName,
            codebasePath));
        return RequestResult.ToolResult(
            new JsonObject
            {
                ["message"] = result.Message,
                ["appId"] = appId,
                ["appName"] = appName,
                ["app"] = result.App is null ? null : BuildAppPayload(result.App)
            },
            isError: !result.IsSuccess);
    }

    private static JsonObject BuildAppPayload(AppDescriptor app)
        => new()
        {
            ["appId"] = app.AppId,
            ["name"] = app.Name,
            ["codebasePath"] = app.CodebasePath,
            ["automaticTrendsMonitoringEnabled"] = app.AutomaticTrendsMonitoringEnabled,
            ["iconImagePath"] = app.IconImagePath,
            ["firstSeenUtc"] = app.FirstSeenUtc,
            ["lastSeenUtc"] = app.LastSeenUtc,
            ["sessionCount"] = app.SessionCount,
            ["liveSessionCount"] = app.LiveSessionCount,
            ["analysisCount"] = app.AnalysisCount,
            ["configCount"] = app.EnrollmentInviteCount
        };
}
