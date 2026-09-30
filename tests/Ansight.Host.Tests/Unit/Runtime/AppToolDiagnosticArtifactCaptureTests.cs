using Ansight.Host.Runtime.Operations;
using Ansight.Host.Tests.TestSupport;
using Ansight.Tools;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class AppToolDiagnosticArtifactCaptureTests
{
    [Fact]
    public void ListOpenFileDescriptors_CapturesFullJsonAsTimelineArtifact()
    {
        using var environment = new TestEnvironment();
        var runtimeState = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        var sessionId = runtimeState.CreateSession(
            "com.example.app",
            "Example App",
            IPAddress.Loopback,
            configId: null,
            processSessionId: null);
        var response = CreateToolResult(
            "file_descriptors.list_open",
            new JsonObject
            {
                ["capturedAtUtc"] = "2026-08-20T01:02:03.0000000Z",
                ["count"] = 2,
                ["matchedCount"] = 2,
                ["returnedCount"] = 2,
                ["descriptors"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["descriptor"] = 4,
                        ["kind"] = "file",
                        ["target"] = "/data/user/0/com.example.app/cache/example.db"
                    },
                    new JsonObject
                    {
                        ["descriptor"] = 7,
                        ["kind"] = "socket",
                        ["target"] = "socket:[12345]"
                    }
                },
                ["truncated"] = false
            });

        var capture = AppToolDiagnosticArtifactCapture.CaptureIfSupported(
            runtimeState,
            sessionId,
            "file_descriptors.list_open",
            response);

        Assert.True(capture.IsSupported);
        Assert.True(capture.IsCaptured, capture.Message);
        Assert.NotNull(capture.SnapshotId);
        Assert.True(runtimeState.TryGetSessionSnapshot(sessionId, out var session));
        var artifact = Assert.Single(session!.ArtifactSnapshots);
        Assert.Equal(capture.SnapshotId, artifact.SnapshotId);
        Assert.Equal("ansight.app-tool.diagnostic", artifact.Source);
        Assert.Equal("file-descriptor-snapshot", artifact.Kind);
        Assert.Equal(DateTimeOffset.Parse("2026-08-20T01:02:03Z"), artifact.CapturedAtUtc);

        var entry = Assert.Single(artifact.Entries);
        Assert.Equal("open-file-descriptors.json", entry.Name);
        Assert.Equal("application/json", entry.MimeType);
        Assert.True(SessionFileLocator.TryResolveArtifactEntryPath(
            environment.ApplicationPaths,
            session,
            artifact,
            entry,
            out var artifactFilePath));
        Assert.True(File.Exists(artifactFilePath));

        using var artifactDocument = JsonDocument.Parse(File.ReadAllText(artifactFilePath));
        var root = artifactDocument.RootElement;
        Assert.Equal("file_descriptors.list_open", root.GetProperty("toolId").GetString());
        Assert.Equal(2, root.GetProperty("payload").GetProperty("result").GetProperty("count").GetInt32());
        Assert.Equal(
            "/data/user/0/com.example.app/cache/example.db",
            root.GetProperty("payload").GetProperty("result").GetProperty("descriptors")[0].GetProperty("target").GetString());

        var timelineEvent = Assert.Single(
            SessionTimelineBuilder.BuildEvents(session, startUtc: null, endUtc: null),
            timelineEvent => timelineEvent.Payload["category"]?.GetValue<string>() == "artifactSnapshot");
        Assert.Equal("file-descriptor-snapshot", timelineEvent.Payload["kind"]?.GetValue<string>());
        Assert.Equal(capture.SnapshotId, timelineEvent.Payload["details"]?["snapshotId"]?.GetValue<string>());
    }

    [Fact]
    public void CaptureJniReferenceGraph_CapturesFullJsonAsTimelineArtifact()
    {
        using var environment = new TestEnvironment();
        var runtimeState = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        var sessionId = runtimeState.CreateSession(
            "com.example.app",
            "Example App",
            IPAddress.Loopback,
            configId: null,
            processSessionId: null);
        var response = CreateToolResult(
            "jni_references.capture_graph",
            new JsonObject
            {
                ["schemaVersion"] = "ansight.jni-reference-graph.v1",
                ["capturedAtUtc"] = "2026-08-20T02:03:04.0000000Z",
                ["jniRootCount"] = 1,
                ["nodes"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["id"] = "object-1",
                        ["className"] = "java.lang.Object"
                    }
                },
                ["edges"] = new JsonArray(),
                ["truncated"] = false
            });

        var capture = AppToolDiagnosticArtifactCapture.CaptureIfSupported(
            runtimeState,
            sessionId,
            "jni_references.capture_graph",
            response);

        Assert.True(capture.IsSupported);
        Assert.True(capture.IsCaptured, capture.Message);
        Assert.True(runtimeState.TryGetSessionSnapshot(sessionId, out var session));
        var artifact = Assert.Single(session!.ArtifactSnapshots);
        Assert.Equal("jni-reference-graph", artifact.Kind);
        Assert.Equal("jni-reference-graph.json", Assert.Single(artifact.Entries).Name);

        Assert.True(SessionFileLocator.TryResolveArtifactEntryPath(
            environment.ApplicationPaths,
            session,
            artifact,
            artifact.Entries[0],
            out var artifactFilePath));
        using var artifactDocument = JsonDocument.Parse(File.ReadAllText(artifactFilePath));
        Assert.Equal(
            "ansight.jni-reference-graph.v1",
            artifactDocument.RootElement
                .GetProperty("payload")
                .GetProperty("result")
                .GetProperty("schemaVersion")
                .GetString());
    }

    [Theory]
    [InlineData("file_descriptors.count_open")]
    [InlineData("file_descriptors.get_usage")]
    public void CountAndUsageResults_RemainCompactAndDoNotCreateArtifacts(string toolId)
    {
        using var environment = new TestEnvironment();
        var runtimeState = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        var sessionId = runtimeState.CreateSession(
            "com.example.app",
            "Example App",
            IPAddress.Loopback,
            configId: null,
            processSessionId: null);
        var response = CreateToolResult(toolId, new JsonObject { ["count"] = 12 });

        var capture = AppToolDiagnosticArtifactCapture.CaptureIfSupported(
            runtimeState,
            sessionId,
            toolId,
            response);

        Assert.False(capture.IsSupported);
        Assert.False(capture.IsCaptured);
        Assert.True(runtimeState.TryGetSessionSnapshot(sessionId, out var session));
        Assert.Empty(session!.ArtifactSnapshots);
    }

    private static ToolProtocolEnvelope CreateToolResult(string toolId, JsonObject result)
    {
        return new ToolProtocolEnvelope
        {
            Type = ToolProtocolMessageTypes.ResultType,
            Id = "response-1",
            ReplyTo = "request-1",
            SessionId = "session-1",
            Payload = new JsonObject
            {
                ["toolId"] = toolId,
                ["success"] = true,
                ["result"] = result
            }
        };
    }
}
