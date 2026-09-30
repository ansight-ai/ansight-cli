#import "AnsightSimulatorRtc.h"

#import <Foundation/Foundation.h>
#import <AVFoundation/AVFoundation.h>
#import <CoreMedia/CoreMedia.h>
#import <CoreVideo/CoreVideo.h>
#import <CoreImage/CoreImage.h>
#import <IOSurface/IOSurface.h>
#import <Metal/Metal.h>
#import <QuartzCore/CAMetalLayer.h>
#import <ScreenCaptureKit/ScreenCaptureKit.h>
#import <VideoToolbox/VideoToolbox.h>
#import <arpa/inet.h>
#import <dlfcn.h>
#import <objc/message.h>
#import <objc/runtime.h>
#import <rtc/rtc.h>
#import <time.h>

static const uint32_t AnsightH264ClockRate = 90000;
static const int64_t AnsightMaximumVideoBufferDurationMilliseconds = 200;
static const int32_t AnsightMinimumVideoBufferBytes = 64 * 1024;
static const NSInteger AnsightMacWindowDiscoveryMaximumAttemptCount = 20;
static const int64_t AnsightMacWindowDiscoveryRetryNanoseconds = 250 * NSEC_PER_MSEC;
static const void *AnsightScreenQueueKey = &AnsightScreenQueueKey;
static const void *AnsightPreviewScreenQueueKey = &AnsightPreviewScreenQueueKey;
static const void *AnsightPreviewRenderQueueKey = &AnsightPreviewRenderQueueKey;
static const void *AnsightMacPreviewQueueKey = &AnsightMacPreviewQueueKey;

typedef NS_ENUM(NSInteger, AnsightSimulatorRtcErrorCode)
{
    AnsightSimulatorRtcErrorRuntimeCoreSimulatorUnavailable = 1,
    AnsightSimulatorRtcErrorRuntimeSimulatorKitUnavailable = 2,
    AnsightSimulatorRtcErrorRuntimeServiceContextUnavailable = 3,
    AnsightSimulatorRtcErrorRuntimeDeviceNotFound = 4,
    AnsightSimulatorRtcErrorPeerConnectionCreationFailed = 5,
    AnsightSimulatorRtcErrorDisplayIoUnavailable = 9,
    AnsightSimulatorRtcErrorDisplayIoConnectionFailed = 10,
    AnsightSimulatorRtcErrorPrimaryLcdUnavailable = 11,
    AnsightSimulatorRtcErrorFrameCallbackRegistrationFailed = 12,
    AnsightSimulatorRtcErrorAnswerUnavailable = 13,
    AnsightSimulatorRtcErrorAnswerBufferTooSmall = 14,
    AnsightSimulatorRtcErrorOfferMissing = 15,
    AnsightSimulatorRtcErrorOfferApplicationFailed = 16,
    AnsightSimulatorRtcErrorExternalAccessUnitMissing = 20,
    AnsightSimulatorRtcErrorExternalSessionShuttingDown = 21,
    AnsightSimulatorRtcErrorExternalFrameSendFailed = 22,
    AnsightSimulatorRtcErrorDataChannelMessageMissing = 23,
    AnsightSimulatorRtcErrorDataChannelMessageSendFailed = 24,
    AnsightSimulatorRtcErrorExternalH264DisplayLayerUnavailable = 30,
    AnsightSimulatorRtcErrorExternalH264AccessUnitEmpty = 31,
    AnsightSimulatorRtcErrorExternalH264DecodeFailed = 32,
    AnsightSimulatorRtcErrorMacWindowCaptureUnsupported = 40,
    AnsightSimulatorRtcErrorMacWindowPreviewMetalUnavailable = 50,
    AnsightSimulatorRtcErrorMacWindowPreviewUnsupported = 51,
    AnsightSimulatorRtcErrorPreviewMetalUnavailable = 60,
    AnsightSimulatorRtcErrorPreviewCoreSimulatorUnavailable = 61,
    AnsightSimulatorRtcErrorPreviewSimulatorKitUnavailable = 62,
    AnsightSimulatorRtcErrorPreviewServiceContextUnavailable = 63,
    AnsightSimulatorRtcErrorPreviewDeviceNotFound = 64,
    AnsightSimulatorRtcErrorPreviewDisplayIoUnavailable = 65,
    AnsightSimulatorRtcErrorPreviewDisplayIoConnectionFailed = 66,
    AnsightSimulatorRtcErrorPreviewPrimaryLcdUnavailable = 67,
    AnsightSimulatorRtcErrorPreviewCallbackRegistrationFailed = 68,
};

@class AnsightSimulatorRtcSession;

static void AnsightRtcDescriptionCallback(int peerConnection, const char *sdp, const char *type, void *pointer);
static void AnsightRtcStateCallback(int peerConnection, rtcState state, void *pointer);
static void AnsightRtcGatheringCallback(int peerConnection, rtcGatheringState state, void *pointer);
static void AnsightRtcTrackCallback(int peerConnection, int track, void *pointer);
static void AnsightRtcDataChannelCallback(int peerConnection, int dataChannel, void *pointer);
static void AnsightRtcErrorCallback(int identifier, const char *message, void *pointer);
static void AnsightRtcInputCallback(int dataChannel, const char *message, int size, void *pointer);
static void AnsightRtcBufferedAmountLowCallback(int track, void *pointer);
static void AnsightRtcPliCallback(int track, void *pointer);
static void AnsightVideoCompressionCallback(
    void *outputCallbackRefCon,
    void *sourceFrameRefCon,
    OSStatus status,
    VTEncodeInfoFlags infoFlags,
    CMSampleBufferRef sampleBuffer);

static id SendId(id receiver, SEL selector)
{
    return ((id (*)(id, SEL))objc_msgSend)(receiver, selector);
}

static id SendIdWithObjectAndError(id receiver, SEL selector, id value, NSError **error)
{
    return ((id (*)(id, SEL, id, NSError **))objc_msgSend)(receiver, selector, value, error);
}

static id SendIdWithError(id receiver, SEL selector, NSError **error)
{
    return ((id (*)(id, SEL, NSError **))objc_msgSend)(receiver, selector, error);
}

static NSInteger SendInteger(id receiver, SEL selector)
{
    return ((NSInteger (*)(id, SEL))objc_msgSend)(receiver, selector);
}

static id SendDeviceQueueHandler(id receiver, SEL selector, id device, id queue, id handler)
{
    return ((id (*)(id, SEL, id, id, id))objc_msgSend)(receiver, selector, device, queue, handler);
}

static NSError *CreateError(NSInteger code, NSString *message)
{
    return [NSError errorWithDomain:@"AnsightSimulatorRtc"
                               code:code
                           userInfo:@{NSLocalizedDescriptionKey: message ?: @"Unknown Simulator WebRTC error."}];
}

static void CopyText(NSString *text, char *buffer, size_t capacity)
{
    if (buffer == NULL || capacity == 0)
    {
        return;
    }

    const char *utf8 = (text ?: @"").UTF8String;
    snprintf(buffer, capacity, "%s", utf8 == NULL ? "" : utf8);
}

static NSString *RtcFailure(NSString *operation, int result)
{
    return [NSString stringWithFormat:@"%@ failed with libdatachannel error %d.", operation, result];
}

typedef CFArrayRef (*AnsightCGWindowListCopyWindowInfoFunction)(uint32_t option, uint32_t relativeToWindow);
typedef CFTypeRef (*AnsightCGEventCreateMouseEventFunction)(
    CFTypeRef source,
    uint32_t mouseType,
    CGPoint mouseCursorPosition,
    uint32_t mouseButton);
typedef CFTypeRef (*AnsightCGEventCreateScrollWheelEventFunction)(
    CFTypeRef source,
    uint32_t units,
    uint32_t wheelCount,
    int32_t wheel1,
    int32_t wheel2,
    int32_t wheel3);
typedef void (*AnsightCGEventSetLocationFunction)(CFTypeRef event, CGPoint location);
typedef CFTypeRef (*AnsightCGEventCreateKeyboardEventFunction)(
    CFTypeRef source,
    uint16_t virtualKey,
    bool keyDown);
typedef void (*AnsightCGEventKeyboardSetUnicodeStringFunction)(
    CFTypeRef event,
    size_t stringLength,
    const uint16_t *unicodeString);
typedef void (*AnsightCGEventPostToPidFunction)(pid_t processIdentifier, CFTypeRef event);
typedef bool (*AnsightScreenCaptureAccessFunction)(void);
typedef Boolean (*AnsightAXIsProcessTrustedWithOptionsFunction)(CFDictionaryRef options);

static void *AnsightCoreGraphicsHandle(void)
{
    static void *handle = NULL;
    static dispatch_once_t once;
    dispatch_once(&once, ^{
        handle = dlopen(
            "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics",
            RTLD_LAZY | RTLD_LOCAL);
    });
    return handle;
}

static void *AnsightApplicationServicesHandle(void)
{
    static void *handle = NULL;
    static dispatch_once_t once;
    dispatch_once(&once, ^{
        handle = dlopen(
            "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices",
            RTLD_LAZY | RTLD_LOCAL);
    });
    return handle;
}

static bool AnsightRequestScreenCaptureAccessIfNeeded(void)
{
    void *handle = AnsightCoreGraphicsHandle();
    AnsightScreenCaptureAccessFunction preflightScreenCaptureAccess = handle == NULL
        ? NULL
        : (AnsightScreenCaptureAccessFunction)dlsym(handle, "CGPreflightScreenCaptureAccess");
    if (preflightScreenCaptureAccess == NULL || preflightScreenCaptureAccess())
    {
        return true;
    }

    AnsightScreenCaptureAccessFunction requestScreenCaptureAccess =
        (AnsightScreenCaptureAccessFunction)dlsym(handle, "CGRequestScreenCaptureAccess");
    return requestScreenCaptureAccess != NULL && requestScreenCaptureAccess();
}

static bool AnsightHasScreenCaptureAccess(void)
{
    void *handle = AnsightCoreGraphicsHandle();
    AnsightScreenCaptureAccessFunction preflightScreenCaptureAccess = handle == NULL
        ? NULL
        : (AnsightScreenCaptureAccessFunction)dlsym(handle, "CGPreflightScreenCaptureAccess");
    return preflightScreenCaptureAccess != NULL && preflightScreenCaptureAccess();
}

static NSMutableDictionary<NSNumber *, NSNumber *> *AnsightCapturedWindowIdentifiers(void)
{
    static NSMutableDictionary<NSNumber *, NSNumber *> *identifiers = nil;
    static dispatch_once_t once;
    dispatch_once(&once, ^{
        identifiers = [NSMutableDictionary dictionary];
    });
    return identifiers;
}

static void RememberCapturedWindowIdentifier(pid_t processIdentifier, uint32_t windowIdentifier)
{
    if (processIdentifier <= 0 || windowIdentifier == 0)
    {
        return;
    }

    NSMutableDictionary<NSNumber *, NSNumber *> *identifiers = AnsightCapturedWindowIdentifiers();
    @synchronized (identifiers)
    {
        identifiers[@(processIdentifier)] = @(windowIdentifier);
    }
}

static uint32_t CapturedWindowIdentifier(pid_t processIdentifier)
{
    NSMutableDictionary<NSNumber *, NSNumber *> *identifiers = AnsightCapturedWindowIdentifiers();
    @synchronized (identifiers)
    {
        return identifiers[@(processIdentifier)].unsignedIntValue;
    }
}

static BOOL FindLargestVisibleWindowBounds(pid_t processIdentifier, CGRect *bounds)
{
    void *handle = AnsightCoreGraphicsHandle();
    AnsightCGWindowListCopyWindowInfoFunction copyWindowInfo = handle == NULL
        ? NULL
        : (AnsightCGWindowListCopyWindowInfoFunction)dlsym(handle, "CGWindowListCopyWindowInfo");
    if (copyWindowInfo == NULL || processIdentifier <= 0 || bounds == NULL)
    {
        return NO;
    }

    // kCGWindowListOptionOnScreenOnly | kCGWindowListExcludeDesktopElements.
    CFArrayRef windowInfo = copyWindowInfo(1u | 16u, 0u);
    if (windowInfo == NULL)
    {
        return NO;
    }

    BOOL found = NO;
    double largestArea = 0;
    uint32_t preferredWindowIdentifier = CapturedWindowIdentifier(processIdentifier);
    for (NSDictionary *entry in (__bridge NSArray *)windowInfo)
    {
        NSNumber *owner = entry[@"kCGWindowOwnerPID"];
        NSNumber *windowIdentifier = entry[@"kCGWindowNumber"];
        NSNumber *layer = entry[@"kCGWindowLayer"];
        NSNumber *alpha = entry[@"kCGWindowAlpha"];
        NSDictionary *encodedBounds = entry[@"kCGWindowBounds"];
        if (owner.intValue != processIdentifier
            || layer.integerValue != 0
            || alpha.doubleValue <= 0
            || ![encodedBounds isKindOfClass:NSDictionary.class])
        {
            continue;
        }

        CGRect candidate = CGRectMake(
            [encodedBounds[@"X"] doubleValue],
            [encodedBounds[@"Y"] doubleValue],
            [encodedBounds[@"Width"] doubleValue],
            [encodedBounds[@"Height"] doubleValue]);
        double area = candidate.size.width * candidate.size.height;
        if (candidate.size.width < 32 || candidate.size.height < 32)
        {
            continue;
        }
        if (preferredWindowIdentifier != 0
            && windowIdentifier.unsignedIntValue == preferredWindowIdentifier)
        {
            *bounds = candidate;
            found = YES;
            break;
        }
        if (area > largestArea)
        {
            *bounds = candidate;
            largestArea = area;
            found = YES;
        }
    }
    CFRelease(windowInfo);
    return found;
}

static uint16_t MacVirtualKeyForHidUsage(uint32_t usageCode)
{
    static const uint16_t letterKeys[] = {
        0x00, 0x0B, 0x08, 0x02, 0x0E, 0x03, 0x05, 0x04, 0x22, 0x26, 0x28, 0x25, 0x2E,
        0x2D, 0x1F, 0x23, 0x0C, 0x0F, 0x01, 0x11, 0x20, 0x09, 0x0D, 0x07, 0x10, 0x06,
    };
    static const uint16_t numberKeys[] = {0x12, 0x13, 0x14, 0x15, 0x17, 0x16, 0x1A, 0x1C, 0x19, 0x1D};
    if (usageCode >= 4 && usageCode <= 29)
    {
        return letterKeys[usageCode - 4];
    }
    if (usageCode >= 30 && usageCode <= 39)
    {
        return numberKeys[usageCode - 30];
    }

    switch (usageCode)
    {
        case 40: return 0x24; // Return
        case 41: return 0x35; // Escape
        case 42: return 0x33; // Delete/backspace
        case 43: return 0x30; // Tab
        case 44: return 0x31; // Space
        case 45: return 0x1B;
        case 46: return 0x18;
        case 47: return 0x21;
        case 48: return 0x1E;
        case 49: return 0x2A;
        case 51: return 0x29;
        case 52: return 0x27;
        case 53: return 0x32;
        case 54: return 0x2B;
        case 55: return 0x2F;
        case 56: return 0x2C;
        case 57: return 0x39;
        case 58: return 0x7A;
        case 59: return 0x78;
        case 60: return 0x63;
        case 61: return 0x76;
        case 62: return 0x60;
        case 63: return 0x61;
        case 64: return 0x62;
        case 65: return 0x64;
        case 66: return 0x65;
        case 67: return 0x6D;
        case 68: return 0x67;
        case 69: return 0x6F;
        case 73: return 0x72; // Help/insert
        case 74: return 0x73; // Home
        case 75: return 0x74; // Page up
        case 76: return 0x75; // Forward delete
        case 77: return 0x77; // End
        case 78: return 0x79; // Page down
        case 79: return 0x7C; // Right
        case 80: return 0x7B; // Left
        case 81: return 0x7D; // Down
        case 82: return 0x7E; // Up
        case 224: return 0x3B;
        case 225: return 0x38;
        case 226: return 0x3A;
        case 227: return 0x37;
        case 228: return 0x3E;
        case 229: return 0x3C;
        case 230: return 0x3D;
        case 231: return 0x36;
        default: return UINT16_MAX;
    }
}

static AnsightCGEventPostToPidFunction MacEventPostFunction(void)
{
    void *handle = AnsightCoreGraphicsHandle();
    return handle == NULL
        ? NULL
        : (AnsightCGEventPostToPidFunction)dlsym(handle, "CGEventPostToPid");
}

static double MonotonicSeconds(void)
{
    struct timespec value = {};
    clock_gettime(CLOCK_MONOTONIC_RAW, &value);
    return (double)value.tv_sec + ((double)value.tv_nsec / 1000000000.0);
}

static int32_t MaximumBufferedVideoBytes(int32_t averageBitRate)
{
    if (averageBitRate <= 0)
    {
        return 256 * 1024;
    }

    int64_t bytesForFreshnessWindow =
        ((int64_t)averageBitRate * AnsightMaximumVideoBufferDurationMilliseconds)
        / (8 * 1000);
    return (int32_t)MAX(AnsightMinimumVideoBufferBytes, bytesForFreshnessWindow);
}

static void InitializeRtcLogger(void)
{
    static dispatch_once_t loggerOnce;
    dispatch_once(&loggerOnce, ^{
        rtcInitLogger(RTC_LOG_WARNING, NULL);
    });
}

static BOOL SampleIsKeyFrame(CMSampleBufferRef sampleBuffer)
{
    CFArrayRef attachments = CMSampleBufferGetSampleAttachmentsArray(sampleBuffer, false);
    if (attachments == NULL || CFArrayGetCount(attachments) == 0)
    {
        return YES;
    }

    CFDictionaryRef attachment = (CFDictionaryRef)CFArrayGetValueAtIndex(attachments, 0);
    return !CFDictionaryContainsKey(attachment, kCMSampleAttachmentKey_NotSync);
}

