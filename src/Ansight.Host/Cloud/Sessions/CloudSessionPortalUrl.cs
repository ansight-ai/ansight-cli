namespace Ansight.Host.Cloud;

public static class CloudSessionPortalUrl
{
    public static Uri BaseUri { get; } = new("https://app.ansight.ai/");

    public static Uri CreateSessionUri(Guid sessionId)
        => new(BaseUri, $"session/{Uri.EscapeDataString(sessionId.ToString("D"))}/");

    public static string CreateSessionUrl(Guid sessionId)
        => CreateSessionUri(sessionId).ToString();
}
