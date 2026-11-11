#include <rtc/rtc.hpp>
#include <chrono>
#include <condition_variable>
#include <iostream>
#include <mutex>
#include <memory>
#include <stdexcept>
#include <string>
#include <vector>

struct HandshakeResult
{
    std::mutex mutex;
    std::condition_variable changed;
    bool received = false;
    bool failed = false;
    bool connected = false;
    std::vector<std::shared_ptr<rtc::DataChannel>> incoming;
};

int main(int argc, char **)
{
    const bool tamper = argc > 1;
    auto result = std::make_shared<HandshakeResult>();
    auto left = std::make_shared<rtc::PeerConnection>();
    auto right = std::make_shared<rtc::PeerConnection>();
    std::weak_ptr<rtc::PeerConnection> weakLeft = left;
    std::weak_ptr<rtc::PeerConnection> weakRight = right;

    left->onLocalDescription([weakRight](rtc::Description description)
    {
        if (auto peer = weakRight.lock())
        {
            peer->setRemoteDescription(description);
        }
    });
    right->onLocalDescription([weakLeft, tamper](rtc::Description description)
    {
        if (auto peer = weakLeft.lock())
        {
            std::string sdp = description;
            if (tamper)
            {
                const std::string prefix = "a=fingerprint:sha-256 ";
                auto offset = sdp.find(prefix);
                if (offset == std::string::npos)
                {
                    throw std::runtime_error("Missing expected fingerprint");
                }
                offset += prefix.size();
                sdp[offset] = sdp[offset] == 'A' ? 'B' : 'A';
            }
            peer->setRemoteDescription(rtc::Description(sdp, description.typeString()));
        }
    });
    left->onLocalCandidate([weakRight](rtc::Candidate candidate)
    {
        if (auto peer = weakRight.lock())
        {
            peer->addRemoteCandidate(candidate);
        }
    });
    right->onLocalCandidate([weakLeft](rtc::Candidate candidate)
    {
        if (auto peer = weakLeft.lock())
        {
            peer->addRemoteCandidate(candidate);
        }
    });
    left->onStateChange([result](rtc::PeerConnection::State state)
    {
        std::lock_guard<std::mutex> lock(result->mutex);
        if (state == rtc::PeerConnection::State::Connected)
        {
            result->connected = true;
        }
        if (state == rtc::PeerConnection::State::Failed)
        {
            result->failed = true;
            result->changed.notify_all();
        }
    });
    right->onDataChannel([result](std::shared_ptr<rtc::DataChannel> channel)
    {
        channel->onMessage([result](rtc::message_variant message)
        {
            const auto text = std::get_if<std::string>(&message);
            if (text && *text == "security-smoke")
            {
                std::lock_guard<std::mutex> lock(result->mutex);
                result->received = true;
                result->changed.notify_all();
            }
        });
        std::lock_guard<std::mutex> lock(result->mutex);
        result->incoming.push_back(channel);
    });
    auto outgoing = left->createDataChannel("security-smoke");
    std::weak_ptr<rtc::DataChannel> weakOutgoing = outgoing;
    outgoing->onOpen([weakOutgoing]
    {
        if (auto channel = weakOutgoing.lock())
        {
            channel->send(std::string("security-smoke"));
        }
    });

    bool passed;
    {
        std::unique_lock<std::mutex> lock(result->mutex);
        result->changed.wait_for(lock, std::chrono::seconds(20), [&result]
        {
            return result->received || result->failed;
        });
        // A mismatched certificate must fail in DTLS before reporting Connected.
        const bool fingerprintRejected = result->failed
            && !result->received
            && !result->connected;

        passed = tamper ? fingerprintRejected : result->received;
    }
    outgoing->close();
    left->close();
    right->close();
    outgoing.reset();
    left.reset();
    right.reset();
    result->incoming.clear();
    rtc::Cleanup().wait();
    std::cout << (tamper ? "Fingerprint mismatch rejected: " : "Encrypted data channel delivered: ")
              << (passed ? "PASS" : "FAIL") << std::endl;
    return passed ? 0 : 1;
}
