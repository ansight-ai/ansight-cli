#import "AnsightSessionVideoMac.h"

#import <AVFoundation/AVFoundation.h>
#import <CoreGraphics/CoreGraphics.h>
#import <CoreVideo/CoreVideo.h>
#import <Foundation/Foundation.h>
#import <ImageIO/ImageIO.h>
#import <dispatch/dispatch.h>
#include <stdio.h>
#include <unistd.h>

static const int32_t AnsightSessionVideoTimescale = 1000000;

static void AnsightSessionVideoWriteError(
    NSString *message,
    char *errorBuffer,
    int32_t errorBufferCapacity)
{
    if (errorBuffer == nullptr || errorBufferCapacity <= 0)
    {
        return;
    }

    snprintf(errorBuffer, (size_t)errorBufferCapacity, "%s", message.UTF8String ?: "Unknown error");
}

static NSString *AnsightSessionVideoWriterError(AVAssetWriter *writer, NSString *fallback)
{
    return writer.error == nil
        ? fallback
        : [NSString stringWithFormat:@"%@: %@", fallback, writer.error.localizedDescription];
}

@interface AnsightSessionVideoMacEncoder : NSObject

@property(nonatomic, strong) AVAssetWriter *writer;
@property(nonatomic, strong) AVAssetWriterInput *writerInput;
@property(nonatomic, strong) AVAssetWriterInputPixelBufferAdaptor *adaptor;
@property(nonatomic, assign) int32_t width;
@property(nonatomic, assign) int32_t height;
@property(nonatomic, assign) BOOL finished;

@end

@implementation AnsightSessionVideoMacEncoder
@end

static CVPixelBufferRef AnsightSessionVideoCreatePixelBuffer(
    AnsightSessionVideoMacEncoder *encoder,
    const char *sourceFilePath,
    NSString **error)
{
    NSString *sourcePath = [NSString stringWithUTF8String:sourceFilePath];
    if (sourcePath.length == 0)
    {
        *error = @"The screenshot path is empty.";
        return nullptr;
    }

    NSURL *sourceUrl = [NSURL fileURLWithPath:sourcePath];
    CGImageSourceRef imageSource = CGImageSourceCreateWithURL((__bridge CFURLRef)sourceUrl, nullptr);
    if (imageSource == nullptr)
    {
        *error = [NSString stringWithFormat:@"Unable to read screenshot '%@'.", sourcePath];
        return nullptr;
    }

    CGImageRef image = CGImageSourceCreateImageAtIndex(imageSource, 0, nullptr);
    CFRelease(imageSource);
    if (image == nullptr)
    {
        *error = [NSString stringWithFormat:@"Unable to decode screenshot '%@'.", sourcePath];
        return nullptr;
    }

    NSDictionary *attributes = @{
        (__bridge NSString *)kCVPixelBufferCGImageCompatibilityKey : @YES,
        (__bridge NSString *)kCVPixelBufferCGBitmapContextCompatibilityKey : @YES,
        (__bridge NSString *)kCVPixelBufferIOSurfacePropertiesKey : @{}
    };
    CVPixelBufferRef pixelBuffer = nullptr;
    CVReturn createResult = CVPixelBufferCreate(
        kCFAllocatorDefault,
        encoder.width,
        encoder.height,
        kCVPixelFormatType_32BGRA,
        (__bridge CFDictionaryRef)attributes,
        &pixelBuffer);
    if (createResult != kCVReturnSuccess || pixelBuffer == nullptr)
    {
        CGImageRelease(image);
        *error = [NSString stringWithFormat:@"Unable to allocate a video frame buffer (%d).", createResult];
        return nullptr;
    }

    CVPixelBufferLockBaseAddress(pixelBuffer, 0);
    void *baseAddress = CVPixelBufferGetBaseAddress(pixelBuffer);
    size_t bytesPerRow = CVPixelBufferGetBytesPerRow(pixelBuffer);
    CGColorSpaceRef colorSpace = CGColorSpaceCreateDeviceRGB();
    CGContextRef context = CGBitmapContextCreate(
        baseAddress,
        (size_t)encoder.width,
        (size_t)encoder.height,
        8,
        bytesPerRow,
        colorSpace,
        kCGBitmapByteOrder32Little | kCGImageAlphaPremultipliedFirst);
    CGColorSpaceRelease(colorSpace);
    if (context == nullptr)
    {
        CVPixelBufferUnlockBaseAddress(pixelBuffer, 0);
        CVPixelBufferRelease(pixelBuffer);
        CGImageRelease(image);
        *error = @"Unable to create a bitmap context for the video frame.";
        return nullptr;
    }

    CGContextSetRGBFillColor(context, 0, 0, 0, 1);
    CGContextFillRect(context, CGRectMake(0, 0, encoder.width, encoder.height));
    CGContextTranslateCTM(context, 0, encoder.height);
    CGContextScaleCTM(context, 1, -1);
    CGContextSetInterpolationQuality(context, kCGInterpolationHigh);
    CGContextDrawImage(context, CGRectMake(0, 0, encoder.width, encoder.height), image);
    CGContextRelease(context);
    CVPixelBufferUnlockBaseAddress(pixelBuffer, 0);
    CGImageRelease(image);
    return pixelBuffer;
}

