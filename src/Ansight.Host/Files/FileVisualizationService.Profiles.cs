using System.Text.Json.Nodes;
using Ansight.Host.Runtime.DotNetProfiling;

namespace Ansight.Host.Files;

public sealed partial class FileVisualizationService
{
    private static Task<FileVisualization> InspectNetTraceAsync(string path, string name, CancellationToken cancellationToken)
        => ConvertNetTraceAsync(path, name, new DotNetTraceProcessRunner(), DotNetTraceToolLocator.FindOnPath("dotnet-trace"), cancellationToken);

    internal static async Task<FileVisualization> ConvertNetTraceAsync(string path, string name,
        IDotNetTraceProcessRunner runner, string? converter, CancellationToken cancellationToken)
    {
        if (converter is null)
            return FileVisualization.Failure("Previewing .nettrace requires dotnet-trace on the host. Install it with 'dotnet tool install --global dotnet-trace', then reopen this artifact.");
        var size = new FileInfo(path).Length;
        if (size > 256 * 1024 * 1024)
            return FileVisualization.Failure("This .nettrace exceeds the 256 MiB conversion limit. Convert a shorter capture to Speedscope.");
        var directory = Path.Combine(Path.GetTempPath(), "ansight-profile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var result = await runner.RunAsync(new DotNetTraceProcessRequest(converter,
                ["convert", Path.GetFullPath(path), "--format", "Speedscope", "--output", Path.Combine(directory, "profile")],
                directory, TimeSpan.FromSeconds(60)), null, cancellationToken).ConfigureAwait(false);
            if (!result.IsSuccess)
                return FileVisualization.Failure(result.TimedOut ? "Trace conversion exceeded 60 seconds." : "Trace conversion failed. " + result.StandardError);
            var output = Directory.EnumerateFiles(directory, "*.speedscope.json").FirstOrDefault();
            if (output is null) return FileVisualization.Failure("Trace conversion did not produce a Speedscope profile.");
            if (new FileInfo(output).Length > 32 * 1024 * 1024)
                return FileVisualization.Failure("The converted profile exceeds the 32 MiB preview limit.");
            var text = await File.ReadAllTextAsync(output, cancellationToken).ConfigureAwait(false);
            return new FileVisualization(true, "Converted .nettrace to Speedscope for preview.", FileViewerKinds.Profile,
                name, ".nettrace", "application/vnd.speedscope+json", "Speedscope", size, size, false,
                text, null, null, text, ".NET trace · Speedscope");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return FileVisualization.Failure("Trace conversion failed. " + exception.Message);
        }
        finally { DeleteProfileTemporaryDirectory(directory); }
    }

    private static async Task<FileVisualization> InspectDownloadedNetTraceAsync(JsonObject result, string name, CancellationToken cancellationToken)
    {
        if (ReadBoolean(result, "truncated")) return FileVisualization.Failure("Trace conversion requires the complete file. Choose Open full file.");
        var base64 = ReadString(result, "base64");
        if (base64 is null) return FileVisualization.Failure("The app did not return binary trace data.");
        if (base64.Length > ((256L * 1024 * 1024 + 2) / 3) * 4) return FileVisualization.Failure("Trace exceeds the 256 MiB conversion limit.");
        var directory = Path.Combine(Path.GetTempPath(), "ansight-trace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "capture.nettrace");
            await File.WriteAllBytesAsync(path, Convert.FromBase64String(base64), cancellationToken).ConfigureAwait(false);
            return await InspectNetTraceAsync(path, name, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is FormatException or IOException or UnauthorizedAccessException)
        {
            return FileVisualization.Failure("Trace download could not be converted. " + exception.Message);
        }
        finally { DeleteProfileTemporaryDirectory(directory); }
    }

    private static void DeleteProfileTemporaryDirectory(string directory)
    {
        try { Directory.Delete(directory, recursive: true); }
        catch (IOException) { /* A locked temporary file must not hide the conversion result. */ }
        catch (UnauthorizedAccessException) { /* The operating system may retain temporary files. */ }
    }
}