static NSData *CopyBlockBufferData(CMBlockBufferRef blockBuffer)
{
    size_t lengthAtOffset = 0;
    size_t totalLength = 0;
    char *dataPointer = NULL;
    OSStatus status = CMBlockBufferGetDataPointer(
        blockBuffer,
        0,
        &lengthAtOffset,
        &totalLength,
        &dataPointer);
    if (status == noErr && lengthAtOffset == totalLength && dataPointer != NULL)
    {
        return [NSData dataWithBytes:dataPointer length:totalLength];
    }

    NSMutableData *copy = [NSMutableData dataWithLength:CMBlockBufferGetDataLength(blockBuffer)];
    status = CMBlockBufferCopyDataBytes(blockBuffer, 0, copy.length, copy.mutableBytes);
    return status == noErr ? copy : nil;
}

static NSData *CreateH264AccessUnit(CMSampleBufferRef sampleBuffer)
{
    CMBlockBufferRef blockBuffer = CMSampleBufferGetDataBuffer(sampleBuffer);
    if (blockBuffer == NULL)
    {
        return nil;
    }

    NSData *sampleData = CopyBlockBufferData(blockBuffer);
    if (sampleData == nil || !SampleIsKeyFrame(sampleBuffer))
    {
        return sampleData;
    }

    CMFormatDescriptionRef format = CMSampleBufferGetFormatDescription(sampleBuffer);
    if (format == NULL)
    {
        return sampleData;
    }

    size_t parameterSetCount = 0;
    int nalHeaderLength = 0;
    OSStatus status = CMVideoFormatDescriptionGetH264ParameterSetAtIndex(
        format,
        0,
        NULL,
        NULL,
        &parameterSetCount,
        &nalHeaderLength);
    if (status != noErr || nalHeaderLength != 4 || parameterSetCount == 0)
    {
        return sampleData;
    }

    NSMutableData *accessUnit = [NSMutableData dataWithCapacity:sampleData.length + 128];
    for (size_t index = 0; index < parameterSetCount; index++)
    {
        const uint8_t *parameterSet = NULL;
        size_t parameterSetSize = 0;
        status = CMVideoFormatDescriptionGetH264ParameterSetAtIndex(
            format,
            index,
            &parameterSet,
            &parameterSetSize,
            NULL,
            NULL);
        if (status != noErr || parameterSet == NULL || parameterSetSize == 0 || parameterSetSize > UINT32_MAX)
        {
            return sampleData;
        }

        uint32_t networkLength = htonl((uint32_t)parameterSetSize);
        [accessUnit appendBytes:&networkLength length:sizeof(networkLength)];
        [accessUnit appendBytes:parameterSet length:parameterSetSize];
    }

    [accessUnit appendData:sampleData];
    return accessUnit;
}

static NSUInteger FindH264StartCode(NSData *data, NSUInteger offset, NSUInteger *startCodeLength)
{
    const uint8_t *bytes = (const uint8_t *)data.bytes;
    for (NSUInteger index = offset; index + 3 <= data.length; index++)
    {
        if (bytes[index] != 0 || bytes[index + 1] != 0)
        {
            continue;
        }
        if (bytes[index + 2] == 1)
        {
            *startCodeLength = 3;
            return index;
        }
        if (index + 4 <= data.length && bytes[index + 2] == 0 && bytes[index + 3] == 1)
        {
            *startCodeLength = 4;
            return index;
        }
    }
    return NSNotFound;
}

static NSArray<NSData *> *SplitH264AccessUnit(NSData *accessUnit)
{
    if (accessUnit.length == 0)
    {
        return @[];
    }

    NSUInteger firstStartCodeLength = 0;
    NSUInteger firstStartCode = FindH264StartCode(accessUnit, 0, &firstStartCodeLength);
    NSMutableArray<NSData *> *units = [NSMutableArray array];
    if (firstStartCode != NSNotFound)
    {
        NSUInteger current = firstStartCode;
        NSUInteger currentStartCodeLength = firstStartCodeLength;
        while (current != NSNotFound)
        {
            NSUInteger unitStart = current + currentStartCodeLength;
            NSUInteger nextStartCodeLength = 0;
            NSUInteger next = FindH264StartCode(accessUnit, unitStart, &nextStartCodeLength);
            NSUInteger unitEnd = next == NSNotFound ? accessUnit.length : next;
            if (unitEnd > unitStart)
            {
                [units addObject:[accessUnit subdataWithRange:NSMakeRange(unitStart, unitEnd - unitStart)]];
            }
            current = next;
            currentStartCodeLength = nextStartCodeLength;
        }
        return units;
    }

    const uint8_t *bytes = (const uint8_t *)accessUnit.bytes;
    NSUInteger offset = 0;
    while (offset + sizeof(uint32_t) <= accessUnit.length)
    {
        uint32_t networkLength = 0;
        memcpy(&networkLength, bytes + offset, sizeof(networkLength));
        NSUInteger unitLength = (NSUInteger)ntohl(networkLength);
        offset += sizeof(uint32_t);
        if (unitLength == 0 || unitLength > accessUnit.length - offset)
        {
            [units removeAllObjects];
            break;
        }
        [units addObject:[accessUnit subdataWithRange:NSMakeRange(offset, unitLength)]];
        offset += unitLength;
    }
    if (units.count > 0 && offset == accessUnit.length)
    {
        return units;
    }
    return @[accessUnit];
}

@interface AnsightExternalH264PreviewView : NSObject

@property(nonatomic, strong) AVSampleBufferDisplayLayer *displayLayer;
@property(nonatomic, strong) dispatch_queue_t decodeQueue;
@property(nonatomic, strong) NSData *sequenceParameterSet;
@property(nonatomic, strong) NSData *pictureParameterSet;
@property(nonatomic, assign) CMVideoFormatDescriptionRef formatDescription;
@property(nonatomic, assign) BOOL shuttingDown;

- (instancetype)initWithError:(NSError **)error;
- (BOOL)enqueueAccessUnit:(NSData *)accessUnit
  presentationTimestampUs:(int64_t)presentationTimestampUs
                  keyFrame:(BOOL)keyFrame
                     error:(NSError **)error;
- (void)shutdown;

@end

@implementation AnsightExternalH264PreviewView

- (instancetype)initWithError:(NSError **)error
{
    self = [super init];
    if (self == nil)
    {
        return nil;
    }

    _displayLayer = [AVSampleBufferDisplayLayer layer];
    if (_displayLayer == nil)
    {
        if (error != NULL)
        {
            *error = CreateError(
                AnsightSimulatorRtcErrorExternalH264DisplayLayerUnavailable,
                @"The native Android H.264 display layer could not be created.");
        }
        return nil;
    }
    _displayLayer.videoGravity = AVLayerVideoGravityResizeAspect;
    _displayLayer.backgroundColor = CGColorGetConstantColor(kCGColorBlack);
    _displayLayer.masksToBounds = YES;
    _decodeQueue = dispatch_queue_create("ai.ansight.host.android-h264-preview", DISPATCH_QUEUE_SERIAL);
    return self;
}

- (BOOL)enqueueAccessUnit:(NSData *)accessUnit
  presentationTimestampUs:(int64_t)presentationTimestampUs
                  keyFrame:(BOOL)keyFrame
                     error:(NSError **)error
{
    if (accessUnit.length == 0)
    {
        if (error != NULL)
        {
            *error = CreateError(
                AnsightSimulatorRtcErrorExternalH264AccessUnitEmpty,
                @"scrcpy supplied an empty H.264 access unit.");
        }
        return NO;
    }

    __block BOOL success = YES;
    __block NSString *failure = nil;
    dispatch_sync(self.decodeQueue, ^{
        if (self.shuttingDown)
        {
            success = NO;
            failure = @"The native Android preview has already stopped.";
            return;
        }

        NSArray<NSData *> *units = SplitH264AccessUnit(accessUnit);
        NSMutableData *sampleData = [NSMutableData dataWithCapacity:accessUnit.length];
        BOOL parameterSetsChanged = NO;
        for (NSData *unit in units)
        {
            if (unit.length == 0)
            {
                continue;
            }
            uint8_t unitType = ((const uint8_t *)unit.bytes)[0] & 0x1f;
            if (unitType == 7)
            {
                if (![self.sequenceParameterSet isEqualToData:unit])
                {
                    self.sequenceParameterSet = unit;
                    parameterSetsChanged = YES;
                }
                continue;
            }
            if (unitType == 8)
            {
                if (![self.pictureParameterSet isEqualToData:unit])
                {
                    self.pictureParameterSet = unit;
                    parameterSetsChanged = YES;
                }
                continue;
            }

            if (unit.length > UINT32_MAX)
            {
                success = NO;
                failure = @"scrcpy supplied an H.264 NAL unit that is too large.";
                return;
            }
            uint32_t networkLength = htonl((uint32_t)unit.length);
            [sampleData appendBytes:&networkLength length:sizeof(networkLength)];
            [sampleData appendData:unit];
        }

        if (parameterSetsChanged && self.formatDescription != NULL)
        {
            CFRelease(self.formatDescription);
            self.formatDescription = NULL;
            [self.displayLayer flushAndRemoveImage];
        }
        if (self.formatDescription == NULL
            && self.sequenceParameterSet.length > 0
            && self.pictureParameterSet.length > 0)
        {
            const uint8_t *parameterSets[] = {
                (const uint8_t *)self.sequenceParameterSet.bytes,
                (const uint8_t *)self.pictureParameterSet.bytes
            };
            const size_t parameterSetSizes[] = {
                self.sequenceParameterSet.length,
                self.pictureParameterSet.length
            };
            CMVideoFormatDescriptionRef format = NULL;
            OSStatus formatStatus = CMVideoFormatDescriptionCreateFromH264ParameterSets(
                kCFAllocatorDefault,
                2,
                parameterSets,
                parameterSetSizes,
                4,
                &format);
            if (formatStatus != noErr || format == NULL)
            {
                success = NO;
                failure = [NSString stringWithFormat:
                    @"VideoToolbox rejected scrcpy's H.264 parameter sets (%d).",
                    formatStatus];
                return;
            }
            self.formatDescription = format;
        }

        if (sampleData.length == 0)
        {
            return;
        }
        if (self.formatDescription == NULL)
        {
            success = NO;
            failure = @"scrcpy has not supplied the H.264 SPS/PPS required by the native decoder.";
            return;
        }

        CMBlockBufferRef blockBuffer = NULL;
        OSStatus blockStatus = CMBlockBufferCreateWithMemoryBlock(
            kCFAllocatorDefault,
            NULL,
            sampleData.length,
            kCFAllocatorDefault,
            NULL,
            0,
            sampleData.length,
            0,
            &blockBuffer);
        if (blockStatus == noErr && blockBuffer != NULL)
        {
            blockStatus = CMBlockBufferReplaceDataBytes(
                sampleData.bytes,
                blockBuffer,
                0,
                sampleData.length);
        }
        if (blockStatus != noErr || blockBuffer == NULL)
        {
            if (blockBuffer != NULL)
            {
                CFRelease(blockBuffer);
            }
            success = NO;
            failure = [NSString stringWithFormat:@"Could not allocate an Android H.264 frame buffer (%d).", blockStatus];
            return;
        }

        CMTime presentationTime = CMTimeMake(presentationTimestampUs, 1000000);
        CMSampleTimingInfo timing = {
            .duration = kCMTimeInvalid,
            .presentationTimeStamp = presentationTime,
            .decodeTimeStamp = kCMTimeInvalid
        };
        size_t sampleSize = sampleData.length;
        CMSampleBufferRef sampleBuffer = NULL;
        OSStatus sampleStatus = CMSampleBufferCreateReady(
            kCFAllocatorDefault,
            blockBuffer,
            self.formatDescription,
            1,
            1,
            &timing,
            1,
            &sampleSize,
            &sampleBuffer);
        CFRelease(blockBuffer);
        if (sampleStatus != noErr || sampleBuffer == NULL)
        {
            success = NO;
            failure = [NSString stringWithFormat:@"Could not create an Android H.264 sample (%d).", sampleStatus];
            return;
        }

        CFArrayRef attachments = CMSampleBufferGetSampleAttachmentsArray(sampleBuffer, true);
        if (attachments != NULL && CFArrayGetCount(attachments) > 0)
        {
            CFMutableDictionaryRef attachment = (CFMutableDictionaryRef)CFArrayGetValueAtIndex(attachments, 0);
            CFDictionarySetValue(attachment, kCMSampleAttachmentKey_DisplayImmediately, kCFBooleanTrue);
            if (!keyFrame)
            {
                CFDictionarySetValue(attachment, kCMSampleAttachmentKey_NotSync, kCFBooleanTrue);
            }
        }

        if (self.displayLayer.status == AVQueuedSampleBufferRenderingStatusFailed)
        {
            [self.displayLayer flush];
        }
        [self.displayLayer enqueueSampleBuffer:sampleBuffer];
        CFRelease(sampleBuffer);
    });

    if (!success && error != NULL)
    {
        *error = CreateError(AnsightSimulatorRtcErrorExternalH264DecodeFailed, failure);
    }
    return success;
}

- (void)shutdown
{
    dispatch_sync(self.decodeQueue, ^{
        if (self.shuttingDown)
        {
            return;
        }
        self.shuttingDown = YES;
        [self.displayLayer flushAndRemoveImage];
        if (self.formatDescription != NULL)
        {
            CFRelease(self.formatDescription);
            self.formatDescription = NULL;
        }
        self.sequenceParameterSet = nil;
        self.pictureParameterSet = nil;
    });
}

- (void)dealloc
{
    [self shutdown];
}

@end

@interface AnsightMacWindowPreviewView : NSObject <SCStreamOutput, SCStreamDelegate>

@property(nonatomic, strong) id<MTLDevice> metalDevice;
@property(nonatomic, strong) id<MTLCommandQueue> commandQueue;
@property(nonatomic, strong) CIContext *ciContext;
@property(nonatomic, strong) CAMetalLayer *displayLayer;
@property(nonatomic, strong) dispatch_queue_t captureQueue;
@property(nonatomic, strong) SCStream *windowStream API_AVAILABLE(macCatalyst(18.2));
@property(nonatomic, copy) NSString *lastError;
@property(nonatomic, assign) CGColorSpaceRef renderColorSpace;
@property(nonatomic, assign) CGSize surfaceSize;
@property(nonatomic, assign) NSInteger windowDiscoveryAttemptCount;
@property(nonatomic, assign) BOOL shuttingDown;

- (instancetype)initWithProcessIdentifier:(int32_t)processIdentifier
                           framesPerSecond:(int32_t)framesPerSecond
                                     error:(NSError **)error;
- (void)startCaptureForProcessIdentifier:(int32_t)processIdentifier
                         framesPerSecond:(int32_t)framesPerSecond;
- (void)renderPixelBuffer:(CVPixelBufferRef)pixelBuffer;
- (void)shutdown;

@end

@implementation AnsightMacWindowPreviewView

- (instancetype)initWithProcessIdentifier:(int32_t)processIdentifier
                           framesPerSecond:(int32_t)framesPerSecond
                                     error:(NSError **)error
{
    self = [super init];
    if (self == nil)
    {
        return nil;
    }
    if (@available(macCatalyst 18.2, *))
    {
        self.metalDevice = MTLCreateSystemDefaultDevice();
        self.commandQueue = [self.metalDevice newCommandQueue];
        self.renderColorSpace = CGColorSpaceCreateWithName(kCGColorSpaceSRGB);
        self.ciContext = self.metalDevice == nil
            ? nil
            : [CIContext contextWithMTLDevice:self.metalDevice
                                      options:@{
                                          kCIContextWorkingColorSpace: [NSNull null],
                                          kCIContextOutputColorSpace: [NSNull null],
                                      }];
        self.displayLayer = [CAMetalLayer layer];
        if (self.metalDevice == nil
            || self.commandQueue == nil
            || self.ciContext == nil
            || self.renderColorSpace == NULL
            || self.displayLayer == nil)
        {
            if (error != NULL)
            {
                *error = CreateError(
                    AnsightSimulatorRtcErrorMacWindowPreviewMetalUnavailable,
                    @"The native macOS Metal preview could not be created.");
            }
            [self shutdown];
            return nil;
        }

        self.displayLayer.device = self.metalDevice;
        self.displayLayer.pixelFormat = MTLPixelFormatBGRA8Unorm;
        self.displayLayer.framebufferOnly = NO;
        self.displayLayer.opaque = YES;
        self.displayLayer.allowsNextDrawableTimeout = YES;
        self.displayLayer.backgroundColor = CGColorGetConstantColor(kCGColorBlack);
        self.displayLayer.masksToBounds = YES;
        self.captureQueue = dispatch_queue_create(
            "com.ansight-ai.mac-window-preview.capture",
            DISPATCH_QUEUE_SERIAL);
        dispatch_queue_set_specific(
            self.captureQueue,
            AnsightMacPreviewQueueKey,
            (void *)AnsightMacPreviewQueueKey,
            NULL);
        [self startCaptureForProcessIdentifier:processIdentifier framesPerSecond:framesPerSecond];
        return self;
    }

    if (error != NULL)
    {
        *error = CreateError(
            AnsightSimulatorRtcErrorMacWindowPreviewUnsupported,
            @"macOS window preview requires macOS 15.2 or later.");
    }
    return nil;
}

