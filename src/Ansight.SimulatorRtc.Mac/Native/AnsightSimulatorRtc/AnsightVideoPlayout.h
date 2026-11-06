#pragma once

#include <rtc/description.hpp>
#include <cstdint>

// libdatachannel has already reciprocated the received track's directions.
// Only advertise extensions that the viewer offered and can receive.
inline uint8_t ConfigureInteractiveVideoPlayout(
    rtc::Description::Video &video,
    const std::string &remoteTrackSdp)
{
    rtc::Description::Media remoteTrack(remoteTrackSdp);
    for (int identifier : remoteTrack.extIds())
    {
        const auto *extension = remoteTrack.extMap(identifier);
        const bool canSendExtension = extension->direction == rtc::Description::Direction::Unknown
            || extension->direction == rtc::Description::Direction::SendOnly
            || extension->direction == rtc::Description::Direction::SendRecv;
        const bool isSupportedPlayoutExtension = identifier > 0
            && identifier <= UINT8_MAX
            && extension->uri == "http://www.webrtc.org/experiments/rtp-hdrext/playout-delay"
            && canSendExtension;
        if (isSupportedPlayoutExtension)
        {
            video.addExtMap(*extension);
            return static_cast<uint8_t>(identifier);
        }
    }

    return 0;
}
