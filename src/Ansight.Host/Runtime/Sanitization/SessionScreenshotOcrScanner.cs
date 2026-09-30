using System.Diagnostics;
using System.Globalization;

namespace Ansight.Host.Runtime.Sanitization;

internal interface ISessionScreenshotOcrScanner
{
    SessionScreenshotOcrResult Scan(string imageFilePath);
}

internal sealed class TesseractSessionScreenshotOcrScanner : ISessionScreenshotOcrScanner
{
    private static readonly TimeSpan scanTimeout = TimeSpan.FromSeconds(15);
    private readonly string? executablePath;

    public TesseractSessionScreenshotOcrScanner(string? configuredExecutablePath = null)
    {
        executablePath = ResolveExecutablePath(configuredExecutablePath);
    }

    public SessionScreenshotOcrResult Scan(string imageFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imageFilePath);
        if (executablePath is null)
        {
            return SessionScreenshotOcrResult.Unavailable(
                "Tesseract was not found. Install it or set ANSIGHT_TESSERACT_PATH.");
        }

        var scans = new List<SessionScreenshotOcrResult>();
        foreach (var pageSegmentationMode in new[] { 11, 6 })
        {
            var scan = Scan(imageFilePath, pageSegmentationMode);
            if (scan.Available)
            {
                scans.Add(scan);
            }
        }

        if (scans.Count == 0)
        {
            return SessionScreenshotOcrResult.Unavailable(
                "Tesseract failed in both sparse-UI and uniform-block scan modes.");
        }