- (void)startCaptureForProcessIdentifier:(int32_t)processIdentifier
                         framesPerSecond:(int32_t)framesPerSecond
{
    if (@available(macCatalyst 18.2, *))
    {
        if (!AnsightRequestScreenCaptureAccessIfNeeded())
        {
            @synchronized (self)
            {
                self.lastError = @"Screen Recording permission is required. Allow the application running Ansight in the macOS prompt or in System Settings > Privacy & Security > Screen & System Audio Recording, then restart the Ansight host. If it is already enabled, turn it off and back on first.";
            }
            return;
        }

        __weak AnsightMacWindowPreviewView *weakSelf = self;
        [SCShareableContent getShareableContentExcludingDesktopWindows:YES
                                                  onScreenWindowsOnly:YES
                                                    completionHandler:^(SCShareableContent *content, NSError *contentError) {
            AnsightMacWindowPreviewView *strongSelf = weakSelf;
            if (strongSelf == nil || strongSelf.shuttingDown)
            {
                return;
            }
            if (contentError != nil)
            {
                @synchronized (strongSelf)
                {
                    strongSelf.lastError = [NSString stringWithFormat:
                        @"macOS window discovery failed: %@",
                        contentError.localizedDescription];
                }
                return;
            }
            SCWindow *selectedWindow = nil;
            double selectedArea = 0;
            for (SCWindow *candidate in content.windows)
            {
                if (candidate.owningApplication.processID != processIdentifier || !candidate.isOnScreen)
                {
                    continue;
                }

                CGRect frame = candidate.frame;
                double area = frame.size.width * frame.size.height;
                if (frame.size.width >= 32 && frame.size.height >= 32 && area > selectedArea)
                {
                    selectedWindow = candidate;
                    selectedArea = area;
                }
            }
            if (selectedWindow == nil)
            {
                NSInteger attemptCount = 0;
                @synchronized (strongSelf)
                {
                    attemptCount = ++strongSelf.windowDiscoveryAttemptCount;
                }
                if (attemptCount < AnsightMacWindowDiscoveryMaximumAttemptCount)
                {
                    dispatch_after(
                        dispatch_time(DISPATCH_TIME_NOW, AnsightMacWindowDiscoveryRetryNanoseconds),
                        dispatch_get_main_queue(),
                        ^{
                            if (!strongSelf.shuttingDown)
                            {
                                [strongSelf startCaptureForProcessIdentifier:processIdentifier
                                                             framesPerSecond:framesPerSecond];
                            }
                        });
                    return;
                }
                @synchronized (strongSelf)
                {
                    strongSelf.lastError = @"The SDK process does not have a visible macOS window to capture.";
                }
                return;
            }
            @synchronized (strongSelf)
            {
                strongSelf.windowDiscoveryAttemptCount = 0;
            }
            RememberCapturedWindowIdentifier(processIdentifier, selectedWindow.windowID);
            CGRect frame = selectedWindow.frame;
            double scale = MIN(2.0, 1920.0 / MAX(frame.size.width, frame.size.height));
            scale = MAX(scale, 0.25);
            size_t width = MAX(2, (size_t)llround(frame.size.width * scale));
            size_t height = MAX(2, (size_t)llround(frame.size.height * scale));
            width -= width % 2;
            height -= height % 2;

            SCContentFilter *filter = [[SCContentFilter alloc]
                initWithDesktopIndependentWindow:selectedWindow];
            SCStreamConfiguration *configuration = [[SCStreamConfiguration alloc] init];
            configuration.width = width;
            configuration.height = height;
            configuration.minimumFrameInterval = CMTimeMake(1, MAX(framesPerSecond, 1));
            configuration.queueDepth = 3;
            configuration.pixelFormat = kCVPixelFormatType_32BGRA;
            configuration.showsCursor = YES;
            configuration.ignoreShadowsSingleWindow = YES;
            configuration.capturesAudio = NO;

            SCStream *stream = [[SCStream alloc]
                initWithFilter:filter
                configuration:configuration
                delegate:strongSelf];
            NSError *outputError = nil;
            if (![stream addStreamOutput:strongSelf
                                    type:SCStreamOutputTypeScreen
                      sampleHandlerQueue:strongSelf.captureQueue
                                   error:&outputError])
            {
                @synchronized (strongSelf)
                {
                    strongSelf.lastError = [NSString stringWithFormat:
                        @"Could not connect macOS window frames: %@",
                        outputError.localizedDescription ?: @"unknown ScreenCaptureKit error"];
                }
                return;
            }

            @synchronized (strongSelf)
            {
                if (strongSelf.shuttingDown)
                {
                    return;
                }
                strongSelf.windowStream = stream;
            }
            [stream startCaptureWithCompletionHandler:^(NSError *startError) {
                if (startError != nil)
                {
                    @synchronized (strongSelf)
                    {
                        strongSelf.lastError = [NSString stringWithFormat:
                            @"macOS window capture failed to start: %@",
                            startError.localizedDescription];
                    }
                }
            }];
        }];
    }
}

- (void)stream:(SCStream *)stream
    didOutputSampleBuffer:(CMSampleBufferRef)sampleBuffer
                  ofType:(SCStreamOutputType)type API_AVAILABLE(macCatalyst(18.2))
{
    (void)stream;
    if (type != SCStreamOutputTypeScreen
        || self.shuttingDown
        || !CMSampleBufferIsValid(sampleBuffer))
    {
        return;
    }

    CFArrayRef attachments = CMSampleBufferGetSampleAttachmentsArray(sampleBuffer, true);
    if (attachments != NULL && CFArrayGetCount(attachments) > 0)
    {
        CFMutableDictionaryRef attachment =
            (CFMutableDictionaryRef)CFArrayGetValueAtIndex(attachments, 0);
        CFDictionarySetValue(attachment, kCMSampleAttachmentKey_DisplayImmediately, kCFBooleanTrue);
    }

    CVPixelBufferRef pixelBuffer = CMSampleBufferGetImageBuffer(sampleBuffer);
    if (pixelBuffer != NULL)
    {
        @synchronized (self)
        {
            self.surfaceSize = CGSizeMake(
                CVPixelBufferGetWidth(pixelBuffer),
                CVPixelBufferGetHeight(pixelBuffer));
        }
        [self renderPixelBuffer:pixelBuffer];
    }
}

- (void)renderPixelBuffer:(CVPixelBufferRef)pixelBuffer
{
    if (pixelBuffer == NULL || self.shuttingDown)
    {
        return;
    }

    id<CAMetalDrawable> drawable = [self.displayLayer nextDrawable];
    if (drawable == nil)
    {
        return;
    }

    size_t sourceWidth = CVPixelBufferGetWidth(pixelBuffer);
    size_t sourceHeight = CVPixelBufferGetHeight(pixelBuffer);
    CGRect drawableBounds = CGRectMake(
        0,
        0,
        drawable.texture.width,
        drawable.texture.height);
    if (sourceWidth == 0
        || sourceHeight == 0
        || CGRectGetWidth(drawableBounds) <= 0
        || CGRectGetHeight(drawableBounds) <= 0)
    {
        return;
    }

    CGFloat renderScale = MIN(
        CGRectGetWidth(drawableBounds) / (CGFloat)sourceWidth,
        CGRectGetHeight(drawableBounds) / (CGFloat)sourceHeight);
    CGSize renderedSize = CGSizeMake(
        (CGFloat)sourceWidth * renderScale,
        (CGFloat)sourceHeight * renderScale);
    CGPoint renderedOrigin = CGPointMake(
        (CGRectGetWidth(drawableBounds) - renderedSize.width) / 2,
        (CGRectGetHeight(drawableBounds) - renderedSize.height) / 2);
    CIImage *sourceImage = [CIImage imageWithCVPixelBuffer:pixelBuffer];
    CIImage *scaledImage = [sourceImage imageByApplyingTransform:
        CGAffineTransformMakeScale(renderScale, renderScale)];
    CIImage *positionedImage = [scaledImage imageByApplyingTransform:
        CGAffineTransformMakeTranslation(renderedOrigin.x, renderedOrigin.y)];
    CIImage *background = [[CIImage imageWithColor:
        [CIColor colorWithRed:0 green:0 blue:0 alpha:1]] imageByCroppingToRect:drawableBounds];
    CIImage *composite = [positionedImage imageByCompositingOverImage:background];
    id<MTLCommandBuffer> commandBuffer = [self.commandQueue commandBuffer];
    if (commandBuffer == nil)
    {
        return;
    }

    [self.ciContext render:composite
              toMTLTexture:drawable.texture
             commandBuffer:commandBuffer
                    bounds:drawableBounds
                colorSpace:self.renderColorSpace];
    [commandBuffer presentDrawable:drawable];
    [commandBuffer commit];
}

- (void)stream:(SCStream *)stream didStopWithError:(NSError *)error API_AVAILABLE(macCatalyst(18.2))
{
    (void)stream;
    @synchronized (self)
    {
        if (!self.shuttingDown)
        {
            self.lastError = [NSString stringWithFormat:
                @"macOS window capture stopped: %@",
                error.localizedDescription];
        }
    }
}

- (void)shutdown
{
    @synchronized (self)
    {
        if (self.shuttingDown)
        {
            return;
        }
        self.shuttingDown = YES;
        if (@available(macCatalyst 18.2, *))
        {
            [self.windowStream stopCaptureWithCompletionHandler:^(NSError *error) {
                (void)error;
            }];
            self.windowStream = nil;
        }
    }

    if (self.captureQueue != nil && dispatch_get_specific(AnsightMacPreviewQueueKey) == NULL)
    {
        dispatch_sync(self.captureQueue, ^{});
    }
    if (self.renderColorSpace != NULL)
    {
        CGColorSpaceRelease(self.renderColorSpace);
        self.renderColorSpace = NULL;
    }
}

- (void)dealloc
{
    [self shutdown];
}

@end

@interface AnsightSimulatorRtcSession : NSObject <SCStreamOutput, SCStreamDelegate>

@property(nonatomic, strong) id serviceContext;
@property(nonatomic, strong) id deviceSet;
@property(nonatomic, strong) id deviceIo;
@property(nonatomic, strong) id screenDescriptor;
@property(nonatomic, strong) id currentSurface;
@property(nonatomic, strong) NSUUID *screenCallbackUuid;
@property(nonatomic, strong) dispatch_queue_t screenQueue;
@property(nonatomic, strong) SCStream *windowStream API_AVAILABLE(macCatalyst(18.2));
@property(nonatomic, copy) NSString *answerSdp;
@property(nonatomic, copy) NSString *lastError;
@property(nonatomic, assign) VTCompressionSessionRef compressionSession;
@property(nonatomic, assign) size_t encodedWidth;
@property(nonatomic, assign) size_t encodedHeight;
@property(nonatomic, assign) int peerConnection;
@property(nonatomic, assign) int videoTrack;
@property(nonatomic, assign) int inputDataChannel;
@property(nonatomic, assign) int32_t framesPerSecond;
@property(nonatomic, assign) int32_t averageBitRate;
@property(nonatomic, assign) uint32_t rtpTimestamp;
@property(nonatomic, assign) uint32_t nativeBaseRtpTimestamp;
@property(nonatomic, assign) double nativeFirstPresentationTime;
@property(nonatomic, assign) uint32_t externalBaseRtpTimestamp;
@property(nonatomic, assign) int64_t externalFirstPresentationTimestamp;
@property(nonatomic, assign) rtcNalUnitSeparator nalSeparator;
@property(nonatomic, assign) NSInteger windowDiscoveryAttemptCount;
@property(nonatomic, assign) BOOL externalVideo;
@property(nonatomic, assign) double nextEncodeTime;
@property(nonatomic, assign) BOOL encodeInFlight;
@property(nonatomic, assign) BOOL videoRefreshPending;
@property(nonatomic, assign) BOOL forceKeyFrame;
@property(nonatomic, assign) BOOL answerStarted;
@property(nonatomic, assign) BOOL shuttingDown;
@property(nonatomic, assign) AnsightSimulatorRtcInputCallback inputCallback;
@property(nonatomic, assign) void *inputContext;

- (instancetype)initWithDeveloperDirectory:(NSString *)developerDirectory
                                deviceUdid:(NSString *)deviceUdid
                           framesPerSecond:(int32_t)framesPerSecond
                            averageBitRate:(int32_t)averageBitRate
                             inputCallback:(AnsightSimulatorRtcInputCallback)inputCallback
                              inputContext:(void *)inputContext
                                     error:(NSError **)error;
- (instancetype)initExternalWithFramesPerSecond:(int32_t)framesPerSecond
                                  inputCallback:(AnsightSimulatorRtcInputCallback)inputCallback
                                   inputContext:(void *)inputContext
                                          error:(NSError **)error;
- (instancetype)initMacWindowWithProcessIdentifier:(int32_t)processIdentifier
                                   framesPerSecond:(int32_t)framesPerSecond
                                    averageBitRate:(int32_t)averageBitRate
                                     inputCallback:(AnsightSimulatorRtcInputCallback)inputCallback
                                      inputContext:(void *)inputContext
                                             error:(NSError **)error;
- (void)handleFrame;
- (void)encodePixelBuffer:(CVPixelBufferRef)pixelBuffer;
- (void)startMacWindowCaptureForProcessIdentifier:(int32_t)processIdentifier;
- (void)handleSurfacesChanged:(id)unmaskedSurface maskedSurface:(id)maskedSurface;
- (void)handleEncodedSample:(CMSampleBufferRef)sampleBuffer status:(OSStatus)status;
- (BOOL)canSendVideoFrame;
- (void)handleVideoBufferedAmountLow:(int)track;
- (void)handleInputMessage:(const char *)message size:(int)size;
- (void)handleRtcState:(rtcState)state;
- (void)handleRtcGatheringState:(rtcGatheringState)state;
- (void)handleRemoteTrack:(int)track;
- (void)handleRemoteDataChannel:(int)dataChannel;
- (void)setRtcError:(const char *)message;
- (void)requestKeyFrame;
- (int32_t)sendExternalAccessUnit:(NSData *)accessUnit
       presentationTimestampUs:(int64_t)presentationTimestampUs
                     keyFrame:(BOOL)keyFrame
                         error:(NSError **)error;
- (int32_t)copyAnswerToBuffer:(char *)buffer capacity:(size_t)capacity error:(NSError **)error;
- (BOOL)setOfferSdp:(NSString *)offerSdp error:(NSError **)error;
- (int32_t)sendDataChannelMessage:(NSData *)message error:(NSError **)error;
- (void)shutdown;

@end

@implementation AnsightSimulatorRtcSession

- (instancetype)initWithDeveloperDirectory:(NSString *)developerDirectory
                                deviceUdid:(NSString *)deviceUdid
                           framesPerSecond:(int32_t)framesPerSecond
                            averageBitRate:(int32_t)averageBitRate
                             inputCallback:(AnsightSimulatorRtcInputCallback)inputCallback
                              inputContext:(void *)inputContext
                                     error:(NSError **)error
{
    self = [super init];
    if (self == nil)
    {
        return nil;
    }

    self.framesPerSecond = framesPerSecond;
    self.averageBitRate = averageBitRate;
    self.inputCallback = inputCallback;
    self.inputContext = inputContext;
    self.peerConnection = -1;
    self.videoTrack = -1;
    self.inputDataChannel = -1;
    self.rtpTimestamp = arc4random();
    self.nativeBaseRtpTimestamp = self.rtpTimestamp;
    self.nativeFirstPresentationTime = -1;
    self.nalSeparator = RTC_NAL_SEPARATOR_LENGTH;
    self.forceKeyFrame = YES;
    self.screenQueue = dispatch_queue_create("com.ansight-ai.simulator-rtc.screen", DISPATCH_QUEUE_SERIAL);
    dispatch_queue_set_specific(self.screenQueue, AnsightScreenQueueKey, (void *)AnsightScreenQueueKey, NULL);

    if (![self loadSimulatorForDeveloperDirectory:developerDirectory error:error]
        || ![self resolveDevice:deviceUdid error:error]
        || ![self createPeerConnectionWithError:error]
        || ![self connectScreenWithError:error])
    {
        [self shutdown];
        return nil;
    }

    return self;
}

- (instancetype)initExternalWithFramesPerSecond:(int32_t)framesPerSecond
                                  inputCallback:(AnsightSimulatorRtcInputCallback)inputCallback
                                   inputContext:(void *)inputContext
                                          error:(NSError **)error
{
    self = [super init];
    if (self == nil)
    {
        return nil;
    }

    self.framesPerSecond = framesPerSecond;
    self.inputCallback = inputCallback;
    self.inputContext = inputContext;
    self.peerConnection = -1;
    self.videoTrack = -1;
    self.inputDataChannel = -1;
    self.rtpTimestamp = arc4random();
    self.nativeBaseRtpTimestamp = self.rtpTimestamp;
    self.nativeFirstPresentationTime = -1;
    self.externalBaseRtpTimestamp = self.rtpTimestamp;
    self.externalFirstPresentationTimestamp = -1;
    self.nalSeparator = RTC_NAL_SEPARATOR_START_SEQUENCE;
    self.externalVideo = YES;
    self.forceKeyFrame = YES;

    InitializeRtcLogger();
    if (![self createPeerConnectionWithError:error])
    {
        [self shutdown];
        return nil;
    }

    return self;
}

- (instancetype)initMacWindowWithProcessIdentifier:(int32_t)processIdentifier
                                   framesPerSecond:(int32_t)framesPerSecond
                                    averageBitRate:(int32_t)averageBitRate
                                     inputCallback:(AnsightSimulatorRtcInputCallback)inputCallback
                                      inputContext:(void *)inputContext
                                             error:(NSError **)error
{
    if (@available(macCatalyst 18.2, *))
    {
        self = [self initExternalWithFramesPerSecond:framesPerSecond
                                      inputCallback:inputCallback
                                       inputContext:inputContext
                                              error:error];
        if (self == nil)
        {
            return nil;
        }

        self.externalVideo = NO;
        self.nalSeparator = RTC_NAL_SEPARATOR_LENGTH;
        self.averageBitRate = averageBitRate;
        self.screenQueue = dispatch_queue_create("com.ansight-ai.mac-window-rtc.screen", DISPATCH_QUEUE_SERIAL);
        dispatch_queue_set_specific(
            self.screenQueue,
            AnsightScreenQueueKey,
            (void *)AnsightScreenQueueKey,
            NULL);
        [self startMacWindowCaptureForProcessIdentifier:processIdentifier];
        return self;
    }

    if (error != NULL)
    {
        *error = CreateError(
            AnsightSimulatorRtcErrorMacWindowCaptureUnsupported,
            @"macOS window capture requires macOS 15.2 or later.");
    }
    return nil;
}

