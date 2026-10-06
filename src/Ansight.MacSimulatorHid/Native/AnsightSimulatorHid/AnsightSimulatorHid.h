#ifndef ANSIGHT_SIMULATOR_HID_H
#define ANSIGHT_SIMULATOR_HID_H

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#if defined(__cplusplus)
extern "C" {
#endif

#define ANSIGHT_SIMULATOR_HID_EXPORT __attribute__((visibility("default")))

typedef void *AnsightSimulatorHidSessionRef;

typedef enum AnsightSimulatorPointerPhase
{
    AnsightSimulatorPointerPhaseDown = 0,
    AnsightSimulatorPointerPhaseMove = 1,
    AnsightSimulatorPointerPhaseUp = 2,
    AnsightSimulatorPointerPhaseCancel = 3,
} AnsightSimulatorPointerPhase;

typedef enum AnsightSimulatorButton
{
    AnsightSimulatorButtonHome = 0,
    AnsightSimulatorButtonLock = 1,
    AnsightSimulatorButtonVolumeUp = 2,
    AnsightSimulatorButtonVolumeDown = 3,
} AnsightSimulatorButton;

typedef enum AnsightSimulatorButtonPhase
{
    AnsightSimulatorButtonPhaseDown = 0,
    AnsightSimulatorButtonPhaseUp = 1,
} AnsightSimulatorButtonPhase;

typedef enum AnsightSimulatorKeyPhase
{
    AnsightSimulatorKeyPhaseDown = 0,
    AnsightSimulatorKeyPhaseUp = 1,
} AnsightSimulatorKeyPhase;

ANSIGHT_SIMULATOR_HID_EXPORT bool AnsightSimulatorHidCheckCompatibility(
    const char *developerDirectory,
    char *errorBuffer,
    size_t errorBufferCapacity);

ANSIGHT_SIMULATOR_HID_EXPORT AnsightSimulatorHidSessionRef AnsightSimulatorHidSessionCreate(
    const char *developerDirectory,
    char *errorBuffer,
    size_t errorBufferCapacity);

ANSIGHT_SIMULATOR_HID_EXPORT bool AnsightSimulatorHidSessionSendPointer(
    AnsightSimulatorHidSessionRef session,
    const char *deviceUdid,
    AnsightSimulatorPointerPhase phase,
    double normalizedX,
    double normalizedY,
    bool hasSecondaryContact,
    double secondaryNormalizedX,
    double secondaryNormalizedY,
    int64_t pointerId,
    int64_t timestampMilliseconds,
    char *errorBuffer,
    size_t errorBufferCapacity);

ANSIGHT_SIMULATOR_HID_EXPORT bool AnsightSimulatorHidSessionGetMainScreenMetrics(
    AnsightSimulatorHidSessionRef session,
    const char *deviceUdid,
    double *pixelWidth,
    double *pixelHeight,
    double *scale,
    char *errorBuffer,
    size_t errorBufferCapacity);

/// Returns a malloc-owned UTF-8 JSON accessibility snapshot for the frontmost
/// simulator application. Release it with AnsightSimulatorHidFreeString.
ANSIGHT_SIMULATOR_HID_EXPORT char *AnsightSimulatorHidSessionCopyAccessibilityTreeJson(
    AnsightSimulatorHidSessionRef session,
    const char *deviceUdid,
    char *errorBuffer,
    size_t errorBufferCapacity);

ANSIGHT_SIMULATOR_HID_EXPORT void AnsightSimulatorHidFreeString(char *value);

ANSIGHT_SIMULATOR_HID_EXPORT bool AnsightSimulatorHidSessionSendButton(
    AnsightSimulatorHidSessionRef session,
    const char *deviceUdid,
    AnsightSimulatorButton button,
    AnsightSimulatorButtonPhase phase,
    char *errorBuffer,
    size_t errorBufferCapacity);

/// Posts the Simulator's UIKit shake gesture to one booted device.
ANSIGHT_SIMULATOR_HID_EXPORT bool AnsightSimulatorHidSessionSendShake(
    AnsightSimulatorHidSessionRef session,
    const char *deviceUdid,
    char *errorBuffer,
    size_t errorBufferCapacity);

ANSIGHT_SIMULATOR_HID_EXPORT bool AnsightSimulatorHidSessionSendKey(
    AnsightSimulatorHidSessionRef session,
    const char *deviceUdid,
    uint32_t usageCode,
    AnsightSimulatorKeyPhase phase,
    char *errorBuffer,
    size_t errorBufferCapacity);

ANSIGHT_SIMULATOR_HID_EXPORT void AnsightSimulatorHidSessionDestroy(
    AnsightSimulatorHidSessionRef session);

#if defined(__cplusplus)
}
#endif

#endif
