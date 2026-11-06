namespace Ansight.Host.Cloud;

public sealed record CloudSessionAttachmentLimits(
    long MaxFileBytes,
    long MaxTotalBytes);