- (void)startMacWindowCaptureForProcessIdentifier:(int32_t)processIdentifier
{
    if (@available(macCatalyst 18.2, *))
    {
        __weak AnsightSimulatorRtcSession *weakSelf = self;
        [SCShareableContent getShareableContentExcludingDesktopWindows:YES
                                                  onScreenWindowsOnly:YES
                                                    completionHandler:^(SCShareableContent *content, NSError *contentError) {
            AnsightSimulatorRtcSession *strongSelf = weakSelf;
            if (strongSelf == nil || strongSelf.shuttingDown)
            {
                return;
            }
            if (contentError != nil)
            {
                @synchronized (strongSelf)
                {
                    strongSelf.lastError = [NSString stringWithFormat:
                        @"macOS window discovery failed: %@", contentError.localizedDescription];
                }
                return;
            }

            SCWindow *selectedWindow = nil;
            double selectedArea = 0;
            for (SCWindow *candidate in content.windows)
            {
                if (candidate.owningApplication.processID != processIdentifier || !candidate.isOnScreen)
                {
                    continue;
                }
                CGRect frame = candidate.frame;
                double area = frame.size.width * frame.size.height;
                if (frame.size.width >= 32 && frame.size.height >= 32 && area > selectedArea)
                {
                    selectedWindow = candidate;
                    selectedArea = area;
                }
            }
            if (selectedWindow == nil)
            {
                NSInteger attemptCount = 0;
                @synchronized (strongSelf)
                {
                    attemptCount = ++strongSelf.windowDiscoveryAttemptCount;
                }
                if (attemptCount < AnsightMacWindowDiscoveryMaximumAttemptCount)
                {
                    dispatch_after(
                        dispatch_time(DISPATCH_TIME_NOW, AnsightMacWindowDiscoveryRetryNanoseconds),
                        dispatch_get_main_queue(),
                        ^{
                            if (!strongSelf.shuttingDown)
                            {
                                [strongSelf startMacWindowCaptureForProcessIdentifier:processIdentifier];
                            }
                        });
                    return;
                }
                @synchronized (strongSelf)
                {
                    strongSelf.lastError = @"The SDK process does not have a visible macOS window to capture.";
                }
                return;
            }

            @synchronized (strongSelf)
            {
                strongSelf.windowDiscoveryAttemptCount = 0;
            }
            RememberCapturedWindowIdentifier(processIdentifier, selectedWindow.windowID);
            CGRect frame = selectedWindow.frame;
            double scale = MIN(2.0, 1920.0 / MAX(frame.size.width, frame.size.height));
            scale = MAX(scale, 0.25);
            size_t width = MAX(2, (size_t)llround(frame.size.width * scale));
            size_t height = MAX(2, (size_t)llround(frame.size.height * scale));
            width -= width % 2;
            height -= height % 2;

            SCContentFilter *filter = [[SCContentFilter alloc]
                initWithDesktopIndependentWindow:selectedWindow];
            SCStreamConfiguration *configuration = [[SCStreamConfiguration alloc] init];
            configuration.width = width;
            configuration.height = height;
            configuration.minimumFrameInterval = CMTimeMake(1, MAX(strongSelf.framesPerSecond, 1));
            configuration.queueDepth = 3;
            configuration.pixelFormat = kCVPixelFormatType_32BGRA;
            configuration.showsCursor = YES;
            configuration.ignoreShadowsSingleWindow = YES;
            configuration.capturesAudio = NO;

            SCStream *stream = [[SCStream alloc]
                initWithFilter:filter
                configuration:configuration
                delegate:strongSelf];
            NSError *outputError = nil;
            if (![stream addStreamOutput:strongSelf
                                    type:SCStreamOutputTypeScreen
                      sampleHandlerQueue:strongSelf.screenQueue
                                   error:&outputError])
            {
                @synchronized (strongSelf)
                {
                    strongSelf.lastError = [NSString stringWithFormat:
                        @"Could not connect macOS window frames: %@",
                        outputError.localizedDescription ?: @"unknown ScreenCaptureKit error"];
                }
                return;
            }

            strongSelf.windowStream = stream;
            [stream startCaptureWithCompletionHandler:^(NSError *startError) {
                if (startError != nil)
                {
                    @synchronized (strongSelf)
                    {
                        strongSelf.lastError = [NSString stringWithFormat:
                            @"macOS window capture failed to start: %@", startError.localizedDescription];
                    }
                }
            }];
        }];
    }
}

- (void)stream:(SCStream *)stream
    didOutputSampleBuffer:(CMSampleBufferRef)sampleBuffer
                  ofType:(SCStreamOutputType)type API_AVAILABLE(macCatalyst(18.2))
{
    (void)stream;
    if (type != SCStreamOutputTypeScreen || !CMSampleBufferIsValid(sampleBuffer))
    {
        return;
    }
    CVPixelBufferRef pixelBuffer = CMSampleBufferGetImageBuffer(sampleBuffer);
    if (pixelBuffer != NULL)
    {
        [self encodePixelBuffer:pixelBuffer];
    }
}

- (void)stream:(SCStream *)stream didStopWithError:(NSError *)error API_AVAILABLE(macCatalyst(18.2))
{
    (void)stream;
    @synchronized (self)
    {
        if (!self.shuttingDown)
        {
            self.lastError = [NSString stringWithFormat:
                @"macOS window capture stopped: %@", error.localizedDescription];
        }
    }
}

- (BOOL)loadSimulatorForDeveloperDirectory:(NSString *)developerDirectory error:(NSError **)error
{
    InitializeRtcLogger();

    NSString *coreSimulatorPath = @"/Library/Developer/PrivateFrameworks/CoreSimulator.framework/CoreSimulator";
    NSString *simulatorKitPath = [developerDirectory stringByAppendingPathComponent:
        @"Library/PrivateFrameworks/SimulatorKit.framework/SimulatorKit"];
    if (dlopen(coreSimulatorPath.fileSystemRepresentation, RTLD_NOW | RTLD_LOCAL) == NULL)
    {
        if (error != NULL)
        {
            *error = CreateError(
                AnsightSimulatorRtcErrorRuntimeCoreSimulatorUnavailable,
                [NSString stringWithFormat:@"Could not load CoreSimulator: %s", dlerror()]);
        }
        return NO;
    }

    if (dlopen(simulatorKitPath.fileSystemRepresentation, RTLD_NOW | RTLD_LOCAL) == NULL)
    {
        if (error != NULL)
        {
            *error = CreateError(
                AnsightSimulatorRtcErrorRuntimeSimulatorKitUnavailable,
                [NSString stringWithFormat:@"Could not load SimulatorKit: %s", dlerror()]);
        }
        return NO;
    }

    Class serviceContextClass = NSClassFromString(@"SimServiceContext");
    SEL contextSelector = sel_registerName("sharedServiceContextForDeveloperDir:error:");
    if (serviceContextClass == Nil || ![serviceContextClass respondsToSelector:contextSelector])
    {
        if (error != NULL)
        {
            *error = CreateError(
                AnsightSimulatorRtcErrorRuntimeServiceContextUnavailable,
                @"CoreSimulator service context is unavailable.");
        }
        return NO;
    }

    self.serviceContext = SendIdWithObjectAndError(serviceContextClass, contextSelector, developerDirectory, error);
    self.deviceSet = self.serviceContext == nil
        ? nil
        : SendIdWithError(self.serviceContext, sel_registerName("defaultDeviceSetWithError:"), error);
    return self.deviceSet != nil;
}

- (BOOL)resolveDevice:(NSString *)deviceUdid error:(NSError **)error
{
    NSDictionary *devicesByUdid = SendId(self.deviceSet, sel_registerName("devicesByUDID"));
    id device = devicesByUdid[deviceUdid];
    if (device == nil)
    {
        NSUUID *uuid = [[NSUUID alloc] initWithUUIDString:deviceUdid];
        device = uuid == nil ? nil : devicesByUdid[uuid];
    }

    if (device == nil)
    {
        for (id candidate in SendId(self.deviceSet, sel_registerName("devices")))
        {
            id candidateUdid = SendId(candidate, sel_registerName("UDID"));
            NSString *candidateText = [candidateUdid respondsToSelector:sel_registerName("UUIDString")]
                ? SendId(candidateUdid, sel_registerName("UUIDString"))
                : [candidateUdid description];
            if ([candidateText caseInsensitiveCompare:deviceUdid] == NSOrderedSame)
            {
                device = candidate;
                break;
            }
        }
    }

    if (device == nil)
    {
        if (error != NULL)
        {
            *error = CreateError(
                AnsightSimulatorRtcErrorRuntimeDeviceNotFound,
                [NSString stringWithFormat:@"Simulator %@ was not found.", deviceUdid]);
        }
        return NO;
    }

    self.deviceIo = device;
    return YES;
}

- (BOOL)createPeerConnectionWithError:(NSError **)error
{
    const char *iceServers[] = {
        "stun:stun.cloudflare.com:3478",
    };
    rtcConfiguration configuration = {};
    configuration.iceServers = iceServers;
    configuration.iceServersCount = (int)(sizeof(iceServers) / sizeof(iceServers[0]));
    configuration.disableAutoNegotiation = true;
    configuration.enableIceTcp = false;
    configuration.forceMediaTransport = true;
    self.peerConnection = rtcCreatePeerConnection(&configuration);
    if (self.peerConnection < 0)
    {
        if (error != NULL)
        {
            *error = CreateError(
                AnsightSimulatorRtcErrorPeerConnectionCreationFailed,
                RtcFailure(@"Creating the WebRTC peer connection", self.peerConnection));
        }
        return NO;
    }

    rtcSetUserPointer(self.peerConnection, (__bridge void *)self);
    rtcSetLocalDescriptionCallback(self.peerConnection, AnsightRtcDescriptionCallback);
    rtcSetStateChangeCallback(self.peerConnection, AnsightRtcStateCallback);
    rtcSetGatheringStateChangeCallback(self.peerConnection, AnsightRtcGatheringCallback);
    rtcSetTrackCallback(self.peerConnection, AnsightRtcTrackCallback);
    rtcSetDataChannelCallback(self.peerConnection, AnsightRtcDataChannelCallback);
    rtcSetErrorCallback(self.peerConnection, AnsightRtcErrorCallback);
    return YES;
}

- (BOOL)connectScreenWithError:(NSError **)error
{
    id device = self.deviceIo;
    Class deviceIoClass = NSClassFromString(@"SimDeviceIO");
    SEL ioSelector = sel_registerName("ioForSimDevice:errorQueue:errorHandler:");
    if (deviceIoClass == Nil || ![deviceIoClass respondsToSelector:ioSelector])
    {
        if (error != NULL)
        {
            *error = CreateError(AnsightSimulatorRtcErrorDisplayIoUnavailable, @"CoreSimulator display IO is unavailable.");
        }
        return NO;
    }

    __weak AnsightSimulatorRtcSession *weakSelf = self;
    id errorHandler = ^(NSError *ioError) {
        AnsightSimulatorRtcSession *strongSelf = weakSelf;
        if (strongSelf != nil)
        {
            @synchronized (strongSelf)
            {
                strongSelf.lastError = ioError.localizedDescription;
            }
        }
    };
    id deviceIo = SendDeviceQueueHandler(deviceIoClass, ioSelector, device, self.screenQueue, errorHandler);
    if (deviceIo == nil)
    {
        if (error != NULL)
        {
            *error = CreateError(AnsightSimulatorRtcErrorDisplayIoConnectionFailed, @"Could not connect to Simulator display IO.");
        }
        return NO;
    }

    self.deviceIo = deviceIo;
    SEL registerSelector = sel_registerName(
        "registerScreenCallbacksWithUUID:callbackQueue:frameCallback:surfacesChangedCallback:propertiesChangedCallback:");
    for (id port in SendId(deviceIo, sel_registerName("ioPorts")))
    {
        id descriptor = SendId(port, sel_registerName("descriptor"));
        if (![descriptor respondsToSelector:registerSelector]
            || ![descriptor respondsToSelector:sel_registerName("screenProperties")])
        {
            continue;
        }

        id properties = SendId(descriptor, sel_registerName("screenProperties"));
        NSInteger screenType = [properties respondsToSelector:sel_registerName("screenType")]
            ? SendInteger(properties, sel_registerName("screenType"))
            : -1;
        NSInteger powerState = [properties respondsToSelector:sel_registerName("powerState")]
            ? SendInteger(properties, sel_registerName("powerState"))
            : 0;
        if (screenType == 0 && powerState != 0)
        {
            self.screenDescriptor = descriptor;
            break;
        }
    }

    if (self.screenDescriptor == nil)
    {
        if (error != NULL)
        {
            *error = CreateError(AnsightSimulatorRtcErrorPrimaryLcdUnavailable, @"The Simulator primary LCD surface is unavailable.");
        }
        return NO;
    }

    self.screenCallbackUuid = [NSUUID UUID];
    id frameCallback = ^{
        [weakSelf handleFrame];
    };
    id surfacesChangedCallback = ^(id unmaskedSurface, id maskedSurface) {
        [weakSelf handleSurfacesChanged:unmaskedSurface maskedSurface:maskedSurface];
    };
    id propertiesChangedCallback = ^(id properties) {
        (void)properties;
    };

    @try
    {
        ((void (*)(id, SEL, id, id, id, id, id))objc_msgSend)(
            self.screenDescriptor,
            registerSelector,
            self.screenCallbackUuid,
            self.screenQueue,
            frameCallback,
            surfacesChangedCallback,
            propertiesChangedCallback);
    }
    @catch (NSException *exception)
    {
        if (error != NULL)
        {
            *error = CreateError(
                AnsightSimulatorRtcErrorFrameCallbackRegistrationFailed,
                [NSString stringWithFormat:@"Could not register Simulator frame callbacks: %@: %@",
                                           exception.name,
                                           exception.reason ?: @"native exception"]);
        }
        return NO;
    }

    SEL framebufferSelector = sel_registerName("framebufferSurface");
    if ([self.screenDescriptor respondsToSelector:framebufferSelector])
    {
        [self handleSurfacesChanged:SendId(self.screenDescriptor, framebufferSelector)
                     maskedSurface:nil];
    }

    return YES;
}

- (void)handleSurfacesChanged:(id)unmaskedSurface maskedSurface:(id)maskedSurface
{
    (void)maskedSurface;
    @synchronized (self)
    {
        if (!self.shuttingDown)
        {
            self.currentSurface = unmaskedSurface;
        }
    }
}

- (void)handleFrame
{
    id surface = nil;
    @synchronized (self)
    {
        if (self.shuttingDown || self.encodeInFlight || self.currentSurface == nil)
        {
            return;
        }

        double now = MonotonicSeconds();
        double frameInterval = 1.0 / (double)self.framesPerSecond;
        double frameDeadlineTolerance = MIN(frameInterval * 0.2, 0.0025);
        if (self.nextEncodeTime > 0 && now + frameDeadlineTolerance < self.nextEncodeTime)
        {
            return;
        }

        if (self.nextEncodeTime <= 0 || now - self.nextEncodeTime > frameInterval * 2)
        {
            self.nextEncodeTime = now + frameInterval;
        }
        else
        {
            self.nextEncodeTime += frameInterval;
        }
        surface = self.currentSurface;
    }

    if (![self canSendVideoFrame])
    {
        return;
    }

    IOSurfaceRef ioSurface = (__bridge IOSurfaceRef)surface;
    size_t width = IOSurfaceGetWidth(ioSurface);
    size_t height = IOSurfaceGetHeight(ioSurface);
    if (width == 0 || height == 0)
    {
        return;
    }

    NSDictionary *attributes = @{
        (NSString *)kCVPixelBufferIOSurfacePropertiesKey: @{},
        (NSString *)kCVPixelBufferMetalCompatibilityKey: @YES,
    };
    CVPixelBufferRef pixelBuffer = NULL;
    CVReturn pixelResult = CVPixelBufferCreateWithIOSurface(
        kCFAllocatorDefault,
        ioSurface,
        (__bridge CFDictionaryRef)attributes,
        &pixelBuffer);
    if (pixelResult != kCVReturnSuccess || pixelBuffer == NULL)
    {
        @synchronized (self)
        {
            self.lastError = [NSString stringWithFormat:@"Could not wrap the Simulator IOSurface (%d).", pixelResult];
        }
        return;
    }

    [self encodePixelBuffer:pixelBuffer];
    CVPixelBufferRelease(pixelBuffer);
}

