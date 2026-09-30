namespace Ansight.Host.Cloud;

public readonly record struct CloudSessionStreamUploadProgress(
    long BytesTransferred,
    long TotalBytes)
{
    public double Fraction => TotalBytes <= 0
        ? 0
        : Math.Clamp(BytesTransferred / (double)TotalBytes, 0, 1);

    public int Percentage => (int)Math.Round(Fraction * 100, MidpointRounding.AwayFromZero);

    public static CloudSessionStreamUploadProgress Create(long bytesTransferred, long totalBytes)
        => new(Math.Max(0, bytesTransferred), Math.Max(0, totalBytes));
}
