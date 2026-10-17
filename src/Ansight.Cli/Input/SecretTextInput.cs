using System.Text.Json.Nodes;
using Ansight.Cli.Commands.Ui;
using Ansight.Host;

namespace Ansight.Cli.Input;

internal static class SecretTextInput
{
    internal sealed record Target(string SessionId, string AppId);

    public static string ReadValue(RuntimeCoordinator runtime, string appId, string alias)
    {
        try
        {
            return runtime.SimulatorAgent.ResolveTestSecrets(appId, [alias],
                name => CliCommandContext.Current?.ResolveSecret(name)
                    ?? CliCommandContext.ResolveScopedSecret(name))[alias];
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Storage-provider errors must not echo decrypted content.
            throw new CliUsageException($"Secret '{alias}' is unavailable for app '{appId}'.");
        }
    }

    public static Task<UiAutomationResult> TypeUiAsync(
        RuntimeCoordinator runtime, JsonObject arguments, CancellationToken cancellationToken)
        => TypeUiAsync(arguments, supplied =>
        {
            var session = runtime.ResolveLiveUiSession(supplied);
            return new Target(session.SessionId, session.AppId);
        }, (appId, alias) => ReadValue(runtime, appId, alias), runtime.Ui.ExecuteAsync, cancellationToken);

    internal static async Task<UiAutomationResult> TypeUiAsync(
        JsonObject arguments,
        Func<JsonObject, Target> resolveTarget,
        Func<string, string, string> readValue,
        UiCommands.ExecuteUiOperation execute,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var alias = arguments["secretAlias"]!.GetValue<string>();
        var target = resolveTarget(arguments);
        if (arguments["secretAppId"]?.GetValue<string>() is { } secretAppId
            && !string.Equals(secretAppId, target.AppId, StringComparison.Ordinal))
            throw new CliUsageException("The secret reference's app ID does not match the selected session's app.");
        var value = readValue(target.AppId, alias);
        var input = arguments.DeepClone().AsObject();
        input.Remove("secretAlias");
        input.Remove("secretAppId");
        input.Remove("appId");
        input["sessionId"] = target.SessionId;
        input["value"] = value;
        input["includeScreenshot"] = false;
        bool succeeded;
        try
        {
            var result = await execute(UiAutomationOperation.Type, input, cancellationToken).ConfigureAwait(false);
            succeeded = result.IsSuccess;
        }
        catch (OperationCanceledException)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception)
        {
            succeeded = false;
        }

        // UI targets, backend messages and evidence payloads may contain the value.
        // Return status only, as the hosted type-secret tool does.
        var message = succeeded
            ? $"Secret '{alias}' was typed successfully."
            : $"Could not type secret '{alias}'. Inspect the app before retrying.";
        return new UiAutomationResult(UiAutomationOperation.Type, succeeded, message, new JsonObject
        {
            ["sessionId"] = target.SessionId,
            ["appId"] = target.AppId,
            ["secretAlias"] = alias,
            ["performed"] = succeeded,
            ["message"] = message
        });
    }
}