- (void)encodePixelBuffer:(CVPixelBufferRef)pixelBuffer
{
    if (pixelBuffer == NULL || ![self canSendVideoFrame])
    {
        return;
    }

    size_t width = CVPixelBufferGetWidth(pixelBuffer);
    size_t height = CVPixelBufferGetHeight(pixelBuffer);
    if (width == 0 || height == 0 || ![self ensureEncoderWidth:width height:height])
    {
        return;
    }

    BOOL forceKeyFrame = NO;
    VTCompressionSessionRef encoder = NULL;
    CMTime presentationTime = kCMTimeInvalid;
    @synchronized (self)
    {
        if (!self.shuttingDown)
        {
            self.encodeInFlight = YES;
            forceKeyFrame = self.forceKeyFrame;
            self.forceKeyFrame = NO;
            encoder = self.compressionSession;
            double now = MonotonicSeconds();
            if (self.nativeFirstPresentationTime < 0)
            {
                self.nativeFirstPresentationTime = now;
                self.nativeBaseRtpTimestamp = self.rtpTimestamp;
            }
            int64_t elapsedRtpTicks = (int64_t)llround(
                MAX(now - self.nativeFirstPresentationTime, 0) * AnsightH264ClockRate);
            presentationTime = CMTimeMake(elapsedRtpTicks, AnsightH264ClockRate);
        }
    }

    if (encoder == NULL)
    {
        return;
    }

    NSDictionary *frameProperties = forceKeyFrame
        ? @{(NSString *)kVTEncodeFrameOptionKey_ForceKeyFrame: @YES}
        : nil;
    OSStatus encodeStatus = VTCompressionSessionEncodeFrame(
        encoder,
        pixelBuffer,
        presentationTime,
        kCMTimeInvalid,
        (__bridge CFDictionaryRef)frameProperties,
        NULL,
        NULL);
    if (encodeStatus != noErr)
    {
        @synchronized (self)
        {
            self.encodeInFlight = NO;
            self.lastError = [NSString stringWithFormat:@"VideoToolbox rejected a captured frame (%d).", encodeStatus];
        }
    }
}

- (BOOL)canSendVideoFrame
{
    int track = -1;
    int32_t maximumBufferedBytes = 0;
    @synchronized (self)
    {
        if (self.shuttingDown)
        {
            return NO;
        }
        track = self.videoTrack;
        maximumBufferedBytes = MaximumBufferedVideoBytes(self.averageBitRate);
    }

    if (track < 0 || !rtcIsOpen(track))
    {
        return NO;
    }
    if (rtcGetBufferedAmount(track) <= maximumBufferedBytes)
    {
        return YES;
    }

    @synchronized (self)
    {
        if (track == self.videoTrack && !self.externalVideo && self.currentSurface != nil)
        {
            self.videoRefreshPending = YES;
        }
    }
    return NO;
}

- (void)handleVideoBufferedAmountLow:(int)track
{
    dispatch_queue_t queue = nil;
    @synchronized (self)
    {
        if (self.shuttingDown || track != self.videoTrack || !self.videoRefreshPending)
        {
            return;
        }
        self.videoRefreshPending = NO;
        self.nextEncodeTime = 0;
        queue = self.screenQueue;
    }

    if (queue == nil)
    {
        return;
    }
    dispatch_async(queue, ^{
        [self handleFrame];
    });
}

- (BOOL)ensureEncoderWidth:(size_t)width height:(size_t)height
{
    if (self.compressionSession != NULL && self.encodedWidth == width && self.encodedHeight == height)
    {
        return YES;
    }

    [self destroyEncoder];
    VTCompressionSessionRef encoder = NULL;
    OSStatus status = VTCompressionSessionCreate(
        kCFAllocatorDefault,
        (int32_t)width,
        (int32_t)height,
        kCMVideoCodecType_H264,
        NULL,
        NULL,
        NULL,
        AnsightVideoCompressionCallback,
        (__bridge void *)self,
        &encoder);
    if (status != noErr || encoder == NULL)
    {
        @synchronized (self)
        {
            self.lastError = [NSString stringWithFormat:@"Could not create the H.264 encoder (%d).", status];
        }
        return NO;
    }

    self.compressionSession = encoder;
    self.encodedWidth = width;
    self.encodedHeight = height;
    VTSessionSetProperty(encoder, kVTCompressionPropertyKey_RealTime, kCFBooleanTrue);
    VTSessionSetProperty(encoder, kVTCompressionPropertyKey_AllowFrameReordering, kCFBooleanFalse);
    VTSessionSetProperty(encoder, kVTCompressionPropertyKey_ProfileLevel, kVTProfileLevel_H264_Baseline_AutoLevel);
    CFNumberRef frameRate = CFNumberCreate(kCFAllocatorDefault, kCFNumberSInt32Type, &_framesPerSecond);
    CFNumberRef bitRate = CFNumberCreate(kCFAllocatorDefault, kCFNumberSInt32Type, &_averageBitRate);
    int32_t keyFrameInterval = self.framesPerSecond * 2;
    CFNumberRef keyFrameIntervalValue = CFNumberCreate(
        kCFAllocatorDefault,
        kCFNumberSInt32Type,
        &keyFrameInterval);
    VTSessionSetProperty(encoder, kVTCompressionPropertyKey_ExpectedFrameRate, frameRate);
    VTSessionSetProperty(encoder, kVTCompressionPropertyKey_AverageBitRate, bitRate);
    VTSessionSetProperty(encoder, kVTCompressionPropertyKey_MaxKeyFrameInterval, keyFrameIntervalValue);
    CFRelease(frameRate);
    CFRelease(bitRate);
    CFRelease(keyFrameIntervalValue);
    status = VTCompressionSessionPrepareToEncodeFrames(encoder);
    if (status != noErr)
    {
        @synchronized (self)
        {
            self.lastError = [NSString stringWithFormat:@"Could not prepare the H.264 encoder (%d).", status];
        }
        [self destroyEncoder];
        return NO;
    }

    return YES;
}

- (void)handleEncodedSample:(CMSampleBufferRef)sampleBuffer status:(OSStatus)status
{
    @synchronized (self)
    {
        self.encodeInFlight = NO;
        if (self.shuttingDown)
        {
            return;
        }
    }

    if (status != noErr || sampleBuffer == NULL || !CMSampleBufferDataIsReady(sampleBuffer))
    {
        if (status != noErr)
        {
            @synchronized (self)
            {
                self.lastError = [NSString stringWithFormat:@"VideoToolbox encoding failed (%d).", status];
            }
        }
        return;
    }

    NSData *accessUnit = CreateH264AccessUnit(sampleBuffer);
    if (accessUnit.length == 0 || self.videoTrack < 0 || !rtcIsOpen(self.videoTrack))
    {
        return;
    }

    CMTime presentationTime = CMSampleBufferGetPresentationTimeStamp(sampleBuffer);
    CMTime rtpTime = CMTIME_IS_VALID(presentationTime)
        ? CMTimeConvertScale(presentationTime, AnsightH264ClockRate, kCMTimeRoundingMethod_RoundHalfAwayFromZero)
        : kCMTimeInvalid;
    @synchronized (self)
    {
        if (CMTIME_IS_VALID(rtpTime) && rtpTime.value >= 0)
        {
            self.rtpTimestamp = self.nativeBaseRtpTimestamp + (uint32_t)rtpTime.value;
        }
        else
        {
            self.rtpTimestamp += AnsightH264ClockRate / (uint32_t)MAX(self.framesPerSecond, 1);
        }
        rtcSetTrackRtpTimestamp(self.videoTrack, self.rtpTimestamp);
    }

    int result = rtcSendMessage(self.videoTrack, (const char *)accessUnit.bytes, (int)accessUnit.length);
    if (result < 0)
    {
        @synchronized (self)
        {
            self.lastError = RtcFailure(@"Sending an encoded Simulator frame", result);
        }
    }
}

- (int32_t)sendExternalAccessUnit:(NSData *)accessUnit
       presentationTimestampUs:(int64_t)presentationTimestampUs
                       keyFrame:(BOOL)keyFrame
                           error:(NSError **)error
{
    if (!self.externalVideo || accessUnit.length == 0)
    {
        if (error != NULL)
        {
            *error = CreateError(AnsightSimulatorRtcErrorExternalAccessUnitMissing, @"A valid external H.264 access unit is required.");
        }
        return -1;
    }

    uint32_t timestamp = 0;
    @synchronized (self)
    {
        if (self.shuttingDown)
        {
            if (error != NULL)
            {
                *error = CreateError(AnsightSimulatorRtcErrorExternalSessionShuttingDown, @"The external WebRTC session is shutting down.");
            }
            return -1;
        }

        if (self.videoTrack < 0 || !rtcIsOpen(self.videoTrack) || (self.forceKeyFrame && !keyFrame))
        {
            return 0;
        }

        if (presentationTimestampUs >= 0)
        {
            if (self.externalFirstPresentationTimestamp < 0)
            {
                self.externalFirstPresentationTimestamp = presentationTimestampUs;
                self.externalBaseRtpTimestamp = self.rtpTimestamp;
            }

            uint64_t elapsedMicroseconds = (uint64_t)MAX(
                presentationTimestampUs - self.externalFirstPresentationTimestamp,
                0);
            timestamp = self.externalBaseRtpTimestamp
                + (uint32_t)((elapsedMicroseconds * AnsightH264ClockRate) / 1000000ULL);
        }
        else
        {
            timestamp = self.rtpTimestamp + AnsightH264ClockRate / (uint32_t)MAX(self.framesPerSecond, 1);
        }

        self.rtpTimestamp = timestamp;
        if (keyFrame)
        {
            self.forceKeyFrame = NO;
        }
    }

    if (rtcGetBufferedAmount(self.videoTrack) > MaximumBufferedVideoBytes(self.averageBitRate))
    {
        return 1;
    }

    rtcSetTrackRtpTimestamp(self.videoTrack, timestamp);
    int result = rtcSendMessage(self.videoTrack, (const char *)accessUnit.bytes, (int)accessUnit.length);
    if (result < 0)
    {
        if (error != NULL)
        {
            *error = CreateError(AnsightSimulatorRtcErrorExternalFrameSendFailed, RtcFailure(@"Sending an external H.264 frame", result));
        }
        return -1;
    }
    return 1;
}

- (void)handleInputMessage:(const char *)message size:(int)size
{
    if (message == NULL || self.inputCallback == NULL || self.shuttingDown)
    {
        return;
    }

    int messageSize = size < 0 ? (int)strlen(message) : size;
    if (messageSize > 0)
    {
        self.inputCallback(message, messageSize, self.inputContext);
    }
}

- (void)handleRtcState:(rtcState)state
{
    if (state == RTC_CONNECTED)
    {
        [self requestKeyFrame];
    }
    else if (state == RTC_FAILED)
    {
        @synchronized (self)
        {
            self.lastError = @"The WebRTC peer connection failed.";
        }
    }
}

- (void)handleRtcGatheringState:(rtcGatheringState)state
{
    if (state != RTC_GATHERING_COMPLETE || self.peerConnection < 0)
    {
        return;
    }

    int required = rtcGetLocalDescription(self.peerConnection, NULL, 0);
    if (required <= 0)
    {
        @synchronized (self)
        {
            self.lastError = RtcFailure(@"Reading the gathered WebRTC offer", required);
        }
        return;
    }

    NSMutableData *buffer = [NSMutableData dataWithLength:(NSUInteger)required];
    int result = rtcGetLocalDescription(self.peerConnection, (char *)buffer.mutableBytes, required);
    if (result < 0)
    {
        @synchronized (self)
        {
            self.lastError = RtcFailure(@"Reading the gathered WebRTC offer", result);
        }
        return;
    }

    NSString *offer = [NSString stringWithUTF8String:(const char *)buffer.bytes];
    @synchronized (self)
    {
        self.answerSdp = offer;
    }
}

- (void)handleRemoteTrack:(int)remoteTrack
{
    if (remoteTrack < 0 || self.peerConnection < 0)
    {
        return;
    }

    int payloadTypes[16] = {};
    int payloadTypeCount = rtcGetTrackPayloadTypesForCodec(remoteTrack, "H264", payloadTypes, 16);
    char mid[128] = {};
    int midResult = rtcGetTrackMid(remoteTrack, mid, sizeof(mid));
    if (payloadTypeCount <= 0 || midResult < 0 || mid[0] == '\0')
    {
        @synchronized (self)
        {
            self.lastError = @"The browser did not offer a compatible H.264 video track.";
        }
        return;
    }

    uint32_t ssrc = arc4random();
    rtcTrackInit track = {};
    track.direction = RTC_DIRECTION_SENDONLY;
    track.codec = RTC_CODEC_H264;
    track.payloadType = payloadTypes[0];
    track.ssrc = ssrc;
    track.mid = mid;
    track.name = self.externalVideo ? "android" : "simulator";
    track.msid = self.externalVideo ? "ansight-android" : "ansight-simulator";
    track.trackId = self.externalVideo ? "android-video" : "simulator-video";
    track.profile = "profile-level-id=42e01f;packetization-mode=1;level-asymmetry-allowed=1";
    int configuredTrack = rtcAddTrackEx(self.peerConnection, &track);
    if (configuredTrack < 0)
    {
        @synchronized (self)
        {
            self.lastError = RtcFailure(@"Accepting the browser H.264 video track", configuredTrack);
        }
        return;
    }

    rtcSetUserPointer(configuredTrack, (__bridge void *)self);
    rtcSetErrorCallback(configuredTrack, AnsightRtcErrorCallback);
    rtcPacketizerInit packetizer = {};
    packetizer.ssrc = ssrc;
    packetizer.cname = "ansight-simulator";
    packetizer.payloadType = (uint8_t)payloadTypes[0];
    packetizer.clockRate = AnsightH264ClockRate;
    packetizer.sequenceNumber = (uint16_t)arc4random();
    packetizer.timestamp = self.rtpTimestamp;
    packetizer.maxFragmentSize = 1200;
    packetizer.nalSeparator = self.nalSeparator;
    int result = rtcSetH264Packetizer(configuredTrack, &packetizer);
    if (result >= 0)
    {
        result = rtcChainRtcpSrReporter(configuredTrack);
    }
    if (result >= 0)
    {
        result = rtcChainRtcpNackResponder(configuredTrack, RTC_DEFAULT_MAX_STORED_PACKET_COUNT);
    }
    if (result >= 0)
    {
        result = rtcChainPliHandler(configuredTrack, AnsightRtcPliCallback);
    }
    if (result >= 0)
    {
        result = rtcSetBufferedAmountLowThreshold(
            configuredTrack,
            MaximumBufferedVideoBytes(self.averageBitRate) / 2);
    }
    if (result >= 0)
    {
        result = rtcSetBufferedAmountLowCallback(configuredTrack, AnsightRtcBufferedAmountLowCallback);
    }
    if (result < 0)
    {
        @synchronized (self)
        {
            self.lastError = RtcFailure(@"Configuring the browser H.264 RTP packetizer", result);
        }
        return;
    }

    @synchronized (self)
    {
        self.videoTrack = configuredTrack;
        if (self.answerStarted)
        {
            return;
        }
        self.answerStarted = YES;
    }

    result = rtcSetLocalDescription(self.peerConnection, "answer");
    if (result < 0)
    {
        @synchronized (self)
        {
            self.lastError = RtcFailure(@"Creating the WebRTC answer", result);
        }
    }
}

- (void)handleRemoteDataChannel:(int)dataChannel
{
    if (dataChannel < 0)
    {
        return;
    }

    @synchronized (self)
    {
        self.inputDataChannel = dataChannel;
    }
    rtcSetUserPointer(dataChannel, (__bridge void *)self);
    rtcSetMessageCallback(dataChannel, AnsightRtcInputCallback);
    rtcSetErrorCallback(dataChannel, AnsightRtcErrorCallback);
}

- (int32_t)sendDataChannelMessage:(NSData *)message error:(NSError **)error
{
    if (message.length == 0)
    {
        if (error != NULL)
        {
            *error = CreateError(AnsightSimulatorRtcErrorDataChannelMessageMissing, @"A WebRTC data-channel message is required.");
        }
        return -1;
    }

    int dataChannel = -1;
    @synchronized (self)
    {
        if (self.shuttingDown)
        {
            return 0;
        }
        dataChannel = self.inputDataChannel;
    }
    if (dataChannel < 0 || !rtcIsOpen(dataChannel) || rtcGetBufferedAmount(dataChannel) > 1024 * 1024)
    {
        return 0;
    }

    int result = rtcSendMessage(dataChannel, (const char *)message.bytes, (int)message.length);
    if (result < 0)
    {
        if (error != NULL)
        {
            *error = CreateError(
                AnsightSimulatorRtcErrorDataChannelMessageSendFailed,
                RtcFailure(@"Sending a WebRTC data-channel message", result));
        }
        return -1;
    }
    return 1;
}

- (void)setRtcError:(const char *)message
{
    @synchronized (self)
    {
        self.lastError = message == NULL
            ? @"Unknown WebRTC transport error."
            : [NSString stringWithUTF8String:message];
    }
}

- (void)requestKeyFrame
{
    BOOL shouldRenderCurrentSurface = NO;
    @synchronized (self)
    {
        // scrcpy owns the external encoder. Setting this flag cannot request
        // an IDR from it, and would discard every intervening screen update.
        // Keep forwarding external frames until its next natural keyframe.
        if (!self.externalVideo)
        {
            self.forceKeyFrame = YES;
        }
        self.nextEncodeTime = 0;
        shouldRenderCurrentSurface = !self.externalVideo
            && self.currentSurface != nil
            && self.screenQueue != nil;
    }

    if (shouldRenderCurrentSurface)
    {
        if (dispatch_get_specific(AnsightScreenQueueKey) != NULL)
        {
            [self handleFrame];
        }
        else
        {
            dispatch_async(self.screenQueue, ^{
                [self handleFrame];
            });
        }
    }
}

- (int32_t)copyAnswerToBuffer:(char *)buffer capacity:(size_t)capacity error:(NSError **)error
{
    @synchronized (self)
    {
        if (self.answerSdp.length == 0)
        {
            if (self.lastError.length > 0)
            {
                if (error != NULL)
                {
                    *error = CreateError(AnsightSimulatorRtcErrorAnswerUnavailable, self.lastError);
                }
                return -1;
            }
            return 0;
        }

        size_t required = strlen(self.answerSdp.UTF8String) + 1;
        if (buffer == NULL || capacity < required)
        {
            if (error != NULL)
            {
                *error = CreateError(AnsightSimulatorRtcErrorAnswerBufferTooSmall, @"The WebRTC answer buffer is too small.");
            }
            return -1;
        }

        CopyText(self.answerSdp, buffer, capacity);
        return 1;
    }
}

