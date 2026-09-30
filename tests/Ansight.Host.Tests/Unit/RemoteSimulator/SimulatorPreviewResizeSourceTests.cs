namespace Ansight.Host.Tests.Unit.RemoteSimulator;

public sealed class SimulatorPreviewResizeSourceTests
{
    [Fact]
    public void DrawableResizeInvalidatesAndRendersTheCurrentSimulatorFrame()
    {
        var source = File.ReadAllText(ResolveNativeSourcePath());
        var methodStart = source.IndexOf(
            "- (void)setPreviewDrawableSize:(CGSize)drawableSize\n{",
            StringComparison.Ordinal);
        var methodEnd = source.IndexOf(
            "- (void)renderSurface:(id)surface drawableSize:(CGSize)drawableSize\n{",
            methodStart,
            StringComparison.Ordinal);

        Assert.True(methodStart >= 0);
        Assert.True(methodEnd > methodStart);
        var method = source[methodStart..methodEnd];

        Assert.Contains("CGSizeEqualToSize", method, StringComparison.Ordinal);
        Assert.Contains("self.frameGeneration += 1", method, StringComparison.Ordinal);
        Assert.Contains("[self scheduleRenderIgnoringThrottle:YES]", method, StringComparison.Ordinal);
    }

    [Fact]
    public void WebRtcConnectionEncodesTheCurrentSurfaceWithoutWaitingForInput()
    {
        var source = File.ReadAllText(ResolveNativeSourcePath());
        var connectStart = source.IndexOf(
            "- (BOOL)connectScreenWithError:(NSError **)error\n{",
            StringComparison.Ordinal);
        var connectEnd = source.IndexOf(
            "- (void)handleSurfacesChanged:(id)unmaskedSurface maskedSurface:(id)maskedSurface\n{",
            connectStart,
            StringComparison.Ordinal);
        var requestStart = source.IndexOf(
            "- (void)requestKeyFrame\n{",
            StringComparison.Ordinal);
        var requestEnd = source.IndexOf(
            "- (int32_t)copyAnswerToBuffer:",
            requestStart,
            StringComparison.Ordinal);

        Assert.True(connectStart >= 0);
        Assert.True(connectEnd > connectStart);
        Assert.True(requestStart >= 0);
        Assert.True(requestEnd > requestStart);

        var connectMethod = source[connectStart..connectEnd];
        var requestMethod = source[requestStart..requestEnd];
        Assert.Contains("framebufferSurface", connectMethod, StringComparison.Ordinal);
        Assert.Contains("[self handleSurfacesChanged:", connectMethod, StringComparison.Ordinal);
        Assert.Contains("self.nextEncodeTime = 0", requestMethod, StringComparison.Ordinal);
        Assert.Contains("dispatch_async(self.screenQueue", requestMethod, StringComparison.Ordinal);
        Assert.Contains("[self handleFrame]", requestMethod, StringComparison.Ordinal);
    }

    [Fact]
    public void WebRtcVideoUsesCaptureTimeForRtpTimestamps()
    {
        var source = File.ReadAllText(ResolveNativeSourcePath());
        var encodeStart = source.IndexOf(
            "- (void)encodePixelBuffer:(CVPixelBufferRef)pixelBuffer\n{",
            StringComparison.Ordinal);
        var encodeEnd = source.IndexOf(
            "- (BOOL)ensureEncoderWidth:",
            encodeStart,
            StringComparison.Ordinal);
        var sampleStart = source.IndexOf(
            "- (void)handleEncodedSample:(CMSampleBufferRef)sampleBuffer status:(OSStatus)status\n{",
            StringComparison.Ordinal);
        var sampleEnd = source.IndexOf(
            "- (int32_t)sendExternalAccessUnit:",
            sampleStart,
            StringComparison.Ordinal);

        Assert.True(encodeStart >= 0);
        Assert.True(encodeEnd > encodeStart);
        Assert.True(sampleStart >= 0);
        Assert.True(sampleEnd > sampleStart);

        var encodeMethod = source[encodeStart..encodeEnd];
        var sampleMethod = source[sampleStart..sampleEnd];
        Assert.Contains("now - self.nativeFirstPresentationTime", encodeMethod, StringComparison.Ordinal);
        Assert.Contains("CMTimeMake(elapsedRtpTicks, AnsightH264ClockRate)", encodeMethod, StringComparison.Ordinal);
        Assert.Contains("CMSampleBufferGetPresentationTimeStamp", sampleMethod, StringComparison.Ordinal);
        Assert.Contains("self.nativeBaseRtpTimestamp + (uint32_t)rtpTime.value", sampleMethod, StringComparison.Ordinal);
    }

    [Fact]
    public void WebRtcVideoBoundsTransportBacklogToFreshnessWindow()
    {
        var source = File.ReadAllText(ResolveNativeSourcePath());
        var capacityStart = source.IndexOf("- (BOOL)canSendVideoFrame\n{", StringComparison.Ordinal);
        var capacityEnd = source.IndexOf(
            "- (void)handleVideoBufferedAmountLow:",
            capacityStart,
            StringComparison.Ordinal);

        Assert.True(capacityStart >= 0);
        Assert.True(capacityEnd > capacityStart);
        var capacityMethod = source[capacityStart..capacityEnd];

        Assert.Contains(
            "AnsightMaximumVideoBufferDurationMilliseconds = 200",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "rtcGetBufferedAmount(track) <= maximumBufferedBytes",
            capacityMethod,
            StringComparison.Ordinal);
        Assert.Contains("self.videoRefreshPending = YES", capacityMethod, StringComparison.Ordinal);
        Assert.Contains("rtcSetBufferedAmountLowCallback", source, StringComparison.Ordinal);
        Assert.Contains("handleVideoBufferedAmountLow:track", source, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "rtcGetBufferedAmount(self.videoTrack) > 2 * 1024 * 1024",
            source,
            StringComparison.Ordinal);
    }

    private static string ResolveNativeSourcePath()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "src",
                "Ansight.SimulatorRtc.Mac",
                "Native",
                "AnsightSimulatorRtc",
                "AnsightSimulatorRtc.mm");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("Could not locate the simulator RTC native source file.");
    }
}