void *AnsightSessionVideoMac_Create(
    const char *outputFilePath,
    int32_t width,
    int32_t height,
    int32_t averageBitRate,
    char *errorBuffer,
    int32_t errorBufferCapacity)
{
    @autoreleasepool
    {
        if (outputFilePath == nullptr || width <= 0 || height <= 0 || averageBitRate <= 0)
        {
            AnsightSessionVideoWriteError(@"The native encoder arguments are invalid.", errorBuffer, errorBufferCapacity);
            return nullptr;
        }

        @try
        {
            NSString *outputPath = [NSString stringWithUTF8String:outputFilePath];
            NSURL *outputUrl = [NSURL fileURLWithPath:outputPath];
            NSError *creationError = nil;
            AVAssetWriter *writer = [[AVAssetWriter alloc]
                initWithURL:outputUrl
                fileType:AVFileTypeMPEG4
                error:&creationError];
            if (writer == nil)
            {
                AnsightSessionVideoWriteError(
                    creationError.localizedDescription ?: @"AVAssetWriter could not be created.",
                    errorBuffer,
                    errorBufferCapacity);
                return nullptr;
            }

            NSDictionary *compressionSettings = @{
                AVVideoAverageBitRateKey : @(averageBitRate),
                AVVideoMaxKeyFrameIntervalKey : @120,
                AVVideoMaxKeyFrameIntervalDurationKey : @5,
                AVVideoAllowFrameReorderingKey : @NO,
                AVVideoProfileLevelKey : AVVideoProfileLevelH264HighAutoLevel
            };
            NSDictionary *outputSettings = @{
                AVVideoCodecKey : AVVideoCodecTypeH264,
                AVVideoWidthKey : @(width),
                AVVideoHeightKey : @(height),
                AVVideoCompressionPropertiesKey : compressionSettings
            };
            AVAssetWriterInput *writerInput = [AVAssetWriterInput
                assetWriterInputWithMediaType:AVMediaTypeVideo
                outputSettings:outputSettings];
            writerInput.expectsMediaDataInRealTime = NO;
            writerInput.mediaTimeScale = AnsightSessionVideoTimescale;
            if (![writer canAddInput:writerInput])
            {
                AnsightSessionVideoWriteError(
                    @"AVAssetWriter rejected the H.264 video input configuration.",
                    errorBuffer,
                    errorBufferCapacity);
                return nullptr;
            }

            NSDictionary *pixelBufferAttributes = @{
                (__bridge NSString *)kCVPixelBufferPixelFormatTypeKey : @(kCVPixelFormatType_32BGRA),
                (__bridge NSString *)kCVPixelBufferWidthKey : @(width),
                (__bridge NSString *)kCVPixelBufferHeightKey : @(height),
                (__bridge NSString *)kCVPixelBufferCGBitmapContextCompatibilityKey : @YES,
                (__bridge NSString *)kCVPixelBufferCGImageCompatibilityKey : @YES,
                (__bridge NSString *)kCVPixelBufferIOSurfacePropertiesKey : @{}
            };
            AVAssetWriterInputPixelBufferAdaptor *adaptor = [AVAssetWriterInputPixelBufferAdaptor
                assetWriterInputPixelBufferAdaptorWithAssetWriterInput:writerInput
                sourcePixelBufferAttributes:pixelBufferAttributes];
            [writer addInput:writerInput];
            writer.shouldOptimizeForNetworkUse = YES;
            writer.movieTimeScale = AnsightSessionVideoTimescale;
            if (![writer startWriting])
            {
                AnsightSessionVideoWriteError(
                    AnsightSessionVideoWriterError(writer, @"AVAssetWriter could not start writing."),
                    errorBuffer,
                    errorBufferCapacity);
                return nullptr;
            }

            [writer startSessionAtSourceTime:CMTimeMake(0, AnsightSessionVideoTimescale)];
            AnsightSessionVideoMacEncoder *encoder = [AnsightSessionVideoMacEncoder new];
            encoder.writer = writer;
            encoder.writerInput = writerInput;
            encoder.adaptor = adaptor;
            encoder.width = width;
            encoder.height = height;
            return (__bridge_retained void *)encoder;
        }
        @catch (NSException *exception)
        {
            AnsightSessionVideoWriteError(exception.reason, errorBuffer, errorBufferCapacity);
            return nullptr;
        }
    }
}