- (BOOL)setOfferSdp:(NSString *)offerSdp error:(NSError **)error
{
    if (self.peerConnection < 0 || offerSdp.length == 0)
    {
        if (error != NULL)
        {
            *error = CreateError(AnsightSimulatorRtcErrorOfferMissing, @"A valid WebRTC offer is required.");
        }
        return NO;
    }

    int result = rtcSetRemoteDescription(self.peerConnection, offerSdp.UTF8String, "offer");
    if (result < 0)
    {
        if (error != NULL)
        {
            *error = CreateError(AnsightSimulatorRtcErrorOfferApplicationFailed, RtcFailure(@"Applying the WebRTC offer", result));
        }
        return NO;
    }
    return YES;
}

- (void)destroyEncoder
{
    VTCompressionSessionRef encoder = self.compressionSession;
    self.compressionSession = NULL;
    self.encodedWidth = 0;
    self.encodedHeight = 0;
    self.encodeInFlight = NO;
    if (encoder != NULL)
    {
        VTCompressionSessionCompleteFrames(encoder, kCMTimeInvalid);
        VTCompressionSessionInvalidate(encoder);
        CFRelease(encoder);
    }
}

- (void)shutdown
{
    @synchronized (self)
    {
        if (self.shuttingDown)
        {
            return;
        }
        self.shuttingDown = YES;
    }

    if (self.screenDescriptor != nil && self.screenCallbackUuid != nil)
    {
        @try
        {
            ((void (*)(id, SEL, id))objc_msgSend)(
                self.screenDescriptor,
                sel_registerName("unregisterScreenCallbacksWithUUID:"),
                self.screenCallbackUuid);
        }
        @catch (NSException *exception)
        {
            (void)exception;
        }
    }

    if (@available(macCatalyst 18.2, *))
    {
        if (self.windowStream != nil)
        {
            [self.windowStream stopCaptureWithCompletionHandler:^(NSError *error) {
                (void)error;
            }];
            self.windowStream = nil;
        }
    }

    if (self.screenQueue != nil && dispatch_get_specific(AnsightScreenQueueKey) == NULL)
    {
        dispatch_sync(self.screenQueue, ^{});
    }
    [self destroyEncoder];
    self.currentSurface = nil;
    self.screenDescriptor = nil;
    self.screenCallbackUuid = nil;

    if (self.inputDataChannel >= 0)
    {
        rtcDeleteDataChannel(self.inputDataChannel);
        self.inputDataChannel = -1;
    }
    if (self.videoTrack >= 0)
    {
        rtcDeleteTrack(self.videoTrack);
        self.videoTrack = -1;
    }
    if (self.peerConnection >= 0)
    {
        rtcDeletePeerConnection(self.peerConnection);
        self.peerConnection = -1;
    }
}

- (void)dealloc
{
    [self shutdown];
}

@end

@interface AnsightSimulatorPreviewView : NSObject

@property(nonatomic, strong) id serviceContext;
@property(nonatomic, strong) id deviceSet;
@property(nonatomic, strong) id deviceIo;
@property(nonatomic, strong) id screenDescriptor;
@property(nonatomic, strong) id currentSurface;
@property(nonatomic, strong) NSUUID *screenCallbackUuid;
@property(nonatomic, strong) dispatch_queue_t screenQueue;
@property(nonatomic, strong) dispatch_queue_t renderQueue;
@property(nonatomic, strong) id<MTLDevice> metalDevice;
@property(nonatomic, strong) id<MTLCommandQueue> commandQueue;
@property(nonatomic, strong) CIContext *ciContext;
@property(nonatomic, strong) CAMetalLayer *metalLayer;
@property(nonatomic, strong) NSDictionary *pixelBufferAttributes;
@property(nonatomic, assign) CGColorSpaceRef renderColorSpace;
@property(nonatomic, assign) CGSize drawableSize;
@property(nonatomic, assign) NSInteger screenPowerState;
@property(nonatomic, assign) int32_t framesPerSecond;
@property(nonatomic, assign) double nextRenderTime;
@property(nonatomic, assign) NSUInteger frameGeneration;
@property(nonatomic, assign) BOOL renderScheduled;
@property(nonatomic, assign) BOOL shuttingDown;

- (instancetype)initWithDeveloperDirectory:(NSString *)developerDirectory
                                deviceUdid:(NSString *)deviceUdid
                                     error:(NSError **)error;
- (void)handleFrame;
- (void)handleSurfacesChanged:(id)unmaskedSurface maskedSurface:(id)maskedSurface;
- (void)handleScreenPropertiesChanged:(id)properties;
- (BOOL)scheduleRenderIgnoringThrottle:(BOOL)ignoringThrottle;
- (void)renderSurface:(id)surface drawableSize:(CGSize)drawableSize;
- (void)setPreviewFramesPerSecond:(int32_t)framesPerSecond;
- (void)setPreviewDrawableSize:(CGSize)drawableSize;
- (void)shutdown;

@end

@implementation AnsightSimulatorPreviewView

- (instancetype)initWithDeveloperDirectory:(NSString *)developerDirectory
                                deviceUdid:(NSString *)deviceUdid
                                     error:(NSError **)error
{
    self = [super init];
    if (self == nil)
    {
        return nil;
    }

    self.screenQueue = dispatch_queue_create("com.ansight-ai.simulator-preview.screen", DISPATCH_QUEUE_SERIAL);
    dispatch_queue_set_specific(
        self.screenQueue,
        AnsightPreviewScreenQueueKey,
        (void *)AnsightPreviewScreenQueueKey,
        NULL);
    self.renderQueue = dispatch_queue_create("com.ansight-ai.simulator-preview.render", DISPATCH_QUEUE_SERIAL);
    dispatch_queue_set_specific(
        self.renderQueue,
        AnsightPreviewRenderQueueKey,
        (void *)AnsightPreviewRenderQueueKey,
        NULL);
    self.screenPowerState = -1;
    self.framesPerSecond = 60;

    self.metalDevice = MTLCreateSystemDefaultDevice();
    self.commandQueue = [self.metalDevice newCommandQueue];
    if (self.metalDevice == nil || self.commandQueue == nil)
    {
        if (error != NULL)
        {
            *error = CreateError(AnsightSimulatorRtcErrorPreviewMetalUnavailable, @"Metal is unavailable for the native Simulator preview.");
        }
        [self shutdown];
        return nil;
    }

    self.metalLayer = [CAMetalLayer layer];
    self.metalLayer.device = self.metalDevice;
    self.metalLayer.pixelFormat = MTLPixelFormatBGRA8Unorm;
    self.metalLayer.framebufferOnly = NO;
    self.metalLayer.opaque = YES;
    self.metalLayer.allowsNextDrawableTimeout = YES;
    self.pixelBufferAttributes = @{
        (NSString *)kCVPixelBufferIOSurfacePropertiesKey: @{},
        (NSString *)kCVPixelBufferMetalCompatibilityKey: @YES,
    };
    self.renderColorSpace = CGColorSpaceCreateWithName(kCGColorSpaceSRGB);
    self.ciContext = [CIContext contextWithMTLDevice:self.metalDevice
                                             options:@{
                                                 kCIContextWorkingColorSpace: [NSNull null],
                                                 kCIContextOutputColorSpace: [NSNull null],
                                             }];
    if (self.ciContext == nil
        || ![self loadSimulatorForDeveloperDirectory:developerDirectory error:error]
        || ![self resolveDevice:deviceUdid error:error]
        || ![self connectScreenWithError:error])
    {
        [self shutdown];
        return nil;
    }

    return self;
}

- (BOOL)loadSimulatorForDeveloperDirectory:(NSString *)developerDirectory error:(NSError **)error
{
    NSString *coreSimulatorPath = @"/Library/Developer/PrivateFrameworks/CoreSimulator.framework/CoreSimulator";
    NSString *simulatorKitPath = [developerDirectory stringByAppendingPathComponent:
        @"Library/PrivateFrameworks/SimulatorKit.framework/SimulatorKit"];
    if (dlopen(coreSimulatorPath.fileSystemRepresentation, RTLD_NOW | RTLD_LOCAL) == NULL)
    {
        if (error != NULL)
        {
            *error = CreateError(
                AnsightSimulatorRtcErrorPreviewCoreSimulatorUnavailable,
                [NSString stringWithFormat:@"Could not load CoreSimulator: %s", dlerror()]);
        }
        return NO;
    }

    if (dlopen(simulatorKitPath.fileSystemRepresentation, RTLD_NOW | RTLD_LOCAL) == NULL)
    {
        if (error != NULL)
        {
            *error = CreateError(
                AnsightSimulatorRtcErrorPreviewSimulatorKitUnavailable,
                [NSString stringWithFormat:@"Could not load SimulatorKit: %s", dlerror()]);
        }
        return NO;
    }

    Class serviceContextClass = NSClassFromString(@"SimServiceContext");
    SEL contextSelector = sel_registerName("sharedServiceContextForDeveloperDir:error:");
    if (serviceContextClass == Nil || ![serviceContextClass respondsToSelector:contextSelector])
    {
        if (error != NULL)
        {
            *error = CreateError(AnsightSimulatorRtcErrorPreviewServiceContextUnavailable, @"CoreSimulator service context is unavailable.");
        }
        return NO;
    }

    self.serviceContext = SendIdWithObjectAndError(serviceContextClass, contextSelector, developerDirectory, error);
    self.deviceSet = self.serviceContext == nil
        ? nil
        : SendIdWithError(self.serviceContext, sel_registerName("defaultDeviceSetWithError:"), error);
    return self.deviceSet != nil;
}

- (BOOL)resolveDevice:(NSString *)deviceUdid error:(NSError **)error
{
    NSDictionary *devicesByUdid = SendId(self.deviceSet, sel_registerName("devicesByUDID"));
    id device = devicesByUdid[deviceUdid];
    if (device == nil)
    {
        NSUUID *uuid = [[NSUUID alloc] initWithUUIDString:deviceUdid];
        device = uuid == nil ? nil : devicesByUdid[uuid];
    }

    if (device == nil)
    {
        for (id candidate in SendId(self.deviceSet, sel_registerName("devices")))
        {
            id candidateUdid = SendId(candidate, sel_registerName("UDID"));
            NSString *candidateText = [candidateUdid respondsToSelector:sel_registerName("UUIDString")]
                ? SendId(candidateUdid, sel_registerName("UUIDString"))
                : [candidateUdid description];
            if ([candidateText caseInsensitiveCompare:deviceUdid] == NSOrderedSame)
            {
                device = candidate;
                break;
            }
        }
    }

    if (device == nil)
    {
        if (error != NULL)
        {
            *error = CreateError(
                AnsightSimulatorRtcErrorPreviewDeviceNotFound,
                [NSString stringWithFormat:@"Simulator %@ was not found.", deviceUdid]);
        }
        return NO;
    }

    self.deviceIo = device;
    return YES;
}

- (BOOL)connectScreenWithError:(NSError **)error
{
    id device = self.deviceIo;
    Class deviceIoClass = NSClassFromString(@"SimDeviceIO");
    SEL ioSelector = sel_registerName("ioForSimDevice:errorQueue:errorHandler:");
    if (deviceIoClass == Nil || ![deviceIoClass respondsToSelector:ioSelector])
    {
        if (error != NULL)
        {
            *error = CreateError(AnsightSimulatorRtcErrorPreviewDisplayIoUnavailable, @"CoreSimulator display IO is unavailable.");
        }
        return NO;
    }

    id errorHandler = ^(NSError *ioError) {
        (void)ioError;
    };
    id deviceIo = SendDeviceQueueHandler(deviceIoClass, ioSelector, device, self.screenQueue, errorHandler);
    if (deviceIo == nil)
    {
        if (error != NULL)
        {
            *error = CreateError(AnsightSimulatorRtcErrorPreviewDisplayIoConnectionFailed, @"Could not connect to Simulator display IO.");
        }
        return NO;
    }

    self.deviceIo = deviceIo;
    SEL registerSelector = sel_registerName(
        "registerScreenCallbacksWithUUID:callbackQueue:frameCallback:surfacesChangedCallback:propertiesChangedCallback:");
    for (id port in SendId(deviceIo, sel_registerName("ioPorts")))
    {
        id descriptor = SendId(port, sel_registerName("descriptor"));
        if (![descriptor respondsToSelector:registerSelector]
            || ![descriptor respondsToSelector:sel_registerName("screenProperties")])
        {
            continue;
        }

        id properties = SendId(descriptor, sel_registerName("screenProperties"));
        NSInteger screenType = [properties respondsToSelector:sel_registerName("screenType")]
            ? SendInteger(properties, sel_registerName("screenType"))
            : -1;
        NSInteger powerState = [properties respondsToSelector:sel_registerName("powerState")]
            ? SendInteger(properties, sel_registerName("powerState"))
            : 0;
        if (screenType == 0)
        {
            self.screenDescriptor = descriptor;
            self.screenPowerState = powerState;
            break;
        }
    }

    if (self.screenDescriptor == nil)
    {
        if (error != NULL)
        {
            *error = CreateError(AnsightSimulatorRtcErrorPreviewPrimaryLcdUnavailable, @"The Simulator primary LCD surface is unavailable.");
        }
        return NO;
    }

    self.screenCallbackUuid = [NSUUID UUID];
    __weak AnsightSimulatorPreviewView *weakSelf = self;
    id frameCallback = ^{
        [weakSelf handleFrame];
    };
    id surfacesChangedCallback = ^(id unmaskedSurface, id maskedSurface) {
        [weakSelf handleSurfacesChanged:unmaskedSurface maskedSurface:maskedSurface];
    };
    id propertiesChangedCallback = ^(id properties) {
        [weakSelf handleScreenPropertiesChanged:properties];
    };

    @try
    {
        ((void (*)(id, SEL, id, id, id, id, id))objc_msgSend)(
            self.screenDescriptor,
            registerSelector,
            self.screenCallbackUuid,
            self.screenQueue,
            frameCallback,
            surfacesChangedCallback,
            propertiesChangedCallback);
    }
    @catch (NSException *exception)
    {
        if (error != NULL)
        {
            *error = CreateError(
                AnsightSimulatorRtcErrorPreviewCallbackRegistrationFailed,
                [NSString stringWithFormat:@"Could not register Simulator preview callbacks: %@: %@",
                                           exception.name,
                                           exception.reason ?: @"native exception"]);
        }
        return NO;
    }

    SEL framebufferSelector = sel_registerName("framebufferSurface");
    if ([self.screenDescriptor respondsToSelector:framebufferSelector])
    {
        [self handleSurfacesChanged:SendId(self.screenDescriptor, framebufferSelector)
                     maskedSurface:nil];
    }

    return YES;
}

- (void)handleSurfacesChanged:(id)unmaskedSurface maskedSurface:(id)maskedSurface
{
    (void)maskedSurface;
    @synchronized (self)
    {
        if (!self.shuttingDown)
        {
            self.currentSurface = unmaskedSurface;
            self.frameGeneration += 1;
        }
    }
}

- (void)handleFrame
{
    @synchronized (self)
    {
        if (!self.shuttingDown)
        {
            self.frameGeneration += 1;
        }
    }

    [self scheduleRenderIgnoringThrottle:NO];
}

- (BOOL)scheduleRenderIgnoringThrottle:(BOOL)ignoringThrottle
{
    @synchronized (self)
    {
        if (self.shuttingDown
            || self.currentSurface == nil
            || self.drawableSize.width <= 0
            || self.drawableSize.height <= 0)
        {
            return NO;
        }
        if (self.renderScheduled)
        {
            return YES;
        }

        double now = MonotonicSeconds();
        double frameInterval = 1.0 / (double)MAX(self.framesPerSecond, 1);
        if (!ignoringThrottle && self.nextRenderTime > 0 && now < self.nextRenderTime)
        {
            return NO;
        }
        self.nextRenderTime = now + frameInterval;
        self.renderScheduled = YES;
    }

    __weak AnsightSimulatorPreviewView *weakSelf = self;
    dispatch_async(self.renderQueue, ^{
        @autoreleasepool
        {
            AnsightSimulatorPreviewView *strongSelf = weakSelf;
            if (strongSelf == nil)
            {
                return;
            }

            id surface = nil;
            CGSize drawableSize = CGSizeZero;
            NSUInteger renderedGeneration = 0;
            @synchronized (strongSelf)
            {
                if (!strongSelf.shuttingDown)
                {
                    surface = strongSelf.currentSurface;
                    drawableSize = strongSelf.drawableSize;
                    renderedGeneration = strongSelf.frameGeneration;
                }
            }

            [strongSelf renderSurface:surface drawableSize:drawableSize];
            BOOL shouldScheduleLatestFrame = NO;
            double retryDelay = 0;
            @synchronized (strongSelf)
            {
                strongSelf.renderScheduled = NO;
                shouldScheduleLatestFrame =
                    !strongSelf.shuttingDown
                    && strongSelf.frameGeneration != renderedGeneration;
                if (shouldScheduleLatestFrame)
                {
                    retryDelay = MAX(0, strongSelf.nextRenderTime - MonotonicSeconds());
                }
            }

            if (shouldScheduleLatestFrame)
            {
                __weak AnsightSimulatorPreviewView *retryWeakSelf = strongSelf;
                dispatch_after(
                    dispatch_time(DISPATCH_TIME_NOW, (int64_t)(retryDelay * NSEC_PER_SEC)),
                    strongSelf.renderQueue,
                    ^{
                        [retryWeakSelf scheduleRenderIgnoringThrottle:YES];
                    });
            }
        }
    });
    return YES;
}