        return new SessionScreenshotOcrResult(
            true,
            "tesseract-ui-psm11+6",
            MergeBlocks(scans.SelectMany(static scan => scan.Blocks)),
            null);
    }

    private SessionScreenshotOcrResult Scan(string imageFilePath, int pageSegmentationMode)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(Path.GetFullPath(imageFilePath));
        startInfo.ArgumentList.Add("stdout");
        startInfo.ArgumentList.Add("--psm");
        startInfo.ArgumentList.Add(pageSegmentationMode.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("tsv");
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            return SessionScreenshotOcrResult.Unavailable("Tesseract did not start.");
        }

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit((int)scanTimeout.TotalMilliseconds))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // The process exited between the timeout and kill request.
            }

            return SessionScreenshotOcrResult.Unavailable(
                $"Tesseract exceeded the {scanTimeout.TotalSeconds:0}-second scan limit.");
        }

        var output = outputTask.GetAwaiter().GetResult();
        var error = errorTask.GetAwaiter().GetResult().Trim();
        if (process.ExitCode != 0)
        {
            return SessionScreenshotOcrResult.Unavailable(
                string.IsNullOrWhiteSpace(error) ? "Tesseract failed to scan the screenshot." : error);
        }

        return new SessionScreenshotOcrResult(
            true,
            "tesseract",
            ParseTsv(output),
            null);
    }

    internal static IReadOnlyList<SessionScreenshotTextBlock> ParseTsv(string source)
    {
        var lines = new Dictionary<string, SessionScreenshotOcrLine>(StringComparer.Ordinal);
        foreach (var line in source.Split('\n').Skip(1))
        {
            var columns = line.TrimEnd('\r').Split('\t', 12);
            if (columns.Length != 12
                || string.IsNullOrWhiteSpace(columns[11])
                || !int.TryParse(columns[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out var left)
                || !int.TryParse(columns[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out var top)
                || !int.TryParse(columns[8], NumberStyles.Integer, CultureInfo.InvariantCulture, out var width)
                || !int.TryParse(columns[9], NumberStyles.Integer, CultureInfo.InvariantCulture, out var height)
                || !double.TryParse(columns[10], NumberStyles.Float, CultureInfo.InvariantCulture, out var confidence)
                || width <= 0
                || height <= 0)
            {
                continue;
            }

            var lineId = $"{columns[1]}:{columns[2]}:{columns[3]}:{columns[4]}";
            if (!lines.TryGetValue(lineId, out var textLine))
            {
                textLine = new SessionScreenshotOcrLine();
                lines[lineId] = textLine;
            }

            textLine.Add(columns[11], confidence, left, top, width, height);
        }

        return lines.Values.SelectMany(static line => line.BuildBlocks()).ToArray();
    }

    private static IReadOnlyList<SessionScreenshotTextBlock> MergeBlocks(
        IEnumerable<SessionScreenshotTextBlock> source)
    {
        var merged = new List<SessionScreenshotTextBlock>();
        foreach (var candidate in source.OrderByDescending(static block => block.Confidence))
        {
            if (merged.Any(existing => string.Equals(
                    existing.Text.Trim(),
                    candidate.Text.Trim(),
                    StringComparison.OrdinalIgnoreCase)
                && IntersectionOverUnion(existing.Bounds, candidate.Bounds) >= 0.65))
            {
                continue;
            }

            merged.Add(candidate);
        }

        return merged;
    }

    private static double IntersectionOverUnion(
        SessionSanitizationRegion left,
        SessionSanitizationRegion right)
    {
        var intersectionWidth = Math.Max(
            0,
            Math.Min(left.X + left.Width, right.X + right.Width) - Math.Max(left.X, right.X));
        var intersectionHeight = Math.Max(
            0,
            Math.Min(left.Y + left.Height, right.Y + right.Height) - Math.Max(left.Y, right.Y));
        var intersectionArea = intersectionWidth * intersectionHeight;
        var unionArea = (left.Width * left.Height) + (right.Width * right.Height) - intersectionArea;
        return unionArea <= 0 ? 0 : intersectionArea / (double)unionArea;
    }

    private static string? ResolveExecutablePath(string? configuredExecutablePath)
    {
        var configured = string.IsNullOrWhiteSpace(configuredExecutablePath)
            ? Environment.GetEnvironmentVariable("ANSIGHT_TESSERACT_PATH")
            : configuredExecutablePath;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var absolutePath = Path.GetFullPath(configured.Trim());
            return File.Exists(absolutePath) ? absolutePath : null;
        }

        var executableName = OperatingSystem.IsWindows() ? "tesseract.exe" : "tesseract";
        foreach (var directoryPath in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(directoryPath, executableName);
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }
}

internal sealed record SessionScreenshotOcrResult(
    bool Available,
    string? Provider,
    IReadOnlyList<SessionScreenshotTextBlock> Blocks,
    string? Message)
{
    public static SessionScreenshotOcrResult Unavailable(string message)
        => new(false, null, [], message);
}

internal sealed record SessionScreenshotTextBlock(
    string Text,
    double Confidence,
    SessionSanitizationRegion Bounds);

internal sealed class SessionScreenshotOcrLine
{
    private readonly List<SessionScreenshotTextBlock> words = [];
    private double confidenceTotal;
    private int left = int.MaxValue;
    private int top = int.MaxValue;
    private int right;
    private int bottom;

    public void Add(string text, double confidence, int x, int y, int width, int height)
    {
        words.Add(new SessionScreenshotTextBlock(
            text,
            confidence,
            new SessionSanitizationRegion(x, y, width, height)));
        confidenceTotal += confidence;
        left = Math.Min(left, x);
        top = Math.Min(top, y);
        right = Math.Max(right, x + width);
        bottom = Math.Max(bottom, y + height);
    }

    public IReadOnlyList<SessionScreenshotTextBlock> BuildBlocks()
    {
        if (words.Count <= 1)
        {
            return words;
        }

        return
        [
            new SessionScreenshotTextBlock(
                string.Join(' ', words.Select(static word => word.Text)),
                confidenceTotal / words.Count,
                new SessionSanitizationRegion(left, top, right - left, bottom - top)),
            .. words
        ];
    }
}
