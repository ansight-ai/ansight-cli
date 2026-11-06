namespace Ansight.Host.Workspaces.Execution;

internal interface ITestVideoRecording
{
    string Source { get; }

    // Completes on any recorder exit, including unexpected exits during execution.
    Task Completion { get; }

    Task<TestVideoRecordingCompletion> StopAsync();
}
