namespace Ansight.Host.Replay;

public sealed record CoreSettings(
    string LogCaptureLevel,
    bool CaptureNativeSessionLogs,
    bool CaptureHostOperationLogs,
    bool CaptureFullHostOperationTrafficToDisk,
    string AdbPath,
    string XcodePath,
    bool SessionAutoCleanupEnabled,
    int SessionAutoCleanupRetentionDays,
    int SessionAutoCompactionAgeDays,
    long SessionAutoCleanupMaximumCacheBytes,
    int MemorySpikeMinimumIncreasePercent,
    int MemorySpikeMinimumIncreaseMegabytes,
    string CompanionMachineName,
    string CompanionTeamId,
    string CompanionAccessMode,
    bool CompanionAccessEnabled,
    bool CompanionAccessAvailable,
    string CompanionAccessStatus,
    int CompanionConnectionCount);
