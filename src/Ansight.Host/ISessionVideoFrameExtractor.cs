namespace Ansight.Host;

/// <summary>Decodes one image from continuous session media; never reads stored screenshots.</summary>
public interface ISessionVideoFrameExtractor
{
    Task ExtractAsync(string videoPath, TimeSpan position, string imagePath,
        CancellationToken cancellationToken = default);
}
