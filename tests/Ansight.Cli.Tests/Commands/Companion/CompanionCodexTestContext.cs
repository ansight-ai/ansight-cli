using System.IO.Compression;
using System.Text.Json;
using Ansight.Host.Runtime.Operations.Tools.SessionEvidence;
using Ansight.Infrastructure.Security;
using Ansight.RemoteSimulator.Core.Agent;
using Ansight.RemoteSimulator.Core.Annotations;
using SkiaSharp;

namespace Ansight.Cli.Tests.Commands.Companion;

internal sealed class CompanionCodexTestContext : IAsyncDisposable
{
    private readonly TestDirectory directory;

    private CompanionCodexTestContext(TestDirectory directory, RuntimeCoordinator runtime, AppSessionSnapshot session)
    {
        this.directory = directory;
        Runtime = runtime;
        Session = session;
        Codex = new ScriptedCompanionCodex(directory.Path);
        Source = new CliRemoteAgentChatSource(runtime, Codex);
    }

    public RuntimeCoordinator Runtime { get; }
    public AppSessionSnapshot Session { get; }
    public ScriptedCompanionCodex Codex { get; }
    public CliRemoteAgentChatSource Source { get; }
    public string Path => directory.Path;

    public RemoteAnnotationBatchSubmissionRequest Submission(bool newTask = false)
        => new("batch-1", Session.SessionId, "frame-1", ["annotation-1"], "codex",
            newTask ? "" : "thread-1", ProjectId: "project-1", StartNewTask: newTask);

    public async Task LinkAsync()
    {
        var result = await Source.UpdateProjectAsync(new RemoteAgentProjectUpdateRequest(Session.AppId, "project-1", Path));
        Assert.True(result.IsSuccess, result.Message);
    }

    public static async Task<CompanionCodexTestContext> CreateAsync(string note = "Keep this button visible when the keyboard opens.")
    {
        var directory = TestDirectory.Create();
        var storagePath = System.IO.Path.Combine(directory.Path, "secure-storage.json");
        var keyPath = FileEncryptionKeyProvider.ResolveDefaultKeyFilePath(storagePath);
        FileEncryptionKeyProvider.CreateKeyFile(keyPath);
        var runtime = new RuntimeCoordinator(new RuntimeOptions
        {
            BaseFolderPath = directory.Path,
            SecureStorageFilePath = storagePath,
            SecureStorageKeyFilePath = keyPath
        });
        var timestamp = DateTimeOffset.UtcNow;
        var frame = new SessionImageFrame
        {
            FrameId = "frame-1", CapturedAtUtc = timestamp, Format = "png",
            Width = 200, Height = 300, Quality = 100, ByteCount = 100
        };
        var snapshot = new AppSessionSnapshot
        {
            SessionId = "codex-test-session", AppId = "com.example.companion-codex", ClientName = "Codex companion test",
            RemoteAddress = "127.0.0.1", CreatedUtc = timestamp, ConfigId = null,
            Status = "Complete", LastUpdatedUtc = timestamp, IsHistorical = true,
            MetricChannels = [], Metrics = [], Images = [frame],
            Annotations =
            [
                new SessionAnnotation
                {
                    AnnotationId = "annotation-1", StartUtc = timestamp, Label = "Keep the button visible",
                    Notes = note, CaptureGroupId = "batch-1",
                    Geometry =
                    [
                        new SessionAnnotationGeometry
                        {
                            GeometryId = "geometry-1", FrameId = frame.FrameId, CapturedAtUtc = timestamp,
                            Kind = SessionAnnotationGeometryKind.Rectangle, X = 0.2, Y = 0.4, Width = 0.5, Height = 0.1
                        }
                    ]
                }
            ]
        };
        var archivePath = System.IO.Path.Combine(directory.Path, "session.zip");

        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            await using (var sessionStream = archive.CreateEntry("session.json").Open())
            {
                await JsonSerializer.SerializeAsync(sessionStream, new SessionCaptureDocument
                {
                    SavedAtUtc = timestamp, Session = snapshot
                }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            }

            using var bitmap = new SKBitmap(200, 300);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.White);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            using var imageStream = archive.CreateEntry(SessionImageArtifactPath.ResolveArchiveEntryPath(frame)).Open();
            data.SaveTo(imageStream);
        }

