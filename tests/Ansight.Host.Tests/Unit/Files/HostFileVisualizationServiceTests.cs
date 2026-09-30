using Ansight.Host.Tests.TestSupport;

namespace Ansight.Host.Tests.Unit.Files;

public sealed class HostFileVisualizationServiceTests
{
    [Theory]
    [InlineData(".json", "application/octet-stream", FileViewerKinds.Json)]
    [InlineData(".xml", "text/plain", FileViewerKinds.Xml)]
    [InlineData(".md", "text/plain", FileViewerKinds.Markdown)]
    [InlineData(".cs", "text/plain", FileViewerKinds.Code)]
    [InlineData(".csv", "text/plain", FileViewerKinds.Csv)]
    [InlineData(".sqlite", "application/octet-stream", FileViewerKinds.Sqlite)]
    [InlineData(".glb", "application/octet-stream", FileViewerKinds.Model3D)]
    [InlineData(".obj", "application/octet-stream", FileViewerKinds.Model3D)]
    [InlineData(".png", "application/octet-stream", FileViewerKinds.Image)]
    [InlineData(".MP4", "application/octet-stream", FileViewerKinds.Video)]
    [InlineData("mov", "text/plain", FileViewerKinds.Video)]
    [InlineData(".m4a", "application/octet-stream", FileViewerKinds.Audio)]
    [InlineData(".ogg", "video/ogg", FileViewerKinds.Video)]
    [InlineData(".webm", "audio/webm", FileViewerKinds.Audio)]
    [InlineData(".dat", "audio/mpeg", FileViewerKinds.Audio)]
    [InlineData(".dat", "video/mp4", FileViewerKinds.Video)]
    public void Classify_MapsSupportedViewerKinds(string extension, string mimeType, string expected)
    {
        Assert.Equal(expected, FileVisualizationService.Classify(extension, mimeType));
    }

    [Theory]
    [InlineData(".mp4", "video/mp4")]
    [InlineData(".m4v", "video/mp4")]
    [InlineData(".mov", "video/quicktime")]
    [InlineData(".webm", "video/webm")]
    [InlineData(".ogv", "video/ogg")]
    [InlineData(".mpg", "video/mpeg")]
    [InlineData(".mpeg", "video/mpeg")]
    [InlineData(".mpe", "video/mpeg")]
    [InlineData(".3gp", "video/3gpp")]
    [InlineData(".3g2", "video/3gpp2")]
    [InlineData(".avi", "video/x-msvideo")]
    [InlineData(".mkv", "video/x-matroska")]
    [InlineData(".mp3", "audio/mpeg")]
    [InlineData(".mp2", "audio/mpeg")]
    [InlineData(".m4a", "audio/mp4")]
    [InlineData(".m4b", "audio/mp4")]
    [InlineData(".aac", "audio/aac")]
    [InlineData(".wav", "audio/wav")]
    [InlineData(".ogg", "audio/ogg")]
    [InlineData(".oga", "audio/ogg")]
    [InlineData(".opus", "audio/ogg")]
    [InlineData(".flac", "audio/flac")]
    [InlineData(".aif", "audio/aiff")]
    [InlineData(".aiff", "audio/aiff")]
    [InlineData(".caf", "audio/x-caf")]
    [InlineData(".weba", "audio/webm")]
    public async Task MediaPreviewsUseStreamMetadataWithoutDownloadingContent(string extension, string mimeType)
    {
        var service = new FileVisualizationService();
        var preview = await service.InspectLiveFileAsync("session", "appData", $"recording{extension}");

        Assert.True(preview.IsSuccess, preview.Message);
        Assert.Equal(mimeType, preview.MimeType);
        Assert.Equal(mimeType.StartsWith("audio/", StringComparison.Ordinal) ? FileViewerKinds.Audio : FileViewerKinds.Video, preview.ViewerKind);
        Assert.Equal(preview.ViewerKind, FileVisualizationService.Classify(extension, "application/octet-stream"));
        Assert.Equal(mimeType, FileVisualizationService.ResolveMimeType(extension, "application/octet-stream"));
        Assert.Equal(mimeType, FileVisualizationService.ResolveMimeType(extension, "text/plain"));
        Assert.Equal(0, preview.BytesRead);
        Assert.False(preview.IsTruncated);
        Assert.Null(preview.Text);
        Assert.Null(preview.Base64);
    }

    [Theory]
    [InlineData(".mp4", FileViewerKinds.Video)]
    [InlineData(".wav", FileViewerKinds.Audio)]
    public async Task InspectLocalFileAsync_DoesNotEmbedOrTruncateLargeMedia(string extension, string viewerKind)
    {
        using var environment = new TestEnvironment();
        var filePath = Path.Combine(environment.RootPath, $"recording{extension}");
        const long sizeBytes = 10 * 1024 * 1024 + 1;
        await using (var stream = File.Create(filePath))
        {
            stream.SetLength(sizeBytes);
        }

        var preview = await new FileVisualizationService().InspectLocalFileAsync(filePath);

        Assert.True(preview.IsSuccess, preview.Message);
        Assert.Equal(viewerKind, preview.ViewerKind);
        Assert.Equal(sizeBytes, preview.SizeBytes);
        Assert.Equal(0, preview.BytesRead);
        Assert.False(preview.IsTruncated);
        Assert.Null(preview.Text);
        Assert.Null(preview.RawText);
        Assert.Null(preview.Base64);
    }

