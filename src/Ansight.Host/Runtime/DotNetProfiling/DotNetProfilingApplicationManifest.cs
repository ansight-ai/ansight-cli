using System.Text.Json.Serialization;

namespace Ansight.Host.Runtime.DotNetProfiling;

internal sealed record DotNetProfilingApplicationManifest
{
    public const string CurrentSchema = "ansight.dotnet-profile-app/v1";

    [JsonPropertyName("schema")]
    public string Schema { get; init; } = string.Empty;

    [JsonPropertyName("target")]
    public string Target { get; init; } = string.Empty;

    [JsonPropertyName("diagnosticAddress")]
    public string DiagnosticAddress { get; init; } = string.Empty;

    [JsonPropertyName("diagnosticPort")]
    public int DiagnosticPort { get; init; }

    [JsonPropertyName("diagnosticSuspend")]
    public bool DiagnosticSuspend { get; init; }

    [JsonPropertyName("diagnosticListenMode")]
    public string DiagnosticListenMode { get; init; } = string.Empty;

    [JsonPropertyName("startupProvider")]
    public string StartupProvider { get; init; } = string.Empty;

    [JsonPropertyName("startupEvent")]
    public string StartupEvent { get; init; } = string.Empty;

    public string BuildDiagnosticConfiguration()
        => $"{DiagnosticAddress}:{DiagnosticPort},{(DiagnosticSuspend ? "suspend" : "nosuspend")},{DiagnosticListenMode}";
}
