#include "../../src/Ansight.SimulatorRtc.Mac/Native/AnsightSimulatorRtc/AnsightVideoPlayout.h"
#include <cassert>
#include <iostream>

static void VerifyNegotiation(const std::string &extensions, uint8_t expectedIdentifier)
{
    rtc::Description::Media offer(
        "m=video 9 UDP/TLS/RTP/SAVPF 102\r\n"
        "a=mid:video\r\n"
        "a=recvonly\r\n"
        "a=rtpmap:102 H264/90000\r\n" + extensions);
    auto remoteTrack = offer.reciprocate();
    rtc::Description::Video answer("video", rtc::Description::Direction::SendOnly);
    answer.addH264Codec(102);

    auto identifier = ConfigureInteractiveVideoPlayout(answer, remoteTrack.generateSdp());

    assert(identifier == expectedIdentifier);
    if (expectedIdentifier == 0)
    {
        assert(answer.extIds().empty());
    }
    else
    {
        assert(answer.extIds().size() == 1);
        assert(answer.extMap(expectedIdentifier)->uri ==
            "http://www.webrtc.org/experiments/rtp-hdrext/playout-delay");
    }
}

int main()
{
    const std::string uri = " http://www.webrtc.org/experiments/rtp-hdrext/playout-delay\r\n";
    VerifyNegotiation("", 0);
    VerifyNegotiation("a=extmap:6" + uri, 6);
    VerifyNegotiation("a=extmap:14/recvonly" + uri, 14);
    VerifyNegotiation("a=extmap:17/sendrecv" + uri, 17);
    VerifyNegotiation("a=extmap:6/sendonly" + uri, 0);
    VerifyNegotiation("a=extmap:6/inactive" + uri, 0);
    VerifyNegotiation("a=extmap:256" + uri, 0);
    VerifyNegotiation("a=extmap:6 urn:ietf:params:rtp-hdrext:toffset\r\n", 0);
    VerifyNegotiation("a=extmap:6 urn:ietf:params:rtp-hdrext:toffset\r\na=extmap:9" + uri, 9);
    std::cout << "Video playout negotiation: 9 cases passed.\n";
}
