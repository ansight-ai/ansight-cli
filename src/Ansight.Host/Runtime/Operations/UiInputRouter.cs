using Ansight.Host;

namespace Ansight.Host.Runtime.Operations;

internal sealed class UiInputRouter
{
    private static readonly TimeSpan AccessibilitySuccessCacheDuration = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan AccessibilityFailureCacheDuration = TimeSpan.FromSeconds(2);
    private readonly Lock gate = new();
    private readonly Dictionary<string, UiInputTargetBinding> targetBindingsBySession = new(StringComparer.Ordinal);
    private readonly Dictionary<UiAccessibilityCacheKey, UiAccessibilityCacheEntry> accessibilityCache = [];
    private IUiInputDriver? driver;
    private IUiAccessibilityDriver? accessibilityDriver;

    public void Configure(IUiInputDriver? value)
    {
        lock (gate)
        {
            driver = value;
        }
    }

    public void ConfigureAccessibility(IUiAccessibilityDriver? value)
    {
        lock (gate)
        {
            accessibilityDriver = value;
            accessibilityCache.Clear();
        }
    }

    public UiInputAvailability GetAvailability(string deviceIdentifier)
    {
        var current = GetDriver();
        return current?.GetAvailability(deviceIdentifier)
            ?? UiInputAvailability.Unavailable("This host has no UI input driver. Start the resident host with ansight host run, then select a connected simulator or emulator session.");
    }

    public IDisposable BeginTargetScope(string sessionId, string? deviceIdentifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var normalizedSessionId = sessionId.Trim();
        var normalizedDeviceIdentifier = string.IsNullOrWhiteSpace(deviceIdentifier)
            ? null
            : deviceIdentifier.Trim();
        if (normalizedDeviceIdentifier is null)
        {
            return UiInputTargetScope.Empty;
        }

        lock (gate)
        {
            if (targetBindingsBySession.TryGetValue(normalizedSessionId, out var existing))
            {
                if (!string.Equals(
                        existing.DeviceIdentifier,
                        normalizedDeviceIdentifier,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Session '{normalizedSessionId}' is already bound to a different host input target.");
                }

                existing.ScopeCount++;
            }
            else
            {
                targetBindingsBySession[normalizedSessionId] = new UiInputTargetBinding(
                    normalizedDeviceIdentifier);
            }
        }

        return new UiInputTargetScope(this, normalizedSessionId);
    }

    public string? ResolveDeviceIdentifier(string sessionId, string? reportedDeviceIdentifier)
    {
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            lock (gate)
            {
                if (targetBindingsBySession.TryGetValue(sessionId.Trim(), out var binding))
                {
                    return binding.DeviceIdentifier;
                }
            }
        }

        return string.IsNullOrWhiteSpace(reportedDeviceIdentifier)
            ? null
            : reportedDeviceIdentifier.Trim();
    }

    public async Task<UiInputResult> TapAsync(
        UiTapRequest request,
        CancellationToken cancellationToken)
    {
        var result = await (GetDriver()?.TapAsync(request, cancellationToken)
                            ?? Task.FromResult(UiInputResult.Failure(
                                "This host has no UI input driver. Start the resident host with ansight host run, then select a connected simulator or emulator session.")))
            .ConfigureAwait(false);
        InvalidateAccessibility(request.SessionId);
        return result;
    }

    public async Task<UiInputResult> PrepareForInputAsync(
        UiInputPreflightRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var preflightDriver = GetInputPreflightDriver();
        var result = preflightDriver is null
            ? new UiInputResult(true, string.Empty, "The configured input driver does not require a preflight.")
            : await preflightDriver.PrepareForInputAsync(request, cancellationToken).ConfigureAwait(false);
        InvalidateAccessibility(request.SessionId);
        return result;
    }

    public async Task<UiInputResult> SwipeAsync(
        UiSwipeRequest request,
        CancellationToken cancellationToken)
    {
        var result = await (GetDriver()?.SwipeAsync(request, cancellationToken)
                            ?? Task.FromResult(UiInputResult.Failure(
                                "This host has no UI input driver. Start the resident host with ansight host run, then select a connected simulator or emulator session.")))
            .ConfigureAwait(false);
        InvalidateAccessibility(request.SessionId);
        return result;
    }

    public async Task<UiInputResult> PinchAsync(
        UiPinchRequest request,
        CancellationToken cancellationToken)
    {
        var result = await (GetDriver()?.PinchAsync(request, cancellationToken)
                            ?? Task.FromResult(UiInputResult.Failure(
                                "This host has no UI input driver. Start the resident host with ansight host run, then select a connected simulator or emulator session.")))
            .ConfigureAwait(false);
        InvalidateAccessibility(request.SessionId);
        return result;
    }

