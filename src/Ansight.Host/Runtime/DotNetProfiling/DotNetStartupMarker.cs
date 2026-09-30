namespace Ansight.Host.Runtime.DotNetProfiling;

internal static class DotNetStartupMarker
{
    public const string ProviderName = "Ansight-DotNet-Startup";

    public const string EventName = "StartupComplete";

    public const string ManagedModuleInitializedEventName = "ManagedModuleInitialized";
}
