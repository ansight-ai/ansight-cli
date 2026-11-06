namespace Ansight.Host.SimulatorAgent;

public sealed class ModelUsageRecordingException : InvalidOperationException
{
    public ModelUsageRecordingException(Exception innerException)
        : base("AI usage could not be saved locally. Execution stopped to avoid unrecorded usage.", innerException)
    {
    }
}