int32_t AnsightSessionVideoMac_AppendFrame(
    void *encoderHandle,
    const char *sourceFilePath,
    int64_t presentationTimeUs,
    char *errorBuffer,
    int32_t errorBufferCapacity)
{
    @autoreleasepool
    {
        if (encoderHandle == nullptr || sourceFilePath == nullptr || presentationTimeUs < 0)
        {
            AnsightSessionVideoWriteError(@"The native frame arguments are invalid.", errorBuffer, errorBufferCapacity);
            return 0;
        }

        @try
        {
            AnsightSessionVideoMacEncoder *encoder = (__bridge AnsightSessionVideoMacEncoder *)encoderHandle;
            for (int32_t attempt = 0; !encoder.writerInput.readyForMoreMediaData; attempt++)
            {
                if (encoder.writer.status == AVAssetWriterStatusFailed ||
                    encoder.writer.status == AVAssetWriterStatusCancelled)
                {
                    AnsightSessionVideoWriteError(
                        AnsightSessionVideoWriterError(encoder.writer, @"AVAssetWriter stopped accepting video frames."),
                        errorBuffer,
                        errorBufferCapacity);
                    return 0;
                }
                if (attempt >= 15000)
                {
                    AnsightSessionVideoWriteError(
                        @"AVAssetWriter timed out waiting for the next video frame.",
                        errorBuffer,
                        errorBufferCapacity);
                    return 0;
                }

                usleep(2000);
            }

            NSString *pixelBufferError = nil;
            CVPixelBufferRef pixelBuffer = AnsightSessionVideoCreatePixelBuffer(
                encoder,
                sourceFilePath,
                &pixelBufferError);
            if (pixelBuffer == nullptr)
            {
                AnsightSessionVideoWriteError(pixelBufferError, errorBuffer, errorBufferCapacity);
                return 0;
            }

            BOOL appended = [encoder.adaptor
                appendPixelBuffer:pixelBuffer
                withPresentationTime:CMTimeMake(presentationTimeUs, AnsightSessionVideoTimescale)];
            CVPixelBufferRelease(pixelBuffer);
            if (!appended)
            {
                AnsightSessionVideoWriteError(
                    AnsightSessionVideoWriterError(encoder.writer, @"AVAssetWriter rejected the video frame."),
                    errorBuffer,
                    errorBufferCapacity);
                return 0;
            }

            return 1;
        }
        @catch (NSException *exception)
        {
            AnsightSessionVideoWriteError(exception.reason, errorBuffer, errorBufferCapacity);
            return 0;
        }
    }
}

