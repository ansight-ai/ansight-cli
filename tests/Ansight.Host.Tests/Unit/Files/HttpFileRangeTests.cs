namespace Ansight.Host.Tests.Unit.Files;

public sealed class HttpFileRangeTests
{
    [Theory]
    [InlineData(null, 10, HttpStatusCode.OK, 0, 10)]
    [InlineData("bytes=2-5", 10, HttpStatusCode.PartialContent, 2, 4)]
    [InlineData("bytes=2-", 10, HttpStatusCode.PartialContent, 2, 8)]
    [InlineData("bytes=-3", 10, HttpStatusCode.PartialContent, 7, 3)]
    [InlineData("bytes=-20", 10, HttpStatusCode.PartialContent, 0, 10)]
    [InlineData("bytes=0-100", 10, HttpStatusCode.PartialContent, 0, 10)]
    [InlineData("bytes=9-9", 10, HttpStatusCode.PartialContent, 9, 1)]
    [InlineData("bytes=10-", 10, HttpStatusCode.RequestedRangeNotSatisfiable, 0, 0)]
    [InlineData("bytes=-0", 10, HttpStatusCode.RequestedRangeNotSatisfiable, 0, 0)]
    [InlineData("bytes=0-", 0, HttpStatusCode.RequestedRangeNotSatisfiable, 0, 0)]
    [InlineData("bytes=2-1", 10, HttpStatusCode.OK, 0, 10)]
    [InlineData("bytes=0-1,4-5", 10, HttpStatusCode.OK, 0, 10)]
    [InlineData("items=0-1", 10, HttpStatusCode.OK, 0, 10)]
    [InlineData("invalid", 10, HttpStatusCode.OK, 0, 10)]
    [InlineData("bytes=99999999999999999999999999-", 10, HttpStatusCode.OK, 0, 10)]
    public void ResolveReturnsTheRequestedByteWindow(string? header, long fileLength, HttpStatusCode status, long offset, long length)
    {
        var range = HttpFileRange.Resolve(header, fileLength);

        Assert.Equal(status, range.StatusCode);
        Assert.Equal(offset, range.Offset);
        Assert.Equal(length, range.Length);
    }
}