- (void)handleScreenPropertiesChanged:(id)properties
{
    if (properties == nil || ![properties respondsToSelector:sel_registerName("powerState")])
    {
        return;
    }

    NSInteger powerState = SendInteger(properties, sel_registerName("powerState"));
    @synchronized (self)
    {
        if (!self.shuttingDown)
        {
            self.screenPowerState = powerState;
        }
    }
}

- (void)setPreviewFramesPerSecond:(int32_t)framesPerSecond
{
    @synchronized (self)
    {
        self.framesPerSecond = framesPerSecond == 30 ? 30 : 60;
        self.nextRenderTime = 0;
    }
}

- (void)setPreviewDrawableSize:(CGSize)drawableSize
{
    CGSize normalizedDrawableSize = CGSizeMake(
        MAX(0, drawableSize.width),
        MAX(0, drawableSize.height));
    BOOL didChange = NO;
    @synchronized (self)
    {
        if (!CGSizeEqualToSize(self.drawableSize, normalizedDrawableSize))
        {
            self.drawableSize = normalizedDrawableSize;
            self.frameGeneration += 1;
            didChange = YES;
        }
    }

    if (didChange)
    {
        [self scheduleRenderIgnoringThrottle:YES];
    }
}

- (void)renderSurface:(id)surface drawableSize:(CGSize)drawableSize
{
    if (surface == nil || drawableSize.width <= 0 || drawableSize.height <= 0)
    {
        return;
    }

    IOSurfaceRef ioSurface = (__bridge IOSurfaceRef)surface;
    size_t surfaceWidth = IOSurfaceGetWidth(ioSurface);
    size_t surfaceHeight = IOSurfaceGetHeight(ioSurface);
    if (surfaceWidth == 0 || surfaceHeight == 0)
    {
        return;
    }

    CVPixelBufferRef pixelBuffer = NULL;
    CVReturn pixelResult = CVPixelBufferCreateWithIOSurface(
        kCFAllocatorDefault,
        ioSurface,
        (__bridge CFDictionaryRef)self.pixelBufferAttributes,
        &pixelBuffer);
    if (pixelResult != kCVReturnSuccess || pixelBuffer == NULL)
    {
        return;
    }

    CAMetalLayer *metalLayer = self.metalLayer;
    id<CAMetalDrawable> drawable = [metalLayer nextDrawable];
    if (drawable == nil)
    {
        CVPixelBufferRelease(pixelBuffer);
        return;
    }

    CGSize textureSize = CGSizeMake(drawable.texture.width, drawable.texture.height);
    if (textureSize.width <= 0 || textureSize.height <= 0)
    {
        CVPixelBufferRelease(pixelBuffer);
        return;
    }

    CGRect drawableBounds = CGRectMake(
        0,
        0,
        textureSize.width,
        textureSize.height);
    CGFloat renderScale = MIN(
        CGRectGetWidth(drawableBounds) / (CGFloat)surfaceWidth,
        CGRectGetHeight(drawableBounds) / (CGFloat)surfaceHeight);
    CGSize renderedSize = CGSizeMake(
        (CGFloat)surfaceWidth * renderScale,
        (CGFloat)surfaceHeight * renderScale);
    CGPoint renderedOrigin = CGPointMake(
        (CGRectGetWidth(drawableBounds) - renderedSize.width) / 2,
        (CGRectGetHeight(drawableBounds) - renderedSize.height) / 2);
    CIImage *surfaceImage = [CIImage imageWithCVPixelBuffer:pixelBuffer];
    CIImage *scaledImage = [surfaceImage imageByApplyingTransform:
        CGAffineTransformMakeScale(renderScale, renderScale)];
    CIImage *positionedImage = [scaledImage imageByApplyingTransform:
        CGAffineTransformMakeTranslation(renderedOrigin.x, renderedOrigin.y)];
    CIImage *background = [[CIImage imageWithColor:
        [CIColor colorWithRed:0 green:0 blue:0 alpha:1]] imageByCroppingToRect:drawableBounds];
    CIImage *composite = [positionedImage imageByCompositingOverImage:background];
    id<MTLCommandBuffer> commandBuffer = [self.commandQueue commandBuffer];
    [self.ciContext render:composite
              toMTLTexture:drawable.texture
             commandBuffer:commandBuffer
                    bounds:drawableBounds
                colorSpace:self.renderColorSpace];
    [commandBuffer presentDrawable:drawable];
    [commandBuffer commit];
    CVPixelBufferRelease(pixelBuffer);
}

- (void)shutdown
{
    @synchronized (self)
    {
        if (self.shuttingDown)
        {
            return;
        }
        self.shuttingDown = YES;
    }

    if (self.screenDescriptor != nil && self.screenCallbackUuid != nil)
    {
        @try
        {
            ((void (*)(id, SEL, id))objc_msgSend)(
                self.screenDescriptor,
                sel_registerName("unregisterScreenCallbacksWithUUID:"),
                self.screenCallbackUuid);
        }
        @catch (NSException *exception)
        {
            (void)exception;
        }
    }

    if (self.screenQueue != nil && dispatch_get_specific(AnsightPreviewScreenQueueKey) == NULL)
    {
        dispatch_sync(self.screenQueue, ^{});
    }
    if (self.renderQueue != nil && dispatch_get_specific(AnsightPreviewRenderQueueKey) == NULL)
    {
        dispatch_sync(self.renderQueue, ^{});
    }

    self.currentSurface = nil;
    self.screenDescriptor = nil;
    self.screenCallbackUuid = nil;
    self.deviceIo = nil;
    self.deviceSet = nil;
    self.serviceContext = nil;
    self.pixelBufferAttributes = nil;
    if (self.renderColorSpace != NULL)
    {
        CGColorSpaceRelease(self.renderColorSpace);
        self.renderColorSpace = NULL;
    }
}

- (void)dealloc
{
    [self shutdown];
}

@end

static AnsightSimulatorRtcSession *SessionFromPointer(void *pointer)
{
    return pointer == NULL ? nil : (__bridge AnsightSimulatorRtcSession *)pointer;
}

static void AnsightRtcDescriptionCallback(int peerConnection, const char *sdp, const char *type, void *pointer)
{
    (void)peerConnection;
    (void)sdp;
    (void)type;
    (void)pointer;
}

static void AnsightRtcStateCallback(int peerConnection, rtcState state, void *pointer)
{
    (void)peerConnection;
    [SessionFromPointer(pointer) handleRtcState:state];
}

static void AnsightRtcGatheringCallback(int peerConnection, rtcGatheringState state, void *pointer)
{
    (void)peerConnection;
    [SessionFromPointer(pointer) handleRtcGatheringState:state];
}

static void AnsightRtcTrackCallback(int peerConnection, int track, void *pointer)
{
    (void)peerConnection;
    AnsightSimulatorRtcSession *session = SessionFromPointer(pointer);
    if (session == nil)
    {
        return;
    }
    dispatch_async(dispatch_get_global_queue(QOS_CLASS_USER_INITIATED, 0), ^{
        [session handleRemoteTrack:track];
    });
}

static void AnsightRtcDataChannelCallback(int peerConnection, int dataChannel, void *pointer)
{
    (void)peerConnection;
    [SessionFromPointer(pointer) handleRemoteDataChannel:dataChannel];
}

static void AnsightRtcErrorCallback(int identifier, const char *message, void *pointer)
{
    (void)identifier;
    [SessionFromPointer(pointer) setRtcError:message];
}

static void AnsightRtcInputCallback(int dataChannel, const char *message, int size, void *pointer)
{
    (void)dataChannel;
    [SessionFromPointer(pointer) handleInputMessage:message size:size];
}

static void AnsightRtcBufferedAmountLowCallback(int track, void *pointer)
{
    [SessionFromPointer(pointer) handleVideoBufferedAmountLow:track];
}

static void AnsightRtcPliCallback(int track, void *pointer)
{
    (void)track;
    [SessionFromPointer(pointer) requestKeyFrame];
}

static void AnsightVideoCompressionCallback(
    void *outputCallbackRefCon,
    void *sourceFrameRefCon,
    OSStatus status,
    VTEncodeInfoFlags infoFlags,
    CMSampleBufferRef sampleBuffer)
{
    (void)sourceFrameRefCon;
    (void)infoFlags;
    [SessionFromPointer(outputCallbackRefCon) handleEncodedSample:sampleBuffer status:status];
}

AnsightSimulatorRtcSessionRef AnsightSimulatorRtcSessionCreate(
    const char *developerDirectory,
    const char *deviceUdid,
    int32_t framesPerSecond,
    int32_t averageBitRate,
    AnsightSimulatorRtcInputCallback inputCallback,
    void *inputContext,
    char *errorBuffer,
    size_t errorBufferCapacity)
{
    @autoreleasepool
    {
        if (developerDirectory == NULL || deviceUdid == NULL || framesPerSecond <= 0 || averageBitRate <= 0)
        {
            CopyText(@"Developer directory, simulator UDID, frame rate, and bit rate are required.", errorBuffer, errorBufferCapacity);
            return NULL;
        }

        NSError *error = nil;
        AnsightSimulatorRtcSession *session = [[AnsightSimulatorRtcSession alloc]
            initWithDeveloperDirectory:[NSString stringWithUTF8String:developerDirectory]
            deviceUdid:[NSString stringWithUTF8String:deviceUdid]
            framesPerSecond:framesPerSecond
            averageBitRate:averageBitRate
            inputCallback:inputCallback
            inputContext:inputContext
            error:&error];
        if (session == nil)
        {
            CopyText(error.localizedDescription, errorBuffer, errorBufferCapacity);
            return NULL;
        }
        return (AnsightSimulatorRtcSessionRef)CFBridgingRetain(session);
    }
}

AnsightSimulatorRtcSessionRef AnsightExternalH264RtcSessionCreate(
    const char *deviceIdentifier,
    int32_t framesPerSecond,
    AnsightSimulatorRtcInputCallback inputCallback,
    void *inputContext,
    char *errorBuffer,
    size_t errorBufferCapacity)
{
    @autoreleasepool
    {
        if (deviceIdentifier == NULL || framesPerSecond <= 0)
        {
            CopyText(@"Device identifier and frame rate are required.", errorBuffer, errorBufferCapacity);
            return NULL;
        }

        NSError *error = nil;
        AnsightSimulatorRtcSession *session = [[AnsightSimulatorRtcSession alloc]
            initExternalWithFramesPerSecond:framesPerSecond
            inputCallback:inputCallback
            inputContext:inputContext
            error:&error];
        if (session == nil)
        {
            CopyText(error.localizedDescription, errorBuffer, errorBufferCapacity);
            return NULL;
        }
        return (AnsightSimulatorRtcSessionRef)CFBridgingRetain(session);
    }
}

bool AnsightMacWindowRtcIsSupported(void)
{
    if (@available(macCatalyst 18.2, *))
    {
        return true;
    }
    return false;
}

AnsightSimulatorRtcSessionRef AnsightMacWindowRtcSessionCreate(
    const char *deviceIdentifier,
    int32_t processIdentifier,
    int32_t framesPerSecond,
    int32_t averageBitRate,
    AnsightSimulatorRtcInputCallback inputCallback,
    void *inputContext,
    char *errorBuffer,
    size_t errorBufferCapacity)
{
    @autoreleasepool
    {
        if (deviceIdentifier == NULL
            || processIdentifier <= 0
            || framesPerSecond <= 0
            || averageBitRate <= 0)
        {
            CopyText(@"SDK target identifier, local process ID, frame rate, and bit rate are required.",
                     errorBuffer,
                     errorBufferCapacity);
            return NULL;
        }

        NSError *error = nil;
        AnsightSimulatorRtcSession *session = [[AnsightSimulatorRtcSession alloc]
            initMacWindowWithProcessIdentifier:processIdentifier
            framesPerSecond:framesPerSecond
            averageBitRate:averageBitRate
            inputCallback:inputCallback
            inputContext:inputContext
            error:&error];
        if (session == nil)
        {
            CopyText(error.localizedDescription, errorBuffer, errorBufferCapacity);
            return NULL;
        }
        return (AnsightSimulatorRtcSessionRef)CFBridgingRetain(session);
    }
}

bool AnsightMacWindowInputIsTrusted(bool prompt)
{
    void *handle = AnsightApplicationServicesHandle();
    AnsightAXIsProcessTrustedWithOptionsFunction isTrusted = handle == NULL
        ? NULL
        : (AnsightAXIsProcessTrustedWithOptionsFunction)dlsym(handle, "AXIsProcessTrustedWithOptions");
    if (isTrusted == NULL)
    {
        return false;
    }

    // Apple explicitly permits NULL when no options are needed. Passing an empty
    // Objective-C dictionary crashes AXIsProcessTrustedWithOptions on macOS 26.5.
    if (!prompt)
    {
        return isTrusted(NULL) != false;
    }

    CFStringRef *exportedPromptKey =
        (CFStringRef *)dlsym(handle, "kAXTrustedCheckOptionPrompt");
    CFStringRef promptKey = exportedPromptKey == NULL || *exportedPromptKey == NULL
        ? CFSTR("AXTrustedCheckOptionPrompt")
        : *exportedPromptKey;
    const void *keys[] = { promptKey };
    const void *values[] = { kCFBooleanTrue };
    CFDictionaryRef options = CFDictionaryCreate(
        kCFAllocatorDefault,
        keys,
        values,
        1,
        &kCFTypeDictionaryKeyCallBacks,
        &kCFTypeDictionaryValueCallBacks);
    if (options == NULL)
    {
        return false;
    }

    Boolean result = isTrusted(options);
    CFRelease(options);
    return result != false;
}

bool AnsightMacWindowScreenCaptureIsAuthorized(void)
{
    return AnsightHasScreenCaptureAccess();
}

bool AnsightMacWindowInputSendPointer(
    int32_t processIdentifier,
    int32_t phase,
    int32_t button,
    double normalizedX,
    double normalizedY,
    double scrollDeltaX,
    double scrollDeltaY,
    char *errorBuffer,
    size_t errorBufferCapacity)
{
    @autoreleasepool
    {
        CGRect windowBounds = CGRectZero;
        if (!FindLargestVisibleWindowBounds(processIdentifier, &windowBounds))
        {
            CopyText(@"The SDK process no longer has a visible macOS window.", errorBuffer, errorBufferCapacity);
            return false;
        }

        void *handle = AnsightCoreGraphicsHandle();
        AnsightCGEventPostToPidFunction postEvent = MacEventPostFunction();
        if (handle == NULL || postEvent == NULL)
        {
            CopyText(@"Quartz pointer event forwarding is unavailable.", errorBuffer, errorBufferCapacity);
            return false;
        }

        double clampedX = MIN(MAX(normalizedX, 0.0), 1.0);
        double clampedY = MIN(MAX(normalizedY, 0.0), 1.0);
        CGPoint point = CGPointMake(
            CGRectGetMinX(windowBounds) + clampedX * MAX(windowBounds.size.width - 1.0, 0.0),
            CGRectGetMinY(windowBounds) + clampedY * MAX(windowBounds.size.height - 1.0, 0.0));
        CFTypeRef event = NULL;
        if (phase == 4)
        {
            AnsightCGEventCreateScrollWheelEventFunction createScrollEvent =
                (AnsightCGEventCreateScrollWheelEventFunction)dlsym(
                    handle,
                    "CGEventCreateScrollWheelEvent2");
            AnsightCGEventSetLocationFunction setEventLocation =
                (AnsightCGEventSetLocationFunction)dlsym(handle, "CGEventSetLocation");
            if (createScrollEvent == NULL || setEventLocation == NULL)
            {
                CopyText(@"Quartz scroll event forwarding is unavailable.", errorBuffer, errorBufferCapacity);
                return false;
            }

            int32_t verticalDelta = (int32_t)llround(-scrollDeltaY);
            int32_t horizontalDelta = (int32_t)llround(-scrollDeltaX);
            if (verticalDelta == 0 && scrollDeltaY != 0)
            {
                verticalDelta = scrollDeltaY > 0 ? -1 : 1;
            }
            if (horizontalDelta == 0 && scrollDeltaX != 0)
            {
                horizontalDelta = scrollDeltaX > 0 ? -1 : 1;
            }

            // CGScrollEventUnitPixel=0. wheel1 is vertical; wheel2 is horizontal.
            event = createScrollEvent(NULL, 0, 2, verticalDelta, horizontalDelta, 0);
            if (event != NULL)
            {
                setEventLocation(event, point);
            }
        }
        else
        {
            AnsightCGEventCreateMouseEventFunction createMouseEvent =
                (AnsightCGEventCreateMouseEventFunction)dlsym(handle, "CGEventCreateMouseEvent");
            if (createMouseEvent == NULL)
            {
                CopyText(@"Quartz mouse event forwarding is unavailable.", errorBuffer, errorBufferCapacity);
                return false;
            }

            uint32_t mouseButton = button < 0 ? 0 : (uint32_t)button;
            uint32_t eventType = 0;
            switch (phase)
            {
                case 0:
                    eventType = button == 1 ? 3 : button == 2 ? 25 : 1;
                    break;
                case 1:
                    eventType = button < 0 ? 5 : button == 1 ? 7 : button == 2 ? 27 : 6;
                    break;
                case 2:
                case 3:
                    eventType = button == 1 ? 4 : button == 2 ? 26 : 2;
                    break;
                default:
                    CopyText(@"The macOS pointer phase is invalid.", errorBuffer, errorBufferCapacity);
                    return false;
            }

            event = createMouseEvent(NULL, eventType, point, mouseButton);
        }

        if (event == NULL)
        {
            CopyText(@"Could not create the macOS pointer event.", errorBuffer, errorBufferCapacity);
            return false;
        }
        postEvent(processIdentifier, event);
        CFRelease(event);
        return true;
    }
}

