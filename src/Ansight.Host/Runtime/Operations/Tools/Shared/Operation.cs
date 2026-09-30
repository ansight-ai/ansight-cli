using System.Text.Json.Nodes;
using Ansight.Host.Runtime.Tasks;
using Ansight.Infrastructure;

namespace Ansight.Host.Runtime.Operations.Tools.Shared;

internal abstract class Operation : IOperation
{
    protected readonly IExternalSessionScreenshotCaptureManager? externalScreenshots;
    protected readonly IRuntimeState runtimeState;
    protected readonly IApplicationPaths applicationPaths;
    protected readonly IKnownAppStore knownAppStore;
    protected readonly IPairingConfigService pairingConfigService;
    protected readonly IPairingConfigCache pairingConfigCache;
    protected readonly AppService hostAppService;
    protected readonly PairingService hostPairingService;
    protected readonly IAppToolBridge appToolBridge;
    protected readonly SessionResolver sessionResolver;
    protected readonly UiInputRouter uiInputRouter;
    protected readonly DeviceLocationRouter deviceLocationRouter;
    protected readonly DeviceLifecycleRouter deviceLifecycleRouter;
    protected readonly RepositoryTaskRouter repositoryTaskRouter;

    protected Operation(OperationServices services)
    {
        externalScreenshots = services.ExternalScreenshots;
        runtimeState = services.RuntimeState;
        applicationPaths = services.ApplicationPaths;
        knownAppStore = services.KnownAppStore;
        pairingConfigService = services.PairingConfigService;
        pairingConfigCache = services.PairingConfigCache;
        hostAppService = services.AppService;
        hostPairingService = services.PairingService;
        appToolBridge = services.AppToolBridge;
        sessionResolver = services.SessionResolver;
        uiInputRouter = services.UiInputRouter;
        deviceLocationRouter = services.DeviceLocationRouter;
        deviceLifecycleRouter = services.DeviceLifecycleRouter;
        repositoryTaskRouter = services.RepositoryTaskRouter;
    }

    public abstract string Name { get; }

    protected abstract string Title { get; }

    protected abstract string Description { get; }

    protected abstract JsonObject InputSchema { get; }

    public JsonObject Definition => PayloadJson.CreateToolDefinition(Name, Title, Description, InputSchema);

    public abstract Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId);

    public virtual Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId, OperationExecutionContext? context)
        => ExecuteAsync(arguments, correlationId);

    protected static RequestResult ToolError(string message)
    {
        return RequestResult.ToolResult(
            new JsonObject
            {
                ["message"] = message
            },
            isError: true);
    }

    protected static string NormalizeRequiredAppId(JsonObject? arguments)
    {
        return NormalizeOptionalString(arguments?["appId"]?.GetValue<string>()) ?? string.Empty;
    }

    protected static string? NormalizeOptionalString(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    protected string ResolveAppName(string appId, string? requestedAppName)
    {
        if (!string.IsNullOrWhiteSpace(requestedAppName))
        {
            return requestedAppName;
        }

        if (knownAppStore.TryGet(appId, out var knownApp)
            && !string.IsNullOrWhiteSpace(knownApp?.Name))
        {
            return knownApp.Name;
        }

        return appId;
    }
}
