using Ansight.Host.Runtime.DotNetProfiling;
using Ansight.Host.Tests.TestSupport;

namespace Ansight.Host.Tests.Unit.Files;

public sealed class ArtifactFormatPreviewTests
{
    [Theory]
    [InlineData(".gltf", "model/gltf+json", FileViewerKinds.Model3D)]
    [InlineData(".svg", "image/svg+xml", FileViewerKinds.Image)]
    [InlineData(".ndjson", "application/json", FileViewerKinds.JsonLines)]
    [InlineData(".jsonl", "application/octet-stream", FileViewerKinds.JsonLines)]
    [InlineData("", "application/x-ndjson; charset=utf-8", FileViewerKinds.JsonLines)]
    [InlineData(".tsv", "text/plain", FileViewerKinds.Tsv)]
    [InlineData("", "text/tab-separated-values; charset=utf-8", FileViewerKinds.Tsv)]
    [InlineData(".har", "application/json", FileViewerKinds.Har)]
    [InlineData(".gpx", "application/xml", FileViewerKinds.Geo)]
    [InlineData(".kml", "application/xml", FileViewerKinds.Geo)]
    [InlineData(".geojson", "application/json", FileViewerKinds.Geo)]
    [InlineData(".htm", "text/plain", FileViewerKinds.Html)]
    [InlineData(".yaml", "text/plain", FileViewerKinds.Yaml)]
    [InlineData(".toml", "text/plain", FileViewerKinds.Toml)]
    [InlineData(".zip", "application/octet-stream", FileViewerKinds.Archive)]
    [InlineData(".tar", "application/octet-stream", FileViewerKinds.Archive)]
    [InlineData(".gz", "application/octet-stream", FileViewerKinds.Archive)]
    [InlineData(".tgz", "application/octet-stream", FileViewerKinds.Archive)]
    [InlineData(".cpuprofile", "application/json", FileViewerKinds.Profile)]
    [InlineData(".pftrace", "application/octet-stream", FileViewerKinds.Perfetto)]
    [InlineData(".nettrace", "application/octet-stream", FileViewerKinds.NetTrace)]
    [InlineData("", "application/pdf; version=1.7", FileViewerKinds.Pdf)]
    public void SpecificFormatsWinOverGenericMimeTypes(string extension, string mimeType, string expected)
        => Assert.Equal(expected, FileVisualizationService.Classify(extension, mimeType));

    [Theory]
    [InlineData("jsonl", "{\"a\":1}\n{\"a\":2}", FileViewerKinds.JsonLines)]
    [InlineData("tsv", "name\tvalue\nhello\tworld", FileViewerKinds.Tsv)]
    [InlineData("yaml", "enabled: true", FileViewerKinds.Yaml)]
    [InlineData("toml", "enabled = true", FileViewerKinds.Toml)]
    [InlineData("html", "<h1>Report</h1>", FileViewerKinds.Html)]
    [InlineData("har", "{\"log\":{\"entries\":[]}}", FileViewerKinds.Har)]
    [InlineData("gpx", "<gpx />", FileViewerKinds.Geo)]
    public async Task TextFormatsPreserveSourceForBrowserParsing(string extension, string source, string expected)
    {
        using var environment = new TestEnvironment();
        var path = Path.Combine(environment.RootPath, "artifact." + extension);
        await File.WriteAllTextAsync(path, source);
        var result = await new FileVisualizationService().InspectLocalFileAsync(path);
        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(expected, result.ViewerKind);
        Assert.Equal(source, result.RawText);
        Assert.Null(result.Base64);
    }

    [Theory]
    [InlineData("zip", FileViewerKinds.Archive)]
    [InlineData("pftrace", FileViewerKinds.Perfetto)]
    public async Task LargeBinaryPreviewsUseContentStreamInsteadOfTruncatedBase64(string extension, string expected)
    {
        using var environment = new TestEnvironment();
        var path = Path.Combine(environment.RootPath, "artifact." + extension);
        await using (var stream = File.Create(path)) stream.SetLength(11 * 1024 * 1024);
        var result = await new FileVisualizationService().InspectLocalFileAsync(path);
        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(expected, result.ViewerKind);
        Assert.False(result.IsTruncated);
        Assert.Null(result.Base64);
        Assert.Equal(0, result.BytesRead);
    }

    [Fact]
    public async Task NetTraceConversionUsesIsolatedOutputAndPreservesSourceIdentity()
    {
        using var environment = new TestEnvironment();
        var path = Path.Combine(environment.RootPath, "capture.nettrace");
        await File.WriteAllTextAsync(path, "trace bytes");
        var runner = new ProfileProcessRunner();
        var result = await FileVisualizationService.ConvertNetTraceAsync(path, "capture.nettrace", runner, "dotnet-trace", CancellationToken.None);
        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(FileViewerKinds.Profile, result.ViewerKind);
        Assert.Equal("capture.nettrace", result.FileName);
        Assert.Contains("profiles", result.Text);
        Assert.Equal("trace bytes", await File.ReadAllTextAsync(path));
        Assert.False(Directory.Exists(runner.OutputDirectory));
    }

    [Fact]
    public async Task NetTraceConversionReportsMissingToolAndTimeout()
    {
        using var environment = new TestEnvironment();
        var path = Path.Combine(environment.RootPath, "capture.nettrace");
        await File.WriteAllTextAsync(path, "trace bytes");
        var missing = await FileVisualizationService.ConvertNetTraceAsync(path, "capture.nettrace", new ProfileProcessRunner(), null, CancellationToken.None);
        Assert.False(missing.IsSuccess);
        Assert.Contains("Install", missing.Message);
        var timedOut = await FileVisualizationService.ConvertNetTraceAsync(path, "capture.nettrace", new ProfileProcessRunner { TimeOut = true }, "dotnet-trace", CancellationToken.None);
        Assert.False(timedOut.IsSuccess);
        Assert.Contains("60 seconds", timedOut.Message);
    }

    private sealed class ProfileProcessRunner : IDotNetTraceProcessRunner
    {
        public bool TimeOut { get; init; }
        public string OutputDirectory { get; private set; } = string.Empty;

        public async Task<DotNetTraceProcessResult> RunAsync(DotNetTraceProcessRequest request, Action<string>? outputReceived, CancellationToken cancellationToken)
        {
            Assert.Equal("convert", request.Arguments[0]);
            Assert.Equal("Speedscope", request.Arguments[3]);
            OutputDirectory = request.WorkingDirectory;
            if (TimeOut) return new DotNetTraceProcessResult(1, "", "", true);
            await File.WriteAllTextAsync(request.Arguments[5] + ".speedscope.json", "{\"shared\":{\"frames\":[]},\"profiles\":[]}", cancellationToken);
            return new DotNetTraceProcessResult(0, "", "", false);
        }
    }
}
