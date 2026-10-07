using System.Diagnostics;
using Ansight.Host.Runtime.Operations;
using Ansight.Host.Runtime.Tasks;
using Ansight.Host.SimulatorAgent.Observations;

namespace Ansight.Host.Workspaces.Execution;

public sealed partial class WorkspaceTestService
{
    private static readonly TimeSpan DefaultSessionWaitTimeout = TimeSpan.FromSeconds(45);
    private const int RequiredSessionConnectionSamples = 2;
    private readonly RuntimeCoordinator runtime;
    private readonly SimulatorAgentService simulatorAgent;
    private readonly IWorkspaceTestTargetLauncher targetLauncher;
    private readonly IWorkspaceTestRunGateway? runGateway;
    private readonly WorkspaceTestBatchAuditStore batchAuditStore;
    private readonly Lock sessionClaimGate = new();
    private readonly HashSet<string> claimedSessionIds = new(StringComparer.Ordinal);

    public WorkspaceTestTraceExporter TraceExports { get; }
    public WorkspaceTestHistoryService History { get; }
    public WorkspaceTestBatchRunner Batches { get; }

    internal IWorkspaceTestRunGateway? RunGateway => runGateway;

    internal WorkspaceTestService(RuntimeCoordinator runtime, SimulatorAgentService simulatorAgent)
        : this(
            runtime,
            simulatorAgent,
            new WorkspaceTestTargetLauncher(runtime.Devices, runtime.Pairing, runtime.FindMonitoredSession),
            runGateway: null)
    {
    }

    internal WorkspaceTestService(
        RuntimeCoordinator runtime,
        SimulatorAgentService simulatorAgent,
        IWorkspaceTestTargetLauncher targetLauncher)
        : this(runtime, simulatorAgent, targetLauncher, runGateway: null)
    {
    }

    internal WorkspaceTestService(
        RuntimeCoordinator runtime,
        SimulatorAgentService simulatorAgent,
        IWorkspaceTestTargetLauncher targetLauncher,
        IWorkspaceTestRunGateway? runGateway)
    {
        this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        this.simulatorAgent = simulatorAgent ?? throw new ArgumentNullException(nameof(simulatorAgent));
        this.targetLauncher = targetLauncher ?? throw new ArgumentNullException(nameof(targetLauncher));
        this.runGateway = runGateway;
        batchAuditStore = new WorkspaceTestBatchAuditStore(runtime.ApplicationPaths);
        History = new WorkspaceTestHistoryService(runtime, simulatorAgent, batchAuditStore);
        Batches = new WorkspaceTestBatchRunner(RunAsync, batchAuditStore, runtime.Analytics);
        TraceExports = new WorkspaceTestTraceExporter(runtime, History.Inspect);
    }

    public WorkspaceTestCatalogResult List(
        string workspacePath,
        CancellationToken cancellationToken = default)
    {
        var catalog = WorkspaceTestCatalog.Load(workspacePath, cancellationToken);
        return catalog;
    }

