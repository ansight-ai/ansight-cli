namespace Ansight.Host.Workspaces.Execution;

internal interface ITestVideoRecorder
{
    Task<ITestVideoRecording> StartAsync(TestVideoRecordingRequest request, CancellationToken cancellationToken);
}
