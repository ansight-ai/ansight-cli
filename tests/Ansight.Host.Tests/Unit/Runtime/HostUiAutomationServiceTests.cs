using System.Text.Json.Nodes;
using Ansight.Host.Runtime.Operations;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class HostUiAutomationServiceTests
{
    [Theory]
    [InlineData(UiAutomationOperation.Snapshot, "ansight_get_live_visual_tree")]
    [InlineData(UiAutomationOperation.Find, "ansight_find_ui")]
    [InlineData(UiAutomationOperation.Tap, "ansight_tap_ui")]
    [InlineData(UiAutomationOperation.Type, "ansight_type_text")]
    [InlineData(UiAutomationOperation.Swipe, "ansight_swipe_ui")]
    [InlineData(UiAutomationOperation.Pinch, "ansight_pinch_ui")]
    [InlineData(UiAutomationOperation.Back, "ansight_back_ui")]
    [InlineData(UiAutomationOperation.Wait, "ansight_wait_for_ui")]
    [InlineData(UiAutomationOperation.Assert, "ansight_assert_ui")]
    [InlineData(UiAutomationOperation.KeyboardOpen, "ansight_open_keyboard")]
    [InlineData(UiAutomationOperation.KeyboardIsOpen, "ansight_is_keyboard_open")]
    [InlineData(UiAutomationOperation.KeyboardDismiss, "ansight_dismiss_keyboard")]
    public async Task ExecuteAsyncDelegatesToTheExistingHostOperation(
        UiAutomationOperation operation,
        string expectedOperationName)
    {
        string? observedOperationName = null;
        JsonObject? observedArguments = null;
        string? observedCorrelationId = null;
        var service = new UiAutomationService((operationName, arguments, correlationId) =>
        {
            observedOperationName = operationName;
            observedArguments = arguments;
            observedCorrelationId = correlationId;
            return Task.FromResult(RequestResult.ToolResult(
                new JsonObject { ["message"] = "completed" },
                isError: false));
        });
        var suppliedArguments = new JsonObject { ["sessionId"] = "session-001" };

        var result = await service.ExecuteAsync(operation, suppliedArguments);

        Assert.Equal(expectedOperationName, observedOperationName);
        Assert.NotSame(suppliedArguments, observedArguments);
        Assert.Equal("session-001", observedArguments!["sessionId"]!.GetValue<string>());
        Assert.StartsWith("cli-ui-", observedCorrelationId, StringComparison.Ordinal);
        Assert.True(result.IsSuccess);
        Assert.Equal("completed", result.Message);
    }

    [Fact]
    public async Task ExecuteAsyncPropagatesCancellationIntoTheHostOperation()
    {
        var observedToken = new TaskCompletionSource<CancellationToken>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new UiAutomationService(async (_, _, _) =>
        {
            var cancellationToken = ToolExecutionCancellation.Current;
            observedToken.SetResult(cancellationToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return RequestResult.ToolResult(new JsonObject(), isError: false);
        });
        using var cancellation = new CancellationTokenSource();

        var operationTask = service.ExecuteAsync(
            UiAutomationOperation.Wait,
            new JsonObject(),
            cancellationToken: cancellation.Token);
        var hostToken = await observedToken.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        Assert.True(hostToken.CanBeCanceled);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operationTask);
    }

    [Theory]
    [InlineData("full", false, true)]
    [InlineData(null, true, true)]
    [InlineData("compact", true, false)]
    [InlineData(null, false, false)]
    public async Task SnapshotDetailIsPresentationOnlyAndDetailedPropertiesRemainOptIn(
        string? detail, bool includeProperties, bool expectFull)
    {
        var source = SnapshotPayload();
        JsonObject? dispatchedArguments = null;
        var service = new UiAutomationService((_, arguments, _) =>
        {
            dispatchedArguments = arguments;
            return Task.FromResult(RequestResult.ToolResult(source, isError: false));
        });
        var supplied = new JsonObject
        {
            ["sessionId"] = "session-001",
            ["includeProperties"] = includeProperties,
            ["maxNodes"] = 2
        };
        if (detail is not null) supplied["detail"] = detail;

        var result = await service.ExecuteAsync(UiAutomationOperation.Snapshot, supplied);

        Assert.True(result.IsSuccess);
        Assert.False(dispatchedArguments!.ContainsKey("detail"));
        Assert.Equal(includeProperties, dispatchedArguments["includeProperties"]!.GetValue<bool>());
        Assert.Equal(2, dispatchedArguments["maxNodes"]!.GetValue<int>());
        Assert.Equal(detail, supplied["detail"]?.GetValue<string>());
        Assert.NotSame(source, result.Payload);
        if (expectFull)
        {
            Assert.True(JsonNode.DeepEquals(source, result.Payload));
        }
        else
        {
            Assert.Null(result.Payload["payload"]);
            Assert.NotNull(result.Payload["root"]);
            Assert.Equal(2, result.Payload["returnedNodeCount"]!.GetValue<int>());
            Assert.True(result.Payload["truncated"]!.GetValue<bool>());
            Assert.DoesNotContain("frameworkOnlyProperty", result.Payload.ToJsonString(), StringComparison.Ordinal);
        }
        Assert.Contains("frameworkOnlyProperty", source.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompactFindHonorsRequestedLimitAndKeepsFailureEvidence()
    {
        var exactId = new string('a', 400);
        var source = new JsonObject
        {
            ["message"] = "Target is hidden.",
            ["selectorSatisfied"] = false,
            ["totalMatches"] = 3,
            ["failures"] = new JsonArray("Target is hidden."),
            ["matches"] = new JsonArray(Enumerable.Range(0, 3).Select(index => (JsonNode)new JsonObject
            {
                ["id"] = $"node-{index}",
                ["automationId"] = index == 0 ? exactId : $"item-{index}",
                ["text"] = $"Item {index}",
                ["role"] = "button",
                ["visible"] = false,
                ["enabled"] = false,
                ["frameworkOnlyProperty"] = new string('x', 1_000)
            }).ToArray())
        };
        var service = new UiAutomationService((_, _, _) =>
            Task.FromResult(RequestResult.ToolResult(source, isError: true)));

        var result = await service.ExecuteAsync(UiAutomationOperation.Find,
            new JsonObject { ["limit"] = 2 });

        Assert.False(result.IsSuccess);
        Assert.Equal("Target is hidden.", result.Message);
        Assert.False(result.Payload["selectorSatisfied"]!.GetValue<bool>());
        Assert.Equal("Target is hidden.", Assert.Single(result.Payload["failures"]!.AsArray())!.GetValue<string>());
        Assert.Equal(3, result.Payload["totalMatches"]!.GetValue<int>());
        Assert.Equal(2, result.Payload["matches"]!.AsArray().Count);
        Assert.Equal(exactId, result.Payload["matches"]![0]!["automationId"]!.GetValue<string>());
        Assert.False(result.Payload["matches"]![0]!["enabled"]!.GetValue<bool>());
        Assert.True(result.Payload["projectionTruncated"]!.GetValue<bool>());
        Assert.DoesNotContain("frameworkOnlyProperty", result.Payload.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal(3, source["matches"]!.AsArray().Count);
    }

    [Fact]
    public async Task FullQueryRetainsTheOriginalResult()
    {
        var source = new JsonObject
        {
            ["matches"] = new JsonArray(new JsonObject
            {
                ["automationId"] = "Save",
                ["frameworkOnlyProperty"] = "retained",
                ["ancestorPath"] = new JsonArray(new JsonObject { ["id"] = "parent" })
            })
        };
        var service = new UiAutomationService((_, arguments, _) =>
        {
            Assert.False(arguments!.ContainsKey("detail"));
            return Task.FromResult(RequestResult.ToolResult(source, isError: false));
        });

        var result = await service.ExecuteAsync(UiAutomationOperation.Find,
            new JsonObject { ["detail"] = "full" });

        Assert.True(JsonNode.DeepEquals(source, result.Payload));
    }

    private static JsonObject SnapshotPayload() => new()
    {
        ["sessionId"] = "session-001",
        ["appId"] = "test.app",
        ["toolId"] = "ui.get_visual_tree",
        ["payload"] = new JsonObject
        {
            ["result"] = new JsonObject
            {
                ["format"] = "ansight.native.visual-tree.compact.v2",
                ["types"] = new JsonArray("Application", "Button"),
                ["root"] = new JsonObject
                {
                    ["typeId"] = 0,
                    ["id"] = "root",
                    ["bounds"] = new JsonArray(0, 0, 100, 200),
                    ["children"] = new JsonArray(Enumerable.Range(0, 3).Select(index => (JsonNode)new JsonObject
                    {
                        ["typeId"] = 1,
                        ["id"] = $"button-{index}",
                        ["automationId"] = $"Button{index}",
                        ["text"] = $"Button {index}",
                        ["role"] = "button",
                        ["bounds"] = new JsonArray(10, index * 50, 80, 30),
                        ["supportedActions"] = new JsonArray("tap"),
                        ["frameworkOnlyProperty"] = new string('x', 1_000)
                    }).ToArray())
                }
            }
        }
    };
}