    [Fact]
    public async Task CalculateArtifactDetailsAsync_ReturnsTimestampAndChecksums()
    {
        const string downloadedAtUtc = "2026-09-03T01:02:03.0000000Z";
        using var environment = new TestEnvironment();
        var filePath = Path.Combine(environment.RootPath, "artifact.glb");
        await File.WriteAllTextAsync(filePath, "Ansight artifact");

        var details = await FileVisualizationService.CalculateArtifactDetailsAsync(
            filePath,
            downloadedAtUtc,
            CancellationToken.None);

        Assert.Equal(downloadedAtUtc, details.DownloadedAtUtc);
        Assert.Equal("727658685738cb0ef5da51d97735836f4fa2fd4aae0face7c9844769ca90a61e", details.Sha256);
        Assert.Equal("8d851359ef2217f8b18c3eac55113b65a87cc8f7", details.Sha1);
        Assert.Equal("28a9d00bec118e7d77e68ccc7067110e", details.Md5);
    }

    [Fact]
    public async Task InspectLocalFileAsync_FormatsJsonInsideHost()
    {
        const string sourceJson = "{\"enabled\":true,\"name\":\"Ansight\"}";
        using var environment = new TestEnvironment();
        await using var runtime = environment.CreateRuntime();
        var filePath = Path.Combine(environment.RootPath, "settings.json");
        await File.WriteAllTextAsync(filePath, sourceJson);

        var preview = await runtime.FileVisualizations.InspectLocalFileAsync(filePath);

        Assert.True(preview.IsSuccess, preview.Message);
        Assert.Equal(FileViewerKinds.Json, preview.ViewerKind);
        Assert.Equal("JSON", preview.Language);
        Assert.Contains(Environment.NewLine, preview.Text, StringComparison.Ordinal);
        Assert.Contains("\"enabled\": true", preview.Text, StringComparison.Ordinal);
        Assert.Equal(sourceJson, preview.RawText);
    }

    [Fact]
    public async Task InspectLocalFileAsync_TruncatesTextPreviewAtTenMegabytes()
    {
        const int previewBytes = 10 * 1024 * 1024;
        using var environment = new TestEnvironment();
        await using var runtime = environment.CreateRuntime();
        var filePath = Path.Combine(environment.RootPath, "large.txt");
        await using (var stream = File.Create(filePath))
        {
            stream.SetLength(previewBytes + 1L);
        }

        var preview = await runtime.FileVisualizations.InspectLocalFileAsync(filePath);

        Assert.True(preview.IsSuccess, preview.Message);
        Assert.True(preview.IsTruncated);
        Assert.Equal(previewBytes, preview.BytesRead);
        Assert.Equal(previewBytes, preview.RawText?.Length);
    }

    [Fact]
    public async Task QueryLocalDatabaseAsync_RejectsMutationBeforeOpeningFile()
    {
        using var environment = new TestEnvironment();
        await using var runtime = environment.CreateRuntime();

        var result = await runtime.FileVisualizations.QueryLocalDatabaseAsync(
            Path.Combine(environment.RootPath, "missing.sqlite"),
            "DELETE FROM users");

        Assert.False(result.IsSuccess);
        Assert.Contains("read-only", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task QueryLocalDatabaseAsync_InitializesSqliteProvider()
    {
        using var environment = new TestEnvironment();
        await using var runtime = environment.CreateRuntime();

        var result = await runtime.FileVisualizations.QueryLocalDatabaseAsync(
            Path.Combine(environment.RootPath, "missing.sqlite"),
            "SELECT 1");

        Assert.False(result.IsSuccess);
        Assert.DoesNotContain("SetProvider", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Batteries.Init", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InspectLocalFileAsync_ConvertsBinaryPropertyListToXml()
    {
        const string binaryPropertyListBase64 =
            "YnBsaXN0MDDUAQIDBAUGBwpUbmFtZVdlbmFibGVkWHZlcnNpb25zVXJhdGlvV0Fuc2lnaHQJoggJEAEQAiM/+AAAAAAAAAgRFh4nLTU2OTs9AAAAAAAAAQEAAAAAAAAACwAAAAAAAAAAAAAAAAAAAEY=";
        using var environment = new TestEnvironment();
        await using var runtime = environment.CreateRuntime();
        var filePath = Path.Combine(environment.RootPath, "settings.plist");
        await File.WriteAllBytesAsync(filePath, Convert.FromBase64String(binaryPropertyListBase64));

        var preview = await runtime.FileVisualizations.InspectLocalFileAsync(filePath);

        Assert.True(preview.IsSuccess, preview.Message);
        Assert.Equal(FileViewerKinds.Xml, preview.ViewerKind);
        Assert.Equal("Property List", preview.Language);
        Assert.Equal("binary plist", preview.FormatLabel);
        Assert.Contains("<plist", preview.Text, StringComparison.Ordinal);
        Assert.Contains("Ansight", preview.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QueryLocalDatabaseAsync_ReturnsRowsWithoutMutatingDatabase()
    {
        SQLitePCL.ISQLite3Provider provider = OperatingSystem.IsWindows()
            ? new SQLitePCL.SQLite3Provider_winsqlite3()
            : new SQLitePCL.SQLite3Provider_sqlite3();
        SQLitePCL.raw.SetProvider(provider);
        SQLitePCL.raw.FreezeProvider();
        using var environment = new TestEnvironment();
        await using var runtime = environment.CreateRuntime();
        var filePath = Path.Combine(environment.RootPath, "items.sqlite");
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={filePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE items (id INTEGER PRIMARY KEY, name TEXT); INSERT INTO items (name) VALUES ('one'), ('two');";
            await command.ExecuteNonQueryAsync();
        }

        var result = await runtime.FileVisualizations.QueryLocalDatabaseAsync(
            filePath,
            "SELECT id, name FROM items ORDER BY id");

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(["1", "one"], result.Rows[0]);
        Assert.Equal(["2", "two"], result.Rows[1]);
    }
}