    public Task<WorkspaceAppSessionLaunchResult> LaunchAppAndWaitForSessionAsync(
        string appId,
        WorkspaceTestTargetRequest? target = null,
        string? requestedSessionId = null,
        TimeSpan? sessionWaitTimeout = null,
        IProgress<WorkspaceTestRunProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => LaunchAppAndWaitForSessionCoreAsync(
            appId,
            target,
            requestedSessionId,
            sessionWaitTimeout,
            progress,
            reserveSession: false,
            cancellationToken: cancellationToken,
            onTargetLaunched: null);

    internal Task<WorkspaceAppSessionLaunchResult> LaunchAppAndWaitForReservedSessionAsync(
        string appId,
        WorkspaceTestTargetRequest? target,
        string? requestedSessionId,
        TimeSpan? sessionWaitTimeout,
        IProgress<WorkspaceTestRunProgress>? progress,
        CancellationToken cancellationToken,
        Action<WorkspaceTestTarget>? onTargetLaunched = null)
        => LaunchAppAndWaitForSessionCoreAsync(
            appId,
            target,
            requestedSessionId,
            sessionWaitTimeout,
            progress,
            reserveSession: true,
            cancellationToken: cancellationToken,
            onTargetLaunched: onTargetLaunched);

    private async Task<WorkspaceAppSessionLaunchResult> LaunchAppAndWaitForSessionCoreAsync(
        string appId,
        WorkspaceTestTargetRequest? target,
        string? requestedSessionId,
        TimeSpan? sessionWaitTimeout,
        IProgress<WorkspaceTestRunProgress>? progress,
        bool reserveSession,
        CancellationToken cancellationToken,
        Action<WorkspaceTestTarget>? onTargetLaunched)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        var launchTiming = new WorkspaceLaunchTiming(progress);
        progress = launchTiming;
        var deviceMode = WorkspaceExecutionModes.Normalize(target?.ExecutionMode) == WorkspaceExecutionModes.Device;
        if (deviceMode && !string.IsNullOrWhiteSpace(requestedSessionId))
            return WorkspaceAppSessionLaunchResult.Failure("Device execution launches a virtual target and cannot use an SDK session ID.");
        var normalizedAppId = appId.Trim();
        var launchStartedUtc = DateTimeOffset.UtcNow;
        var sessionClaim = reserveSession ? new WorkspaceTestSessionClaim(this) : null;
        var transferredSessionClaim = false;
        try
        {
            var launchResult = await targetLauncher.LaunchAsync(
                normalizedAppId,
                target,
                progress,
                cancellationToken).ConfigureAwait(false);
            if (!launchResult.IsSuccess || launchResult.Target is null)
            {
                return WorkspaceAppSessionLaunchResult.Failure(
                    launchResult.Message,
                    launchResult.Target) with { StartupSteps = launchTiming.Complete("failed") };
            }

            var launchedTarget = launchResult.Target;
            DeviceRunSession? launchedDeviceSession = null;
            try
            {
                if (launchResult.ExistingSession is { } monitoredSession)
                {
                    var claim = reserveSession ? ReserveSession(monitoredSession) : null;
                    try { onTargetLaunched?.Invoke(launchedTarget); }
                    catch { claim?.Dispose(); throw; }
                    return new WorkspaceAppSessionLaunchResult(true, "Monitored capture is ready.", monitoredSession, launchedTarget)
                    {
                        SessionClaim = claim,
                        StartupSteps = launchTiming.Complete()
                    };
                }
                // Transfer cleanup ownership before waiting, which can time out or be cancelled.
                onTargetLaunched?.Invoke(launchedTarget);
                if (deviceMode)
                {
                    var deviceSession = launchedDeviceSession = await runtime.StartDeviceSessionAsync(
                        launchedTarget, launchResult.DeviceClaim, cancellationToken).ConfigureAwait(false);
                    progress?.Report(new WorkspaceTestRunProgress("device.session", $"Capturing device session '{deviceSession.Session.SessionId}'."));
                    return new WorkspaceAppSessionLaunchResult(true, "Device capture is ready.", deviceSession.Session, launchedTarget)
                    {
                        DeviceSession = deviceSession,
                        SessionClaim = reserveSession ? ReserveSession(deviceSession.Session) : null,
                        StartupSteps = launchTiming.Complete()
                    };
                }
                var sessionTarget = string.IsNullOrWhiteSpace(requestedSessionId)
                    ? $"'{normalizedAppId}'"
                    : $"session '{requestedSessionId.Trim()}'";
                progress?.Report(new WorkspaceTestRunProgress(
                    "session.wait",
                    $"Waiting for {sessionTarget} to connect to the Ansight host."));
                var session = await WaitForSessionAsync(
                    normalizedAppId,
                    requestedSessionId,
                    launchedTarget,
                    launchStartedUtc,
                    sessionWaitTimeout ?? DefaultSessionWaitTimeout,
                    cancellationToken,
                    sessionClaim).ConfigureAwait(false);
                if (session is null)
                {
                    return WorkspaceAppSessionLaunchResult.Failure(
                        $"Launched '{normalizedAppId}' on '{launchedTarget.DeviceName}', but it did not connect to Ansight before the timeout. "
                        + "Confirm the installed app includes and starts the Ansight SDK.",
                        launchedTarget) with { StartupSteps = launchTiming.Complete("timed-out") };
                }

                progress?.Report(new WorkspaceTestRunProgress(
                    "session.selected",
                    $"Using Ansight session '{session.SessionId}' for '{normalizedAppId}'."));
                var result = WorkspaceAppSessionLaunchResult.Success(session, launchedTarget) with
                {
                    StartupSteps = launchTiming.Complete(),
                    SessionClaim = sessionClaim
                };
                transferredSessionClaim = true;
                return result;
            }
            catch
            {
                if (launchedDeviceSession is not null) await launchedDeviceSession.DisposeAsync().ConfigureAwait(false);
                launchResult.DeviceClaim?.Dispose();
                throw;
            }
            finally
            {
                runtime.Pairing.RevokeUnconsumedEnrollment(
                    launchResult.EnrollmentInviteId,
                    normalizedAppId);
            }
        }
        finally
        {
            if (!transferredSessionClaim)
            {
                sessionClaim?.Dispose();
            }
        }
    }

