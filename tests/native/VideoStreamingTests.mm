#import <Foundation/Foundation.h>
#import <CoreVideo/CoreVideo.h>
#import <IOSurface/IOSurface.h>
#import <VideoToolbox/VideoToolbox.h>
#import <ScreenCaptureKit/ScreenCaptureKit.h>
#include <rtc/rtc.hpp>
#include <dlfcn.h>
#include <atomic>
#include <chrono>
#include <iostream>
#include <stdexcept>
#include <thread>
#include "../../src/Ansight.SimulatorRtc.Mac/Native/AnsightSimulatorRtc/AnsightSimulatorRtc.h"

// Inspect the built library through Objective-C dispatch. This exercises the
// production encoder and callbacks without requiring a running simulator.
@interface VideoTestSession : NSObject
@property(nonatomic, strong) dispatch_queue_t screenQueue;
@property(nonatomic, strong) id currentSurface;
@property(nonatomic, assign) BOOL externalVideo;
@property(nonatomic, assign) rtcNalUnitSeparator nalSeparator;
@property(nonatomic, assign) int32_t averageBitRate;
@property(nonatomic, assign) int32_t appliedBitRate;
@property(nonatomic, assign) VTCompressionSessionRef compressionSession;
@property(nonatomic, copy) NSString *answerSdp;
- (instancetype)initExternalWithFramesPerSecond:(int32_t)framesPerSecond
                                 inputCallback:(AnsightSimulatorRtcInputCallback)callback
                                  inputContext:(void *)context
                                         error:(NSError **)error;
- (BOOL)setOfferSdp:(NSString *)offer error:(NSError **)error;
- (void)handleFrame;
- (void)stream:(SCStream *)stream didOutputSampleBuffer:(CMSampleBufferRef)sampleBuffer ofType:(SCStreamOutputType)type;
- (void)shutdown;
@end

static void Require(bool condition, const char *message)
{
    if (!condition)
    {
        throw std::runtime_error(message);
    }
}

template<typename Predicate>
static void WaitFor(Predicate predicate, const char *message, int timeoutMilliseconds = 5000)
{
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::milliseconds(timeoutMilliseconds);
    while (!predicate())
    {
        Require(std::chrono::steady_clock::now() < deadline, message);
        std::this_thread::sleep_for(std::chrono::milliseconds(5));
    }
}

class TestFrame
{
public:
    explicit TestFrame(uint8_t shade)
    {
        NSDictionary *attributes = @{
            (NSString *)kCVPixelBufferIOSurfacePropertiesKey: @{},
            (NSString *)kCVPixelBufferMetalCompatibilityKey: @YES,
        };
        const auto status = CVPixelBufferCreate(kCFAllocatorDefault, 320, 480,
            kCVPixelFormatType_32BGRA, (__bridge CFDictionaryRef)attributes, &buffer);
        Require(status == kCVReturnSuccess, "Could not create synthetic IOSurface");
        CVPixelBufferLockBaseAddress(buffer, 0);
        memset(CVPixelBufferGetBaseAddress(buffer), shade,
            CVPixelBufferGetBytesPerRow(buffer) * CVPixelBufferGetHeight(buffer));
        CVPixelBufferUnlockBaseAddress(buffer, 0);

        CMVideoFormatDescriptionRef format = NULL;
        Require(CMVideoFormatDescriptionCreateForImageBuffer(kCFAllocatorDefault, buffer, &format) == noErr,
            "Could not describe the synthetic frame");
        CMSampleTimingInfo timing = {CMTimeMake(1, 30), kCMTimeZero, kCMTimeInvalid};
        const auto sampleStatus = CMSampleBufferCreateReadyWithImageBuffer(kCFAllocatorDefault, buffer,
            format, &timing, &sample);
        CFRelease(format);
        Require(sampleStatus == noErr, "Could not wrap the synthetic window frame");
    }

    ~TestFrame()
    {
        CFRelease(sample);
        CVPixelBufferRelease(buffer);
    }

    id Surface() const
    {
        return (__bridge id)CVPixelBufferGetIOSurface(buffer);
    }

    CMSampleBufferRef Sample() const
    {
        return sample;
    }

private:
    CVPixelBufferRef buffer = NULL;
    CMSampleBufferRef sample = NULL;
};

static int32_t ReadEncoderBitrate(VideoTestSession *sender)
{
    __block int32_t bitrate = 0;
    dispatch_sync(sender.screenQueue, ^{
        CFTypeRef value = NULL;
        if (sender.compressionSession != NULL)
        {
            VTSessionCopyProperty(sender.compressionSession, kVTCompressionPropertyKey_AverageBitRate,
                kCFAllocatorDefault, &value);
        }
        if (value != NULL)
        {
            CFNumberGetValue((CFNumberRef)value, kCFNumberSInt32Type, &bitrate);
            CFRelease(value);
        }
    });
    return bitrate;
}