int32_t AnsightSessionVideoMac_Finish(
    void *encoderHandle,
    int64_t endTimeUs,
    char *errorBuffer,
    int32_t errorBufferCapacity)
{
    @autoreleasepool
    {
        if (encoderHandle == nullptr || endTimeUs <= 0)
        {
            AnsightSessionVideoWriteError(@"The native finish arguments are invalid.", errorBuffer, errorBufferCapacity);
            return 0;
        }

        @try
        {
            AnsightSessionVideoMacEncoder *encoder = (__bridge AnsightSessionVideoMacEncoder *)encoderHandle;
            [encoder.writerInput markAsFinished];
            [encoder.writer endSessionAtSourceTime:CMTimeMake(endTimeUs, AnsightSessionVideoTimescale)];
            dispatch_semaphore_t completion = dispatch_semaphore_create(0);
            [encoder.writer finishWritingWithCompletionHandler:^{
                dispatch_semaphore_signal(completion);
            }];
            dispatch_semaphore_wait(completion, DISPATCH_TIME_FOREVER);
            encoder.finished = YES;
            if (encoder.writer.status != AVAssetWriterStatusCompleted)
            {
                AnsightSessionVideoWriteError(
                    AnsightSessionVideoWriterError(encoder.writer, @"AVAssetWriter did not complete the H.264 file."),
                    errorBuffer,
                    errorBufferCapacity);
                return 0;
            }

            return 1;
        }
        @catch (NSException *exception)
        {
            AnsightSessionVideoWriteError(exception.reason, errorBuffer, errorBufferCapacity);
            return 0;
        }
    }
}

void AnsightSessionVideoMac_Destroy(void *encoderHandle)
{
    @autoreleasepool
    {
        if (encoderHandle == nullptr)
        {
            return;
        }

        AnsightSessionVideoMacEncoder *encoder = (__bridge_transfer AnsightSessionVideoMacEncoder *)encoderHandle;
        if (!encoder.finished && encoder.writer.status == AVAssetWriterStatusWriting)
        {
            [encoder.writer cancelWriting];
        }
    }
}

