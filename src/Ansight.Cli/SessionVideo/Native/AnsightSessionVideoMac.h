#pragma once

#include <stdint.h>

#if defined(__cplusplus)
extern "C" {
#endif

#define ANSIGHT_SESSION_VIDEO_EXPORT __attribute__((visibility("default")))

ANSIGHT_SESSION_VIDEO_EXPORT void *AnsightSessionVideoMac_Create(
    const char *outputFilePath,
    int32_t width,
    int32_t height,
    int32_t averageBitRate,
    char *errorBuffer,
    int32_t errorBufferCapacity);

ANSIGHT_SESSION_VIDEO_EXPORT int32_t AnsightSessionVideoMac_AppendFrame(
    void *encoderHandle,
    const char *sourceFilePath,
    int64_t presentationTimeUs,
    char *errorBuffer,
    int32_t errorBufferCapacity);

ANSIGHT_SESSION_VIDEO_EXPORT int32_t AnsightSessionVideoMac_Finish(
    void *encoderHandle,
    int64_t endTimeUs,
    char *errorBuffer,
    int32_t errorBufferCapacity);

ANSIGHT_SESSION_VIDEO_EXPORT void AnsightSessionVideoMac_Destroy(void *encoderHandle);

#if defined(__cplusplus)
}
#endif