    public async Task<UiInputResult> TypeTextAsync(
        UiTextRequest request,
        CancellationToken cancellationToken)
    {
        var result = await (GetDriver()?.TypeTextAsync(request, cancellationToken)
                            ?? Task.FromResult(UiInputResult.Failure(
                                "This host has no UI input driver. Start the resident host with ansight host run, then select a connected simulator or emulator session.")))
            .ConfigureAwait(false);
        InvalidateAccessibility(request.SessionId);
        return result;
    }

    public async Task<UiInputResult> PressButtonAsync(
        UiButtonRequest request,
        CancellationToken cancellationToken)
    {
        var result = await (GetDriver()?.PressButtonAsync(request, cancellationToken)
                            ?? Task.FromResult(UiInputResult.Failure(
                                "This host has no UI input driver. Start the resident host with ansight host run, then select a connected simulator or emulator session.")))
            .ConfigureAwait(false);
        InvalidateAccessibility(request.SessionId);
        return result;
    }

    public async Task<UiAccessibilityResult> CaptureAccessibilityAsync(
        UiAccessibilityRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var cacheKey = new UiAccessibilityCacheKey(
            request.SessionId,
            request.DeviceIdentifier,
            request.ApplicationIdentifier,
            request.MaxNodes,
            request.MaxDepth);
        var now = DateTimeOffset.UtcNow;
        lock (gate)
        {
            if (accessibilityCache.TryGetValue(cacheKey, out var cached)
                && request.AllowCached
                && (cached.Result.IsSuccess
                    && now - cached.CapturedAtUtc <= AccessibilitySuccessCacheDuration
                    || !cached.Result.IsSuccess
                    && now - cached.CapturedAtUtc <= AccessibilityFailureCacheDuration))
            {
                return CloneAccessibilityResult(cached.Result);
            }
        }

        var current = GetAccessibilityDriver();
        var result = current is null
            ? UiAccessibilityResult.Failure(
                "Device accessibility capture is not running. Start the Ansight host first.")
            : await current.CaptureAccessibilityAsync(request, cancellationToken).ConfigureAwait(false);
        lock (gate)
        {
            accessibilityCache[cacheKey] = new UiAccessibilityCacheEntry(
                DateTimeOffset.UtcNow,
                CloneAccessibilityResult(result));
        }

        return result;
    }

    private IUiInputDriver? GetDriver()
    {
        lock (gate)
        {
            return driver;
        }
    }

    private IUiAccessibilityDriver? GetAccessibilityDriver()
    {
        lock (gate)
        {
            return accessibilityDriver;
        }
    }

    private IUiInputPreflightDriver? GetInputPreflightDriver()
    {
        lock (gate)
        {
            return accessibilityDriver as IUiInputPreflightDriver
                   ?? driver as IUiInputPreflightDriver;
        }
    }

    private void EndTargetScope(string sessionId)
    {
        lock (gate)
        {
            if (!targetBindingsBySession.TryGetValue(sessionId, out var binding))
            {
                return;
            }

            binding.ScopeCount--;
            if (binding.ScopeCount <= 0)
            {
                targetBindingsBySession.Remove(sessionId);
            }
        }
    }

    private void InvalidateAccessibility(string sessionId)
    {
        lock (gate)
        {
            foreach (var key in accessibilityCache.Keys
                         .Where(key => string.Equals(key.SessionId, sessionId, StringComparison.Ordinal))
                         .ToArray())
            {
                accessibilityCache.Remove(key);
            }
        }
    }

    private static UiAccessibilityResult CloneAccessibilityResult(UiAccessibilityResult result)
        => result with
        {
            Payload = result.Payload?.DeepClone().AsObject()
        };

    private sealed record UiAccessibilityCacheKey(
        string SessionId,
        string DeviceIdentifier,
        string ApplicationIdentifier,
        int MaxNodes,
        int MaxDepth);

    private sealed record UiAccessibilityCacheEntry(
        DateTimeOffset CapturedAtUtc,
        UiAccessibilityResult Result);

    private sealed class UiInputTargetBinding(string deviceIdentifier)
    {
        public string DeviceIdentifier { get; } = deviceIdentifier;

        public int ScopeCount { get; set; } = 1;
    }

    private sealed class UiInputTargetScope : IDisposable
    {
        public static IDisposable Empty { get; } = new UiInputTargetScope(null, string.Empty);

        private UiInputRouter? owner;
        private readonly string sessionId;

        public UiInputTargetScope(UiInputRouter? owner, string sessionId)
        {
            this.owner = owner;
            this.sessionId = sessionId;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref owner, null)?.EndTargetScope(sessionId);
        }
    }
}
