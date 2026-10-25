namespace Ansight.Host.Tests.TestSupport;

internal sealed class FakeTestVideoRecorder : ITestVideoRecorder, ITestVideoRecording
{
    public string Source => "test-recorder";
    public string? OutputPath { get; private set; }
    public int Starts { get; private set; }
    public int Stops { get; private set; }
    public Exception? StartError { get; init; }
    public bool Complete { get; init; } = true;
    public bool InvalidFile { get; init; }

    public Task<ITestVideoRecording> StartAsync(TestVideoRecordingRequest request, CancellationToken cancellationToken)
    {
        Starts++;
        if (StartError is not null) throw StartError;
        OutputPath = request.OutputPath;
        return Task.FromResult<ITestVideoRecording>(this);
    }

    public Task<TestVideoRecordingCompletion> StopAsync()
    {
        Stops++;
        if (InvalidFile) File.WriteAllText(OutputPath!, "incomplete MP4");
        else File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "test-recording.mp4"), OutputPath!, overwrite: true);
        return Task.FromResult(new TestVideoRecordingCompletion(Complete, Complete ? null : "Device disconnected."));
    }
}
