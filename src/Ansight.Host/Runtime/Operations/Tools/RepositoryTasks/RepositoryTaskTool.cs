using System.Text.Json.Nodes;
using Ansight.Host.Runtime.Tasks;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.RepositoryTasks;

internal abstract class RepositoryTaskTool : Operation
{
    protected RepositoryTaskTool(OperationServices services)
        : base(services)
    {
    }

    protected bool TryResolveRepository(
        JsonObject? arguments,
        bool requireLiveSession,
        out AppSessionSnapshot? session,
        out KnownAppDefinition? app,
        out RepositoryTaskLoadResult? loadResult,
        out string errorMessage)
    {
        session = null;
        app = null;
        loadResult = null;
        errorMessage = string.Empty;

        var sessionId = NormalizeOptionalString(arguments?["sessionId"]?.GetValue<string>());
        var appId = NormalizeOptionalString(arguments?["appId"]?.GetValue<string>());

        if (requireLiveSession)
        {
            if (!sessionResolver.TryResolveLiveSession(arguments, out session, out errorMessage))
            {
                return false;
            }

            appId = session!.AppId;
        }
        else if (sessionId is not null)
        {
            if (!runtimeState.TryGetSessionSnapshot(sessionId, out session) || session is null)
            {
                errorMessage = $"No Ansight session named '{sessionId}' exists.";

                return false;
            }

            if (appId is not null && !string.Equals(appId, session.AppId, StringComparison.Ordinal))
            {
                errorMessage = $"Session '{sessionId}' belongs to app '{session.AppId}', not '{appId}'.";

                return false;
            }

            appId = session.AppId;
        }

        if (appId is null)
        {
            errorMessage = "Provide appId or sessionId to resolve repository tasks.";

            return false;
        }

        if (!knownAppStore.TryGet(appId, out app) || app is null)
        {
            errorMessage = $"App '{appId}' is not registered in Ansight.";

            return false;
        }

        if (!app.RepositoryAutomationsEnabled)
        {
            errorMessage =
                        $"Repository automations are not connected for app '{appId}'. Register its trusted repository with ansight app register <app-id> --codebase <path> first.";

            return false;
        }

        if (string.IsNullOrWhiteSpace(app.CodebasePath))
        {
            errorMessage = $"App '{appId}' does not have a linked repository. Register it with ansight app register <app-id> --codebase <path>.";

            return false;
        }

        loadResult = repositoryTaskRouter.Load(RepositoryTaskWorkspaceScope.Resolve(appId, app.CodebasePath), appId);

        return true;
    }
}