// The composition reader normalizes variable/duplicate Simulator timestamps while
// retaining elapsed time (including a held final frame). No screenshot sampling.
int32_t AnsightSessionVideoMac_Transcode(
    const char *sourceFilePath, const char *outputFilePath,
    int32_t maxDimension, int32_t framesPerSecond, int32_t bitRate, int64_t durationUs,
    char *errorBuffer, int32_t errorBufferCapacity)
{
    @autoreleasepool
    {
        AVAssetReader *reader = nil;
        AVAssetWriter *writer = nil;
        @try
        {
            if (!sourceFilePath || !outputFilePath || maxDimension < 2 || framesPerSecond < 1 || bitRate < 1 || durationUs <= 0)
                @throw [NSException exceptionWithName:@"Video" reason:@"Invalid compression arguments." userInfo:nil];
            AVURLAsset *asset = [AVURLAsset URLAssetWithURL:[NSURL fileURLWithPath:[NSString stringWithUTF8String:sourceFilePath]] options:nil];
            AVAssetTrack *track = [[asset tracksWithMediaType:AVMediaTypeVideo] firstObject];
            if (!track || !CMTIME_IS_NUMERIC(asset.duration) || CMTimeGetSeconds(asset.duration) <= 0)
                @throw [NSException exceptionWithName:@"Video" reason:@"The recording has no video track." userInfo:nil];
            CMTime duration = CMTimeMake(durationUs, AnsightSessionVideoTimescale);
            CGRect bounds = CGRectApplyAffineTransform((CGRect){CGPointZero, track.naturalSize}, track.preferredTransform);
            CGFloat scale = MIN(1.0, maxDimension / MAX(bounds.size.width, bounds.size.height));
            int width = MAX(2, ((int)(bounds.size.width * scale)) & ~1);
            int height = MAX(2, ((int)(bounds.size.height * scale)) & ~1);
            AVMutableVideoComposition *composition = [AVMutableVideoComposition videoComposition];
            composition.renderSize = CGSizeMake(width, height);
            composition.frameDuration = CMTimeMake(1, framesPerSecond);
            AVMutableVideoCompositionInstruction *instruction = [AVMutableVideoCompositionInstruction videoCompositionInstruction];
            instruction.timeRange = CMTimeRangeMake(kCMTimeZero, duration);
            AVMutableVideoCompositionLayerInstruction *layer = [AVMutableVideoCompositionLayerInstruction videoCompositionLayerInstructionWithAssetTrack:track];
            CGAffineTransform transform = CGAffineTransformConcat(track.preferredTransform,
                CGAffineTransformMakeTranslation(-bounds.origin.x, -bounds.origin.y));
            transform = CGAffineTransformConcat(transform, CGAffineTransformMakeScale(width / bounds.size.width, height / bounds.size.height));
            [layer setTransform:transform atTime:kCMTimeZero];
            instruction.layerInstructions = @[layer];
            composition.instructions = @[instruction];
            NSError *error = nil;
            reader = [[AVAssetReader alloc] initWithAsset:asset error:&error];
            AVAssetReaderVideoCompositionOutput *output = [AVAssetReaderVideoCompositionOutput
                assetReaderVideoCompositionOutputWithVideoTracks:@[track]
                videoSettings:@{(__bridge NSString *)kCVPixelBufferPixelFormatTypeKey: @(kCVPixelFormatType_32BGRA)}];
            output.videoComposition = composition;
            output.alwaysCopiesSampleData = NO;
            reader.timeRange = CMTimeRangeMake(kCMTimeZero, duration);
            [reader addOutput:output];
            writer = [[AVAssetWriter alloc] initWithURL:[NSURL fileURLWithPath:[NSString stringWithUTF8String:outputFilePath]]
                fileType:AVFileTypeMPEG4 error:&error];
            NSDictionary *settings = @{
                AVVideoCodecKey: AVVideoCodecTypeH264, AVVideoWidthKey: @(width), AVVideoHeightKey: @(height),
                AVVideoCompressionPropertiesKey: @{
                    AVVideoAverageBitRateKey: @(bitRate), AVVideoExpectedSourceFrameRateKey: @(framesPerSecond),
                    AVVideoMaxKeyFrameIntervalDurationKey: @2, AVVideoAllowFrameReorderingKey: @NO,
                    AVVideoProfileLevelKey: AVVideoProfileLevelH264HighAutoLevel
                }
            };
            AVAssetWriterInput *input = [AVAssetWriterInput assetWriterInputWithMediaType:AVMediaTypeVideo outputSettings:settings];
            input.expectsMediaDataInRealTime = NO;
            input.mediaTimeScale = AnsightSessionVideoTimescale;
            [writer addInput:input];
            writer.shouldOptimizeForNetworkUse = YES;
            if (!reader || !writer || ![reader startReading] || ![writer startWriting])
                @throw [NSException exceptionWithName:@"Video" reason:(reader.error ?: writer.error ?: error).localizedDescription userInfo:nil];
            [writer startSessionAtSourceTime:kCMTimeZero];
            int count = 0;
            CMSampleBufferRef lastSample = nullptr;
            CMSampleBufferRef sample;
            while ((sample = [output copyNextSampleBuffer]))
            {
                @autoreleasepool
                {
                    int attempt = 0;
                    while (!input.readyForMoreMediaData && writer.status == AVAssetWriterStatusWriting && attempt++ < 15000)
                        usleep(2000);
                    BOOL appended = input.readyForMoreMediaData && [input appendSampleBuffer:sample];
                    if (lastSample) CFRelease(lastSample);
                    lastSample = (CMSampleBufferRef)CFRetain(sample);
                    CFRelease(sample);
                    if (!appended)
                        @throw [NSException exceptionWithName:@"Video" reason:AnsightSessionVideoWriterError(writer, @"Timed out encoding recording.") userInfo:nil];
                    count++;
                }
            }
            if (reader.status != AVAssetReaderStatusCompleted || count == 0)
            {
                if (lastSample) CFRelease(lastSample);
                @throw [NSException exceptionWithName:@"Video" reason:reader.error.localizedDescription ?: @"No frames decoded." userInfo:nil];
            }
            // Display recorders may emit nothing during the final idle period. The
            // writer does not extend its last sample merely from endSessionAtSourceTime.
            CMTime finalTime = CMTimeSubtract(duration, composition.frameDuration);
            if (CMTimeCompare(finalTime, CMSampleBufferGetPresentationTimeStamp(lastSample)) > 0)
            {
                CMSampleTimingInfo timing = {CMTimeSubtract(duration, finalTime), finalTime, kCMTimeInvalid};
                CMSampleBufferRef heldSample = nullptr;
                OSStatus copyStatus = CMSampleBufferCreateCopyWithNewTiming(kCFAllocatorDefault, lastSample, 1, &timing, &heldSample);
                int attempt = 0;
                while (!input.readyForMoreMediaData && writer.status == AVAssetWriterStatusWriting && attempt++ < 15000)
                    usleep(2000);
                BOOL appended = copyStatus == noErr && heldSample && input.readyForMoreMediaData && [input appendSampleBuffer:heldSample];
                if (heldSample) CFRelease(heldSample);
                if (!appended)
                {
                    CFRelease(lastSample);
                    @throw [NSException exceptionWithName:@"Video" reason:@"Could not retain the final idle frame." userInfo:nil];
                }
            }
            CFRelease(lastSample);
            [input markAsFinished];
            [writer endSessionAtSourceTime:duration];
            dispatch_semaphore_t done = dispatch_semaphore_create(0);
            [writer finishWritingWithCompletionHandler:^{ dispatch_semaphore_signal(done); }];
            if (dispatch_semaphore_wait(done, dispatch_time(DISPATCH_TIME_NOW, 60 * NSEC_PER_SEC)) != 0 || writer.status != AVAssetWriterStatusCompleted)
                @throw [NSException exceptionWithName:@"Video" reason:AnsightSessionVideoWriterError(writer, @"Timed out finalizing compressed video.") userInfo:nil];
            return 1;
        }
        @catch (NSException *exception)
        {
            [reader cancelReading];
            [writer cancelWriting];
            AnsightSessionVideoWriteError(exception.reason, errorBuffer, errorBufferCapacity);
            return 0;
        }
    }
}

