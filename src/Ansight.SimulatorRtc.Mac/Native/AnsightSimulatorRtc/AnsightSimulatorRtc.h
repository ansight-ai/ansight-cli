#ifndef ANSIGHT_SIMULATOR_RTC_H
#define ANSIGHT_SIMULATOR_RTC_H

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#if defined(__cplusplus)
extern "C" {
#endif

#define ANSIGHT_SIMULATOR_RTC_EXPORT __attribute__((visibility("default")))

typedef void *AnsightSimulatorRtcSessionRef;
typedef void *AnsightSimulatorPreviewViewRef;
typedef void *AnsightExternalH264PreviewViewRef;
typedef void *AnsightMacWindowPreviewViewRef;
typedef void (*AnsightSimulatorRtcInputCallback)(const char *message, int32_t size, void *context);

ANSIGHT_SIMULATOR_RTC_EXPORT AnsightSimulatorRtcSessionRef AnsightSimulatorRtcSessionCreate(
    const char *developerDirectory,
    const char *deviceUdid,
    int32_t framesPerSecond,
    int32_t averageBitRate,
    AnsightSimulatorRtcInputCallback inputCallback,
    void *inputContext,
    char *errorBuffer,
    size_t errorBufferCapacity);

ANSIGHT_SIMULATOR_RTC_EXPORT AnsightSimulatorRtcSessionRef AnsightExternalH264RtcSessionCreate(
    const char *deviceIdentifier,
    int32_t framesPerSecond,
    AnsightSimulatorRtcInputCallback inputCallback,
    void *inputContext,
    char *errorBuffer,
    size_t errorBufferCapacity);

// Creates a WebRTC session that captures the largest visible window owned by
// the supplied local process. The caller is responsible for resolving the PID
// from a trusted SDK session rather than accepting it from a remote client.
ANSIGHT_SIMULATOR_RTC_EXPORT AnsightSimulatorRtcSessionRef AnsightMacWindowRtcSessionCreate(
    const char *deviceIdentifier,
    int32_t processIdentifier,
    int32_t framesPerSecond,
    int32_t averageBitRate,
    AnsightSimulatorRtcInputCallback inputCallback,
    void *inputContext,
    char *errorBuffer,
    size_t errorBufferCapacity);

ANSIGHT_SIMULATOR_RTC_EXPORT bool AnsightMacWindowRtcIsSupported(void);

ANSIGHT_SIMULATOR_RTC_EXPORT bool AnsightMacWindowInputIsTrusted(bool prompt);

ANSIGHT_SIMULATOR_RTC_EXPORT bool AnsightMacWindowScreenCaptureIsAuthorized(void);

ANSIGHT_SIMULATOR_RTC_EXPORT bool AnsightMacWindowInputSendPointer(
    int32_t processIdentifier,
    int32_t phase,
    int32_t button,
    double normalizedX,
    double normalizedY,
    double scrollDeltaX,
    double scrollDeltaY,
    char *errorBuffer,
    size_t errorBufferCapacity);

ANSIGHT_SIMULATOR_RTC_EXPORT bool AnsightMacWindowInputSendKey(
    int32_t processIdentifier,
    uint32_t usageCode,
    bool isKeyDown,
    char *errorBuffer,
    size_t errorBufferCapacity);

ANSIGHT_SIMULATOR_RTC_EXPORT bool AnsightMacWindowInputSendText(
    int32_t processIdentifier,
    const char *text,
    char *errorBuffer,
    size_t errorBufferCapacity);

// Returns 1 when sent, 0 while the transport is not ready or is awaiting a key frame, and -1 on failure.
ANSIGHT_SIMULATOR_RTC_EXPORT int32_t AnsightExternalH264RtcSessionSendAccessUnit(
    AnsightSimulatorRtcSessionRef session,
    const uint8_t *data,
    int32_t size,
    int64_t presentationTimestampMicroseconds,
    bool isKeyFrame,
    char *errorBuffer,
    size_t errorBufferCapacity);

// Returns 1 when the answer was copied, 0 while ICE gathering is in progress, and -1 on failure.
ANSIGHT_SIMULATOR_RTC_EXPORT int32_t AnsightSimulatorRtcSessionCopyAnswer(
    AnsightSimulatorRtcSessionRef session,
    char *answerBuffer,
    size_t answerBufferCapacity,
    char *errorBuffer,
    size_t errorBufferCapacity);

ANSIGHT_SIMULATOR_RTC_EXPORT bool AnsightSimulatorRtcSessionSetOffer(
    AnsightSimulatorRtcSessionRef session,
    const char *offerSdp,
    char *errorBuffer,
    size_t errorBufferCapacity);

// Returns 1 when sent, 0 while the bidirectional data channel is not ready, and -1 on failure.
ANSIGHT_SIMULATOR_RTC_EXPORT int32_t AnsightSimulatorRtcSessionSendMessage(
    AnsightSimulatorRtcSessionRef session,
    const uint8_t *data,
    int32_t size,
    char *errorBuffer,
    size_t errorBufferCapacity);

ANSIGHT_SIMULATOR_RTC_EXPORT void AnsightSimulatorRtcSessionDestroy(
    AnsightSimulatorRtcSessionRef session);

ANSIGHT_SIMULATOR_RTC_EXPORT AnsightSimulatorPreviewViewRef AnsightSimulatorPreviewViewCreate(
    const char *developerDirectory,
    const char *deviceUdid,
    char *errorBuffer,
    size_t errorBufferCapacity);

ANSIGHT_SIMULATOR_RTC_EXPORT void *AnsightSimulatorPreviewViewGetLayer(
    AnsightSimulatorPreviewViewRef previewView);

ANSIGHT_SIMULATOR_RTC_EXPORT bool AnsightSimulatorPreviewViewGetSurfaceSize(
    AnsightSimulatorPreviewViewRef previewView,
    int32_t *width,
    int32_t *height);

ANSIGHT_SIMULATOR_RTC_EXPORT bool AnsightSimulatorPreviewViewRenderCurrentFrame(
    AnsightSimulatorPreviewViewRef previewView);

ANSIGHT_SIMULATOR_RTC_EXPORT void AnsightSimulatorPreviewViewSetFramesPerSecond(
    AnsightSimulatorPreviewViewRef previewView,
    int32_t framesPerSecond);

ANSIGHT_SIMULATOR_RTC_EXPORT void AnsightSimulatorPreviewViewSetDrawableSize(
    AnsightSimulatorPreviewViewRef previewView,
    double width,
    double height);

ANSIGHT_SIMULATOR_RTC_EXPORT bool AnsightSimulatorPreviewViewGetIsDeviceLocked(
    AnsightSimulatorPreviewViewRef previewView,
    bool *isDeviceLocked);

ANSIGHT_SIMULATOR_RTC_EXPORT void AnsightSimulatorPreviewViewDestroy(
    AnsightSimulatorPreviewViewRef previewView);

ANSIGHT_SIMULATOR_RTC_EXPORT AnsightExternalH264PreviewViewRef AnsightExternalH264PreviewViewCreate(
    char *errorBuffer,
    size_t errorBufferCapacity);

ANSIGHT_SIMULATOR_RTC_EXPORT void *AnsightExternalH264PreviewViewGetLayer(
    AnsightExternalH264PreviewViewRef previewView);

ANSIGHT_SIMULATOR_RTC_EXPORT bool AnsightExternalH264PreviewViewEnqueueAccessUnit(
    AnsightExternalH264PreviewViewRef previewView,
    const uint8_t *data,
    int32_t size,
    int64_t presentationTimestampMicroseconds,
    bool isKeyFrame,
    char *errorBuffer,
    size_t errorBufferCapacity);

ANSIGHT_SIMULATOR_RTC_EXPORT void AnsightExternalH264PreviewViewDestroy(
    AnsightExternalH264PreviewViewRef previewView);

ANSIGHT_SIMULATOR_RTC_EXPORT AnsightMacWindowPreviewViewRef AnsightMacWindowPreviewViewCreate(
    int32_t processIdentifier,
    int32_t framesPerSecond,
    char *errorBuffer,
    size_t errorBufferCapacity);

ANSIGHT_SIMULATOR_RTC_EXPORT void *AnsightMacWindowPreviewViewGetLayer(
    AnsightMacWindowPreviewViewRef previewView);

ANSIGHT_SIMULATOR_RTC_EXPORT bool AnsightMacWindowPreviewViewGetSurfaceSize(
    AnsightMacWindowPreviewViewRef previewView,
    int32_t *width,
    int32_t *height);

ANSIGHT_SIMULATOR_RTC_EXPORT bool AnsightMacWindowPreviewViewCopyLastError(
    AnsightMacWindowPreviewViewRef previewView,
    char *errorBuffer,
    size_t errorBufferCapacity);

ANSIGHT_SIMULATOR_RTC_EXPORT void AnsightMacWindowPreviewViewDestroy(
    AnsightMacWindowPreviewViewRef previewView);

#if defined(__cplusplus)
}
#endif

#endif
