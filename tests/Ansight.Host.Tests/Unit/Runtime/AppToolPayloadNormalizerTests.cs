using Ansight.Host.Runtime.Operations;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class AppToolPayloadNormalizerTests
{
    [Fact]
    public void ApplyHostToolDefaults_AddsVisualTreeDefaultsForMauiTool()
    {
        var normalized = AppToolPayloadNormalizer.ApplyHostToolDefaults(
            "maui.get_visual_tree",
            null,
            defaultVisualTreeMaxDepth: 32,
            defaultScreenshotQuality: 80,
            defaultScreenshotMaxWidth: 1440,
            out var appliedDefaults);

        Assert.Equal(32, normalized["maxDepth"]?.GetValue<int>());
        Assert.Equal(32, appliedDefaults?["maxDepth"]?.GetValue<int>());
    }

    [Fact]
    public void NormalizeCallToolPayload_WhenMauiVisualTreeIncludesScreenshot_ExternalizesTreeAndScreenshot()
    {
        var snapshot = CreateSnapshot();
        var screenshotPath = CreateStreamedScreenshotArtifact(snapshot);
        var payload = new JsonObject
        {
            ["toolId"] = "maui.get_visual_tree",
            ["success"] = true,
            ["result"] = new JsonObject
            {
                ["platform"] = "ios",
                ["format"] = "ansight.maui.visual-tree.compact.v2",
                ["capturedAtUtc"] = "2026-04-30T00:00:00.0000000Z",
                ["rootScope"] = "rootPage",
                ["types"] = new JsonArray(
                    "Microsoft.Maui.Controls.ContentPage",
                    "Microsoft.Maui.Controls.Button"),
                ["screenshot"] = new JsonObject
                {
                    ["format"] = "png",
                    ["width"] = 10,
                    ["height"] = 8,
                    ["mimeType"] = "image/png",
                    ["artifactPath"] = screenshotPath,
                    ["deliveryMode"] = "websocket_binary",
                    ["status"] = "complete"
                },
                ["root"] = new JsonObject
                {
                    ["id"] = "root-1",
                    ["typeId"] = 0,
                    ["label"] = "Home",
                    ["childCount"] = 1,
                    ["children"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["id"] = "button-1",
                            ["typeId"] = 1,
                            ["label"] = "Save",
                            ["childCount"] = 0
                        }
                    }
                }
            }
        };
        var artifacts = new JsonArray();

        var normalizedPayload = Assert.IsType<JsonObject>(
            AppToolPayloadNormalizer.NormalizeCallToolPayload(
                snapshot,
                "maui.get_visual_tree",
                payload,
                artifacts));
        var result = Assert.IsType<JsonObject>(normalizedPayload["result"]);
        var artifactPath = result["artifactPath"]?.GetValue<string>();

        try
        {
            Assert.Equal("visual_tree", result["artifactKind"]?.GetValue<string>());
            Assert.Equal("maui", result["visualTreeKind"]?.GetValue<string>());
            Assert.Equal("ansight.maui.visual-tree.compact.v2", result["visualTreeFormat"]?.GetValue<string>());
            Assert.Equal("ios", result["runtimePlatform"]?.GetValue<string>());
            Assert.Equal("rootPage", result["rootScope"]?.GetValue<string>());
            Assert.Null(result["root"]);
            Assert.Equal("root-1", (result["rootSummary"] as JsonObject)?["id"]?.GetValue<string>());
            Assert.False(string.IsNullOrWhiteSpace(artifactPath));
            Assert.True(File.Exists(artifactPath));

            var screenshot = Assert.IsType<JsonObject>(result["screenshot"]);
            Assert.Equal("screenshot", screenshot["artifactKind"]?.GetValue<string>());
            Assert.Null(screenshot["base64"]);
            Assert.Equal(screenshotPath, screenshot["artifactPath"]?.GetValue<string>());
            Assert.True(File.Exists(screenshotPath));

            var artifactKinds = artifacts
                .OfType<JsonObject>()
                .Select(artifact => artifact["kind"]?.GetValue<string>())
                .ToArray();
            Assert.Contains("screenshot", artifactKinds);
            Assert.Contains("visual_tree", artifactKinds);

            var visualTreeArtifactJson = File.ReadAllText(artifactPath!);
            Assert.Contains("button-1", visualTreeArtifactJson, StringComparison.Ordinal);
            Assert.DoesNotContain("base64", visualTreeArtifactJson, StringComparison.Ordinal);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(artifactPath))
            {
                var artifactDirectory = Path.GetDirectoryName(artifactPath);
                if (!string.IsNullOrWhiteSpace(artifactDirectory) && Directory.Exists(artifactDirectory))
                {
                    Directory.Delete(artifactDirectory, recursive: true);
                }
            }
        }
    }

    [Fact]
    public void NormalizeCallToolPayload_WhenArtifactRequestIncludesStreamedPayload_ExternalizesArtifact()
    {
        var snapshot = CreateSnapshot();
        var artifactPath = CreateStreamedArtifact(snapshot, "diagnostics.txt");
        var payload = new JsonObject
        {
            ["toolId"] = "artifacts.request",
            ["success"] = true,
            ["result"] = new JsonObject
            {
                ["artifact"] = new JsonObject
                {
                    ["artifactId"] = "diagnostics",
                    ["providerId"] = "debug",
                    ["name"] = "Diagnostics",
                    ["kind"] = "log",
                    ["mimeType"] = "text/plain",
                    ["fileName"] = "diagnostics.txt"
                },
                ["artifactPath"] = artifactPath,
                ["deliveryMode"] = "websocket_binary",
                ["status"] = "complete"
            }
        };
        var artifacts = new JsonArray();

        try
        {
            var normalizedPayload = Assert.IsType<JsonObject>(
                AppToolPayloadNormalizer.NormalizeCallToolPayload(
                    snapshot,
                    "artifacts.request",
                    payload,
                    artifacts));
            var result = Assert.IsType<JsonObject>(normalizedPayload["result"]);

            Assert.Equal("log", result["artifactKind"]?.GetValue<string>());
            Assert.Equal("text/plain", result["mimeType"]?.GetValue<string>());
            var artifact = Assert.Single(artifacts.OfType<JsonObject>());
            Assert.Equal("log", artifact["kind"]?.GetValue<string>());
            Assert.Equal(artifactPath, artifact["path"]?.GetValue<string>());
            Assert.Equal("text/plain", artifact["mimeType"]?.GetValue<string>());
            Assert.Equal(4, artifact["sizeBytes"]?.GetValue<long>());
        }
        finally
        {
            var artifactDirectory = Path.GetDirectoryName(artifactPath);
            if (!string.IsNullOrWhiteSpace(artifactDirectory) && Directory.Exists(artifactDirectory))
            {
                Directory.Delete(artifactDirectory, recursive: true);
            }
        }
    }

    private static string CreateStreamedScreenshotArtifact(AppSessionSnapshot snapshot)
    {
        return CreateStreamedArtifact(snapshot, $"streamed-screenshot-{Guid.NewGuid():N}.png");
    }

    private static string CreateStreamedArtifact(AppSessionSnapshot snapshot, string fileName)
    {
        var artifactDirectory = Path.Combine(
            Path.GetTempPath(),
            "AnsightHost",
            "tool-artifacts",
            snapshot.AppId,
            snapshot.SessionId);
        Directory.CreateDirectory(artifactDirectory);

        var artifactPath = Path.Combine(artifactDirectory, fileName);
        File.WriteAllBytes(artifactPath, [1, 2, 3, 4]);
        return artifactPath;
    }

    private static AppSessionSnapshot CreateSnapshot()
    {
        var id = Guid.NewGuid().ToString("N");
        return new AppSessionSnapshot
        {
            SessionId = $"session-{id}",
            AppId = $"com.example.{id}",
            ClientName = "Payload Test App",
            RemoteAddress = "127.0.0.1",
            CreatedUtc = DateTimeOffset.UtcNow,
            ConfigId = null,
            Status = "Connected",
            LastUpdatedUtc = DateTimeOffset.UtcNow,
            IsHistorical = false,
            Logs = Array.Empty<LogEntry>(),
            MetricChannels = Array.Empty<SessionMetricChannel>(),
            Metrics = Array.Empty<SessionMetricSample>()
        };
    }
}
