using Ansight.Host.Tests.TestSupport;

namespace Ansight.Host.Tests.Unit.Runtime;

internal sealed class AnnotationMutationTestContext : IDisposable
{
    private readonly TestEnvironment environment = new();

    public AnnotationMutationTestContext()
    {
        Store = new SessionCaptureStore(environment.ApplicationPaths);
        State = new RuntimeState(Store);
        SessionId = State.CreateSession("annotation-app", "Annotations", IPAddress.Loopback, null, null);
    }

    public RuntimeState State { get; }
    public SessionCaptureStore Store { get; }
    public string SessionId { get; }
    public AppSessionSnapshot Snapshot
    {
        get
        {
            Assert.True(State.TryGetSessionSnapshot(SessionId, out var snapshot));
            return snapshot!;
        }
    }

    public SessionAnnotationMutationResult Mutate(SessionAnnotationMutationKind kind, string json)
        => State.MutateSessionAnnotation(SessionId, AnnotationMutationTests.ReadMutation(kind, json));

    public async Task<SessionImageFrame> AddFrameAsync(DateTimeOffset capturedAt)
    {
        var frame = await State.AddSessionEvidenceImageAsync(SessionId, capturedAt, "png", 1000, 500, 100, new byte[] { 1, 2, 3 });
        Assert.NotNull(frame);
        return frame!;
    }

    public void AddTree(string snapshotId, string nodeId, string? sessionId = null)
    {
        Assert.True(State.AddSessionVisualTreeSnapshot(sessionId ?? SessionId, new SessionVisualTreeSnapshot
        {
            SnapshotId = snapshotId,
            CapturedAtUtc = DateTimeOffset.UtcNow,
            Source = "test",
            NodeCount = 2,
            Payload = new JsonObject
            {
                ["types"] = new JsonArray("Button"),
                ["coordinateSpace"] = new JsonObject { ["x"] = 0, ["y"] = 0, ["width"] = 1000, ["height"] = 500 },
                ["root"] = new JsonObject
                {
                    ["id"] = "root",
                    ["children"] = new JsonArray(new JsonObject
                    {
                        ["id"] = nodeId,
                        ["typeId"] = 0,
                        ["label"] = "Pay now",
                        ["automationId"] = "checkout.pay",
                        ["bounds"] = new JsonArray(100, 50, 200, 100),
                        ["children"] = new JsonArray()
                    })
                }
            }
        }).IsSuccess);
    }

    public void Dispose() => environment.Dispose();
}
