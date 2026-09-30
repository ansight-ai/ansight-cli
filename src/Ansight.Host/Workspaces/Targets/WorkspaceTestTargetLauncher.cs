namespace Ansight.Host.Workspaces.Targets;

internal sealed class WorkspaceTestTargetLauncher : IWorkspaceTestTargetLauncher
{
    private readonly IDeviceService devices;
    private readonly IWorkspaceTestEnrollmentIssuer? enrollmentIssuer;
    private readonly Func<DeviceDescriptor, string, AppSessionSnapshot?>? findMonitoredSession;

    public WorkspaceTestTargetLauncher(
        IDeviceService devices,
        IWorkspaceTestEnrollmentIssuer? enrollmentIssuer = null,
        Func<DeviceDescriptor, string, AppSessionSnapshot?>? findMonitoredSession = null)
    {
        this.devices = devices ?? throw new ArgumentNullException(nameof(devices));
        this.enrollmentIssuer = enrollmentIssuer;
        this.findMonitoredSession = findMonitoredSession;
    }

    public async Task<WorkspaceTestTargetLaunchResult> LaunchAsync(
        string applicationIdentifier,
        WorkspaceTestTargetRequest? request,
        IProgress<WorkspaceTestRunProgress>? progress,
        CancellationToken cancellationToken)
    {
        string? enrollmentInviteId = null;
        IDisposable? deviceClaim = null;
        if (string.IsNullOrWhiteSpace(applicationIdentifier))
        {
            return WorkspaceTestTargetLaunchResult.Failure("The workspace test does not define an application ID.");
        }

        request ??= new WorkspaceTestTargetRequest();
        var headless = request.Headless || DeviceLaunchContext.Headless;
        try
        {
            var executionMode = WorkspaceExecutionModes.Normalize(request.ExecutionMode);
            var requestedPlatform = NormalizePlatform(request.Platform);
            var explicitAndroidDevice = executionMode == WorkspaceExecutionModes.Device
                && requestedPlatform == DevicePlatforms.Android
                && !string.IsNullOrWhiteSpace(request.DeviceIdentifier);
            if (executionMode == WorkspaceExecutionModes.Device && DeviceKinds.IsPhysical(request.DeviceKind)
                && !explicitAndroidDevice)
                return WorkspaceTestTargetLaunchResult.Failure(
                    "Physical Android device execution requires --platform android and an exact --device-id.");
            if (!string.IsNullOrWhiteSpace(request.ApplicationPath))
                progress?.Report(new WorkspaceTestRunProgress("app.inspect", "Inspecting application package."));
            using var package = string.IsNullOrWhiteSpace(request.ApplicationPath)
                ? null
                : WorkspaceTestApplicationPackage.Open(request.ApplicationPath);
            if (package is not null
                && requestedPlatform is not null
                && !string.Equals(package.Platform, requestedPlatform, StringComparison.Ordinal))
            {
                return WorkspaceTestTargetLaunchResult.Failure(
                    $"The supplied {Path.GetExtension(package.SourcePath)} artifact targets '{package.Platform}', "
                    + $"but --platform requested '{requestedPlatform}'.");
            }

            var targetPlatform = requestedPlatform ?? package?.Platform;
            var requestedDeviceKind = executionMode == WorkspaceExecutionModes.Device
                ? explicitAndroidDevice ? NormalizeDeviceKind(request.DeviceKind) : DeviceKinds.Virtual
                : NormalizeDeviceKind(request.DeviceKind);
            var packageDeviceKind = NormalizeDeviceKind(package?.RequiredDeviceKind);
            if (requestedDeviceKind is not null
                && packageDeviceKind is not null
                && !string.Equals(requestedDeviceKind, packageDeviceKind, StringComparison.Ordinal))
            {
                return WorkspaceTestTargetLaunchResult.Failure(
                    $"The supplied {Path.GetExtension(package!.SourcePath)} artifact requires a "
                    + $"'{packageDeviceKind}' target, but --device-kind requested '{requestedDeviceKind}'.");
            }

            var targetKind = requestedDeviceKind ?? packageDeviceKind;
            progress?.Report(new WorkspaceTestRunProgress(
                "target.resolve",
                $"Finding a compatible target for '{applicationIdentifier}'."));
            var inventory = await devices.ListAsync(cancellationToken).ConfigureAwait(false);
            var selection = await SelectDeviceAsync(
                inventory,
                applicationIdentifier.Trim(),
                targetPlatform,
                targetKind,
                request.DeviceIdentifier,
                package is not null,
                cancellationToken).ConfigureAwait(false);
            if (!selection.IsSuccess || selection.Device is null)
            {
                return WorkspaceTestTargetLaunchResult.Failure(selection.Message);
            }

            var device = selection.Device;
            if (executionMode == WorkspaceExecutionModes.Device)
            {
                if (!device.IsVirtual && !(explicitAndroidDevice && device.IsPhysical
                    && device.Identifier.Equals(request.DeviceIdentifier, StringComparison.OrdinalIgnoreCase)))
                    return WorkspaceTestTargetLaunchResult.Failure(
                        "Physical Android device execution requires --platform android and an exact --device-id.");
                // Reuse the monitor's capture lease without taking ownership of its lifetime.
                // Supplying an artifact still requests installation and must acquire the device.
                if (package is null && device.IsBooted
                    && findMonitoredSession?.Invoke(device, applicationIdentifier.Trim()) is { } monitoredSession)
                {
                    var existingTarget = new WorkspaceTestTarget(device.Platform, device.Identifier, device.Name,
                        applicationIdentifier.Trim(), false, false, false)
                    { ExecutionMode = executionMode, DeviceKind = device.Kind };
                    progress?.Report(new WorkspaceTestRunProgress("session.reused",
                        $"Using monitored session '{monitoredSession.SessionId}' on '{device.Name}'."));
                    return WorkspaceTestTargetLaunchResult.Success(existingTarget) with { ExistingSession = monitoredSession };
                }
                deviceClaim = DeviceExecutionClaim.Acquire(device);
            }
            var deviceStarted = false;
            if (!device.IsBooted)
            {
                progress?.Report(new WorkspaceTestRunProgress(
                    "device.start",
                    $"Starting {device.Platform} target '{device.Name}' ({device.Identifier})."));
                var startResult = await devices.StartAsync(
                    device.Platform,
                    device.Identifier,
                    new DeviceStartOptions(Headless: headless),
                    cancellationToken).ConfigureAwait(false);
                if (!startResult.IsSuccess)
                {
                    return WorkspaceTestTargetLaunchResult.Failure(startResult.Message);
                }

                device = device with { Identifier = startResult.DeviceIdentifier, IsBooted = true };
                deviceStarted = true;
            }

            if (!headless && device.IsVirtual && !deviceStarted)
            {
                progress?.Report(new WorkspaceTestRunProgress(
                    "device.show-window",
                    $"Showing {device.Platform} target '{device.Name}' ({device.Identifier})."));
                var showWindowResult = await devices.ShowWindowAsync(
                    device.Platform,
                    device.Identifier,
                    cancellationToken).ConfigureAwait(false);
                if (!showWindowResult.IsSuccess)
                {
                    return WorkspaceTestTargetLaunchResult.Failure(showWindowResult.Message);
                }
            }

            var applicationInstalled = false;
            if (package is not null)
            {
                progress?.Report(new WorkspaceTestRunProgress("app.check-install", "Checking the installed application checksum."));
                var installedChecksum = await devices.GetInstalledApplicationChecksumAsync(
                    device.Platform,
                    device.Identifier,
                    applicationIdentifier.Trim(),
                    cancellationToken).ConfigureAwait(false);
                var packageChecksum = installedChecksum is null
                    ? null
                    : await package.GetChecksumAsync(cancellationToken).ConfigureAwait(false);
                if (installedChecksum is not null && installedChecksum == packageChecksum)
                {
                    progress?.Report(new WorkspaceTestRunProgress(
                        "app.reuse",
                        $"Reusing '{applicationIdentifier}' on '{device.Name}'; the installed checksum matches."));
                }
                else
                {
                    progress?.Report(new WorkspaceTestRunProgress(
                        "app.install",
                        $"Installing '{package.SourcePath}' on '{device.Name}'."));
                    var installResult = await devices.InstallApplicationAsync(
                        device.Platform,
                        device.Identifier,
                        package.InstallPath,
                        cancellationToken).ConfigureAwait(false);
                    if (!installResult.IsSuccess)
                    {
                        return WorkspaceTestTargetLaunchResult.Failure(installResult.Message);
                    }

                    applicationInstalled = true;
                }
            }

            progress?.Report(new WorkspaceTestRunProgress(
                "app.stop",
                $"Ensuring '{applicationIdentifier}' is stopped on '{device.Name}' before launch."));
            var terminateResult = await devices.TerminateApplicationAsync(
                device.Platform,
                device.Identifier,
                applicationIdentifier.Trim(),
                cancellationToken).ConfigureAwait(false);
            if (!terminateResult.IsSuccess)
            {
                return WorkspaceTestTargetLaunchResult.Failure(terminateResult.Message);
            }

            ApplicationLaunchOptions? launchOptions = null;
            if (executionMode != WorkspaceExecutionModes.Device
                && DeviceKinds.IsPhysical(device.Kind) && enrollmentIssuer is not null)
            {
                progress?.Report(new WorkspaceTestRunProgress(
                    "enrollment.issue",
                    $"Issuing a one-use Ansight enrollment payload for '{device.Name}'."));
                var enrollment = enrollmentIssuer.Issue(applicationIdentifier.Trim());
                if (!enrollment.IsSuccess
                    || string.IsNullOrWhiteSpace(enrollment.InviteId)
                    || string.IsNullOrWhiteSpace(enrollment.Payload))
                {
                    return WorkspaceTestTargetLaunchResult.Failure(enrollment.Message);
                }

                enrollmentInviteId = enrollment.InviteId;
                launchOptions = new ApplicationLaunchOptions(enrollment.Payload);
            }

            progress?.Report(new WorkspaceTestRunProgress(
                "app.launch",
                $"Launching '{applicationIdentifier}' on '{device.Name}'."));
            var launchResult = launchOptions is null
                ? await devices.LaunchApplicationAsync(
                    device.Platform,
                    device.Identifier,
                    applicationIdentifier.Trim(),
                    cancellationToken).ConfigureAwait(false)
                : await devices.LaunchApplicationAsync(
                    device.Platform,
                    device.Identifier,
                    applicationIdentifier.Trim(),
                    launchOptions,
                    cancellationToken).ConfigureAwait(false);
            if (!launchResult.IsSuccess)
            {
                RevokeUnconsumedEnrollment(enrollmentInviteId, applicationIdentifier);
                return WorkspaceTestTargetLaunchResult.Failure(launchResult.Message);
            }

            progress?.Report(new WorkspaceTestRunProgress("app.launched", launchResult.Message));

            var result = WorkspaceTestTargetLaunchResult.Success(
                new WorkspaceTestTarget(
                    device.Platform,
                    device.Identifier,
                    device.Name,
                    applicationIdentifier.Trim(),
                    deviceStarted,
                    applicationInstalled,
                    ApplicationLaunched: true)
                {
                    DeviceKind = device.Kind,
                    ExecutionMode = executionMode
                },
                enrollmentInviteId) with { DeviceClaim = deviceClaim };
            deviceClaim = null;
            return result;
        }
        catch (OperationCanceledException)
        {
            RevokeUnconsumedEnrollment(enrollmentInviteId, applicationIdentifier);
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException
                                           or DirectoryNotFoundException
                                           or FileNotFoundException
                                           or InvalidDataException
                                           or InvalidOperationException
                                           or IOException
                                           or TimeoutException
                                           or UnauthorizedAccessException)
        {
            RevokeUnconsumedEnrollment(enrollmentInviteId, applicationIdentifier);
            return WorkspaceTestTargetLaunchResult.Failure(exception.Message);
        }
        finally
        {
            deviceClaim?.Dispose();
        }
    }

