using System.Text.Json.Nodes;
using Ansight.Host.Runtime.Automation;
using Ansight.Host.Tests.Unit.Runtime;

namespace Ansight.Host.Tests.Unit.Sanitization;

public sealed class JavaScriptSessionSanitizerExecutorTests
{
    [Fact]
    public void Constructor_RejectsLegacyJavaScriptModuleExtensions()
    {
        using var directory = new TemporaryDirectory();
        var modulePath = Path.Combine(directory.RootPath, "sanitizer.mjs");
        File.WriteAllText(modulePath, "export function sanitizeLog(log) { return log; }");

        var exception = Assert.Throws<InvalidDataException>(() =>
            new JavaScriptSessionSanitizerExecutor(modulePath, "node"));

        Assert.Equal("Sanitizer modules must use .ts.", exception.Message);
    }

    [Fact]
    public void Invoke_DispatchesNamedHandlerAndProvidesPiiTools()
    {
        var runtime = JavaScriptRuntimeResolver.Resolve("node");
        if (!runtime.IsAvailable)
        {
            return;
        }

        using var directory = new TemporaryDirectory();
        var modulePath = Path.Combine(directory.RootPath, "sanitizer.ts");
        File.WriteAllText(
            modulePath,
            """
            export function sanitizeLog(log, { pii }) {
              return { ...log, message: pii.redact(log.message) };
            }
            """);
        using var executor = new JavaScriptSessionSanitizerExecutor(modulePath, runtime.ExecutablePath);

        var result = executor.Invoke(
            "sanitizeLog",
            new JsonObject
            {
                ["eventId"] = "event-1",
                ["message"] = "Email person@example.com or call +61 412 345 678"
            });

        Assert.False(result.WasRemoved);
        Assert.Equal("event-1", result.Value?["eventId"]?.GetValue<string>());
        Assert.Equal(
            "Email [REDACTED] or call [REDACTED]",
            result.Value?["message"]?.GetValue<string>());
        Assert.Equal(2, result.RedactionCount);
    }

    [Fact]
    public void Invoke_MissingScreenshotHandlerRedactsAll()
    {
        var runtime = JavaScriptRuntimeResolver.Resolve("node");
        if (!runtime.IsAvailable)
        {
            return;
        }

        using var directory = new TemporaryDirectory();
        var modulePath = Path.Combine(directory.RootPath, "sanitizer.ts");
        File.WriteAllText(modulePath, "export function sanitizeLog(log) { return log; }");
        using var executor = new JavaScriptSessionSanitizerExecutor(modulePath, runtime.ExecutablePath);

        var result = executor.Invoke(
            "sanitizeScreenshot",
            new JsonObject { ["frameId"] = "frame-1" });

        Assert.Equal(
            "redactAll",
            result.Value?["sanitization"]?["action"]?.GetValue<string>());
    }

    [Fact]
    public void Invoke_ProvidesShareOperationContext()
    {
        var runtime = JavaScriptRuntimeResolver.Resolve("node");
        if (!runtime.IsAvailable)
        {
            return;
        }

        using var directory = new TemporaryDirectory();
        var modulePath = Path.Combine(directory.RootPath, "sanitizer.ts");
        File.WriteAllText(
            modulePath,
            """
            export function sanitizeSession(session, { operation }) {
              return {
                ...session,
                audience: operation.share?.audience ?? null,
                teamId: operation.share?.teamId ?? null
              };
            }
            """);
        using var executor = new JavaScriptSessionSanitizerExecutor(modulePath, runtime.ExecutablePath);

        var result = executor.Invoke(
            "sanitizeSession",
            new JsonObject { ["sessionId"] = "session-1" },
            new JsonObject
            {
                ["operation"] = new JsonObject
                {
                    ["kind"] = "share",
                    ["share"] = new JsonObject
                    {
                        ["audience"] = "public_no_auth",
                        ["teamId"] = "11111111-1111-4111-8111-111111111111"
                    }
                }
            });

        Assert.Equal("public_no_auth", result.Value?["audience"]?.GetValue<string>());
        Assert.Equal(
            "11111111-1111-4111-8111-111111111111",
            result.Value?["teamId"]?.GetValue<string>());
    }

    [Fact]
    public void Invoke_HandlerFailureAbortsSanitization()
    {
        var runtime = JavaScriptRuntimeResolver.Resolve("node");
        if (!runtime.IsAvailable)
        {
            return;
        }

        using var directory = new TemporaryDirectory();
        var modulePath = Path.Combine(directory.RootPath, "sanitizer.ts");
        File.WriteAllText(
            modulePath,
            "export function sanitizeLog() { throw new Error('do not export'); }");
        using var executor = new JavaScriptSessionSanitizerExecutor(modulePath, runtime.ExecutablePath);

        var exception = Assert.Throws<InvalidDataException>(() => executor.Invoke(
            "sanitizeLog",
            new JsonObject()));

        Assert.Contains("do not export", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Invoke_OversizedResponseReportsActualAndAllowedSizes()
    {
        var runtime = JavaScriptRuntimeResolver.Resolve("node");
        if (!runtime.IsAvailable)
        {
            return;
        }

        using var directory = new TemporaryDirectory();
        var modulePath = Path.Combine(directory.RootPath, "sanitizer.ts");
        File.WriteAllText(
            modulePath,
            "export function sanitizeLog(log) { return { ...log, value: 'x'.repeat(4 * 1024 * 1024) }; }");
        using var executor = new JavaScriptSessionSanitizerExecutor(modulePath, runtime.ExecutablePath);

        var exception = Assert.Throws<InvalidDataException>(() => executor.Invoke(
            "sanitizeLog",
            new JsonObject { ["message"] = "small input" }));

        Assert.Contains("returned 4,194,", exception.Message, StringComparison.Ordinal);
        Assert.Contains("from a", exception.Message, StringComparison.Ordinal);
        Assert.Contains("4,194,304-character per-item limit", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Return a smaller object or null", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseTsv_ReturnsTextAndPixelBounds()
    {
        var blocks = TesseractSessionScreenshotOcrScanner.ParseTsv(
            "level\tpage_num\tblock_num\tpar_num\tline_num\tword_num\tleft\ttop\twidth\theight\tconf\ttext\n"
            + "5\t1\t1\t1\t1\t1\t12\t24\t80\t18\t96.5\tperson@example.com\n");

        var block = Assert.Single(blocks);
        Assert.Equal("person@example.com", block.Text);
        Assert.Equal(96.5, block.Confidence);
        Assert.Equal(new SessionSanitizationRegion(12, 24, 80, 18), block.Bounds);
    }

    [Fact]
    public void ParseTsv_PreservesMultiWordLineAndIndividualUiLabels()
    {
        var blocks = TesseractSessionScreenshotOcrScanner.ParseTsv(
            "level\tpage_num\tblock_num\tpar_num\tline_num\tword_num\tleft\ttop\twidth\theight\tconf\ttext\n"
            + "5\t1\t1\t1\t1\t1\t10\t20\t30\t12\t95\tMap\n"
            + "5\t1\t1\t1\t1\t2\t60\t20\t40\t12\t96\tCrags\n");

        Assert.Collection(
            blocks,
            block => Assert.Equal("Map Crags", block.Text),
            block => Assert.Equal("Map", block.Text),
            block => Assert.Equal("Crags", block.Text));
    }
}