int main(int argc, char **argv)
{
    @autoreleasepool
    {
        try
        {
            Require(argc == 2, "Pass the built native library path");
            Require(dlopen(argv[1], RTLD_NOW | RTLD_LOCAL) != NULL, "Could not load native video library");
            Class sessionClass = NSClassFromString(@"AnsightSimulatorRtcSession");
            Require(sessionClass != Nil, "Native session class is missing");
            NSError *error = nil;
            VideoTestSession *sender = [[sessionClass alloc] initExternalWithFramesPerSecond:30
                inputCallback:NULL inputContext:NULL error:&error];
            Require(sender != nil, "Could not create native sender");
            sender.screenQueue = dispatch_queue_create("ansight.video.test", DISPATCH_QUEUE_SERIAL);
            sender.externalVideo = NO;
            sender.nalSeparator = RTC_NAL_SEPARATOR_LENGTH;
            sender.averageBitRate = 5000000;

            rtc::Configuration configuration;
            configuration.disableAutoNegotiation = true;
            auto receiver = std::make_shared<rtc::PeerConnection>(configuration);
            rtc::Description::Video video("video", rtc::Description::Direction::RecvOnly);
            video.addH264Codec(102, "profile-level-id=42e01f;packetization-mode=1;level-asymmetry-allowed=1");
            auto track = receiver->addTrack(video);
            auto rtcp = std::make_shared<rtc::RtcpReceivingSession>();
            track->setMediaHandler(rtcp);
            auto receivedFrames = std::make_shared<std::atomic<int>>(0);
            track->onMessage([receivedFrames](rtc::binary packet)
            {
                if (packet.size() >= 12 && (std::to_integer<uint8_t>(packet[1]) & 0x80) != 0)
                {
                    receivedFrames->fetch_add(1);
                }
            }, nullptr);

            receiver->setLocalDescription(rtc::Description::Type::Offer);
            WaitFor([&] { return receiver->gatheringState() == rtc::PeerConnection::GatheringState::Complete; },
                "Receiver ICE gathering timed out");
            std::string offer = std::string(*receiver->localDescription());
            Require([sender setOfferSdp:[NSString stringWithUTF8String:offer.c_str()] error:&error],
                "Sender rejected the offer");
            WaitFor([&] { return sender.answerSdp.length > 0; }, "Sender ICE gathering timed out", 15000);
            receiver->setRemoteDescription(rtc::Description(sender.answerSdp.UTF8String, "answer"));
            WaitFor([&] { return track->isOpen(); }, "Video connection did not open");

            TestFrame first(50);
            TestFrame last(200);
            id firstSurface = first.Surface();
            id lastSurface = last.Surface();
            dispatch_sync(sender.screenQueue, ^{
                sender.currentSurface = firstSurface;
                [sender handleFrame];
            });
            WaitFor([&] { return receivedFrames->load() > 0; }, "Initial frame never reached the receiver");
            Require(ReadEncoderBitrate(sender) == 5000000, "Initial bitrate changed without feedback");
            std::this_thread::sleep_for(std::chrono::milliseconds(100));

            const int beforeBurst = receivedFrames->load();
            const auto burstStartedAt = std::chrono::steady_clock::now();
            dispatch_sync(sender.screenQueue, ^{
                sender.currentSurface = firstSurface;
                [sender handleFrame];
                sender.currentSurface = lastSurface;
                [sender handleFrame];
            });
            WaitFor([&] { return receivedFrames->load() >= beforeBurst + 2; },
                "Final throttled frame was lost", 1000);
            const auto burstDelay = std::chrono::duration_cast<std::chrono::milliseconds>(
                std::chrono::steady_clock::now() - burstStartedAt).count();
            const int afterBurst = receivedFrames->load();
            std::this_thread::sleep_for(std::chrono::milliseconds(150));
            Require(receivedFrames->load() == afterBurst, "Idle sender is repeating frames");

            CMSampleBufferRef firstSample = first.Sample();
            CMSampleBufferRef lastSample = last.Sample();
            dispatch_sync(sender.screenQueue, ^{
                sender.currentSurface = nil;
                [sender stream:nil didOutputSampleBuffer:firstSample ofType:SCStreamOutputTypeScreen];
                [sender stream:nil didOutputSampleBuffer:lastSample ofType:SCStreamOutputTypeScreen];
            });
            WaitFor([&] { return receivedFrames->load() >= afterBurst + 2; },
                "Final macOS window frame was lost", 1000);
            const int beforeKeyframe = receivedFrames->load();
            Require(track->requestKeyframe(), "Could not request a keyframe");
            WaitFor([&] { return receivedFrames->load() > beforeKeyframe; },
                "Idle window did not refresh after a keyframe request");

            Require(track->requestBitrate(1000000), "Could not send receiver bandwidth feedback");
            WaitFor([&] { return ReadEncoderBitrate(sender) == 900000; },
                "Receiver feedback did not lower the live encoder bitrate");
            const int beforeAdaptedFrame = receivedFrames->load();
            dispatch_sync(sender.screenQueue, ^{
                [sender handleFrame];
            });
            WaitFor([&] { return receivedFrames->load() > beforeAdaptedFrame; },
                "Video stopped after the live bitrate changed");
            Require(track->requestBitrate(5000000), "Could not send recovery feedback");
            std::this_thread::sleep_for(std::chrono::milliseconds(100));
            Require(ReadEncoderBitrate(sender) == 900000, "Bitrate recovered before the hold-down expired");
            std::this_thread::sleep_for(std::chrono::milliseconds(4100));
            Require(track->requestBitrate(5000000), "Could not send sustained recovery feedback");
            WaitFor([&] { return ReadEncoderBitrate(sender) == 1000000; },
                "Encoder did not recover bitrate gradually");

            // Exercise shutdown with a frame encoded and a later frame pending.
            dispatch_sync(sender.screenQueue, ^{
                [sender handleFrame];
                [sender handleFrame];
            });
            [sender shutdown];
            track->close();
            receiver->close();
            std::this_thread::sleep_for(std::chrono::milliseconds(100));
            std::cout << "Native video loopback: final burst frame received in " << burstDelay
                << " ms; window capture and idle keyframe refresh passed; live encoder adapted 5 Mbps -> 900 kbps -> 1 Mbps; pending-frame shutdown passed.\n";
            return 0;
        }
        catch (const std::exception &error)
        {
            std::cerr << error.what() << '\n';
            return 1;
        }
    }
}
