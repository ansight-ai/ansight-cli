using System.Text.Json;

namespace Ansight.Host.Explorer;

internal sealed record GettingStartedState(
    bool Opened = false,
    bool Skipped = false,
    bool NotificationSent = false,
    string? ReplayedSessionId = null,
    bool AutomationSaved = false,
    string? CapturePath = null);