    public Task<DeviceOperationResult> StopAsync(
        WorkspaceTestTarget target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        return devices.TerminateApplicationAsync(
            target.Platform,
            target.DeviceIdentifier,
            target.ApplicationIdentifier,
            cancellationToken);
    }

    private void RevokeUnconsumedEnrollment(string? inviteId, string applicationIdentifier)
        => enrollmentIssuer?.RevokeUnconsumed(inviteId, applicationIdentifier);

    private async Task<WorkspaceTestDeviceSelection> SelectDeviceAsync(
        DeviceInventory inventory,
        string applicationIdentifier,
        string? platform,
        string? deviceKind,
        string? requestedDeviceIdentifier,
        bool willInstallApplication,
        CancellationToken cancellationToken)
    {
        var availableDevices = inventory.Devices
            .Where(static device => device.IsAvailable)
            .Where(device => platform is null
                             || string.Equals(device.Platform, platform, StringComparison.Ordinal))
            .Where(device => DeviceKinds.Matches(deviceKind, device.Kind))
            .ToArray();
        if (!string.IsNullOrWhiteSpace(requestedDeviceIdentifier))
        {
            var normalizedIdentifier = requestedDeviceIdentifier.Trim();
            var exactMatches = availableDevices
                .Where(device => string.Equals(
                    device.Identifier,
                    normalizedIdentifier,
                    StringComparison.OrdinalIgnoreCase)
                    || (device.Platform == DevicePlatforms.Android && device.IsVirtual
                        && string.Equals(device.Name, normalizedIdentifier, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
            if (exactMatches.Length == 0)
            {
                return WorkspaceTestDeviceSelection.Failure(
                    $"Device '{normalizedIdentifier}' was not found or is unavailable"
                    + BuildTargetQualifier(platform, deviceKind) + ".");
            }

            if (exactMatches.Length > 1)
            {
                return WorkspaceTestDeviceSelection.Failure(
                    $"Device identifier '{normalizedIdentifier}' exists on multiple platforms; also pass --platform.");
            }

            var requestedDevice = exactMatches[0];
            if (!willInstallApplication && requestedDevice.IsBooted
                && !await IsApplicationInstalledAsync(
                    requestedDevice,
                    applicationIdentifier,
                    cancellationToken).ConfigureAwait(false))
            {
                return WorkspaceTestDeviceSelection.Failure(
                    $"Application '{applicationIdentifier}' is not installed on '{requestedDevice.Name}'. "
                    + "Pass --app <path> or --ipa <path> to install it first.");
            }

            return WorkspaceTestDeviceSelection.Success(requestedDevice);
        }

        var bootedDevices = availableDevices.Where(static device => device.IsBooted).ToArray();
        if (willInstallApplication)
        {
            var installCandidates = bootedDevices.Length > 0
                ? bootedDevices
                : availableDevices;
            return SelectSingleTarget(
                installCandidates,
                platform,
                deviceKind,
                applicationIdentifier,
                inventory.Warnings);
        }

        var matchingDevices = new List<DeviceDescriptor>();
        foreach (var device in bootedDevices)
        {
            if (await IsApplicationInstalledAsync(device, applicationIdentifier, cancellationToken)
                .ConfigureAwait(false))
            {
                matchingDevices.Add(device);
            }
        }

        if (matchingDevices.Count == 1)
        {
            return WorkspaceTestDeviceSelection.Success(matchingDevices[0]);
        }

        if (matchingDevices.Count > 1)
        {
            return WorkspaceTestDeviceSelection.Failure(
                $"Application '{applicationIdentifier}' is installed on multiple booted targets: "
                + string.Join(", ", matchingDevices.Select(DescribeDevice))
                + ". Pass --device-id <identifier> to select one.");
        }

        if (bootedDevices.Length == 0 && availableDevices.Length == 1)
        {
            return WorkspaceTestDeviceSelection.Success(availableDevices[0]);
        }

        var targetDescription = BuildTargetDescription(platform, deviceKind);
        var warningSuffix = inventory.Warnings.Count == 0
            ? string.Empty
            : $" Device discovery warnings: {string.Join(" ", inventory.Warnings)}";
        return WorkspaceTestDeviceSelection.Failure(
            $"Application '{applicationIdentifier}' was not found on a ready{targetDescription} target. "
            + "Connect or start a target where it is already installed, pass --device-id <identifier> to select a specific target, "
            + "or pass --app <path> or --ipa <path> to install it first."
            + warningSuffix);
    }

    private async Task<bool> IsApplicationInstalledAsync(
        DeviceDescriptor device,
        string applicationIdentifier,
        CancellationToken cancellationToken)
    {
        var applications = await devices.ListApplicationsAsync(
            device.Platform,
            device.Identifier,
            cancellationToken).ConfigureAwait(false);
        return applications.Any(application => string.Equals(
            application.Identifier,
            applicationIdentifier,
            StringComparison.OrdinalIgnoreCase));
    }

    private static WorkspaceTestDeviceSelection SelectSingleTarget(
        IReadOnlyList<DeviceDescriptor> candidateDevices,
        string? platform,
        string? deviceKind,
        string applicationIdentifier,
        IReadOnlyList<string> warnings)
    {
        if (candidateDevices.Count == 1)
        {
            return WorkspaceTestDeviceSelection.Success(candidateDevices[0]);
        }

        if (candidateDevices.Count > 1)
        {
            return WorkspaceTestDeviceSelection.Failure(
                "Multiple compatible targets are available: "
                + string.Join(", ", candidateDevices.Select(DescribeDevice))
                + ". Pass --device-id <identifier> to select one.");
        }

        var targetDescription = BuildTargetDescription(platform, deviceKind);
        var warningSuffix = warnings.Count == 0 ? string.Empty : $" Device discovery warnings: {string.Join(" ", warnings)}";
        return WorkspaceTestDeviceSelection.Failure(
            $"No compatible{targetDescription} target is available for '{applicationIdentifier}'. "
            + "Connect or start one first, or pass --device-id <identifier> to select it."
            + warningSuffix);
    }

    private static string? NormalizePlatform(string? platform)
    {
        if (string.IsNullOrWhiteSpace(platform))
        {
            return null;
        }

        var normalized = platform.Trim().ToLowerInvariant();
        return normalized is DevicePlatforms.Android or DevicePlatforms.Ios
            ? normalized
            : throw new ArgumentException(
                $"Unsupported target platform '{platform}'. Expected '{DevicePlatforms.Ios}' or "
                + $"'{DevicePlatforms.Android}'.",
                nameof(platform));
    }

    private static string? NormalizeDeviceKind(string? deviceKind)
    {
        if (string.IsNullOrWhiteSpace(deviceKind))
        {
            return null;
        }

        var normalized = deviceKind.Trim().ToLowerInvariant();
        if (DeviceKinds.IsPhysical(normalized))
        {
            return DeviceKinds.Physical;
        }

        if (DeviceKinds.IsVirtual(normalized))
        {
            return DeviceKinds.Virtual;
        }

        throw new ArgumentException(
            $"Unsupported target kind '{deviceKind}'. Expected a physical device or virtual target.",
            nameof(deviceKind));
    }

    private static string BuildTargetQualifier(string? platform, string? deviceKind)
    {
        var qualifiers = new[] { platform, deviceKind }
            .Where(static value => !string.IsNullOrWhiteSpace(value));
        var value = string.Join(" ", qualifiers);
        return value.Length == 0 ? string.Empty : $" for {value}";
    }

    private static string BuildTargetDescription(string? platform, string? deviceKind)
    {
        var qualifiers = new[] { platform, deviceKind }
            .Where(static value => !string.IsNullOrWhiteSpace(value));
        var value = string.Join(" ", qualifiers);
        return value.Length == 0 ? string.Empty : $" {value}";
    }

    private static string DescribeDevice(DeviceDescriptor device)
        => $"{device.Name} ({device.Platform}, {device.Identifier})";
}