int32_t AnsightSessionVideoMac_ExtractFrame(
    const char *videoPath, const char *imagePath, int64_t positionUs, int32_t maxDimension,
    char *errorBuffer, int32_t errorBufferCapacity)
{
    @autoreleasepool
    {
        AVURLAsset *asset = [AVURLAsset URLAssetWithURL:[NSURL fileURLWithPath:[NSString stringWithUTF8String:videoPath]] options:nil];
        AVAssetImageGenerator *generator = [AVAssetImageGenerator assetImageGeneratorWithAsset:asset];
        generator.appliesPreferredTrackTransform = YES;
        generator.maximumSize = CGSizeMake(maxDimension, maxDimension);
        generator.requestedTimeToleranceBefore = kCMTimeZero;
        generator.requestedTimeToleranceAfter = kCMTimeZero;
        NSError *error = nil;
        CGImageRef frame = [generator copyCGImageAtTime:CMTimeMake(positionUs, AnsightSessionVideoTimescale)
                                            actualTime:nullptr error:&error];
        if (frame == nullptr)
        {
            AnsightSessionVideoWriteError(error.localizedDescription ?: @"Unable to decode video frame.", errorBuffer, errorBufferCapacity);
            return 0;
        }
        NSURL *output = [NSURL fileURLWithPath:[NSString stringWithUTF8String:imagePath]];
        CGImageDestinationRef destination = CGImageDestinationCreateWithURL((__bridge CFURLRef)output, CFSTR("public.jpeg"), 1, nullptr);
        BOOL success = NO;
        if (destination != nullptr)
        {
            NSDictionary *options = @{(__bridge NSString *)kCGImageDestinationLossyCompressionQuality: @0.78};
            CGImageDestinationAddImage(destination, frame, (__bridge CFDictionaryRef)options);
            success = CGImageDestinationFinalize(destination);
            CFRelease(destination);
        }
        CGImageRelease(frame);
        if (!success) AnsightSessionVideoWriteError(@"Unable to write the extracted video frame.", errorBuffer, errorBufferCapacity);
        return success ? 1 : 0;
    }
}
