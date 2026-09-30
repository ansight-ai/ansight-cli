using Ansight.Infrastructure;
using System.Text.Json;

namespace Ansight.Host.Tests.Unit.SimulatorAgent;

public sealed class SimulatorAgentAuditStoreTests
{
    [Fact]
    public void Save_RecordsChargedCostAndReasoningWithoutModelOrProviderCost()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "ansight-trace-cost-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new AuditStore(new DataToolApplicationPaths(rootPath));
            var audit = CreateAudit("cost-trace", DateTimeOffset.UtcNow) with
            {
                Reasoning = "balanced",
                CalculatedCost = new SimulatorAgentRunCost(1498, 1873, "USD", "calculated"),
                WarmupResponses = [new SimulatorAgentModelPassUsage("warmup-1", "warmup-model", null,
                    DateTimeOffset.UtcNow, new SimulatorAgentTokenUsage(0, 0, 0, 0, 0, 0))]
            };

            var saved = store.Save(audit);

            Assert.Null(saved.ErrorMessage);
            var json = File.ReadAllText(saved.FilePath!);
            using var document = JsonDocument.Parse(json);
            var trace = document.RootElement;
            Assert.Equal("balanced", trace.GetProperty("reasoning").GetString());
            Assert.Equal(1873, trace.GetProperty("calculatedCost").GetProperty("costMicros").GetInt64());
            Assert.DoesNotContain("providerCostMicros", json);
            Assert.DoesNotContain("customerCostMicros", json);
            Assert.DoesNotContain("\"model\"", json);
            Assert.DoesNotContain("responseModel", json);
            Assert.DoesNotContain("test-model", json);
            Assert.DoesNotContain("warmup-model", json);
            var reloaded = Assert.Single(store.List()).Audit;
            Assert.Equal(1873, reloaded.CalculatedCost!.CostMicros);
            Assert.Equal(audit.CreateModelPassUsages(), reloaded.CreateModelPassUsages());
            Assert.DoesNotContain("test-model", JsonSerializer.Serialize(reloaded));
        }
        finally
        {
            if (Directory.Exists(rootPath)) Directory.Delete(rootPath, recursive: true);
        }
    }

    [Theory]
    [InlineData("{\"providerCostMicros\":1498,\"customerCostMicros\":1873,\"currency\":\"USD\",\"status\":\"calculated\"}", 1873L)]
    [InlineData("{\"providerCostMicros\":1498,\"currency\":\"USD\",\"status\":\"calculated\"}", null)]
    [InlineData("{\"costMicros\":0,\"currency\":\"USD\",\"status\":\"calculated\"}", 0L)]
    public void Cost_ReadsLegacyAmountsWithoutExposingProviderCosts(string json, long? expected)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var cost = JsonSerializer.Deserialize<SimulatorAgentRunCost>(json, options)!;
        Assert.Equal(expected, cost.CostMicros);
        var serialized = JsonSerializer.Serialize(cost, options);
        Assert.DoesNotContain("providerCostMicros", serialized);
        Assert.DoesNotContain("customerCostMicros", serialized);
        Assert.Equal(expected, JsonSerializer.Deserialize<SimulatorAgentRunCost>(serialized, options)!.CostMicros);
    }

    [Fact]
    public void Save_TraceEnabledCopiesOcrScreenshotAndResultsBesideAudit()
    {
        var rootPath = Path.Combine(
            Path.GetTempPath(),
            "ansight-simulator-agent-store-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootPath);
        try
        {
            var screenshotPath = Path.Combine(rootPath, "ocr-source.png");
            var screenshotBytes = new byte[] { 1, 2, 3, 4, 5 };
            File.WriteAllBytes(screenshotPath, screenshotBytes);
            var startedUtc = DateTimeOffset.Parse("2026-08-27T03:00:00Z");
            var resultsJson = "{\"provider\":\"tesseract\",\"detectionCount\":1,\"detections\":[{\"text\":\"Account\"}]}";
            var payload = new SimulatorAgentAuditPayload(
                resultsJson,
                resultsJson.Length,
                false,
                "ocr-results-hash");
            var toolCall = new SimulatorAgentToolCallAudit(
                1,
                1,
                1,
                "call-1",
                "ansight_scan_screen",
                true,
                "correlation-1",
                startedUtc,
                25,
                payload,
                payload,
                false,
                "Scanned.")
            {
                OcrEvidence = new SimulatorAgentOcrTraceEvidence(
                    "tesseract",
                    true,
                    null,
                    startedUtc,
                    "frame-1",
                    "screenshot-hash",
                    "png",
                    390,
                    844,
                    1,
                    payload)
                {
                    SourceScreenshotPath = screenshotPath
                }
            };
            var audit = CreateAudit("ocr-trace", startedUtc) with
            {
                TraceEnabled = true,
                ToolCalls = [toolCall]
            };
            var store = new AuditStore(new DataToolApplicationPaths(rootPath));

            var save = store.Save(audit);

            Assert.Null(save.ErrorMessage);
            Assert.NotNull(save.FilePath);
            var saved = Assert.Single(store.List()).Audit;
            var evidence = Assert.Single(saved.ToolCalls).OcrEvidence;
            Assert.NotNull(evidence);
            Assert.NotNull(evidence.ScreenshotPath);
            Assert.NotNull(evidence.ResultsPath);
            var auditDirectory = Path.GetDirectoryName(save.FilePath)!;
            Assert.Equal(
                screenshotBytes,
                File.ReadAllBytes(Path.Combine(auditDirectory, evidence.ScreenshotPath!)));
            Assert.Equal(
                resultsJson,
                File.ReadAllText(Path.Combine(auditDirectory, evidence.ResultsPath!)));
            Assert.DoesNotContain(screenshotPath, File.ReadAllText(save.FilePath!), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public void List_ReturnsNewestFirstAndSkipsCorruptAudits()
    {
        var rootPath = Path.Combine(
            Path.GetTempPath(),
            "ansight-simulator-agent-store-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootPath);
        try
        {
            var store = new AuditStore(new DataToolApplicationPaths(rootPath));
            var older = CreateAudit("older", DateTimeOffset.Parse("2026-08-15T01:00:00Z"));
            var newer = CreateAudit("newer", DateTimeOffset.Parse("2026-08-15T02:00:00Z")) with
            {
                AppId = "com.example.app",
                AgentPrompt = "Use the recorded run prompt.",
                PromptCacheKey = "test-prompt-v1",
                MaximumModelOutputTokens = 2_400,
                Environment = new SimulatorAgentRunEnvironment(
                    "com.example.app",
                    "Example App",
                    true,
                    "foreground",
                    new SimulatorAgentRunDevice(
                        "simulator-123",
                        "Apple",
                        "iPhone 16 Pro",
                        "phone",
                        "iOS",
                        "18.6",
                        IsVirtual: true,
                        IsEmulator: true))
            };

            Assert.Null(store.Save(older).ErrorMessage);
            Assert.Null(store.Save(newer).ErrorMessage);
            var auditDirectory = Path.Combine(rootPath, "data", "simulator-agent-runs");
            File.WriteAllText(Path.Combine(auditDirectory, "corrupt.json"), "{not-json");

            var history = store.List();

            Assert.Equal(["newer", "older"], history.Select(static entry => entry.Audit.RunId));
            Assert.Equal("com.example.app", history[0].Audit.AppId);
            Assert.Equal("Use the recorded run prompt.", history[0].Audit.AgentPrompt);
            Assert.Equal("iPhone 16 Pro", history[0].Audit.Environment?.Device?.Model);
            Assert.Null(history[1].Audit.AppId);
            Assert.Null(history[1].Audit.AgentPrompt);
            Assert.Null(history[1].Audit.Environment);
            Assert.All(history, static entry => Assert.True(File.Exists(entry.FilePath)));
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    private static SimulatorAgentRunAudit CreateAudit(string runId, DateTimeOffset startedUtc)
    {
        return new SimulatorAgentRunAudit(
            1,
            runId,
            "session-001",
            "test-model",
            SimulatorAgentRunStatus.Succeeded,
            "Completed.",
            startedUtc,
            startedUtc.AddSeconds(1),
            1_000,
            12,
            60,
            1,
            1,
            0,
            0,
            1,
            0,
            0,
            0,
            0,
            0,
            new SimulatorAgentTokenUsage(100, 10, 110, 80, 0, 2),
            ["Verify history."],
            [new SimulatorAgentInstructionResult(1, "Verify history.", SimulatorAgentInstructionStatus.Succeeded, "Done.", 1, 0)],
            [new SimulatorAgentModelPassAudit(1, 1, 1, startedUtc, 500, true, "response-1", "test-model", "Done.", 0, new SimulatorAgentTokenUsage(100, 10, 110, 80, 0, 2), null)],
            Array.Empty<SimulatorAgentToolCallAudit>());
    }
}
