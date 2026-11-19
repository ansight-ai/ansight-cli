#pragma once

#include <algorithm>
#include <cstdint>
#include <optional>

// Accessed only on the capture queue. Coalesce updates into the newest surface,
// including the final update in a burst, instead of queuing old frames.
class AnsightVideoFrameScheduler
{
public:
    void RequestFrame()
    {
        pending = true;
    }

    void RequestImmediateFrame()
    {
        pending = true;
        nextFrameAt = 0;
    }

    std::optional<double> NextFrameDelay(double now) const
    {
        if (!pending || encoding)
        {
            return std::nullopt;
        }

        return std::max(0.0, nextFrameAt - now);
    }

    bool TryBeginFrame(double now, int framesPerSecond)
    {
        const auto delay = NextFrameDelay(now);
        if (!delay || *delay > 0)
        {
            return false;
        }

        pending = false;
        encoding = true;
        const double interval = 1.0 / std::max(framesPerSecond, 1);
        if (nextFrameAt > 0 && now - nextFrameAt < interval)
        {
            nextFrameAt += interval;
        }
        else
        {
            nextFrameAt = now + interval;
        }
        return true;
    }

    void FinishFrame(bool retryLatestFrame = false)
    {
        encoding = false;
        pending = pending || retryLatestFrame;
    }

private:
    bool pending = false;
    bool encoding = false;
    double nextFrameAt = 0;
};

class AnsightVideoBitrateController
{
public:
    void Configure(int32_t maximum)
    {
        maximumBitrate = std::max(minimumBitrate, maximum);
        currentBitrate = maximumBitrate;
        lastReductionAt = -10;
        lastIncreaseAt = -10;
    }

    int32_t CurrentBitrate() const
    {
        return currentBitrate;
    }

    bool ObserveReceiverEstimate(uint32_t estimate, double now)
    {
        // Reserve some of the receiver's budget for RTP/RTCP and control data.
        const auto budget = static_cast<int32_t>(std::clamp<int64_t>(
            static_cast<int64_t>(estimate) * 9 / 10, minimumBitrate, maximumBitrate));
        if (budget < currentBitrate)
        {
            currentBitrate = budget;
            lastReductionAt = now;
            return true;
        }

        const bool canRecover = budget > currentBitrate
            && now - lastReductionAt >= 4
            && now - lastIncreaseAt >= 2;
        if (!canRecover)
        {
            return false;
        }

        currentBitrate = std::min(budget, currentBitrate + std::max(100000, currentBitrate / 10));
        lastIncreaseAt = now;
        return true;
    }

    bool ObserveCongestion(double now)
    {
        if (now - lastReductionAt < 1 || currentBitrate == minimumBitrate)
        {
            return false;
        }

        currentBitrate = std::max(minimumBitrate, currentBitrate * 4 / 5);
        lastReductionAt = now;
        return true;
    }

private:
    static constexpr int32_t minimumBitrate = 250000;
    int32_t maximumBitrate = 5000000;
    int32_t currentBitrate = 5000000;
    double lastReductionAt = -10;
    double lastIncreaseAt = -10;
};