        var imported = await runtime.SessionArchives.ImportSessionArchiveAsync(archivePath);
        Assert.True(imported.IsSuccess, imported.Message);
        var session = Assert.IsType<AppSessionSnapshot>(imported.ImportedSession);
        runtime.Apps.Register(new AppRegistrationRequest(session.AppId, "Companion test", directory.Path));

        return new CompanionCodexTestContext(directory, runtime, session);
    }

    public async ValueTask DisposeAsync()
    {
        await Runtime.DisposeAsync();
        directory.Dispose();
    }
}

internal sealed record CompanionCodexCall(string Method, JsonElement Parameters);

internal sealed class ScriptedCompanionCodex : ICodexRpcConnectionFactory, ICodexRpcConnection
{
    private readonly string root;

    public ScriptedCompanionCodex(string root)
    {
        this.root = root;
    }

    public List<CompanionCodexCall> Calls { get; } = [];
    public string Status { get; set; } = "idle";
    public string? ThreadProjectId { get; set; } = "project-1";
    public string? WorkingDirectory { get; set; }
    public string TurnStatus { get; set; } = "inProgress";
    public Exception? ConnectionError { get; set; }
    public Exception? DeliveryError { get; set; }
    public string? NextProjectCursor { get; set; }

    public Task QueueAsync(string threadId, string prompt, CancellationToken cancellationToken)
    {
        Calls.Add(new CompanionCodexCall("queue", JsonSerializer.SerializeToElement(new { threadId, prompt })));

        return Task.CompletedTask;
    }

    public Task<ICodexRpcConnection> ConnectAsync(CancellationToken cancellationToken)
    {
        if (ConnectionError is not null)
        {
            throw ConnectionError;
        }

        return Task.FromResult<ICodexRpcConnection>(this);
    }

    public Task<JsonElement> CallAsync(string method, object parameters, CancellationToken cancellationToken)
    {
        var value = JsonSerializer.SerializeToElement(parameters);
        Calls.Add(new CompanionCodexCall(method, value));

        if (method is "turn/start" or "turn/steer" && DeliveryError is not null)
        {
            throw DeliveryError;
        }

        var project = new { id = "project-1", name = "My app", roots = new[] { new { path = root } } };
        object result = method switch
        {
            "project/list" => new { data = new[] { project }, nextCursor = NextProjectCursor },
            "project/read" => new { project },
            "thread/list" => new { data = new[] { Thread("thread-1", ThreadProjectId), Thread("foreign-thread", "other-project") } },
            "thread/read" => new { thread = Thread(value.GetProperty("threadId").GetString()!, ThreadProjectId) },
            "thread/start" => new { thread = Thread("new-thread", "project-1") },
            "thread/resume" => new { thread = Thread("thread-1", "project-1") },
            "thread/turns/list" => new { data = new[] { new { id = Status == "active" ? "active-turn" : "new-turn", status = TurnStatus } } },
            "turn/start" => new { turn = new { id = "new-turn" } },
            "turn/steer" => new { turnId = "active-turn" },
            _ => throw new InvalidOperationException("Unexpected Codex method: " + method)
        };

        return Task.FromResult(JsonSerializer.SerializeToElement(result));
    }

    private object Thread(string id, string? projectId)
        => new
        {
            id, projectId, name = "Fix keyboard layout", preview = "", cwd = WorkingDirectory ?? root + "/worktree",
            updatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), recencyAt = (long?)null,
            status = new { type = Status }, gitInfo = new { branch = "fix-keyboard" }
        };

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
