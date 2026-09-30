using System.Text.Json;
using Ansight.RemoteSimulator.Core.Server;
using Ansight.RemoteSimulator.Core.Server.WebRtc;

namespace Ansight.Host.Tests.Unit.RemoteSimulator;

public sealed class WebRtcInputDispatcherTests
{
    [Fact]
    public async Task Enqueue_PreservesMoveCadenceAndGestureBoundaries()
    {
        var firstDeliveryStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstDelivery = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var gestureCompleted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var deliveredInputs = new List<DeliveredInput>();
        var replayDelays = new List<TimeSpan>();
        await using var dispatcher = new InputDispatcher(
            async message =>
            {
                var input = Deserialize(message);
                lock (deliveredInputs)
                {
                    deliveredInputs.Add(input);
                }

                if (input.Phase == "down")
                {
                    firstDeliveryStarted.TrySetResult();
                    await releaseFirstDelivery.Task;
                }
                else if (input.Phase == "up")
                {
                    gestureCompleted.TrySetResult();
                }
            },
            (delay, _) =>
            {
                replayDelays.Add(delay);
                return Task.CompletedTask;
            });

        dispatcher.Enqueue(CreateMessage("down", 0.1));
        await firstDeliveryStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        dispatcher.Enqueue(CreateMessage("move", 0.2));
        dispatcher.Enqueue(CreateMessage("move", 0.3));
        dispatcher.Enqueue(CreateMessage("move", 0.4));
        dispatcher.Enqueue(CreateMessage("up", 0.5));

        releaseFirstDelivery.TrySetResult();
        await gestureCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(
            [
                new DeliveredInput("down", 0.1),
                new DeliveredInput("move", 0.2),
                new DeliveredInput("move", 0.3),
                new DeliveredInput("move", 0.4),
                new DeliveredInput("up", 0.5)
            ],
            deliveredInputs);
        Assert.Equal(3, replayDelays.Count);
        Assert.All(replayDelays, delay => Assert.True(delay > TimeSpan.Zero));
    }

    [Fact]
    public async Task Enqueue_BoundsPendingMovesWithoutDroppingTheLatestPosition()
    {
        var firstDeliveryStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstDelivery = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var gestureCompleted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var deliveredInputs = new List<DeliveredInput>();
        await using var dispatcher = new InputDispatcher(
            async message =>
            {
                var input = Deserialize(message);
                lock (deliveredInputs)
                {
                    deliveredInputs.Add(input);
                }

                if (input.Phase == "down")
                {
                    firstDeliveryStarted.TrySetResult();
                    await releaseFirstDelivery.Task;
                }
                else if (input.Phase == "up")
                {
                    gestureCompleted.TrySetResult();
                }
            },
            (_, _) => Task.CompletedTask);

        dispatcher.Enqueue(CreateMessage("down", 0));
        await firstDeliveryStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        for (var index = 1; index <= 20; index++)
        {
            dispatcher.Enqueue(CreateMessage("move", index / 100d));
        }
        dispatcher.Enqueue(CreateMessage("up", 0.2));

        releaseFirstDelivery.TrySetResult();
        await gestureCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var deliveredMoves = deliveredInputs
            .Where(input => input.Phase == "move")
            .ToArray();
        Assert.Equal(InputDispatcher.MaximumPendingMovesPerGesture, deliveredMoves.Length);
        Assert.Equal(new DeliveredInput("move", 0.01), deliveredMoves[0]);
        Assert.Equal(new DeliveredInput("move", 0.2), deliveredMoves[^1]);
        Assert.Equal("down", deliveredInputs[0].Phase);
        Assert.Equal("up", deliveredInputs[^1].Phase);
    }

    private static string CreateMessage(string phase, double x)
        => JsonSerializer.Serialize(new
        {
            udid = "device-1",
            phase,
            x,
            y = 0.5,
            pointerId = 1,
            timestamp = 123
        });

    private static DeliveredInput Deserialize(string message)
    {
        using var document = JsonDocument.Parse(message);
        return new DeliveredInput(
            document.RootElement.GetProperty("phase").GetString()!,
            document.RootElement.GetProperty("x").GetDouble());
    }

    private sealed record DeliveredInput(string Phase, double X);
}
