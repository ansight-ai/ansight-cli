namespace Ansight.RemoteSimulator.Core.Streaming;

public sealed record RemoteFrame(byte[] Content, string ContentType)
{
    public static RemoteFrame Jpeg(byte[] content) => new(content, "image/jpeg");

    public static RemoteFrame Png(byte[] content) => new(content, "image/png");
}
