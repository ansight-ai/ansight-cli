using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Ansight.Host.AppGraphs;
using Ansight.Host.Files;
using Ansight.Host.Trends;
using Ansight.Host.Runtime.BinaryTransfers;
using Ansight.Host.Runtime.Operations;
using Ansight.Host.Runtime.Operations.Tools.UiAutomation;
using Ansight.Infrastructure.Preferences;

namespace Ansight.Host.Explorer;

internal sealed partial class ExplorerServer : IAsyncDisposable
{
    private async Task<bool> TryHandleConfigurationGetAsync(string route, HttpListenerRequest request, HttpListenerResponse response, bool isHead, CancellationToken cancellationToken)
    {
        switch (route)
        {
            case "api/settings" when isExplorer:
                await WriteJsonAsync(response, CreateCoreSettings(), HttpStatusCode.OK, isHead, cancellationToken).ConfigureAwait(false);
                return true;
        }

        return false;
    }

    private async Task<bool> TryHandleConfigurationPostAsync(string route, HttpListenerRequest request, HttpListenerResponse response, CancellationToken cancellationToken)
    {
        switch (route)
        {
            case "api/settings" when isExplorer:
                {
                    var body = await ReadJsonAsync<CoreSettingsUpdateRequest>(request, cancellationToken).ConfigureAwait(false);
                    var result = await UpdateCoreSettingsAsync(body, cancellationToken).ConfigureAwait(false);
                    await WriteJsonAsync(response, result, result.IsSuccess ? HttpStatusCode.OK : HttpStatusCode.Conflict, false, cancellationToken).ConfigureAwait(false);
                    return true;
                }
        }

        return false;
    }

    private CoreSettings CreateCoreSettings()
    {
        var preferences = runtime.UserPreferences;
        var companion = runtime.ActiveCompanion?.GetAccessStatus() ?? new CompanionAccessStatus(CompanionAccessMode.Disabled, false, false, "Optional cloud extension is inactive.", 0);
        return new CoreSettings(TryNormalizeLogCaptureLevel(preferences.LogCaptureLevel) ?? "Information", preferences.CaptureNativeSessionLogs, preferences.CaptureHostOperationLogs, preferences.CaptureFullHostOperationTrafficToDisk, preferences.AdbPath, preferences.XcodePath, preferences.SessionAutoCleanupEnabled, preferences.SessionAutoCleanupRetentionDays, preferences.SessionAutoCompactionAgeDays, preferences.SessionAutoCleanupMaximumCacheBytes, preferences.MemorySpikeMinimumIncreasePercent, preferences.MemorySpikeMinimumIncreaseMegabytes, preferences.ExternalSimulatorMachineName, preferences.ExternalSimulatorTeamId, companion.Mode.ToString().ToLowerInvariant(), companion.IsEnabled, companion.IsAvailable, companion.Status, companion.ConnectionCount);
    }

    private async Task<CoreSettingsUpdateResult> UpdateCoreSettingsAsync(CoreSettingsUpdateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var logCaptureLevel = NormalizeLogCaptureLevel(request.LogCaptureLevel);
        if (!string.IsNullOrWhiteSpace(request.CompanionAccessMode) && request.CompanionAccessMode != "disabled"
            || runtime.ActiveCompanion is not null)
        {
            var error = await UpdateHostedSettingsAsync(request, cancellationToken).ConfigureAwait(false);
            if (error is not null) return new CoreSettingsUpdateResult(false, error, CreateCoreSettings());
        }

        EnsureInRange(request.SessionAutoCleanupRetentionDays, SessionCleanupPreferenceDefaults.MinimumRetentionDays, SessionCleanupPreferenceDefaults.MaximumRetentionDays, "Session retention days");
        EnsureInRange(request.SessionAutoCompactionAgeDays, SessionCleanupPreferenceDefaults.MinimumRetentionDays, SessionCleanupPreferenceDefaults.MaximumRetentionDays, "Session compaction age");
        EnsureInRange(request.SessionAutoCleanupMaximumCacheBytes, SessionCleanupPreferenceDefaults.MinimumMaximumCacheBytes, SessionCleanupPreferenceDefaults.MaximumMaximumCacheBytes, "Session cache limit");
        EnsureInRange(request.MemorySpikeMinimumIncreasePercent, TelemetryAnalysisPreferenceDefaults.MinimumMemorySpikeMinimumIncreasePercent, TelemetryAnalysisPreferenceDefaults.MaximumMemorySpikeMinimumIncreasePercent, "Memory spike minimum percentage");
        EnsureInRange(request.MemorySpikeMinimumIncreaseMegabytes, TelemetryAnalysisPreferenceDefaults.MinimumMemorySpikeMinimumIncreaseMegabytes, TelemetryAnalysisPreferenceDefaults.MaximumMemorySpikeMinimumIncreaseMegabytes, "Memory spike minimum size");
        var preferences = runtime.UserPreferences;
        preferences.LogCaptureLevel = logCaptureLevel;
        preferences.CaptureNativeSessionLogs = request.CaptureNativeSessionLogs;
        preferences.CaptureHostOperationLogs = request.CaptureHostOperationLogs;
        preferences.CaptureFullHostOperationTrafficToDisk = request.CaptureFullHostOperationTrafficToDisk;
        preferences.AdbPath = request.AdbPath ?? string.Empty;
        preferences.XcodePath = request.XcodePath ?? string.Empty;
        preferences.SessionAutoCleanupEnabled = request.SessionAutoCleanupEnabled;
        preferences.SessionAutoCleanupRetentionDays = request.SessionAutoCleanupRetentionDays;
        preferences.SessionAutoCompactionAgeDays = request.SessionAutoCompactionAgeDays;
        preferences.SessionAutoCleanupMaximumCacheBytes = request.SessionAutoCleanupMaximumCacheBytes;
        preferences.MemorySpikeMinimumIncreasePercent = request.MemorySpikeMinimumIncreasePercent;
        preferences.MemorySpikeMinimumIncreaseMegabytes = request.MemorySpikeMinimumIncreaseMegabytes;
        return new CoreSettingsUpdateResult(true, "Settings saved. Native tool path changes apply to newly started host processes.", CreateCoreSettings());
    }

    private static string NormalizeLogCaptureLevel(string? value) => TryNormalizeLogCaptureLevel(value) ?? throw new InvalidDataException("Log capture level must be Verbose, Debug, Information, Warning, Error, or Fatal.");
    private static string? TryNormalizeLogCaptureLevel(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "verbose" => "Verbose",
        "debug" => "Debug",
        "information" or "info" => "Information",
        "warning" => "Warning",
        "error" => "Error",
        "fatal" => "Fatal",
        _ => null
    };
}
