namespace Ansight.Host;

public interface ISessionVideoTranscoder
{
    Task TranscodeAsync(string sourcePath, string outputPath, TimeSpan duration);
}
