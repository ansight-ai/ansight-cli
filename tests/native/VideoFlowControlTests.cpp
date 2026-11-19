#include "../../src/Ansight.SimulatorRtc.Mac/Native/AnsightSimulatorRtc/AnsightVideoFlowControl.h"
#include <cassert>
#include <cmath>
#include <iostream>

static void VerifyLatestFrameScheduling()
{
    AnsightVideoFrameScheduler scheduler;
    assert(!scheduler.NextFrameDelay(0));
    scheduler.RequestFrame();
    assert(scheduler.TryBeginFrame(0, 30));

    // Updates arriving during encoding are coalesced, including the last one.
    for (int update = 0; update < 100; ++update)
    {
        scheduler.RequestFrame();
    }
    assert(!scheduler.NextFrameDelay(0.01));
    assert(!scheduler.TryBeginFrame(0.01, 30));
    scheduler.FinishFrame();
    assert(std::abs(*scheduler.NextFrameDelay(0.01) - (1.0 / 30 - 0.01)) < 0.000001);
    assert(!scheduler.TryBeginFrame(0.02, 30));
    assert(scheduler.TryBeginFrame(1.0 / 30, 30));
    scheduler.FinishFrame();
    assert(!scheduler.NextFrameDelay(1));

    // A busy encoder never accumulates old frames or bursts to catch up.
    scheduler.RequestFrame();
    assert(scheduler.TryBeginFrame(1, 60));
    scheduler.RequestFrame();
    scheduler.FinishFrame();
    assert(scheduler.TryBeginFrame(2, 60));
    scheduler.FinishFrame();
    assert(!scheduler.TryBeginFrame(2, 60));

    // A keyframe bypasses the throttle, but never overlaps an active encode.
    scheduler.RequestImmediateFrame();
    assert(scheduler.TryBeginFrame(2.001, 60));
    scheduler.RequestImmediateFrame();
    assert(!scheduler.TryBeginFrame(2.002, 60));
    scheduler.FinishFrame();
    assert(scheduler.TryBeginFrame(2.003, 60));
    scheduler.FinishFrame();
    assert(!scheduler.NextFrameDelay(3));

    // Minor timer lateness must not accumulate into a lower sustained FPS.
    scheduler.RequestFrame();
    assert(scheduler.TryBeginFrame(10, 30));
    scheduler.FinishFrame();
    scheduler.RequestFrame();
    assert(scheduler.TryBeginFrame(10 + 1.0 / 30 + 0.001, 30));
    scheduler.FinishFrame();
    scheduler.RequestFrame();
    assert(std::abs(*scheduler.NextFrameDelay(10 + 2.0 / 30 - 0.002) - 0.002) < 0.000001);

    // An encoder rate-limit drop retries the latest surface at the next slot.
    assert(scheduler.TryBeginFrame(11, 30));
    scheduler.FinishFrame(true);
    assert(!scheduler.TryBeginFrame(11.01, 30));
    assert(scheduler.TryBeginFrame(11 + 1.0 / 30, 30));
    scheduler.FinishFrame();
    assert(!scheduler.NextFrameDelay(12));
}

static void VerifyAdaptiveBitrate()
{
    AnsightVideoBitrateController controller;
    controller.Configure(5000000);
    assert(controller.CurrentBitrate() == 5000000);
    assert(controller.ObserveReceiverEstimate(1000000, 1));
    assert(controller.CurrentBitrate() == 900000);
    assert(!controller.ObserveReceiverEstimate(10000000, 2));
    assert(!controller.ObserveReceiverEstimate(10000000, 4.99));
    assert(controller.ObserveReceiverEstimate(10000000, 5));
    assert(controller.CurrentBitrate() == 1000000);
    assert(!controller.ObserveReceiverEstimate(10000000, 5.5));

    for (int time = 7; time < 100; time += 2)
    {
        controller.ObserveReceiverEstimate(UINT32_MAX, time);
    }
    assert(controller.CurrentBitrate() == 5000000);
    assert(controller.ObserveCongestion(100));
    assert(controller.CurrentBitrate() == 4000000);
    assert(!controller.ObserveCongestion(100.5));
    assert(controller.ObserveCongestion(101));
    assert(controller.CurrentBitrate() == 3200000);

    assert(controller.ObserveReceiverEstimate(0, 102));
    assert(controller.CurrentBitrate() == 250000);
    assert(!controller.ObserveReceiverEstimate(1, 103));
    assert(!controller.ObserveCongestion(104));
    assert(!controller.ObserveReceiverEstimate(UINT32_MAX, 105));
    assert(controller.ObserveReceiverEstimate(UINT32_MAX, 106));
    assert(controller.CurrentBitrate() == 350000);

    controller.Configure(8000000);
    assert(controller.CurrentBitrate() == 8000000);
    assert(controller.ObserveReceiverEstimate(5000000, 1));
    assert(controller.CurrentBitrate() == 4500000);
}

int main()
{
    VerifyLatestFrameScheduling();
    VerifyAdaptiveBitrate();
    std::cout << "Video flow control: final frames, bounded scheduling, encoder backpressure, bitrate reduction and paced recovery passed.\n";
}