    public Task<WorkspaceTestRunResult> RunAsync(
        WorkspaceTestRunRequest request,
        IProgress<WorkspaceTestRunProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => runtime.Analytics.ObserveUsageAsync("test",
            () => RunMeasuredCoreAsync(request, progress, cancellationToken),
            result => result.IsSuccess ? "succeeded"
                : result.IsSkipped ? "skipped"
                : result.AgentResult?.Status == SimulatorAgentRunStatus.Cancelled ? "cancelled"
                : result.AgentResult is null ? "blocked" : "failed");

    private async Task<WorkspaceTestRunResult> RunMeasuredCoreAsync(
        WorkspaceTestRunRequest request,
        IProgress<WorkspaceTestRunProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var startParticipant = request.StartParticipant;
        var deviceMode = WorkspaceExecutionModes.Normalize(request.Target?.ExecutionMode) == WorkspaceExecutionModes.Device;
        var catalog = WorkspaceTestCatalog.Load(request.WorkspacePath, cancellationToken);
        var draftTest = string.IsNullOrWhiteSpace(request.DraftSource)
            ? null
            : WorkspaceTestCatalog.Parse(Path.Combine(catalog.WorkspacePath, "ansight", "tests"),
                Path.Combine(catalog.WorkspacePath, "ansight", "tests", "draft.yaml"), request.DraftSource);
        var test = draftTest ?? catalog.Tests.FirstOrDefault(candidate => string.Equals(
            candidate.TestId,
            request.TestId,
            StringComparison.OrdinalIgnoreCase));
        if (test is null)
        {
            return WorkspaceTestRunResult.Failure(
                BuildMissingTestMessage(catalog, request.TestId));
        }

        if (!test.Enabled)
        {
            return WorkspaceTestRunResult.Failure(
                $"Workspace test '{test.TestId}' is disabled.",
                test);
        }

        using var draftTaskScope = string.IsNullOrWhiteSpace(request.DraftTaskRootPath)
            ? null
            : RepositoryTaskWorkspaceScope.Push(test.AppId, request.DraftTaskRootPath);

        if (request.EnableWorkspaceTools)
        {
            var workspaceConnection = runtime.Apps.ConfigureRepositoryWorkspace(
                test.AppId,
                catalog.WorkspacePath);
            if (!workspaceConnection.IsSuccess)
            {
                return WorkspaceTestRunResult.Failure(
                    workspaceConnection.Message,
                    test);
            }
        }

        RepositoryTask? declaredTask = null;
        if (!string.IsNullOrWhiteSpace(test.TaskId))
        {
            var taskCatalog = runtime.InspectRepositoryTasks(test.AppId,
                RepositoryTaskWorkspaceScope.Resolve(test.AppId, catalog.WorkspacePath));
            declaredTask = taskCatalog.Tasks.FirstOrDefault(candidate => string.Equals(
                candidate.TaskId,
                test.TaskId,
                StringComparison.OrdinalIgnoreCase));
            if (declaredTask is null)
            {
                var warningDetails = taskCatalog.Warnings.Count == 0
                    ? string.Empty
                    : $" Loader warnings: {string.Join(" ", taskCatalog.Warnings)}";
                return WorkspaceTestRunResult.Failure(
                    $"Workspace test '{test.TestId}' references repository task '{test.TaskId}', but that task was not loaded."
                    + warningDetails,
                    test);
            }

            if (!SupportsRequestedTarget(declaredTask, request.Target))
            {
                return WorkspaceTestRunResult.Skipped(
                    BuildUnsupportedTargetMessage(test, declaredTask, request.Target),
                    test);
            }

            if (string.IsNullOrWhiteSpace(request.SessionId))
            {
                request = request with { Target = ApplyTaskTargetMetadata(declaredTask, request.Target) };
            }
        }

        var missingSecrets = test.RequiredSecrets
            .Where(alias => simulatorAgent.GetTestSecretMetadata(
                test.AppId,
                alias,
                request.SecretResolver) is null)
            .ToArray();
        if (missingSecrets.Length > 0)
        {
            return WorkspaceTestRunResult.Failure(
                $"Required test secrets are missing: {string.Join(", ", missingSecrets)}.",
                test,
                missingSecrets: missingSecrets);
        }

        var runnerPrompt = test.BuildRunnerPrompt();
        if (runnerPrompt.Length > SimulatorAgentService.MaximumInstructionCharacters)
        {
            return WorkspaceTestRunResult.Failure(
                $"The expanded runner instruction contains {runnerPrompt.Length:N0} characters; "
                + $"the maximum is {SimulatorAgentService.MaximumInstructionCharacters:N0}.",
                test);
        }

        var session = deviceMode && string.IsNullOrWhiteSpace(request.SessionId)
            ? null : FindConnectedSession(test.AppId, request.SessionId);
        if (deviceMode && session is not null && session.CaptureSource != WorkspaceExecutionModes.Device)
            return WorkspaceTestRunResult.Failure("The selected session is not an external device capture.", test, session.SessionId);
        var attachedDeviceSession = session?.CaptureSource == WorkspaceExecutionModes.Device;
        if (attachedDeviceSession)
        {
            deviceMode = true;
            var targetError = await ValidateAttachedDeviceTargetAsync(session!, request.Target, cancellationToken).ConfigureAwait(false);
            if (targetError is not null) return WorkspaceTestRunResult.Failure(targetError, test, session!.SessionId);
        }
        if (session is not null && declaredTask is not null && !SupportsSession(declaredTask, session))
        {
            return WorkspaceTestRunResult.Skipped(
                BuildUnsupportedSessionMessage(test, declaredTask, session),
                test,
                session.SessionId);
        }
        if (session is not null && RequiresExplicitHostTargetBinding(session))
        {
            if (!string.IsNullOrWhiteSpace(request.SessionId)
                && !HasExplicitTarget(request.Target))
            {
                return WorkspaceTestRunResult.Failure(
                    "A physical iOS session requires a host device target for UI input. "
                    + "Also pass --device-id <identifier> when selecting the session.",
                    test,
                    request.SessionId);
            }

            // CoreDevice UDIDs are not exposed to iOS apps. Resolve and relaunch the
            // physical target so host input can be bound to it for the whole run.
            session = null;
        }

        var launchTiming = new WorkspaceLaunchTiming(progress);
        progress = launchTiming;
        WorkspaceTestTarget? launchedTarget = null;
        IDisposable? deviceClaim = null;
        DeviceRunSession? deviceSession = null;
        WorkspaceTestRunPreparation? meteredPreparation = null;
        var meteringCompleted = false;
        string? enrollmentInviteId = null;
        DateTimeOffset? targetLaunchStartedUtc = null;
        if (!attachedDeviceSession && ((session is null && string.IsNullOrWhiteSpace(request.SessionId))
            || HasExplicitTarget(request.Target)))
        {
            targetLaunchStartedUtc = DateTimeOffset.UtcNow;
            var launchResult = await targetLauncher.LaunchAsync(
                test.AppId,
                request.Target,
                progress,
                cancellationToken).ConfigureAwait(false);
            if (!launchResult.IsSuccess)
            {
                return WorkspaceTestRunResult.Failure(launchResult.Message, test, target: launchResult.Target);
            }

            launchedTarget = launchResult.Target;
            enrollmentInviteId = launchResult.EnrollmentInviteId;
            deviceClaim = launchResult.DeviceClaim;
            session = launchResult.ExistingSession;
            attachedDeviceSession = session is not null;
        }

        using var sessionClaim = new WorkspaceTestSessionClaim(this);
        try
        {
            if (deviceMode && !attachedDeviceSession && launchedTarget is not null)
            {
                try
                {
                    deviceSession = await runtime.StartDeviceSessionAsync(launchedTarget!, deviceClaim, cancellationToken).ConfigureAwait(false);
                    session = deviceSession.Session;
                }
                catch (InvalidOperationException exception)
                {
                    return WorkspaceTestRunResult.Failure(exception.Message, test, target: launchedTarget);
                }
            }
            if (session is null)
            {
                var sessionTarget = string.IsNullOrWhiteSpace(request.SessionId)
                    ? $"'{test.AppId}'"
                    : $"session '{request.SessionId}'";
                progress?.Report(new WorkspaceTestRunProgress(
                    "session.wait",
                    $"Waiting for {sessionTarget} to connect to the Ansight host."));
                try
                {
                    session = await WaitForSessionAsync(
                        test.AppId,
                        request.SessionId,
                        launchedTarget,
                        targetLaunchStartedUtc,
                        request.SessionWaitTimeout ?? DefaultSessionWaitTimeout,
                        cancellationToken,
                        sessionClaim).ConfigureAwait(false);
                }
                finally
                {
                    runtime.Pairing.RevokeUnconsumedEnrollment(enrollmentInviteId, test.AppId);
                }
            }

            if (session is null)
            {
                var sessionDescription = string.IsNullOrWhiteSpace(request.SessionId)
                    ? $"a connected session for '{test.AppId}'"
                    : $"connected session '{request.SessionId}'";
                var launchGuidance = launchedTarget is null
                    ? string.Empty
                    : " The app was launched, but it did not connect to Ansight. "
                      + "Confirm this installed app includes and starts the Ansight SDK.";
                return WorkspaceTestRunResult.Failure(
                    $"Timed out waiting for {sessionDescription}.{launchGuidance}",
                    test,
                    request.SessionId,
                    target: launchedTarget);
            }

            if (declaredTask is not null && !SupportsSession(declaredTask, session))
            {
                return WorkspaceTestRunResult.Skipped(
                    BuildUnsupportedSessionMessage(test, declaredTask, session),
                    test,
                    session.SessionId,
                    target: launchedTarget);
            }

            if (!runtime.IsSessionLive(session.SessionId) || sessionClaim.Select([session]) is null)
                return WorkspaceTestRunResult.Failure(
                    $"Session '{session.SessionId}' is no longer live or is already reserved by another execution.",
                    test, session.SessionId, target: launchedTarget);

            progress?.Report(new WorkspaceTestRunProgress(
                "session.selected",
                $"Using Ansight session '{session.SessionId}' for '{test.AppId}'."));

            var preparation = runGateway is null
                ? WorkspaceTestRunPreparation.Failure(
                    "Workspace tests require the Ansight cloud gateway. Sign in with 'ansight account login'.")
                : await runGateway.PrepareAsync(
                    new WorkspaceTestRunPreparationRequest(
                        request.TeamId,
                        request.WorkspacePath,
                        test.TestId,
                        test.Name,
                        test.AppId,
                        request.Model,
                        test.Validation.Assertions.Count,
                        InstructionCount: 1)
                    {
                        Reasoning = request.Reasoning
                    },
                    cancellationToken).ConfigureAwait(false);
            meteredPreparation = preparation;
            if (preparation.HasResolvedTeam)
            {
                progress?.Report(new WorkspaceTestRunProgress(
                    "organisation.selected",
                    $"Hosted test execution resolved to '{preparation.TeamName}' ({preparation.TeamId:D})."));
            }
            if (!preparation.IsSuccess)
            {
                return WorkspaceTestRunResult.Failure(
                    preparation.Message,
                    test,
                    session.SessionId,
                    target: launchedTarget);
            }

            if (preparation.HasResolvedTeam && preparation.TeamId is { } activeBusiness)
                runtime.Analytics.RecordBusinessActivity(activeBusiness);

            var maximumRoundTrips = Math.Min(
                request.MaximumRoundTrips,
                preparation.MaximumRoundTrips ?? request.MaximumRoundTrips);
            var maximumTurnsPerInstruction = Math.Min(
                request.MaximumTurnsPerInstruction,
                maximumRoundTrips);
            if (startParticipant is not null)
            {
                progress?.Report(new WorkspaceTestRunProgress(
                    "run.ready",
                    $"Ready to start '{test.Name}' on session '{session.SessionId}'; waiting for the other targets."));
                await startParticipant.ArriveAndWaitAsync(cancellationToken).ConfigureAwait(false);
            }
            var stopwatch = Stopwatch.StartNew();
            WorkspaceTestRunResult runResult;
            try
            {
                var reasoningConfiguration = preparation.ReasoningConfiguration
                    ?? AgentReasoningConfiguration.CreateDefault(request.Reasoning, request.Model);
                var agentRequest = new SimulatorAgentRunRequest(
                    session.SessionId,
                    [runnerPrompt],
                    reasoningConfiguration.Model,
                    maximumTurnsPerInstruction,
                    request.MaximumToolCalls,
                    test.AppId,
                    request.ContinueAfterInstructionFailure)
                {
                    SecretAliases = test.RequiredSecrets,
                    Reasoning = reasoningConfiguration.Reasoning,
                    ReasoningEffort = reasoningConfiguration.ReasoningEffort,
                    ReasoningConfigurationRevision = reasoningConfiguration.Revision,
                    SecretResolver = request.SecretResolver,
                    MaximumRoundTrips = maximumRoundTrips,
                    ModelTransport = preparation.ModelTransport,
                    StartupSteps = launchTiming.Complete().Concat(preparation.StartupSteps).ToArray(),
                    TrackingRunId = preparation.TrackingRunId,
                    TargetDeviceIdentifier = launchedTarget?.DeviceIdentifier,
                    WorkspacePath = catalog.WorkspacePath,
                    WorkspaceTestId = test.TestId,
                    WorkspaceTestName = test.Name,
                    BatchRunId = request.BatchRunId,
                    OperationContext = request.OperationContext,
                    CaptureTrace = request.CaptureTrace,
                    OpenAiProtocol = request.OpenAiProtocol
                };
                var agentProgress = progress is null
                    ? null
                    : new DelegatingProgress<SimulatorAgentProgress>(value => progress.Report(
                        new WorkspaceTestRunProgress(value.Stage.ToString(), value.Message, value)));
                var result = await simulatorAgent.RunAsync(agentRequest, agentProgress, cancellationToken)
                    .ConfigureAwait(false);
                runResult = new WorkspaceTestRunResult(
                    result.Status == SimulatorAgentRunStatus.Succeeded,
                    result.Message,
                    test,
                    session.SessionId,
                    result,
                    [],
                    launchedTarget);
            }
            catch (Exception exception) when (exception is ArgumentException
                                               or InvalidOperationException
                                               or IOException
                                               or UnauthorizedAccessException)
            {
                runResult = WorkspaceTestRunResult.Failure(
                    exception.Message,
                    test,
                    session.SessionId,
                    target: launchedTarget);
            }

            stopwatch.Stop();
            if (preparation.UsesExternalTransport && preparation.TrackingRunId is { } trackingRunId)
            {
                var audit = runResult.AgentResult?.Audit;
                meteringCompleted = true;
                var completion = await runGateway!.CompleteAsync(
                    new WorkspaceTestRunMeterCompletion(
                        trackingRunId,
                        ResolveCompletionStatus(runResult),
                        audit?.DurationMilliseconds ?? stopwatch.ElapsedMilliseconds,
                        audit?.InstructionCount ?? 1,
                        audit?.ModelPassCount ?? 0,
                        audit?.AnsightToolCallCount ?? 0,
                        audit?.SuccessfulAnsightToolCallCount ?? 0,
                        preparation.ModelTransport is not null ? audit?.Tokens : null,
                        preparation.ModelTransport is not null ? audit?.CreateModelPassUsages() : null),
                    CancellationToken.None).ConfigureAwait(false);
                if (!completion.IsSuccess)
                {
                    runResult = runResult with
                    {
                        Message = runResult.Message
                                  + $" Organisation metering completion failed: {completion.Message}"
                    };
                }
                else if (completion.Cost is { } cost
                         && runResult.AgentResult is { } agentResult)
                {
                    var costedAudit = agentResult.Audit with { CalculatedCost = cost };
                    var persistence = simulatorAgent.PersistRunAudit(costedAudit);
                    runResult = runResult with
                    {
                        AgentResult = agentResult with
                        {
                            Audit = costedAudit,
                            AuditFilePath = persistence.FilePath ?? agentResult.AuditFilePath,
                            AuditPersistenceError = persistence.ErrorMessage
                                                    ?? agentResult.AuditPersistenceError
                        }
                    };
                }
            }

            if (launchedTarget is not null)
            {
                runResult = runResult with
                {
                    AppiumSessionId = runtime.GetAppiumSessionId(
                        launchedTarget.DeviceIdentifier,
                        launchedTarget.ApplicationIdentifier)
                };
            }

            return runResult;
        }
        finally
        {
            if (!meteringCompleted && meteredPreparation?.UsesExternalTransport == true
                && meteredPreparation.TrackingRunId is { } unfinishedRunId && runGateway is not null)
            {
                try
                {
                    await runGateway.CompleteAsync(new WorkspaceTestRunMeterCompletion(
                        unfinishedRunId, cancellationToken.IsCancellationRequested ? "cancelled" : "failed",
                        0, 0, 0, 0, 0), CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // Preserve cancellation or the original failure; server cleanup reconciles abandoned runs.
                    progress?.Report(new WorkspaceTestRunProgress("metering.failed", "Organisation metering completion failed."));
                }
            }
            try
            {
                if (launchedTarget?.ApplicationLaunched == true)
                    await StopTargetAfterTestAsync(launchedTarget, progress).ConfigureAwait(false);
            }
            finally
            {
                if (deviceSession is not null) await deviceSession.DisposeAsync().ConfigureAwait(false);
                deviceClaim?.Dispose();
            }
        }
    }

}





public sealed partial class WorkspaceTestService
{

    private async Task<AppSessionSnapshot?> WaitForSessionAsync(
        string appId,
        string? requestedSessionId,
        WorkspaceTestTarget? launchedTarget,
        DateTimeOffset? targetLaunchStartedUtc,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        WorkspaceTestSessionClaim? sessionClaim = null)
    {
        var normalizedSessionId = string.IsNullOrWhiteSpace(requestedSessionId)
            ? null
            : requestedSessionId.Trim();
        var boundedTimeout = timeout < TimeSpan.Zero ? TimeSpan.Zero : timeout;
        var timeoutAtUtc = DateTimeOffset.UtcNow.Add(boundedTimeout);
        var connectionTracker = new WorkspaceSessionConnectionTracker(RequiredSessionConnectionSamples);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var session = FindConnectedSession(
                appId,
                normalizedSessionId,
                launchedTarget,
                targetLaunchStartedUtc,
                sessionClaim);
            if (session is not null)
            {
                if (connectionTracker.Observe(session.SessionId))
                {
                    return session;
                }
            }
            else
            {
                connectionTracker.Reset();
            }

            if (DateTimeOffset.UtcNow >= timeoutAtUtc)
            {
                return null;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
        }
    }

    private AppSessionSnapshot? FindConnectedSession(
        string appId,
        string? requestedSessionId,
        WorkspaceTestTarget? launchedTarget = null,
        DateTimeOffset? targetLaunchStartedUtc = null,
        WorkspaceTestSessionClaim? sessionClaim = null)
    {
        var normalizedSessionId = string.IsNullOrWhiteSpace(requestedSessionId)
            ? null
            : requestedSessionId.Trim();
        var candidates = runtime.Sessions.GetSummaries()
            .Where(candidate => string.Equals(candidate.AppId, appId, StringComparison.OrdinalIgnoreCase)
                && (normalizedSessionId is null || string.Equals(candidate.SessionId, normalizedSessionId, StringComparison.Ordinal)))
            .Where(candidate => runtime.IsSessionLive(candidate.SessionId)
                && (normalizedSessionId is not null || candidate.CaptureSource != WorkspaceExecutionModes.Device))
            .Where(candidate => launchedTarget is null
                || MatchesLaunchedTarget(candidate, launchedTarget, targetLaunchStartedUtc))
            .OrderByDescending(static candidate => candidate.LastUpdatedUtc)
            .ToArray();
        return sessionClaim is null
            ? candidates.FirstOrDefault()
            : sessionClaim.Select(candidates);
    }

    private static bool MatchesLaunchedTarget(
        AppSessionSnapshot candidate,
        WorkspaceTestTarget target,
        DateTimeOffset? targetLaunchStartedUtc)
    {
        var nativeDeviceIdentifier = DeviceLifecycleTool.ResolveNativeDeviceIdentifier(candidate);
        if (string.Equals(
                nativeDeviceIdentifier,
                target.DeviceIdentifier,
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Physical iOS apps cannot report the CoreDevice UDID. In that case, only accept a
        // freshly updated physical session on the same platform; virtual targets must match
        // their reported native identifier exactly.
        if (!DeviceKinds.IsPhysical(target.DeviceKind)
            || targetLaunchStartedUtc is null
            || !SessionConnectedAfterTargetLaunch(candidate, targetLaunchStartedUtc.Value)
            || candidate.DeviceProfile?.Device is not { } device
            || device.IsVirtual == true
            || device.IsEmulator == true)
        {
            return false;
        }

        return MatchesSessionPlatform(target.Platform, device.OsName);
    }

    private static bool SessionConnectedAfterTargetLaunch(
        AppSessionSnapshot candidate,
        DateTimeOffset targetLaunchStartedUtc)
        => candidate.CreatedUtc >= targetLaunchStartedUtc
           || candidate.AppStateChangedUtc >= targetLaunchStartedUtc;

    private static bool MatchesSessionPlatform(string platform, string? osName)
    {
        if (string.IsNullOrWhiteSpace(osName))
        {
            return false;
        }

        return platform switch
        {
            DevicePlatforms.Ios => osName.Contains("ios", StringComparison.OrdinalIgnoreCase),
            DevicePlatforms.Android => osName.Contains("android", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private static bool RequiresExplicitHostTargetBinding(AppSessionSnapshot session)
        => session.DeviceProfile?.Device is { } device
           && device.IsVirtual != true
           && device.IsEmulator != true
           && device.OsName?.Contains("ios", StringComparison.OrdinalIgnoreCase) == true;

    private static bool HasExplicitTarget(WorkspaceTestTargetRequest? target)
        => target is not null
           && (!string.IsNullOrWhiteSpace(target.Platform)
               || !string.IsNullOrWhiteSpace(target.DeviceIdentifier)
               || !string.IsNullOrWhiteSpace(target.ApplicationPath)
               || !string.IsNullOrWhiteSpace(target.DeviceKind));

    private static WorkspaceTestTargetRequest? ApplyTaskTargetMetadata(
        RepositoryTask task,
        WorkspaceTestTargetRequest? target)
    {
        var inferredPlatform = task.Platforms.Count == 1
            ? task.Platforms[0]
            : RepositoryTaskTargets.ResolveRequiredPlatform(task.Frameworks);
        var platform = string.IsNullOrWhiteSpace(target?.Platform)
            ? inferredPlatform
            : target?.Platform;
        var deviceKind = string.IsNullOrWhiteSpace(target?.DeviceKind) && task.DeviceKinds.Count == 1
            ? task.DeviceKinds[0]
            : target?.DeviceKind;
        if (target is null && platform is null && deviceKind is null)
        {
            return null;
        }

        return (target ?? new WorkspaceTestTargetRequest()) with
        {
            Platform = platform,
            DeviceKind = deviceKind
        };
    }

    private static bool SupportsRequestedTarget(RepositoryTask task, WorkspaceTestTargetRequest? target)
    {
        if (target is null)
        {
            return true;
        }

        var platformSupported = task.Platforms.Count == 0
                                || string.IsNullOrWhiteSpace(target.Platform)
                                || task.Platforms.Contains(target.Platform.Trim(), StringComparer.OrdinalIgnoreCase);
        var deviceKindSupported = task.DeviceKinds.Count == 0
                                  || string.IsNullOrWhiteSpace(target.DeviceKind)
                                  || task.DeviceKinds.Any(kind => DeviceKinds.Matches(kind, target.DeviceKind));
        var frameworkSupportsPlatform = task.Frameworks.Count == 0
                                        || string.IsNullOrWhiteSpace(target.Platform)
                                        || task.Frameworks.Any(framework =>
                                            RepositoryTaskTargets.FrameworkSupportsPlatform(framework, target.Platform));
        return platformSupported && deviceKindSupported && frameworkSupportsPlatform;
    }

    private static bool SupportsSession(RepositoryTask task, AppSessionSnapshot session)
    {
        var device = session.DeviceProfile?.Device;
        var platform = device?.OsName?.Contains("ios", StringComparison.OrdinalIgnoreCase) == true
            ? DevicePlatforms.Ios
            : device?.OsName?.Contains("android", StringComparison.OrdinalIgnoreCase) == true
                ? DevicePlatforms.Android
                : null;
        var deviceKind = device?.IsVirtual == true || device?.IsEmulator == true
            ? DeviceKinds.Virtual
            : device is null
                ? null
                : DeviceKinds.Physical;
        var frameworks = SessionCapabilities.FromPublishedTools(
                platform,
                session.AppToolCatalog?.ToolCatalog)
            .NavigationFrameworks;
        return (task.Platforms.Count == 0
                || platform is not null && task.Platforms.Contains(platform, StringComparer.OrdinalIgnoreCase))
               && (task.DeviceKinds.Count == 0
                   || deviceKind is not null && task.DeviceKinds.Any(kind => DeviceKinds.Matches(kind, deviceKind)))
               && RepositoryTaskTargets.SupportsAnyFramework(task.Frameworks, frameworks, platform);
    }

    private static string BuildUnsupportedTargetMessage(
        WorkspaceTestDefinition test,
        RepositoryTask task,
        WorkspaceTestTargetRequest? target)
    {
        var requested = string.Join(
            " ",
            new[] { target?.Platform, target?.DeviceKind }
                .Where(static value => !string.IsNullOrWhiteSpace(value)));
        return $"Workspace test '{test.TestId}' was skipped because repository task '{task.TaskId}' "
               + $"does not support target '{requested}'. {BuildConstraintDescription(task)}";
    }

    private static string BuildUnsupportedSessionMessage(
        WorkspaceTestDefinition test,
        RepositoryTask task,
        AppSessionSnapshot session)
        => $"Workspace test '{test.TestId}' was skipped because repository task '{task.TaskId}' "
           + $"does not support session '{session.SessionId}' "
           + $"({session.DeviceProfile?.Device?.OsName ?? "unknown platform"}). "
           + BuildConstraintDescription(task);

    private static string BuildConstraintDescription(RepositoryTask task)
    {
        var platforms = task.Platforms.Count == 0 ? "any platform" : string.Join(" or ", task.Platforms);
        var deviceKinds = task.DeviceKinds.Count == 0 ? "any device kind" : string.Join(" or ", task.DeviceKinds);
        var frameworks = task.Frameworks.Count == 0 ? "any framework" : string.Join(" or ", task.Frameworks);
        return $"Supported targets: {platforms}; {deviceKinds}; {frameworks}.";
    }

    private sealed class WorkspaceTestSessionClaim(WorkspaceTestService owner) : IDisposable
    {
        private string? sessionId;
        private bool disposed;

        public AppSessionSnapshot? Select(IReadOnlyList<AppSessionSnapshot> candidates)
        {
            lock (owner.sessionClaimGate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (sessionId is not null)
                {
                    var existing = candidates.FirstOrDefault(candidate => string.Equals(
                        candidate.SessionId,
                        sessionId,
                        StringComparison.Ordinal));
                    if (existing is not null)
                    {
                        return existing;
                    }

                    owner.claimedSessionIds.Remove(sessionId);
                    sessionId = null;
                }

                foreach (var candidate in candidates)
                {
                    if (owner.claimedSessionIds.Add(candidate.SessionId))
                    {
                        sessionId = candidate.SessionId;
                        return candidate;
                    }
                }

                return null;
            }
        }

        public void Dispose()
        {
            lock (owner.sessionClaimGate)
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
                if (sessionId is not null)
                {
                    owner.claimedSessionIds.Remove(sessionId);
                    sessionId = null;
                }
            }
        }
    }

    internal static WorkspaceTestTargetRequest? PinBatchTarget(
        WorkspaceTestTargetRequest? requestedTarget,
        WorkspaceTestTarget? resolvedTarget)
    {
        if (HasExplicitTarget(requestedTarget) || resolvedTarget is null)
        {
            return requestedTarget;
        }

        return new WorkspaceTestTargetRequest(
            resolvedTarget.Platform,
            resolvedTarget.DeviceIdentifier,
            Headless: requestedTarget?.Headless ?? false)
        { ExecutionMode = resolvedTarget.ExecutionMode };
    }

    internal static string BuildMissingTestMessage(
        WorkspaceTestCatalogResult catalog,
        string requestedTestId)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var availableTestIds = catalog.Tests
            .Select(static test => test.TestId)
            .OrderBy(static testId => testId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var lines = new List<string>
        {
            $"Workspace test '{requestedTestId}' was not found beneath '{catalog.WorkspacePath}'."
        };
        var closestTestId = FindClosestTestId(requestedTestId, availableTestIds);
        if (closestTestId is not null)
        {
            lines.Add($"Did you mean '{closestTestId}'?");
        }

        lines.Add(availableTestIds.Length == 0
            ? "No workspace tests are available."
            : $"Available tests: {string.Join(", ", availableTestIds)}.");
        return string.Join(Environment.NewLine, lines);
    }

    private static string? FindClosestTestId(
        string requestedTestId,
        IReadOnlyList<string> availableTestIds)
    {
        var normalizedRequest = requestedTestId.Trim().ToLowerInvariant();
        string? closestTestId = null;
        var closestDistance = int.MaxValue;
        foreach (var availableTestId in availableTestIds)
        {
            var distance = CalculateEditDistance(
                normalizedRequest,
                availableTestId.ToLowerInvariant());
            if (distance < closestDistance
                || distance == closestDistance
                && string.Compare(availableTestId, closestTestId, StringComparison.OrdinalIgnoreCase) < 0)
            {
                closestTestId = availableTestId;
                closestDistance = distance;
            }
        }

        if (closestTestId is null)
        {
            return null;
        }

        var maximumDistance = Math.Max(
            2,
            Math.Max(normalizedRequest.Length, closestTestId.Length) / 3);
        return closestDistance <= maximumDistance
            ? closestTestId
            : null;
    }

    private static int CalculateEditDistance(string left, string right)
    {
        var previous = Enumerable.Range(0, right.Length + 1).ToArray();
        var current = new int[right.Length + 1];
        for (var leftIndex = 1; leftIndex <= left.Length; leftIndex++)
        {
            current[0] = leftIndex;
            for (var rightIndex = 1; rightIndex <= right.Length; rightIndex++)
            {
                var substitutionCost = left[leftIndex - 1] == right[rightIndex - 1] ? 0 : 1;
                current[rightIndex] = Math.Min(
                    Math.Min(
                        current[rightIndex - 1] + 1,
                        previous[rightIndex] + 1),
                    previous[rightIndex - 1] + substitutionCost);
            }

            var completedRow = previous;
            previous = current;
            current = completedRow;
        }

        return previous[right.Length];
    }

    private static string ResolveCompletionStatus(WorkspaceTestRunResult result)
        => result.AgentResult?.Status switch
        {
            SimulatorAgentRunStatus.Cancelled => "cancelled",
            _ => result.IsSuccess ? "succeeded" : "failed"
        };

    private async Task StopTargetAfterTestAsync(
        WorkspaceTestTarget target,
        IProgress<WorkspaceTestRunProgress>? progress)
    {
        progress?.Report(new WorkspaceTestRunProgress(
            "app.stop",
            $"Stopping '{target.ApplicationIdentifier}' on '{target.DeviceName}' after the test."));
        try
        {
            var result = await targetLauncher.StopAsync(target, CancellationToken.None).ConfigureAwait(false);
            progress?.Report(new WorkspaceTestRunProgress(
                "app.stop",
                result.IsSuccess
                    ? $"Stopped '{target.ApplicationIdentifier}' on '{target.DeviceName}' after the test."
                    : $"Failed to stop '{target.ApplicationIdentifier}' on '{target.DeviceName}' after the test: {result.Message}"));
        }
        catch (Exception exception)
        {
            progress?.Report(new WorkspaceTestRunProgress(
                "app.stop",
                $"Failed to stop '{target.ApplicationIdentifier}' on '{target.DeviceName}' after the test: {exception.Message}"));
        }
    }
}

internal sealed class WorkspaceSessionConnectionTracker
{
    private readonly int requiredSamples;
    private string? sessionId;
    private int successfulSamples;

    public WorkspaceSessionConnectionTracker(int requiredSamples)
    {
        if (requiredSamples < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(requiredSamples));
        }

        this.requiredSamples = requiredSamples;
    }

    public bool Observe(string observedSessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(observedSessionId);
        if (string.Equals(sessionId, observedSessionId, StringComparison.Ordinal))
        {
            successfulSamples++;
        }
        else
        {
            sessionId = observedSessionId;
            successfulSamples = 1;
        }

        return successfulSamples >= requiredSamples;
    }

    public void Reset()
    {
        sessionId = null;
        successfulSamples = 0;
    }
}
