using System.Text.Json.Nodes;
using SkiaSharp;

namespace Ansight.Host.Runtime.Operations.Tools.UiAutomation;

internal sealed record InspectionImage(byte[] Bytes, int Width, int Height, string Sha256,
    string? FrameId, string? WorkspacePath, DateTimeOffset? CapturedAtUtc, JsonObject? Geometry = null)
{
    public JsonObject Describe() => new()
    {
        ["frameId"] = FrameId, ["workspacePath"] = WorkspacePath, ["width"] = Width,
        ["height"] = Height, ["sha256"] = Sha256, ["capturedAtUtc"] = CapturedAtUtc,
        ["geometry"] = Geometry?.DeepClone()
    };
}

internal sealed class ScreenshotInspectionEvidence(OperationServices services, AppSessionSnapshot session) : IDisposable
{
    internal const int MaximumImageBytes = 32 * 1024 * 1024;
    internal const int MaximumImagePixels = 16 * 1024 * 1024;
    private readonly string snapshotId = Guid.NewGuid().ToString("N");
    private readonly string directory = Path.Combine(services.ApplicationPaths.ApplicationTempPath,
        "image-inspection-" + Guid.NewGuid().ToString("N"));

    public static string ResolveWorkspaceImage(string repositoryRoot, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath)
            || relativePath.Contains('\\') || relativePath.Contains(':'))
            throw new ArgumentException("workspacePath must be a relative path inside the ansight workspace.");
        var parts = relativePath.Split('/');
        if (parts.Any(part => part is ".." or "." or ""))
            throw new ArgumentException("workspacePath cannot contain empty, '.' or '..' path segments.");
        var root = Path.Combine(Path.GetFullPath(repositoryRoot), "ansight");
        // Reject links at and below the workspace root, including linked directories.
        // This keeps workspace image reads inside the explicitly selected workspace.
        var path = root;
        EnsureNotLink(path);
        foreach (var part in parts)
        {
            path = Path.Combine(path, part);
            EnsureNotLink(path);
        }
        return path;
    }

    private static void EnsureNotLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new ArgumentException("Workspace image paths cannot traverse symbolic links or junctions.");
    }

    public async Task<InspectionImage> ReadAsync(JsonObject source, bool allowWorkspace, CancellationToken token)
    {
        var frameId = source["frameId"]?.GetValue<string>();
        var workspacePath = source["workspacePath"]?.GetValue<string>();
        if (source.Count != 1 || (string.IsNullOrWhiteSpace(frameId) == string.IsNullOrWhiteSpace(workspacePath)))
            throw new ArgumentException("An image source must specify exactly one non-empty frameId or workspacePath.");
        string path;
        DateTimeOffset? capturedAt = null;
        JsonObject? geometry = null;
        if (frameId is not null)
        {
            var frame = session.Images.FirstOrDefault(image => image.FrameId == frameId)
                ?? throw new ArgumentException($"Screenshot frame '{frameId}' was not found in this session.");
            path = SessionFileLocator.ResolveScreenshotPath(services.ApplicationPaths, session, frame);
            capturedAt = frame.CapturedAtUtc;
            geometry = ScreenshotGeometry.Validate(frame.Geometry);
        }
        else
        {
            if (!allowWorkspace) throw new ArgumentException("image must reference a retained frameId in this session.");
            var root = ScreenshotWorkspaceScope.RepositoryRoot ?? services.AppService.Get(session.AppId)?.CodebasePath;
            if (string.IsNullOrWhiteSpace(root))
                throw new ArgumentException("A workspace reference requires a running workspace task or a registered app codebase.");
            path = ResolveWorkspaceImage(root, workspacePath!);
        }
        // Read at most the limit even if the file grows while it is being read.
        await using var stream = File.OpenRead(path);
        if (stream.Length > MaximumImageBytes) throw new ArgumentException("Image exceeds the 32 MiB limit.");
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        if (await stream.ReadAsync(new byte[1], token).ConfigureAwait(false) != 0)
            throw new ArgumentException("Image changed while it was being read.");
        using var input = new SKMemoryStream(bytes);
        using var codec = SKCodec.Create(input);
        if (codec is null || codec.EncodedFormat is not (SKEncodedImageFormat.Png or SKEncodedImageFormat.Jpeg))
            throw new ArgumentException("Image must be a decodable PNG or JPEG.");
        if (codec.Info.Width <= 0 || codec.Info.Height <= 0
            || (long)codec.Info.Width * codec.Info.Height > MaximumImagePixels)
            throw new ArgumentException("Image exceeds the 16 megapixel limit.");
        if (codec.EncodedOrigin != SKEncodedOrigin.TopLeft)
            throw new ArgumentException("Normalize image EXIF orientation before using it as a reference.");
        return new InspectionImage(bytes, codec.Info.Width, codec.Info.Height,
            Convert.ToHexStringLower(SHA256.HashData(bytes)), frameId, workspacePath, capturedAt, geometry);
    }

    public JsonObject Write(string fileName, byte[] bytes)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, fileName), bytes);
        return new JsonObject { ["snapshotId"] = snapshotId, ["path"] = fileName };
    }

    public JsonObject WriteImage(string name, InspectionImage image)
    {
        // Retain the exact input bytes and hash, including JPEG reference inputs.
        var extension = image.Bytes[0] == 0x89 ? ".png" : ".jpg";
        return Write(name + extension, image.Bytes);
    }

    public void AddCrops(JsonArray regions, InspectionImage actual, CancellationToken token)
    {
        using var bitmap = SKBitmap.Decode(actual.Bytes) ?? throw new InvalidDataException("Image could not be decoded.");
        foreach (var region in regions.OfType<JsonObject>())
        {
            token.ThrowIfCancellationRequested();
            var geometry = region["geometry"]!;
            var x = geometry["x"]!.GetValue<double>();
            var y = geometry["y"]!.GetValue<double>();
            var width = geometry["width"]!.GetValue<double>();
            var height = geometry["height"]!.GetValue<double>();
            var left = Math.Clamp((int)Math.Floor(x * bitmap.Width + 1e-8), 0, bitmap.Width - 1);
            var top = Math.Clamp((int)Math.Floor(y * bitmap.Height + 1e-8), 0, bitmap.Height - 1);
            var rectangle = new SKRectI(left, top,
                Math.Clamp((int)Math.Ceiling((x + width) * bitmap.Width - 1e-8), left + 1, bitmap.Width),
                Math.Clamp((int)Math.Ceiling((y + height) * bitmap.Height - 1e-8), top + 1, bitmap.Height));
            using var crop = new SKBitmap();
            if (!bitmap.ExtractSubset(crop, rectangle)) throw new InvalidDataException("Region crop could not be extracted.");
            // Bound retained crop sizes even when many model boxes cover most of a large frame.
            var scale = Math.Min(1, 512.0 / Math.Max(crop.Width, crop.Height));
            using var preview = scale == 1 ? crop.Copy() : crop.Resize(new SKImageInfo(
                Math.Max(1, (int)Math.Round(crop.Width * scale)), Math.Max(1, (int)Math.Round(crop.Height * scale))),
                new SKSamplingOptions(SKFilterMode.Linear));
            using var encoded = preview!.Encode(SKEncodedImageFormat.Png, 100);
            region["crop"] = Write($"region-{region["id"]!.GetValue<string>()}.png", encoded.ToArray());
        }
    }

    public void Persist(JsonObject result, DateTimeOffset capturedAt)
    {
        result["evidence"] = new JsonObject { ["snapshotId"] = snapshotId, ["path"] = "result.json" };
        Write("result.json", Encoding.UTF8.GetBytes(result.ToJsonString(JsonUtil.Pretty)));
        var entries = Directory.GetFiles(directory).Select(path => new SessionArtifactEntry
        {
            Name = Path.GetFileName(path), RootAlias = "screenshots", RelativePath = Path.GetFileName(path),
            SnapshotRelativePath = Path.GetFileName(path), ArchiveRelativePath = Path.GetFileName(path),
            Kind = "file", SizeBytes = new FileInfo(path).Length, FileExtension = Path.GetExtension(path),
            MimeType = Path.GetExtension(path) switch { ".png" => "image/png", ".jpg" => "image/jpeg", ".txt" => "text/plain", _ => "application/json" }
        }).ToArray();
        var saved = services.RuntimeState.AddSessionArtifactSnapshot(session.SessionId, new SessionArtifactSnapshot
        {
            SnapshotId = snapshotId, CapturedAtUtc = capturedAt, Source = "ansight.screenshots",
            RootAlias = "screenshots", RelativePath = snapshotId,
            Name = result["capability"]!.GetValue<string>() == "screenshot.compare" ? "Screenshot comparison" : "Screenshot assessment",
            Kind = "directory", ArtifactDirectoryName = "image-inspection-" + snapshotId,
            FileCount = entries.Length, ByteCount = entries.Sum(entry => entry.SizeBytes), Entries = entries
        }, directory);
        if (!saved.IsSuccess) throw new IOException(saved.Message);
    }

    public static JsonObject Region(string id, string label, string source, string frameId,
        double x, double y, double width, double height) => new()
    {
        ["id"] = id, ["label"] = label, ["source"] = source,
        ["geometry"] = new JsonObject
        {
            ["frameId"] = frameId, ["kind"] = "rectangle", ["text"] = label,
            ["x"] = x, ["y"] = y, ["width"] = width, ["height"] = height
        }
    };

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
