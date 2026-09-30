using System.Text.Json.Nodes;
using Ansight.Host.Runtime.Operations;
using Ansight.Host.Runtime.RepositoryContracts;
using Ansight.Host.Runtime.Tasks;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed partial class RepositoryTaskTests
{
    [Fact]
    public async Task ClipboardSuiteRoutesToBoundSessionAndRedactsOnlyDiagnosticPayloads()
    {
        using var repository = CreateRepository(CreatePassingModule());
        var task = Assert.Single(LoadRepository(repository.RootPath).Tasks);
        File.WriteAllText(task.ModulePath, """
            export default async function ({ app, expect }) {
              await app.clipboard.setText({ text: "  clipboard-value\n世界", sessionId: "wrong-session" });
              const read = await app.clipboard.getText();
              expect(read.payload.result.text, { id: "exact-text" }).toBe("  clipboard-value\n世界");
              const present = await app.clipboard.hasText();
              expect(present.payload.result.hasText, { id: "present" }).toBe(true);
              await app.clipboard.clear();
            }
            """);
        var names = new List<string>();
        string? clipboard = null;
        var executor = CreateExecutor((toolName, arguments, _) =>
        {
            Assert.Equal("ansight_call_app_tool", toolName);
            Assert.Equal("session-clipboard", arguments["sessionId"]?.GetValue<string>());
            Assert.Null(arguments["arguments"]?["sessionId"]);
            var id = arguments["toolId"]!.GetValue<string>();
            names.Add(id);
            JsonObject payload;
            switch (id)
            {
                case "clipboard.set_text":
                    clipboard = arguments["arguments"]?["text"]?.GetValue<string>();
                    payload = new JsonObject { ["updated"] = true };
                    break;
                case "clipboard.get_text": payload = new JsonObject { ["text"] = clipboard, ["hasText"] = clipboard is not null }; break;
                case "clipboard.has_text": payload = new JsonObject { ["hasText"] = clipboard is not null }; break;
                default: clipboard = null; payload = new JsonObject { ["cleared"] = true }; break;
            }
            return Task.FromResult(RequestResult.ToolResult(new JsonObject
            {
                ["responseType"] = "tool.result",
                ["toolId"] = id,
                ["payload"] = new JsonObject { ["success"] = true, ["toolId"] = id, ["result"] = payload }
            }, isError: false));
        });
        var result = await executor.ExecuteAsync(new RepositoryTaskExecutionRequest(
            "clipboard-run", task, "session-clipboard", new JsonObject(), "clipboard") { CaptureTrace = true }, CancellationToken.None);
        Assert.Equal(RepositoryTaskRunStatus.Passed, result.Status);
        Assert.Equal(new[] { "clipboard.set_text", "clipboard.get_text", "clipboard.has_text", "clipboard.clear" }, names);
        Assert.Null(clipboard);
        Assert.DoesNotContain("clipboard-value", result.ToolCalls[0].Arguments!.Content);
        Assert.DoesNotContain("clipboard-value", result.ToolCalls[1].Result!.Content);
        Assert.Contains("redacted", result.ToolCalls[0].Arguments!.Content);
        Assert.Contains("redacted", result.ToolCalls[1].Result!.Content);
    }

    [Fact]
    public void ClipboardDiagnosticCapturePreservesMalformedAndErrorPayloads()
    {
        var arguments = new JsonObject { ["toolId"] = 123 };
        Assert.Contains("123", RepositoryTaskCallTrace.CaptureArguments("ansight_call_app_tool", arguments).Content);
        Assert.Equal("\"error\"", RepositoryTaskCallTrace.CaptureResult(JsonValue.Create("error"), "clipboard.get_text").Content);
        var result = new JsonObject { ["payload"] = "error" };
        Assert.Contains("error", RepositoryTaskCallTrace.CaptureResult(result, "clipboard.get_text").Content);
    }

    [Fact]
    public void ClipboardContractsExposeTypedNativeToolSuite()
    {
        var suite = RepositoryJavaScriptApiMethods.StandardAppToolSuites["clipboard"];
        Assert.Equal("clipboard.get_text", suite["getText"]);
        Assert.Equal("clipboard.has_text", suite["hasText"]);
        Assert.Equal("clipboard.set_text", suite["setText"]);
        Assert.Equal("clipboard.clear", suite["clear"]);
        var types = RepositoryModuleContractArtifacts.GetTaskTypeDefinitions();
        Assert.Contains("readonly clipboard: TaskClipboardContext", types);
        Assert.Contains("Promise<AppToolCallResult<ClipboardTextResult>>", types);
    }
}
