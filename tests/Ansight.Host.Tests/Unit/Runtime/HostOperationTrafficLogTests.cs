using System.Text.Json.Nodes;
using System.Text.Json;
using Ansight.Host.Runtime.Operations;
using Ansight.Host.Tests.TestSupport;
using Ansight.Infrastructure;
using Ansight.Infrastructure.Preferences;
using Ansight.Tools;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class HostOperationTrafficLogTests
{
    [Fact]
    public void RecordToolBridgeRequest_DoesNotWriteFileWhenCaptureIsDisabled()
    {
        using var temporaryDirectory = TestDirectory.Create();
        var applicationPaths = new DataToolApplicationPaths(temporaryDirectory.Path);
        var preferences = CreateUserPreferences(applicationPaths);
        using var auditLog = new HostOperationTrafficLog(applicationPaths, preferences);

        auditLog.RecordToolBridgeRequest(
            "session-001",
            "com.example.app",
            "Example App",
            new ToolProtocolEnvelope
            {
                Id = "request-001",
                Type = "tool.call"
            },
            "disabled-marker",
            new AppToolBridgeRequestContext("tests", "call_app_tool", "corr-001"));

        Assert.False(Directory.Exists(auditLog.DirectoryPath));
        Assert.Null(auditLog.CurrentFilePath);
    }

    [Fact]
    public async Task RecordToolBridgeResponse_WritesFullRequestAndResponseBodies()
    {
        using var temporaryDirectory = TestDirectory.Create();
        var applicationPaths = new DataToolApplicationPaths(temporaryDirectory.Path);
        var preferences = CreateUserPreferences(applicationPaths);
        using var auditLog = new HostOperationTrafficLog(applicationPaths, preferences);
        var requestMarker = $"tool-request-{Guid.NewGuid():N}";
        var responseMarker = $"tool-response-{Guid.NewGuid():N}";

        preferences.CaptureFullHostOperationTrafficToDisk = true;
        auditLog.RecordToolBridgeResponse(
            "session-001",
            "com.example.app",
            "Example App",
            new ToolProtocolEnvelope
            {
                Id = "request-001",
                Type = "tool.call",
                Payload = new JsonObject
                {
                    ["toolId"] = "app.get_state"
                }
            },
            new ToolProtocolEnvelope
            {
                Id = "response-001",
                ReplyTo = "request-001",
                Type = ToolProtocolMessageTypes.ResultType
            },
            requestMarker,
            responseMarker,
            42,
            new AppToolBridgeRequestContext("tests", "call_app_tool", "corr-001"));

        await TestWait.UntilAsync(
            () => auditLog.CurrentFilePath is { Length: > 0 } path
                  && File.Exists(path)
                  && ReadAllTextShared(path).Contains(responseMarker, StringComparison.Ordinal),
            because: "The tool bridge response should be appended to the audit file.");

        var filePath = auditLog.CurrentFilePath!;
        var line = Assert.Single(ReadAllLinesShared(filePath));
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        Assert.Equal("tool_bridge_response_received", root.GetProperty("kind").GetString());
        Assert.Equal(requestMarker, root.GetProperty("requestBody").GetString());
        Assert.Equal(responseMarker, root.GetProperty("responseBody").GetString());
        Assert.Equal("app.get_state", root.GetProperty("toolId").GetString());
        Assert.Equal(requestMarker.Length, root.GetProperty("requestBodyChars").GetInt32());
        Assert.Equal(System.Text.Encoding.UTF8.GetByteCount(requestMarker), root.GetProperty("requestBodyBytes").GetInt32());
        Assert.Equal(responseMarker.Length, root.GetProperty("responseBodyChars").GetInt32());
        Assert.Equal(System.Text.Encoding.UTF8.GetByteCount(responseMarker), root.GetProperty("responseBodyBytes").GetInt32());

        preferences.CaptureFullHostOperationTrafficToDisk = false;
        Assert.Null(auditLog.CurrentFilePath);
    }

    private static UserPreferences CreateUserPreferences(IApplicationPaths applicationPaths)
    {
        var preferencesFilePath = Path.Combine(applicationPaths.ApplicationDataPath, "preferences.json");
        return new UserPreferences(new FilePreferencesStore(preferencesFilePath));
    }

    private static string ReadAllTextShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string[] ReadAllLinesShared(string path)
        => ReadAllTextShared(path)
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
}
