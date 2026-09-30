using System.Text.Json.Nodes;
using Ansight.Host.Runtime.Operations;
using Ansight.Host.Runtime.Operations.Tools.UiAutomation;

namespace Ansight.Host.UiAutomation;

public enum UiAutomationOperation
{
    Snapshot,
    Find,
    Tap,
    Type,
    Swipe,
    Pinch,
    Back,
    Wait,
    Assert,
    KeyboardOpen,
    KeyboardIsOpen,
    KeyboardDismiss
}

public sealed record UiAutomationResult(
    UiAutomationOperation Operation,
    bool IsSuccess,
    string Message,
    JsonObject Payload);

/// <summary>
/// Transport-neutral semantic UI operations shared by the CLI and the bounded host agent.
/// </summary>
public sealed class UiAutomationService
{
    private readonly Func<string, JsonObject?, string?, Task<RequestResult>> executeOperation;

    internal UiAutomationService(
        Func<string, JsonObject?, string?, Task<RequestResult>> executeOperation)
    {
        this.executeOperation = executeOperation
                                ?? throw new ArgumentNullException(nameof(executeOperation));
    }

    public async Task<UiAutomationResult> ExecuteAsync(
        UiAutomationOperation operation,
        JsonObject? arguments,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var operationArguments = arguments?.DeepClone().AsObject();
        var detail = operationArguments?["detail"]?.GetValue<string>()?.Trim().ToLowerInvariant();
        if (detail is not (null or "compact" or "full"))
        {
            throw new ArgumentException("detail must be compact or full.", nameof(arguments));
        }
        operationArguments?.Remove("detail");
        var fullDetail = detail == "full"
                         || detail is null && operationArguments?["includeProperties"]?.GetValue<bool>() == true;
        var projectionOptions = UiProjectionOptions.Cli with
        {
            MaximumNodes = Math.Clamp(operationArguments?["maxNodes"]?.GetValue<int>()
                                     ?? UiProjectionOptions.Cli.MaximumNodes, 1, 2_000),
            MaximumMatches = Math.Clamp(operationArguments?["limit"]?.GetValue<int>()
                                       ?? UiProjectionOptions.Cli.MaximumMatches, 1, 1_000)
        };
        using var cancellationScope = ToolExecutionCancellation.Push(cancellationToken);
        var operationTask = executeOperation(
            ResolveOperationName(operation),
            operationArguments,
            $"cli-ui-{Guid.NewGuid():N}");
        var result = await operationTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (result.IsError)
        {
            return new UiAutomationResult(
                operation,
                false,
                result.ErrorMessage ?? "The host UI operation failed.",
                new JsonObject());
        }

        var toolFailed = result.Payload?["isError"] is JsonValue errorValue
                         && errorValue.TryGetValue<bool>(out var isError)
                         && isError;
        var structuredContent = result.Payload?["structuredContent"] as JsonObject;
        var payload = structuredContent?.DeepClone().AsObject() ?? new JsonObject();
        var message = ReadMessage(payload, toolFailed);
        if (!fullDetail)
        {
            payload = operation == UiAutomationOperation.Snapshot
                ? VisualTreeObservation.Build(payload, projectionOptions)
                  ?? UiResultProjection.Project(payload, projectionOptions)
                : UiResultProjection.Project(payload, projectionOptions);
        }
        return new UiAutomationResult(
            operation,
            !toolFailed,
            message,
            payload);
    }

    private static string ResolveOperationName(UiAutomationOperation operation)
        => operation switch
        {
            UiAutomationOperation.Snapshot => "ansight_get_live_visual_tree",
            UiAutomationOperation.Find => "ansight_find_ui",
            UiAutomationOperation.Tap => "ansight_tap_ui",
            UiAutomationOperation.Type => "ansight_type_text",
            UiAutomationOperation.Swipe => "ansight_swipe_ui",
            UiAutomationOperation.Pinch => "ansight_pinch_ui",
            UiAutomationOperation.Back => "ansight_back_ui",
            UiAutomationOperation.Wait => "ansight_wait_for_ui",
            UiAutomationOperation.Assert => "ansight_assert_ui",
            UiAutomationOperation.KeyboardOpen => "ansight_open_keyboard",
            UiAutomationOperation.KeyboardIsOpen => "ansight_is_keyboard_open",
            UiAutomationOperation.KeyboardDismiss => "ansight_dismiss_keyboard",
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null)
        };

    private static string ReadMessage(JsonObject payload, bool failed)
    {
        if (payload["message"] is JsonValue messageValue
            && messageValue.TryGetValue<string>(out var message)
            && !string.IsNullOrWhiteSpace(message))
        {
            return message.Trim();
        }

        if (failed && payload["failures"] is JsonArray failures && failures.Count > 0)
        {
            return string.Join(
                " ",
                failures.Select(static failure => failure?.GetValue<string>())
                    .Where(static failure => !string.IsNullOrWhiteSpace(failure)));
        }

        return failed
            ? "The host UI operation failed."
            : "The host UI operation completed.";
    }
}