bool AnsightMacWindowInputSendKey(
    int32_t processIdentifier,
    uint32_t usageCode,
    bool isKeyDown,
    char *errorBuffer,
    size_t errorBufferCapacity)
{
    @autoreleasepool
    {
        uint16_t virtualKey = MacVirtualKeyForHidUsage(usageCode);
        if (virtualKey == UINT16_MAX)
        {
            CopyText([NSString stringWithFormat:@"USB HID usage %u is not mapped for macOS.", usageCode],
                     errorBuffer,
                     errorBufferCapacity);
            return false;
        }

        void *handle = AnsightCoreGraphicsHandle();
        AnsightCGEventCreateKeyboardEventFunction createKeyboardEvent = handle == NULL
            ? NULL
            : (AnsightCGEventCreateKeyboardEventFunction)dlsym(handle, "CGEventCreateKeyboardEvent");
        AnsightCGEventPostToPidFunction postEvent = MacEventPostFunction();
        if (createKeyboardEvent == NULL || postEvent == NULL)
        {
            CopyText(@"Quartz keyboard event forwarding is unavailable.", errorBuffer, errorBufferCapacity);
            return false;
        }

        CFTypeRef event = createKeyboardEvent(NULL, virtualKey, isKeyDown);
        if (event == NULL)
        {
            CopyText(@"Could not create the macOS keyboard event.", errorBuffer, errorBufferCapacity);
            return false;
        }
        postEvent(processIdentifier, event);
        CFRelease(event);
        return true;
    }
}

bool AnsightMacWindowInputSendText(
    int32_t processIdentifier,
    const char *text,
    char *errorBuffer,
    size_t errorBufferCapacity)
{
    @autoreleasepool
    {
        if (text == NULL)
        {
            CopyText(@"Text is required for macOS keyboard forwarding.", errorBuffer, errorBufferCapacity);
            return false;
        }

        NSString *value = [NSString stringWithUTF8String:text];
        if (value == nil)
        {
            CopyText(@"Text must be valid UTF-8.", errorBuffer, errorBufferCapacity);
            return false;
        }
        if (value.length == 0)
        {
            return true;
        }

        void *handle = AnsightCoreGraphicsHandle();
        AnsightCGEventCreateKeyboardEventFunction createKeyboardEvent = handle == NULL
            ? NULL
            : (AnsightCGEventCreateKeyboardEventFunction)dlsym(handle, "CGEventCreateKeyboardEvent");
        AnsightCGEventKeyboardSetUnicodeStringFunction setUnicodeString = handle == NULL
            ? NULL
            : (AnsightCGEventKeyboardSetUnicodeStringFunction)dlsym(handle, "CGEventKeyboardSetUnicodeString");
        AnsightCGEventPostToPidFunction postEvent = MacEventPostFunction();
        if (createKeyboardEvent == NULL || setUnicodeString == NULL || postEvent == NULL)
        {
            CopyText(@"Quartz Unicode keyboard forwarding is unavailable.", errorBuffer, errorBufferCapacity);
            return false;
        }

        NSUInteger index = 0;
        while (index < value.length)
        {
            unichar characters[2] = {};
            characters[0] = [value characterAtIndex:index++];
            size_t length = 1;
            if (CFStringIsSurrogateHighCharacter(characters[0])
                && index < value.length
                && CFStringIsSurrogateLowCharacter([value characterAtIndex:index]))
            {
                characters[1] = [value characterAtIndex:index++];
                length = 2;
            }

            CFTypeRef keyDown = createKeyboardEvent(NULL, 0, true);
            CFTypeRef keyUp = createKeyboardEvent(NULL, 0, false);
            if (keyDown == NULL || keyUp == NULL)
            {
                if (keyDown != NULL) CFRelease(keyDown);
                if (keyUp != NULL) CFRelease(keyUp);
                CopyText(@"Could not create a macOS Unicode keyboard event.", errorBuffer, errorBufferCapacity);
                return false;
            }
            setUnicodeString(keyDown, length, characters);
            setUnicodeString(keyUp, length, characters);
            postEvent(processIdentifier, keyDown);
            postEvent(processIdentifier, keyUp);
            CFRelease(keyDown);
            CFRelease(keyUp);
        }
        return true;
    }
}

int32_t AnsightExternalH264RtcSessionSendAccessUnit(
    AnsightSimulatorRtcSessionRef session,
    const uint8_t *data,
    int32_t size,
    int64_t presentationTimestampMicroseconds,
    bool isKeyFrame,
    char *errorBuffer,
    size_t errorBufferCapacity)
{
    @autoreleasepool
    {
        if (session == NULL || data == NULL || size <= 0)
        {
            CopyText(@"A valid external WebRTC session and H.264 access unit are required.", errorBuffer, errorBufferCapacity);
            return -1;
        }

        NSError *error = nil;
        NSData *accessUnit = [NSData dataWithBytesNoCopy:(void *)data
                                                  length:(NSUInteger)size
                                            freeWhenDone:NO];
        int32_t result = [(__bridge AnsightSimulatorRtcSession *)session
            sendExternalAccessUnit:accessUnit
            presentationTimestampUs:presentationTimestampMicroseconds
            keyFrame:isKeyFrame
            error:&error];
        if (result < 0)
        {
            CopyText(error.localizedDescription, errorBuffer, errorBufferCapacity);
        }
        return result;
    }
}

int32_t AnsightSimulatorRtcSessionCopyAnswer(
    AnsightSimulatorRtcSessionRef session,
    char *answerBuffer,
    size_t answerBufferCapacity,
    char *errorBuffer,
    size_t errorBufferCapacity)
{
    @autoreleasepool
    {
        if (session == NULL)
        {
            CopyText(@"A valid Simulator WebRTC session is required.", errorBuffer, errorBufferCapacity);
            return -1;
        }

        NSError *error = nil;
        int32_t result = [(__bridge AnsightSimulatorRtcSession *)session
            copyAnswerToBuffer:answerBuffer
            capacity:answerBufferCapacity
            error:&error];
        if (result < 0)
        {
            CopyText(error.localizedDescription, errorBuffer, errorBufferCapacity);
        }
        return result;
    }
}

bool AnsightSimulatorRtcSessionSetOffer(
    AnsightSimulatorRtcSessionRef session,
    const char *offerSdp,
    char *errorBuffer,
    size_t errorBufferCapacity)
{
    @autoreleasepool
    {
        if (session == NULL || offerSdp == NULL)
        {
            CopyText(@"A valid Simulator WebRTC session and offer are required.", errorBuffer, errorBufferCapacity);
            return false;
        }

        NSError *error = nil;
        BOOL success = [(__bridge AnsightSimulatorRtcSession *)session
            setOfferSdp:[NSString stringWithUTF8String:offerSdp]
            error:&error];
        if (!success)
        {
            CopyText(error.localizedDescription, errorBuffer, errorBufferCapacity);
        }
        return success;
    }
}

int32_t AnsightSimulatorRtcSessionSendMessage(
    AnsightSimulatorRtcSessionRef session,
    const uint8_t *data,
    int32_t size,
    char *errorBuffer,
    size_t errorBufferCapacity)
{
    @autoreleasepool
    {
        if (session == NULL || data == NULL || size <= 0)
        {
            CopyText(@"A valid WebRTC session and data-channel message are required.", errorBuffer, errorBufferCapacity);
            return -1;
        }

        NSError *error = nil;
        NSData *message = [NSData dataWithBytesNoCopy:(void *)data
                                               length:(NSUInteger)size
                                         freeWhenDone:NO];
        int32_t result = [(__bridge AnsightSimulatorRtcSession *)session
            sendDataChannelMessage:message
            error:&error];
        if (result < 0)
        {
            CopyText(error.localizedDescription, errorBuffer, errorBufferCapacity);
        }
        return result;
    }
}

void AnsightSimulatorRtcSessionDestroy(AnsightSimulatorRtcSessionRef session)
{
    if (session != NULL)
    {
        AnsightSimulatorRtcSession *sessionValue = (__bridge AnsightSimulatorRtcSession *)session;
        [sessionValue shutdown];
        CFBridgingRelease(session);
    }
}

AnsightSimulatorPreviewViewRef AnsightSimulatorPreviewViewCreate(
    const char *developerDirectory,
    const char *deviceUdid,
    char *errorBuffer,
    size_t errorBufferCapacity)
{
    @autoreleasepool
    {
        if (developerDirectory == NULL || deviceUdid == NULL)
        {
            CopyText(
                @"Developer directory and simulator UDID are required.",
                errorBuffer,
                errorBufferCapacity);
            return NULL;
        }

        NSError *error = nil;
        AnsightSimulatorPreviewView *previewView = [[AnsightSimulatorPreviewView alloc]
            initWithDeveloperDirectory:[NSString stringWithUTF8String:developerDirectory]
            deviceUdid:[NSString stringWithUTF8String:deviceUdid]
            error:&error];
        if (previewView == nil)
        {
            CopyText(error.localizedDescription, errorBuffer, errorBufferCapacity);
            return NULL;
        }

        return (AnsightSimulatorPreviewViewRef)CFBridgingRetain(previewView);
    }
}

void *AnsightSimulatorPreviewViewGetLayer(AnsightSimulatorPreviewViewRef previewView)
{
    if (previewView == NULL)
    {
        return NULL;
    }

    AnsightSimulatorPreviewView *previewValue = (__bridge AnsightSimulatorPreviewView *)previewView;
    return (__bridge void *)previewValue.metalLayer;
}

bool AnsightSimulatorPreviewViewGetSurfaceSize(
    AnsightSimulatorPreviewViewRef previewView,
    int32_t *width,
    int32_t *height)
{
    if (previewView == NULL || width == NULL || height == NULL)
    {
        return false;
    }

    AnsightSimulatorPreviewView *previewValue = (__bridge AnsightSimulatorPreviewView *)previewView;
    id surface = nil;
    @synchronized (previewValue)
    {
        surface = previewValue.currentSurface;
    }
    if (surface == nil)
    {
        return false;
    }

    IOSurfaceRef ioSurface = (__bridge IOSurfaceRef)surface;
    size_t surfaceWidth = IOSurfaceGetWidth(ioSurface);
    size_t surfaceHeight = IOSurfaceGetHeight(ioSurface);
    if (surfaceWidth == 0 || surfaceHeight == 0 || surfaceWidth > INT32_MAX || surfaceHeight > INT32_MAX)
    {
        return false;
    }

    *width = (int32_t)surfaceWidth;
    *height = (int32_t)surfaceHeight;
    return true;
}

bool AnsightSimulatorPreviewViewRenderCurrentFrame(
    AnsightSimulatorPreviewViewRef previewView)
{
    if (previewView == NULL)
    {
        return false;
    }

    AnsightSimulatorPreviewView *previewValue = (__bridge AnsightSimulatorPreviewView *)previewView;
    return [previewValue scheduleRenderIgnoringThrottle:YES];
}

void AnsightSimulatorPreviewViewSetFramesPerSecond(
    AnsightSimulatorPreviewViewRef previewView,
    int32_t framesPerSecond)
{
    if (previewView == NULL)
    {
        return;
    }

    AnsightSimulatorPreviewView *previewValue = (__bridge AnsightSimulatorPreviewView *)previewView;
    [previewValue setPreviewFramesPerSecond:framesPerSecond];
}

void AnsightSimulatorPreviewViewSetDrawableSize(
    AnsightSimulatorPreviewViewRef previewView,
    double width,
    double height)
{
    if (previewView == NULL)
    {
        return;
    }

    AnsightSimulatorPreviewView *previewValue = (__bridge AnsightSimulatorPreviewView *)previewView;
    [previewValue setPreviewDrawableSize:CGSizeMake(width, height)];
}

bool AnsightSimulatorPreviewViewGetIsDeviceLocked(
    AnsightSimulatorPreviewViewRef previewView,
    bool *isDeviceLocked)
{
    if (previewView == NULL || isDeviceLocked == NULL)
    {
        return false;
    }

    AnsightSimulatorPreviewView *previewValue = (__bridge AnsightSimulatorPreviewView *)previewView;
    NSInteger screenPowerState = -1;
    @synchronized (previewValue)
    {
        screenPowerState = previewValue.screenPowerState;
    }
    if (screenPowerState < 0)
    {
        return false;
    }

    *isDeviceLocked = screenPowerState == 0;
    return true;
}

void AnsightSimulatorPreviewViewDestroy(AnsightSimulatorPreviewViewRef previewView)
{
    if (previewView != NULL)
    {
        AnsightSimulatorPreviewView *previewValue = (__bridge AnsightSimulatorPreviewView *)previewView;
        [previewValue shutdown];
        CFBridgingRelease(previewView);
    }
}

AnsightExternalH264PreviewViewRef AnsightExternalH264PreviewViewCreate(
    char *errorBuffer,
    size_t errorBufferCapacity)
{
    @autoreleasepool
    {
        NSError *error = nil;
        AnsightExternalH264PreviewView *previewView = [[AnsightExternalH264PreviewView alloc]
            initWithError:&error];
        if (previewView == nil)
        {
            CopyText(error.localizedDescription, errorBuffer, errorBufferCapacity);
            return NULL;
        }
        return (AnsightExternalH264PreviewViewRef)CFBridgingRetain(previewView);
    }
}

void *AnsightExternalH264PreviewViewGetLayer(AnsightExternalH264PreviewViewRef previewView)
{
    if (previewView == NULL)
    {
        return NULL;
    }
    AnsightExternalH264PreviewView *previewValue = (__bridge AnsightExternalH264PreviewView *)previewView;
    return (__bridge void *)previewValue.displayLayer;
}

bool AnsightExternalH264PreviewViewEnqueueAccessUnit(
    AnsightExternalH264PreviewViewRef previewView,
    const uint8_t *data,
    int32_t size,
    int64_t presentationTimestampMicroseconds,
    bool isKeyFrame,
    char *errorBuffer,
    size_t errorBufferCapacity)
{
    @autoreleasepool
    {
        if (previewView == NULL || data == NULL || size <= 0)
        {
            CopyText(@"A valid native Android preview and H.264 access unit are required.", errorBuffer, errorBufferCapacity);
            return false;
        }
        NSData *accessUnit = [NSData dataWithBytesNoCopy:(void *)data
                                                  length:(NSUInteger)size
                                            freeWhenDone:NO];
        NSError *error = nil;
        BOOL success = [(__bridge AnsightExternalH264PreviewView *)previewView
            enqueueAccessUnit:accessUnit
            presentationTimestampUs:presentationTimestampMicroseconds
            keyFrame:isKeyFrame
            error:&error];
        if (!success)
        {
            CopyText(error.localizedDescription, errorBuffer, errorBufferCapacity);
        }
        return success;
    }
}

void AnsightExternalH264PreviewViewDestroy(AnsightExternalH264PreviewViewRef previewView)
{
    if (previewView != NULL)
    {
        AnsightExternalH264PreviewView *previewValue = (__bridge AnsightExternalH264PreviewView *)previewView;
        [previewValue shutdown];
        CFBridgingRelease(previewView);
    }
}

AnsightMacWindowPreviewViewRef AnsightMacWindowPreviewViewCreate(
    int32_t processIdentifier,
    int32_t framesPerSecond,
    char *errorBuffer,
    size_t errorBufferCapacity)
{
    @autoreleasepool
    {
        if (processIdentifier <= 0 || framesPerSecond <= 0)
        {
            CopyText(
                @"A local macOS process ID and frame rate are required.",
                errorBuffer,
                errorBufferCapacity);
            return NULL;
        }

        NSError *error = nil;
        AnsightMacWindowPreviewView *previewView = [[AnsightMacWindowPreviewView alloc]
            initWithProcessIdentifier:processIdentifier
            framesPerSecond:framesPerSecond
            error:&error];
        if (previewView == nil)
        {
            CopyText(error.localizedDescription, errorBuffer, errorBufferCapacity);
            return NULL;
        }
        return (AnsightMacWindowPreviewViewRef)CFBridgingRetain(previewView);
    }
}

void *AnsightMacWindowPreviewViewGetLayer(AnsightMacWindowPreviewViewRef previewView)
{
    if (previewView == NULL)
    {
        return NULL;
    }
    AnsightMacWindowPreviewView *previewValue = (__bridge AnsightMacWindowPreviewView *)previewView;
    return (__bridge void *)previewValue.displayLayer;
}

bool AnsightMacWindowPreviewViewGetSurfaceSize(
    AnsightMacWindowPreviewViewRef previewView,
    int32_t *width,
    int32_t *height)
{
    if (previewView == NULL || width == NULL || height == NULL)
    {
        return false;
    }

    AnsightMacWindowPreviewView *previewValue = (__bridge AnsightMacWindowPreviewView *)previewView;
    CGSize surfaceSize = CGSizeZero;
    @synchronized (previewValue)
    {
        surfaceSize = previewValue.surfaceSize;
    }
    if (surfaceSize.width <= 0 || surfaceSize.height <= 0)
    {
        return false;
    }

    *width = (int32_t)llround(surfaceSize.width);
    *height = (int32_t)llround(surfaceSize.height);
    return true;
}

bool AnsightMacWindowPreviewViewCopyLastError(
    AnsightMacWindowPreviewViewRef previewView,
    char *errorBuffer,
    size_t errorBufferCapacity)
{
    if (previewView == NULL)
    {
        return false;
    }

    AnsightMacWindowPreviewView *previewValue = (__bridge AnsightMacWindowPreviewView *)previewView;
    NSString *lastError = nil;
    @synchronized (previewValue)
    {
        lastError = previewValue.lastError;
    }
    if (lastError.length == 0)
    {
        return false;
    }

    CopyText(lastError, errorBuffer, errorBufferCapacity);
    return true;
}

void AnsightMacWindowPreviewViewDestroy(AnsightMacWindowPreviewViewRef previewView)
{
    if (previewView != NULL)
    {
        AnsightMacWindowPreviewView *previewValue = (__bridge AnsightMacWindowPreviewView *)previewView;
        [previewValue shutdown];
        CFBridgingRelease(previewView);
    }
}
