#if TOOLS
using System;
using System.Collections.Generic;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using SystemExplorer.CodeService.Runtime;
using SystemExplorer.CodeService.Documents;
using SystemExplorer.CodeService.Completion;

namespace SystemExplorer.CodeService.Client;

internal sealed class CodeServiceClientCoordinator
{
	private static readonly TimeSpan[] ReconnectDelays =
	{
		TimeSpan.FromMilliseconds(100),
		TimeSpan.FromMilliseconds(250),
		TimeSpan.FromMilliseconds(500),
	};
	private static readonly TimeSpan BootstrapDeadline = TimeSpan.FromSeconds(12);
	private static readonly TimeSpan WorkspaceReadyObservationDeadline = TimeSpan.FromSeconds(60);
	private static readonly TimeSpan[] WorkspaceStatusObservationDelays =
	{
		TimeSpan.FromMilliseconds(100),
		TimeSpan.FromMilliseconds(250),
		TimeSpan.FromMilliseconds(500),
		TimeSpan.FromSeconds(1),
	};

	private readonly object _gate = new();
	private readonly CodeServiceProcessIdentity _ownerIdentity;
	private readonly string _expectedServiceVersion;
	private readonly CodeServiceSessionDescriptorReader _descriptorReader;
	private readonly CodeServiceProcessLauncher _processLauncher;
	private readonly AssemblyLoadContext _assemblyLoadContext;
	private Func<CodeServiceClientLaunchPreparation> _prepareLaunch;
	private Action<string, string> _logOperation;
	private Action<CodeServiceClientSessionInfo, string> _sessionReadyCallback;
	private Action<CodeServiceClientSessionInfo, string, bool?, string> _workspaceReadyCallback;
	private readonly CancellationTokenSource _lifetimeCancellation = new();

	private CodeServiceClientCoordinatorState _state = CodeServiceClientCoordinatorState.Disconnected;
	private CodeServiceClientSession _currentSession;
	private Task<CodeServiceClientEnsureResult> _activeFlight;
	private long _activeFlightGeneration;
	private Task<CodeServiceWorkspaceEnsureResult> _workspaceFlight;
	private long _workspaceFlightGeneration;
	private CodeServiceClientSession _workspaceFlightSession;
	private long _workspaceFlightReadyGeneration;
	private string _workspaceFlightProjectRoot = "";
	private CancellationTokenSource _workspaceFlightCancellation;
	private long _readyGeneration;
	private bool _recoveryAdmissionEnabled = true;
	private bool _retirementStarted;
	private bool _assemblyUnloadHandlerAttached;
	private CodeServiceClientSession _retirementSession;
	private CodeServiceProcessObservation _retirementProcessObservation;
	private Task<CodeServiceClientEnsureResult> _retirementActiveFlight;
	private Task<CodeServiceWorkspaceEnsureResult> _retirementWorkspaceFlight;
	private Task _retirementTask;
	private Task _retirementWorkerTask;
	private CodeServiceClientSession _pendingWatcherRecoverySession;
	private CodeServiceClientSessionInfo _pendingWatcherRecoveryInfo;
	private long _pendingWatcherRecoveryGeneration;
	private string _pendingWatcherRecoveryReason = "";
	private bool _pendingWatcherRecoveryDeathEvidence;

	internal CodeServiceClientCoordinator(
		CodeServiceProcessIdentity ownerIdentity,
		string expectedServiceVersion,
		CodeServiceProcessLauncher processLauncher,
		Func<CodeServiceClientLaunchPreparation> prepareLaunch,
		Action<string, string> logOperation,
		Action<CodeServiceClientSessionInfo, string> sessionReadyCallback,
		Action<CodeServiceClientSessionInfo, string, bool?, string> workspaceReadyCallback
	)
	{
		if (ownerIdentity.ProcessId <= 0 || ownerIdentity.StartTimeUtcTicks <= 0)
			throw new ArgumentException("Godot owner identity is invalid.", nameof(ownerIdentity));
		if (string.IsNullOrWhiteSpace(expectedServiceVersion))
			throw new ArgumentException("Expected CodeService version is required.", nameof(expectedServiceVersion));

		_ownerIdentity = ownerIdentity;
		_expectedServiceVersion = expectedServiceVersion;
		_descriptorReader = new CodeServiceSessionDescriptorReader();
		_processLauncher = processLauncher ?? throw new ArgumentNullException(nameof(processLauncher));
		_prepareLaunch = prepareLaunch ?? throw new ArgumentNullException(nameof(prepareLaunch));
		_logOperation = logOperation ?? throw new ArgumentNullException(nameof(logOperation));
		_sessionReadyCallback = sessionReadyCallback ?? throw new ArgumentNullException(nameof(sessionReadyCallback));
		_workspaceReadyCallback = workspaceReadyCallback ?? throw new ArgumentNullException(nameof(workspaceReadyCallback));

		_assemblyLoadContext = AssemblyLoadContext.GetLoadContext(
			typeof(CodeServiceClientCoordinator).Assembly
		);
		if (_assemblyLoadContext != null)
		{
			_assemblyLoadContext.Unloading += OnAssemblyUnloading;
			_assemblyUnloadHandlerAttached = true;
		}
	}

	internal CodeServiceClientCoordinatorState State
	{
		get
		{
			lock (_gate)
				return _state;
		}
	}

	internal bool TryGetReadySessionInfo(out CodeServiceClientSessionInfo info)
	{
		lock (_gate)
		{
			if (_retirementStarted || _state != CodeServiceClientCoordinatorState.Ready || _currentSession == null)
			{
				info = default;
				return false;
			}

			info = _currentSession.ToInfo();
			return true;
		}
	}

	internal Task<CodeServiceWorkspaceEnsureResult> ObserveWorkspaceReadyAsync(
		CodeServiceClientSessionInfo expectedSessionInfo,
		string projectRoot,
		string reason,
		CancellationToken callerCancellationToken = default
	)
	{
		if (
			!CodeServiceWorkspacePath.TryNormalize(
				projectRoot,
				out string normalizedProjectRoot,
				out string normalizationDetail
			)
		)
		{
			SafeLog(
				"CodeService Workspace Protocol Failure",
				$"Reason='{reason}', Outcome='InvalidRequest', Detail='{ToSingleLine(normalizationDetail)}'"
			);
			return Task.FromResult(
				CodeServiceWorkspaceEnsureResult.InvalidRequest(normalizationDetail)
			);
		}

		Task<CodeServiceWorkspaceEnsureResult> sharedFlight;
		lock (_gate)
		{
			if (_retirementStarted)
			{
				return Task.FromResult(
					CodeServiceWorkspaceEnsureResult.Disposed(
						"CodeService workspace coordinator is retiring."
					)
				);
			}

			if (
				_state != CodeServiceClientCoordinatorState.Ready
				|| _currentSession == null
				|| !IsExactSessionInfo(_currentSession.ToInfo(), expectedSessionInfo)
			)
			{
				return Task.FromResult(
					CodeServiceWorkspaceEnsureResult.StaleSession(
						"The verified Ready session changed before workspace readiness observation was admitted."
					)
				);
			}

			CodeServiceClientSession session = _currentSession;
			long readyGeneration = _readyGeneration;
			if (_workspaceFlight != null)
			{
				if (
					ReferenceEquals(_workspaceFlightSession, session)
					&& _workspaceFlightReadyGeneration == readyGeneration
					&& CodeServiceWorkspacePath.EqualsNormalized(
						_workspaceFlightProjectRoot,
						normalizedProjectRoot
					)
				)
				{
					sharedFlight = _workspaceFlight;
				}
				else
				{
					return Task.FromResult(
						CodeServiceWorkspaceEnsureResult.Unavailable(
							"A different workspace observation flight is already active; no request queue is permitted."
						)
					);
				}
			}
			else
			{
				CancellationTokenSource flightCancellation =
					CancellationTokenSource.CreateLinkedTokenSource(
						_lifetimeCancellation.Token
					);
				long flightGeneration = ++_workspaceFlightGeneration;
				sharedFlight = RunWorkspaceObservationAndFinalizeAsync(
					flightGeneration,
					session,
					readyGeneration,
					normalizedProjectRoot,
					reason ?? "CodeService Ready",
					flightCancellation
				);
				_workspaceFlight = sharedFlight;
				_workspaceFlightSession = session;
				_workspaceFlightReadyGeneration = readyGeneration;
				_workspaceFlightProjectRoot = normalizedProjectRoot;
				_workspaceFlightCancellation = flightCancellation;
			}
		}

		return WaitForWorkspaceCallerAsync(sharedFlight, callerCancellationToken);
	}


	internal async Task<CodeServiceDocumentEpochResult> ReconcileDocumentEpochAsync(
		CodeServiceClientSessionInfo expectedSession,
		long clientGeneration,
		string epochId,
		IReadOnlyList<string> openDocumentPaths,
		CancellationToken cancellationToken = default
	)
	{
		CodeServiceClientSession session;
		long readyGeneration;
		lock (_gate)
		{
			if (_retirementStarted)
				return CodeServiceDocumentEpochResult.Failure(CodeServiceDocumentOutcome.Disposed, "CodeService coordinator is retiring.");
			if (_state != CodeServiceClientCoordinatorState.Ready || _currentSession == null || !IsExactSessionInfo(_currentSession.ToInfo(), expectedSession))
				return CodeServiceDocumentEpochResult.Failure(CodeServiceDocumentOutcome.StaleSession, "Expected document session is no longer the current Ready session.");
			session = _currentSession;
			readyGeneration = _readyGeneration;
		}

		CodeServiceDocumentEpochResult result = await session.ReconcileDocumentEpochAsync(
			clientGeneration, epochId, openDocumentPaths, cancellationToken
		).ConfigureAwait(false);

		lock (_gate)
		{
			if (_retirementStarted || _state != CodeServiceClientCoordinatorState.Ready || _readyGeneration != readyGeneration || !ReferenceEquals(_currentSession, session) || !IsExactSessionInfo(session.ToInfo(), expectedSession))
				return CodeServiceDocumentEpochResult.Failure(CodeServiceDocumentOutcome.StaleSession, "Document epoch result belongs to a retired logical service session.");
		}
		return result;
	}

	internal async Task<CodeServiceDocumentSnapshotResult> SynchronizeDocumentSnapshotAsync(
		CodeServiceClientSessionInfo expectedSession,
		long clientGeneration,
		string epochId,
		CodeServiceDocumentSnapshot snapshot,
		CancellationToken cancellationToken = default
	)
	{
		CodeServiceClientSession session;
		long readyGeneration;
		lock (_gate)
		{
			if (_retirementStarted)
				return CodeServiceDocumentSnapshotResult.Failure(CodeServiceDocumentOutcome.Disposed, "CodeService coordinator is retiring.");
			if (_state != CodeServiceClientCoordinatorState.Ready || _currentSession == null || !IsExactSessionInfo(_currentSession.ToInfo(), expectedSession))
				return CodeServiceDocumentSnapshotResult.Failure(CodeServiceDocumentOutcome.StaleSession, "Expected document session is no longer the current Ready session.");
			session = _currentSession;
			readyGeneration = _readyGeneration;
		}

		CodeServiceDocumentSnapshotResult result = await session.SynchronizeDocumentSnapshotAsync(
			clientGeneration, epochId, snapshot, cancellationToken
		).ConfigureAwait(false);

		lock (_gate)
		{
			if (_retirementStarted || _state != CodeServiceClientCoordinatorState.Ready || _readyGeneration != readyGeneration || !ReferenceEquals(_currentSession, session) || !IsExactSessionInfo(session.ToInfo(), expectedSession))
				return CodeServiceDocumentSnapshotResult.Failure(CodeServiceDocumentOutcome.StaleSession, "Document snapshot result belongs to a retired logical service session.");
		}
		return result;
	}

	internal async Task<CodeServiceCompletionResult> CompleteDocumentAsync(
		CodeServiceClientSessionInfo expectedSession,
		CodeServiceCompletionRequest request,
		CancellationToken cancellationToken = default
	)
	{
		CodeServiceClientSession session;
		long readyGeneration;
		lock (_gate)
		{
			if (_retirementStarted)
			{
				return CodeServiceCompletionResult.Failure(
					CodeServiceCompletionOutcome.Disposed,
					"CodeService coordinator is retiring."
				);
			}
			if (_state != CodeServiceClientCoordinatorState.Ready
				|| _currentSession == null
				|| !IsExactSessionInfo(_currentSession.ToInfo(), expectedSession))
			{
				return CodeServiceCompletionResult.Failure(
					CodeServiceCompletionOutcome.StaleSession,
					"Expected completion session is no longer the current Ready session."
				);
			}
			session = _currentSession;
			readyGeneration = _readyGeneration;
		}

		CodeServiceCompletionResult result = await session.CompleteDocumentAsync(
			request,
			cancellationToken
		).ConfigureAwait(false);

		lock (_gate)
		{
			if (_retirementStarted
				|| _state != CodeServiceClientCoordinatorState.Ready
				|| _readyGeneration != readyGeneration
				|| !ReferenceEquals(_currentSession, session)
				|| !IsExactSessionInfo(session.ToInfo(), expectedSession))
			{
				return CodeServiceCompletionResult.Failure(
					CodeServiceCompletionOutcome.StaleSession,
					"Completion result belongs to a retired logical service session."
				);
			}
		}

		return result;
	}

	internal Task<CodeServiceClientEnsureResult> ReportSessionFailureAndEnsureReadyAsync(
		CodeServiceClientSessionInfo expectedSession,
		string reason,
		CancellationToken callerCancellationToken = default
	)
	{
		CodeServiceClientSession failedSession;
		lock (_gate)
		{
			if (_retirementStarted || !_recoveryAdmissionEnabled)
				return Task.FromResult(CodeServiceClientEnsureResult.Disposed("CodeService recovery admission is disabled."));
			if (_state != CodeServiceClientCoordinatorState.Ready || _currentSession == null || !IsExactSessionInfo(_currentSession.ToInfo(), expectedSession))
			{
				if (_currentSession != null && _state == CodeServiceClientCoordinatorState.Ready)
					return Task.FromResult(CodeServiceClientEnsureResult.Ready(_currentSession));
				return Task.FromResult(CodeServiceClientEnsureResult.Unavailable("The failed document session is no longer current."));
			}
			failedSession = _currentSession;
		}
		return ReportSessionFailureAndEnsureReadyAsync(failedSession, reason, callerCancellationToken);
	}

	internal Task<CodeServiceClientEnsureResult> EnsureReadyAsync(
		string reason,
		CodeServiceClientLaunchHint launchHint,
		CancellationToken callerCancellationToken = default
	)
	{
		Task<CodeServiceClientEnsureResult> sharedFlight;
		lock (_gate)
		{
			if (_retirementStarted)
				return Task.FromResult(CodeServiceClientEnsureResult.Disposed("CodeService client coordinator is retiring."));

			if (_currentSession != null && _state == CodeServiceClientCoordinatorState.Ready)
				return Task.FromResult(CodeServiceClientEnsureResult.Ready(_currentSession));

			if (_activeFlight != null)
			{
				sharedFlight = _activeFlight;
			}
			else
			{
				sharedFlight = StartFlightLocked(
					reason,
					isRecovery: false,
					launchHint,
					oldSession: null,
					oldSessionInfo: default,
					positiveDeathEvidence: false
				);
			}
		}

		return WaitForCallerAsync(sharedFlight, callerCancellationToken);
	}

	internal Task<CodeServiceClientEnsureResult> ReportSessionFailureAndEnsureReadyAsync(
		CodeServiceClientSession failedSession,
		string reason,
		CancellationToken callerCancellationToken = default
	)
	{
		if (failedSession == null)
			throw new ArgumentNullException(nameof(failedSession));

		Task<CodeServiceClientEnsureResult> sharedFlight;
		lock (_gate)
		{
			if (_retirementStarted || !_recoveryAdmissionEnabled)
				return Task.FromResult(CodeServiceClientEnsureResult.Disposed("CodeService recovery admission is disabled."));

			if (!ReferenceEquals(_currentSession, failedSession))
			{
				if (_currentSession != null && _state == CodeServiceClientCoordinatorState.Ready)
					return Task.FromResult(CodeServiceClientEnsureResult.Ready(_currentSession));

				if (_activeFlight != null)
					return WaitForCallerAsync(_activeFlight, callerCancellationToken);
			}

			if (_activeFlight != null)
			{
				sharedFlight = _activeFlight;
			}
			else
			{
				CodeServiceClientSessionInfo oldInfo = failedSession.ToInfo();
				sharedFlight = StartFlightLocked(
					reason,
					isRecovery: true,
					default,
					failedSession,
					oldInfo,
					positiveDeathEvidence: false
				);
			}
		}

		return WaitForCallerAsync(sharedFlight, callerCancellationToken);
	}

	internal Task RetireAsync(string reason)
	{
		SignalRetirement();
		DetachAssemblyUnloadHandler();

		CodeServiceClientSession session;
		Task<CodeServiceClientEnsureResult> activeFlight;
		Task<CodeServiceWorkspaceEnsureResult> workspaceFlight;
		TaskCompletionSource<bool> retirementCompletion;

		lock (_gate)
		{
			if (_retirementTask != null)
				return _retirementTask;

			session = _retirementSession;
			activeFlight = _retirementActiveFlight;
			workspaceFlight = _retirementWorkspaceFlight;

			retirementCompletion = new TaskCompletionSource<bool>(
				TaskCreationOptions.RunContinuationsAsynchronously
			);
			_retirementTask = retirementCompletion.Task;
		}

		Task retirementWorker = CompleteRetirementAndSignalAsync(
			reason,
			session,
			workspaceFlight,
			activeFlight,
			retirementCompletion
		);
		lock (_gate)
		{
			_retirementWorkerTask = retirementWorker;
		}
		return retirementCompletion.Task;
	}

	private void OnAssemblyUnloading(AssemblyLoadContext context)
	{
		// The collectible managed generation may be disappearing without Godot first
		// reaching the normal plugin shutdown path. Close managed admission, synchronously
		// detach the local Ready Process.Exited observation, then cancel remaining client work.
		// Never wait, call Godot, or stop service here.
		SignalRetirement();
	}

	private void SignalRetirement()
	{
		CodeServiceProcessObservation retirementProcessObservation;

		lock (_gate)
		{
			if (!_retirementStarted)
			{
				_recoveryAdmissionEnabled = false;
				_retirementStarted = true;
				_state = CodeServiceClientCoordinatorState.Disposed;
				_readyGeneration++;
				ClearPendingWatcherRecoveryLocked();

				_retirementSession = _currentSession;
				_retirementProcessObservation = _currentSession?.ProcessObservation;
				_currentSession = null;
				_retirementActiveFlight = _activeFlight;
				_retirementWorkspaceFlight = _workspaceFlight;

				// These delegates are the only coordinator-owned routes back into the plugin
				// generation. Clear them before watcher detachment/cancellation can race late work.
				_prepareLaunch = null;
				_logOperation = null;
				_sessionReadyCallback = null;
				_workspaceReadyCallback = null;
			}

			retirementProcessObservation = _retirementProcessObservation;
		}

		// Never take the ProcessObservation watch lock while holding coordinator _gate.
		// This synchronous boundary removes this managed generation's Process.Exited
		// registration before an AssemblyLoadContext.Unloading callback can return.
		try
		{
			retirementProcessObservation?.StopExitObservation();
		}
		catch
		{
			// Retirement is fail-closed; stale Ready/session guards remain final authority.
		}

		// The workspace flight is linked to lifetime cancellation. Cancellation can
		// synchronously invoke callbacks, so request it only after Process.Exited has
		// been synchronously deregistered and never while holding _gate.
		try
		{
			_lifetimeCancellation.Cancel();
		}
		catch (ObjectDisposedException)
		{
		}
	}

	private void DetachAssemblyUnloadHandler()
	{
		bool shouldDetach;
		lock (_gate)
		{
			shouldDetach = _assemblyUnloadHandlerAttached;
			_assemblyUnloadHandlerAttached = false;
		}

		if (!shouldDetach || _assemblyLoadContext == null)
			return;

		try
		{
			_assemblyLoadContext.Unloading -= OnAssemblyUnloading;
		}
		catch
		{
			// Handler detachment is best-effort once synchronous retirement is closed.
		}
	}

	private Task<CodeServiceClientEnsureResult> StartFlightLocked(
		string reason,
		bool isRecovery,
		CodeServiceClientLaunchHint launchHint,
		CodeServiceClientSession oldSession,
		CodeServiceClientSessionInfo oldSessionInfo,
		bool positiveDeathEvidence
	)
	{
		long flightGeneration = ++_activeFlightGeneration;
		_state = isRecovery
			? CodeServiceClientCoordinatorState.Recovering
			: CodeServiceClientCoordinatorState.DiscoveringExisting;

		Task<CodeServiceClientEnsureResult> flight = RunFlightAndFinalizeAsync(
			flightGeneration,
			reason ?? "CodeService Ensure",
			isRecovery,
			launchHint,
			oldSession,
			oldSessionInfo,
			positiveDeathEvidence,
			_lifetimeCancellation.Token
		);
		_activeFlight = flight;
		return flight;
	}

	private async Task<CodeServiceClientEnsureResult> RunFlightAndFinalizeAsync(
		long flightGeneration,
		string reason,
		bool isRecovery,
		CodeServiceClientLaunchHint launchHint,
		CodeServiceClientSession oldSession,
		CodeServiceClientSessionInfo oldSessionInfo,
		bool positiveDeathEvidence,
		CancellationToken cancellationToken
	)
	{
		await Task.Yield();
		CodeServiceClientEnsureResult result;
		Task<CodeServiceClientEnsureResult> pendingWatcherRecoveryFlight = null;
		try
		{
			if (isRecovery)
			{
				SafeLog(
					"CodeService Session Recovery Started",
					$"Reason='{reason}', PreviousSessionId='{oldSessionInfo.SessionId}', PreviousServicePid='{oldSessionInfo.ServiceProcessIdentity.ProcessId}', PreviousServiceStartTimeUtcTicks='{oldSessionInfo.ServiceProcessIdentity.StartTimeUtcTicks}', PositiveDeathEvidence='{positiveDeathEvidence}'"
				);
				await RetireCurrentReadySessionForRecoveryAsync(oldSession, cancellationToken)
					.ConfigureAwait(false);
			}

			result = await EstablishReadySessionAsync(
				reason,
				isRecovery,
				launchHint,
				oldSessionInfo,
				positiveDeathEvidence,
				cancellationToken
			).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			result = CodeServiceClientEnsureResult.Disposed("CodeService client operation was retired.");
		}
		catch (Exception exception)
		{
			result = CodeServiceClientEnsureResult.Unavailable(
				"Unexpected CodeService client failure: " + ToSingleLine(exception.Message)
			);
			SafeLog("CodeService Session Unavailable", $"Reason='{reason}', Detail='{ToSingleLine(exception.ToString())}'");
		}
		finally
		{
			lock (_gate)
			{
				if (
					_activeFlightGeneration == flightGeneration
					&& _activeFlight != null
				)
				{
					_activeFlight = null;
				}

				pendingWatcherRecoveryFlight = TryStartPendingWatcherRecoveryLocked();
			}
		}

		if (pendingWatcherRecoveryFlight != null)
		{
			result = await pendingWatcherRecoveryFlight.ConfigureAwait(false);
		}

		if (isRecovery)
		{
			if (result.IsReady)
			{
				CodeServiceClientSessionInfo newInfo = result.Session.ToInfo();
				bool sameSession = oldSessionInfo.IsSameLogicalSession(newInfo);
				SafeLog(
					"CodeService Session Recovery Succeeded",
					$"Reason='{reason}', RetainedSameSession='{sameSession}', SessionId='{newInfo.SessionId}', ServicePid='{newInfo.ServiceProcessIdentity.ProcessId}', ServiceStartTimeUtcTicks='{newInfo.ServiceProcessIdentity.StartTimeUtcTicks}'"
				);
			}
			else if (result.Status != CodeServiceClientEnsureStatus.Disposed)
			{
				SafeLog(
					"CodeService Session Recovery Failed",
					$"Reason='{reason}', Status='{result.Status}', Detail='{result.Detail}'"
				);
			}
		}

		return result;
	}

	private async Task<CodeServiceClientEnsureResult> EstablishReadySessionAsync(
		string reason,
		bool isRecovery,
		CodeServiceClientLaunchHint launchHint,
		CodeServiceClientSessionInfo oldSessionInfo,
		bool positiveDeathEvidence,
		CancellationToken cancellationToken
	)
	{
		SafeLog(
			"CodeService Session Discovery Started",
			$"Reason='{reason}', OwnerPid='{_ownerIdentity.ProcessId}', OwnerStartTimeUtcTicks='{_ownerIdentity.StartTimeUtcTicks}'"
		);

		bool retryAbsentState = isRecovery || launchHint.HasVerifiedLiveService;
		CodeServiceDiscoveryResult discovery = await DiscoverWithRetriesAsync(
			reason,
			retryAbsentState,
			cancellationToken
		).ConfigureAwait(false);

		if (discovery.Status == CodeServiceDiscoveryStatus.Ready)
			return PublishReadySession(discovery.Session, reason, wasExistingSession: true);
		if (discovery.Status == CodeServiceDiscoveryStatus.Incompatible)
			return PublishIncompatible(reason, discovery.Detail);
		if (!discovery.CanConsiderLaunch)
			return PublishUnavailable(reason, discovery.Detail);

		bool launchAllowed = true;
		string launchBlockDetail = "";

		if (isRecovery)
		{
			if (!positiveDeathEvidence)
			{
				CodeServiceProcessObservationCreateResult oldProcessProbe =
					CodeServiceProcessObservation.TryCreate(oldSessionInfo.ServiceProcessIdentity);
				if (oldProcessProbe.IsSuccess)
				{
					oldProcessProbe.Observation.Dispose();
					launchAllowed = false;
					launchBlockDetail =
						"The previously verified CodeService process is still alive; transport/session failure alone is not process-death evidence.";
				}
				else if (!oldProcessProbe.IsDefinitelyUnavailable)
				{
					launchAllowed = false;
					launchBlockDetail =
						"The previous CodeService process identity could not be revalidated safely: "
						+ oldProcessProbe.Detail;
				}
			}
		}
		else if (launchHint.LaunchBlocked)
		{
			launchAllowed = false;
			launchBlockDetail = launchHint.Detail;
		}
		else if (launchHint.HasVerifiedLiveService)
		{
			CodeServiceProcessObservationCreateResult markerProbe =
				CodeServiceProcessObservation.TryCreate(launchHint.ServiceIdentity);
			if (markerProbe.IsSuccess)
			{
				markerProbe.Observation.Dispose();
				launchAllowed = false;
				launchBlockDetail =
					"The native launch marker still identifies an exact live CodeService process, so a missing/unusable descriptor cannot authorize a parallel launch.";
			}
			else if (!markerProbe.IsDefinitelyUnavailable)
			{
				launchAllowed = false;
				launchBlockDetail =
					"The marked CodeService process could not be revalidated safely: "
					+ markerProbe.Detail;
			}
		}

		if (!launchAllowed)
			return PublishUnavailable(reason, launchBlockDetail);

		if (isRecovery || positiveDeathEvidence)
		{
			CodeServiceDiscoveryResult lastRaceDiscovery = await DiscoverWithRetriesAsync(
				reason + " Pre-Launch Rediscovery",
				retryAbsentState: true,
				cancellationToken
			).ConfigureAwait(false);
			if (lastRaceDiscovery.Status == CodeServiceDiscoveryStatus.Ready)
				return PublishReadySession(lastRaceDiscovery.Session, reason, wasExistingSession: true);
			if (lastRaceDiscovery.Status == CodeServiceDiscoveryStatus.Incompatible)
				return PublishIncompatible(reason, lastRaceDiscovery.Detail);
			if (!lastRaceDiscovery.CanConsiderLaunch)
				return PublishUnavailable(reason, lastRaceDiscovery.Detail);
		}

		return await LaunchAndEstablishAsync(reason, cancellationToken).ConfigureAwait(false);
	}

	private async Task<CodeServiceClientEnsureResult> LaunchAndEstablishAsync(
		string reason,
		CancellationToken cancellationToken
	)
	{
		CodeServiceClientLaunchPreparation launchPreparation;
		Exception launchPreparationException = null;
		lock (_gate)
		{
			if (_retirementStarted || !_recoveryAdmissionEnabled || _prepareLaunch == null)
			{
				return CodeServiceClientEnsureResult.Disposed(
					"CodeService launch preparation was retired."
				);
			}

			try
			{
				// Keep delegate admission atomic with retirement. The callback is managed-only
				// and uses the plugin's immutable/current composition snapshot, not Godot APIs.
				launchPreparation = _prepareLaunch();
			}
			catch (Exception exception)
			{
				launchPreparation = default;
				launchPreparationException = exception;
			}
		}

		if (launchPreparationException != null)
		{
			return PublishUnavailable(
				reason,
				"CodeService launch preparation failed: "
					+ ToSingleLine(launchPreparationException.Message)
			);
		}

		if (!launchPreparation.CanLaunch)
			return PublishUnavailable(reason, launchPreparation.Detail);

		CodeServiceLaunchResult launchResult;
		lock (_gate)
		{
			if (_retirementStarted || !_recoveryAdmissionEnabled)
				return CodeServiceClientEnsureResult.Disposed("CodeService launch was retired before Process.Start.");

			_state = CodeServiceClientCoordinatorState.Starting;
			SafeLog(
				"CodeService Launch Requested",
				$"Reason='{reason}', OwnerPid='{_ownerIdentity.ProcessId}', OwnerStartTimeUtcTicks='{_ownerIdentity.StartTimeUtcTicks}', Executable='{launchPreparation.Executable}', ProjectRoot='{ToSingleLine(launchPreparation.ProjectRoot)}', DiagnosticLoggingRequested='{launchPreparation.DiagnosticLoggingRequested}'"
			);
			// Keep the recovery-admission gate and Process.Start atomic with respect to retirement.
			launchResult = _processLauncher.Start(
				launchPreparation.Executable,
				launchPreparation.WorkingDirectory,
				_ownerIdentity,
				launchPreparation.ProjectRoot,
				launchPreparation.DiagnosticLoggingRequested
			);
		}
		if (!launchResult.Started || launchResult.LaunchedProcess == null)
			return PublishUnavailable(reason, "CodeService process could not be started: " + launchResult.Detail);

		using CodeServiceLaunchedProcess launchedProcess = launchResult.LaunchedProcess;
		string launchIdentity = launchedProcess.ServiceIdentityAvailable
			? $"ServicePid='{launchedProcess.ServiceIdentity.ProcessId}', ServiceStartTimeUtcTicks='{launchedProcess.ServiceIdentity.StartTimeUtcTicks}'"
			: $"ServiceIdentity='<unavailable>', IdentityDetail='{ToSingleLine(launchedProcess.IdentityDetail)}'";
		SafeLog(
			"CodeService Launch Started",
			$"Reason='{reason}', {launchIdentity}, ProjectRoot='{ToSingleLine(launchPreparation.ProjectRoot)}', DiagnosticLoggingRequested='{launchPreparation.DiagnosticLoggingRequested}'"
		);

		lock (_gate)
		{
			if (!_retirementStarted)
				_state = CodeServiceClientCoordinatorState.WaitingForReadiness;
		}

		string expectedDescriptorPath;
		try
		{
			expectedDescriptorPath = CodeServiceSessionPathResolver.ResolveDescriptorPath(_ownerIdentity);
		}
		catch (Exception exception)
		{
			return PublishUnavailable(reason, "descriptor path resolution failed after launch: " + ToSingleLine(exception.Message));
		}

		CodeServiceBootstrapWaitResult bootstrap = await launchedProcess
			.WaitForReadinessAsync(
				_ownerIdentity,
				_expectedServiceVersion,
				expectedDescriptorPath,
				BootstrapDeadline,
				cancellationToken
			)
			.ConfigureAwait(false);

		if (bootstrap.Status == CodeServiceBootstrapWaitStatus.Ready)
		{
			SafeLog(
				"CodeService Bootstrap Ready",
				$"Reason='{reason}', SessionId='{bootstrap.Record.SessionId}', ServicePid='{bootstrap.Record.ServiceProcessIdentity.ProcessId}', ServiceStartTimeUtcTicks='{bootstrap.Record.ServiceProcessIdentity.StartTimeUtcTicks}', DescriptorPath='{bootstrap.Record.DescriptorPath}'"
			);
		}
		else
		{
			string exitDetail = bootstrap.ExitCode.HasValue
				? $", ExitCode='{bootstrap.ExitCode.Value}'"
				: "";
			SafeLog(
				"CodeService Bootstrap Failed",
				$"Reason='{reason}', Status='{bootstrap.Status}'{exitDetail}, Detail='{bootstrap.Detail}'"
			);
		}

		CodeServiceDiscoveryResult postLaunchDiscovery = await DiscoverWithRetriesAsync(
			reason + " Post-Launch",
			retryAbsentState: true,
			cancellationToken
		).ConfigureAwait(false);

		if (postLaunchDiscovery.Status == CodeServiceDiscoveryStatus.Ready)
			return PublishReadySession(postLaunchDiscovery.Session, reason, wasExistingSession: false);
		if (postLaunchDiscovery.Status == CodeServiceDiscoveryStatus.Incompatible)
			return PublishIncompatible(reason, postLaunchDiscovery.Detail);
		if (!postLaunchDiscovery.CanConsiderLaunch)
			return PublishUnavailable(reason, postLaunchDiscovery.Detail);

		bool processStillLive = false;
		string processStateDetail = "";
		if (launchedProcess.TryCheckExited(out bool hasExited, out string exitObservationDetail))
			processStillLive = !hasExited;
		else
			processStateDetail = exitObservationDetail;

		if (bootstrap.Status == CodeServiceBootstrapWaitStatus.ProcessExited && bootstrap.ExitCode == 5)
		{
			return PublishUnavailable(
				reason,
				"The launched CodeService lost session/launch authority (exit code 5), and bounded rediscovery did not find the authoritative winner."
			);
		}

		if (processStillLive)
		{
			return PublishUnavailable(
				reason,
				"The launched CodeService process is still alive but no authenticated Ready session could be verified. It was not killed and no second replacement was launched."
			);
		}

		return PublishUnavailable(
			reason,
			string.IsNullOrWhiteSpace(processStateDetail)
				? "CodeService bootstrap did not produce a verifiable authenticated session."
				: "CodeService bootstrap did not produce a verifiable authenticated session; process state could not be determined safely: " + processStateDetail
		);
	}

	private async Task<CodeServiceDiscoveryResult> DiscoverWithRetriesAsync(
		string reason,
		bool retryAbsentState,
		CancellationToken cancellationToken
	)
	{
		CodeServiceDiscoveryResult last = default;
		int maximumAttempts = ReconnectDelays.Length + 1;
		for (int attempt = 1; attempt <= maximumAttempts; attempt++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			last = await DiscoverOnceAsync(reason, attempt, cancellationToken).ConfigureAwait(false);

			if (
				last.Status == CodeServiceDiscoveryStatus.Ready
				|| last.Status == CodeServiceDiscoveryStatus.Incompatible
				|| last.Status == CodeServiceDiscoveryStatus.AmbiguousProcess
			)
			{
				return last;
			}

			bool shouldRetry = last.Status == CodeServiceDiscoveryStatus.LiveHandshakeFailure
				|| last.Status == CodeServiceDiscoveryStatus.DescriptorUnavailable
				|| (retryAbsentState && last.CanConsiderLaunch);

			if (!shouldRetry || attempt == maximumAttempts)
				return last;

			TimeSpan delay = ReconnectDelays[attempt - 1];
			SafeLog(
				"CodeService Reconnect Retry",
				$"Reason='{reason}', Attempt='{attempt + 1}', DelayMilliseconds='{delay.TotalMilliseconds:0}', PreviousStatus='{last.Status}', Detail='{last.Detail}'"
			);
			await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
		}

		return last;
	}

	private async Task<CodeServiceDiscoveryResult> DiscoverOnceAsync(
		string reason,
		int attempt,
		CancellationToken cancellationToken
	)
	{
		CodeServiceSessionDescriptorReadResult readResult = await _descriptorReader
			.ReadAsync(_ownerIdentity, cancellationToken)
			.ConfigureAwait(false);

		if (readResult.Status == CodeServiceSessionDescriptorReadStatus.NotFound)
			return CodeServiceDiscoveryResult.Missing(readResult.Detail);
		if (readResult.Status == CodeServiceSessionDescriptorReadStatus.Invalid)
			return CodeServiceDiscoveryResult.InvalidDescriptor(readResult.Detail);
		if (readResult.Status == CodeServiceSessionDescriptorReadStatus.Unavailable)
			return CodeServiceDiscoveryResult.DescriptorUnavailable(readResult.Detail);

		CodeServiceSessionDescriptor descriptor = readResult.Descriptor;
		SafeLog(
			"CodeService Existing Session Discovered",
			$"Reason='{reason}', Attempt='{attempt}', SessionId='{descriptor.SessionId}', ServicePid='{descriptor.ServiceProcessIdentity.ProcessId}', ServiceStartTimeUtcTicks='{descriptor.ServiceProcessIdentity.StartTimeUtcTicks}', ProtocolVersion='{descriptor.ProtocolVersion}', ServiceVersion='{descriptor.ServiceVersion}', Endpoint='{descriptor.Transport}://{descriptor.Address}:{descriptor.Port}'"
		);

		CodeServiceProcessObservationCreateResult processResult =
			CodeServiceProcessObservation.TryCreate(descriptor.ServiceProcessIdentity);
		if (!processResult.IsSuccess)
		{
			descriptor.Dispose();
			return processResult.IsDefinitelyUnavailable
				? CodeServiceDiscoveryResult.StaleDead(processResult.Detail)
				: CodeServiceDiscoveryResult.AmbiguousProcess(processResult.Detail);
		}

		CodeServiceProcessObservation processObservation = processResult.Observation;
		if (
			descriptor.ProtocolVersion != CodeServiceClientProtocol.ProtocolVersion
			|| !string.Equals(
				descriptor.ServiceVersion,
				_expectedServiceVersion,
				StringComparison.Ordinal
			)
		)
		{
			string detail =
				$"A live CodeService session is incompatible with this plugin generation. DescriptorProtocolVersion='{descriptor.ProtocolVersion}', RequiredProtocolVersion='{CodeServiceClientProtocol.ProtocolVersion}', DescriptorServiceVersion='{descriptor.ServiceVersion}', RequiredServiceVersion='{_expectedServiceVersion}'. Restart the Godot editor session after installing the required CodeService version.";
			processObservation.Dispose();
			descriptor.Dispose();
			return CodeServiceDiscoveryResult.Incompatible(detail);
		}

		CodeServiceClientCredentials credentials = null;
		CodeServiceHandshakeClient handshakeClient = null;
		bool transferred = false;
		try
		{
			credentials = descriptor.TakeCredentials();
			handshakeClient = new CodeServiceHandshakeClient(descriptor.Address, descriptor.Port);
			lock (_gate)
			{
				if (!_retirementStarted)
					_state = CodeServiceClientCoordinatorState.Handshaking;
			}
			SafeLog(
				"CodeService Handshake Started",
				$"Reason='{reason}', Attempt='{attempt}', SessionId='{descriptor.SessionId}', ServicePid='{descriptor.ServiceProcessIdentity.ProcessId}', Endpoint='{descriptor.Transport}://{descriptor.Address}:{descriptor.Port}'"
			);

			CodeServiceHandshakeResult handshake = await handshakeClient
				.HandshakeAsync(
					descriptor,
					_ownerIdentity,
					_expectedServiceVersion,
					credentials,
					cancellationToken
				)
				.ConfigureAwait(false);

			if (!handshake.IsSuccess)
			{
				if (handshake.Outcome == CodeServiceHandshakeOutcome.VersionMismatch)
				{
					return CodeServiceDiscoveryResult.Incompatible(
						"The authenticated CodeService handshake reported a protocol version mismatch. Restart the Godot editor session after installing the compatible CodeService version."
					);
				}

				return CodeServiceDiscoveryResult.LiveHandshakeFailure(
					handshake.Outcome,
					handshake.Detail
				);
			}

			CodeServiceClientSession session = new(
				descriptor.SessionId,
				descriptor.ServiceVersion,
				descriptor.GodotOwnerIdentity,
				descriptor.ServiceProcessIdentity,
				processObservation,
				descriptor.DescriptorPath,
				descriptor.Transport,
				descriptor.Address,
				descriptor.Port,
				handshakeClient,
				credentials
			);
			transferred = true;
			return CodeServiceDiscoveryResult.Ready(session);
		}
		finally
		{
			descriptor.Dispose();
			if (!transferred)
			{
				handshakeClient?.Dispose();
				credentials?.Dispose();
				processObservation.Dispose();
			}
		}
	}

	private CodeServiceClientEnsureResult PublishReadySession(
		CodeServiceClientSession session,
		string reason,
		bool wasExistingSession
	)
	{
		if (session == null)
			return PublishUnavailable(reason, "Ready publication received no session.");

		long readyGeneration;
		bool watchArmed;
		string watchDetail;
		CodeServiceProcessObservation processObservation;
		lock (_gate)
		{
			if (_retirementStarted || !_recoveryAdmissionEnabled)
			{
				session.Dispose();
				return CodeServiceClientEnsureResult.Disposed("CodeService Ready publication was retired.");
			}

			_currentSession = session;
			_state = CodeServiceClientCoordinatorState.Ready;
			readyGeneration = ++_readyGeneration;
			processObservation = session.ProcessObservation;
			watchArmed = processObservation.TryArmExitObservation(
				() => OnReadyProcessExitObserved(session, readyGeneration),
				out watchDetail
			);
		}

		CodeServiceClientSessionInfo info = session.ToInfo();
		SafeLog(
			"CodeService Session Ready",
			$"Reason='{reason}', SessionId='{info.SessionId}', ProtocolVersion='{CodeServiceClientProtocol.ProtocolVersion}', ServiceVersion='{info.ServiceVersion}', OwnerPid='{info.GodotOwnerIdentity.ProcessId}', OwnerStartTimeUtcTicks='{info.GodotOwnerIdentity.StartTimeUtcTicks}', ServicePid='{info.ServiceProcessIdentity.ProcessId}', ServiceStartTimeUtcTicks='{info.ServiceProcessIdentity.StartTimeUtcTicks}', Endpoint='{info.Transport}://{info.Address}:{info.Port}'"
		);
		if (wasExistingSession)
		{
			SafeLog(
				"CodeService Session Reconnected",
				$"Reason='{reason}', SessionId='{info.SessionId}', ServicePid='{info.ServiceProcessIdentity.ProcessId}', ServiceStartTimeUtcTicks='{info.ServiceProcessIdentity.StartTimeUtcTicks}'"
			);
		}
		if (watchArmed)
		{
			SafeLog(
				"CodeService Service Process Watch Started",
				$"Reason='{reason}', ReadyGeneration='{readyGeneration}', SessionId='{info.SessionId}', ServicePid='{info.ServiceProcessIdentity.ProcessId}', ServiceStartTimeUtcTicks='{info.ServiceProcessIdentity.StartTimeUtcTicks}'"
			);
		}

		PublishReadyCallbackIfCurrent(session, readyGeneration, info, reason);

		if (watchArmed)
		{
			// Activation happens only after the coordinator lock and Ready publication callback
			// boundary are clear. A pre-activation exit is delivered exactly once here.
			processObservation.ActivateExitObservation();
		}
		else
		{
			SafeLog(
				"CodeService Session Unavailable",
				$"Reason='Service Process Watch Failure', ReadyGeneration='{readyGeneration}', SessionId='{info.SessionId}', ServicePid='{info.ServiceProcessIdentity.ProcessId}', Detail='{ToSingleLine(watchDetail)}'"
			);
			StartRecoveryFromWatcher(
				session,
				readyGeneration,
				info,
				"Service Process Watch Failure",
				positiveDeathEvidence: false
			);
		}

		return CodeServiceClientEnsureResult.Ready(session);
	}

	private void PublishReadyCallbackIfCurrent(
		CodeServiceClientSession session,
		long readyGeneration,
		CodeServiceClientSessionInfo info,
		string reason
	)
	{
		lock (_gate)
		{
			if (
				_retirementStarted
				|| !_recoveryAdmissionEnabled
				|| _state != CodeServiceClientCoordinatorState.Ready
				|| _readyGeneration != readyGeneration
				|| !ReferenceEquals(_currentSession, session)
				|| _sessionReadyCallback == null
			)
			{
				return;
			}

			try
			{
				// Serialize the final callback admission with synchronous retirement so a
				// Ready continuation cannot cross the retirement boundary after it closes.
				_sessionReadyCallback(info, reason);
			}
			catch (Exception exception)
			{
				SafeLog(
					"CodeService Launch Marker Write Failed",
					$"Reason='{reason}', Detail='{ToSingleLine(exception.Message)}'"
				);
			}
		}
	}


	private void PublishWorkspaceReadyCallbackIfCurrent(
		CodeServiceClientSession session,
		long readyGeneration,
		CodeServiceClientSessionInfo info,
		string normalizedProjectRoot,
		bool? reusedExistingWorkspace,
		string reason
	)
	{
		Action<CodeServiceClientSessionInfo, string, bool?, string> callback;
		lock (_gate)
		{
			if (_retirementStarted || !_recoveryAdmissionEnabled || _state != CodeServiceClientCoordinatorState.Ready || _readyGeneration != readyGeneration || !ReferenceEquals(_currentSession, session) || _workspaceReadyCallback == null)
				return;
			callback = _workspaceReadyCallback;
		}

		try
		{
			callback(info, normalizedProjectRoot, reusedExistingWorkspace, reason ?? "CodeService Workspace Ready");
		}
		catch (Exception exception)
		{
			SafeLog("CodeService Workspace Ready Callback Failed", $"Reason='{reason}', Detail='{ToSingleLine(exception.Message)}'");
		}
	}

	private CodeServiceClientEnsureResult PublishIncompatible(string reason, string detail)
	{
		lock (_gate)
		{
			if (!_retirementStarted)
				_state = CodeServiceClientCoordinatorState.Unavailable;
		}
		SafeLog("CodeService Session Incompatible", $"Reason='{reason}', Detail='{detail}'");
		return CodeServiceClientEnsureResult.Incompatible(detail);
	}

	private CodeServiceClientEnsureResult PublishUnavailable(string reason, string detail)
	{
		lock (_gate)
		{
			if (!_retirementStarted)
				_state = CodeServiceClientCoordinatorState.Unavailable;
		}
		SafeLog("CodeService Session Unavailable", $"Reason='{reason}', Detail='{detail}'");
		return CodeServiceClientEnsureResult.Unavailable(detail);
	}

	private void OnReadyProcessExitObserved(
		CodeServiceClientSession session,
		long readyGeneration
	)
	{
		if (!IsCurrentReadySession(session, readyGeneration))
			return;

		CodeServiceClientSessionInfo info = session.ToInfo();
		SafeLog(
			"CodeService Service Process Exit Observed",
			$"ReadyGeneration='{readyGeneration}', SessionId='{info.SessionId}', ServicePid='{info.ServiceProcessIdentity.ProcessId}', ServiceStartTimeUtcTicks='{info.ServiceProcessIdentity.StartTimeUtcTicks}'"
		);
		StartRecoveryFromWatcher(
			session,
			readyGeneration,
			info,
			"Verified CodeService Process Exit",
			positiveDeathEvidence: true
		);
	}

	private void StartRecoveryFromWatcher(
		CodeServiceClientSession session,
		long readyGeneration,
		CodeServiceClientSessionInfo oldInfo,
		string reason,
		bool positiveDeathEvidence
	)
	{
		lock (_gate)
		{
			if (
				_retirementStarted
				|| !_recoveryAdmissionEnabled
				|| _readyGeneration != readyGeneration
				|| !ReferenceEquals(_currentSession, session)
			)
			{
				return;
			}

			if (_activeFlight != null)
			{
				_pendingWatcherRecoverySession = session;
				_pendingWatcherRecoveryInfo = oldInfo;
				_pendingWatcherRecoveryGeneration = readyGeneration;
				_pendingWatcherRecoveryReason = reason ?? "Service Process Exit";
				_pendingWatcherRecoveryDeathEvidence = positiveDeathEvidence;
				return;
			}

			StartFlightLocked(
				reason,
				isRecovery: true,
				default,
				session,
				oldInfo,
				positiveDeathEvidence
			);
		}
	}

	private Task<CodeServiceClientEnsureResult> TryStartPendingWatcherRecoveryLocked()
	{
		if (_activeFlight != null || _pendingWatcherRecoverySession == null)
			return null;

		CodeServiceClientSession session = _pendingWatcherRecoverySession;
		CodeServiceClientSessionInfo info = _pendingWatcherRecoveryInfo;
		long generation = _pendingWatcherRecoveryGeneration;
		string reason = _pendingWatcherRecoveryReason;
		bool deathEvidence = _pendingWatcherRecoveryDeathEvidence;
		ClearPendingWatcherRecoveryLocked();

		if (
			_retirementStarted
			|| !_recoveryAdmissionEnabled
			|| _readyGeneration != generation
			|| !ReferenceEquals(_currentSession, session)
		)
		{
			return null;
		}

		return StartFlightLocked(
			reason,
			isRecovery: true,
			default,
			session,
			info,
			deathEvidence
		);
	}

	private void ClearPendingWatcherRecoveryLocked()
	{
		_pendingWatcherRecoverySession = null;
		_pendingWatcherRecoveryInfo = default;
		_pendingWatcherRecoveryGeneration = 0;
		_pendingWatcherRecoveryReason = "";
		_pendingWatcherRecoveryDeathEvidence = false;
	}

	private bool IsCurrentReadySession(CodeServiceClientSession session, long readyGeneration)
	{
		lock (_gate)
		{
			return !_retirementStarted
				&& _recoveryAdmissionEnabled
				&& _state == CodeServiceClientCoordinatorState.Ready
				&& _readyGeneration == readyGeneration
				&& ReferenceEquals(_currentSession, session);
		}
	}

	private async Task<CodeServiceWorkspaceEnsureResult> RunWorkspaceObservationAndFinalizeAsync(
		long flightGeneration,
		CodeServiceClientSession session,
		long readyGeneration,
		string normalizedProjectRoot,
		string reason,
		CancellationTokenSource flightCancellation
	)
	{
		await Task.Yield();
		try
		{
			if (!IsCurrentReadySession(session, readyGeneration))
				return CodeServiceWorkspaceEnsureResult.StaleSession("Ready session changed before workspace observation started.");

			CodeServiceClientSessionInfo info = session.ToInfo();
			SafeLog(
				"CodeService Workspace Observation Started",
				$"Reason='{reason}', ReadyGeneration='{readyGeneration}', SessionId='{info.SessionId}', ServicePid='{info.ServiceProcessIdentity.ProcessId}', ProjectRoot='{ToSingleLine(normalizedProjectRoot)}'"
			);

			return await ObserveWorkspaceReadyStatusAsync(
				session,
				readyGeneration,
				normalizedProjectRoot,
				reason,
				info,
				flightCancellation.Token
			).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (flightCancellation.IsCancellationRequested)
		{
			if (_lifetimeCancellation.IsCancellationRequested)
				return CodeServiceWorkspaceEnsureResult.Disposed("CodeService workspace observation flight was retired with the coordinator lifetime.");
			if (!IsCurrentReadySession(session, readyGeneration))
				return CodeServiceWorkspaceEnsureResult.StaleSession("CodeService workspace observation flight was retired with its Ready session.");
			return CodeServiceWorkspaceEnsureResult.Unavailable("CodeService workspace observation flight was canceled.");
		}
		catch (Exception exception)
		{
			if (!IsCurrentReadySession(session, readyGeneration))
				return CodeServiceWorkspaceEnsureResult.StaleSession("Workspace result became stale while handling an exception.");

			CodeServiceClientSessionInfo info = session.ToInfo();
			string detail = ToSingleLine(exception.Message);
			LogWorkspaceUnavailable(info, reason, "TransportUnavailable", detail);
			return CodeServiceWorkspaceEnsureResult.TransportUnavailable(detail);
		}
		finally
		{
			CancellationTokenSource cancellationToDispose = null;
			lock (_gate)
			{
				if (_workspaceFlightGeneration == flightGeneration)
				{
					cancellationToDispose = _workspaceFlightCancellation;
					_workspaceFlight = null;
					_workspaceFlightSession = null;
					_workspaceFlightReadyGeneration = 0;
					_workspaceFlightProjectRoot = "";
					_workspaceFlightCancellation = null;
				}
			}

			try
			{
				cancellationToDispose?.Dispose();
			}
			catch
			{
			}
		}
	}

	private async Task<CodeServiceWorkspaceEnsureResult> ObserveWorkspaceReadyStatusAsync(
		CodeServiceClientSession session,
		long readyGeneration,
		string normalizedProjectRoot,
		string reason,
		CodeServiceClientSessionInfo info,
		CancellationToken cancellationToken
	)
	{
		using CancellationTokenSource observationCancellation =
			CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		observationCancellation.CancelAfter(WorkspaceReadyObservationDeadline);
		int delayIndex = 0;
		bool waitingLogged = false;

		try
		{
			while (true)
			{
				if (!IsCurrentReadySession(session, readyGeneration))
					return CodeServiceWorkspaceEnsureResult.StaleSession("Ready session changed during workspace readiness observation.");

				// First status request is immediate. Delays only follow a matching transient state.
				CodeServiceWorkspaceStatusResult status = await session
					.GetWorkspaceStatusAsync(observationCancellation.Token)
					.ConfigureAwait(false);

				if (!IsCurrentReadySession(session, readyGeneration))
					return CodeServiceWorkspaceEnsureResult.StaleSession("Ready session changed before workspace status result publication.");

				if (status.Outcome == CodeServiceWorkspaceStatusOutcome.Success)
				{
					if (!CodeServiceWorkspacePath.EqualsNormalized(status.ProjectRoot, normalizedProjectRoot))
					{
						string detail = string.IsNullOrEmpty(status.ProjectRoot)
							? "Workspace status did not contain the startup projectRoot expected by this plugin generation."
							: "Workspace status returned a different projectRoot than this plugin generation expects.";
						SafeLog(
							"CodeService Workspace Protocol Failure",
							$"Reason='{reason}', Outcome='WorkspaceMismatch', SessionId='{info.SessionId}', ServicePid='{info.ServiceProcessIdentity.ProcessId}', ExpectedProjectRoot='{ToSingleLine(normalizedProjectRoot)}', CurrentProjectRoot='{ToSingleLine(status.ProjectRoot)}', Detail='{detail}'"
						);
						return CodeServiceWorkspaceEnsureResult.WorkspaceMismatchFromStatus(status, detail);
					}

					switch (status.State)
					{
						case CodeServiceWorkspaceState.Uninitialized:
						case CodeServiceWorkspaceState.Initializing:
						case CodeServiceWorkspaceState.Indexing:
							if (!waitingLogged)
							{
								waitingLogged = true;
								SafeLog(
									"CodeService Workspace Waiting",
									$"Reason='{reason}', SessionId='{info.SessionId}', ServicePid='{info.ServiceProcessIdentity.ProcessId}', State='{status.State}', ProjectRoot='{ToSingleLine(status.ProjectRoot)}'"
								);
							}

							TimeSpan delay = WorkspaceStatusObservationDelays[
								Math.Min(delayIndex, WorkspaceStatusObservationDelays.Length - 1)
							];
							delayIndex++;
							await Task.Delay(delay, observationCancellation.Token).ConfigureAwait(false);
							continue;

						case CodeServiceWorkspaceState.Ready:
							LogWorkspaceReady(info, reason, null, status.SourceFileCount, status.ProjectFileCount, status.SolutionFileCount);
							CodeServiceWorkspaceEnsureResult readyResult = CodeServiceWorkspaceEnsureResult.ReadyFromStatus(status);
							PublishWorkspaceReadyCallbackIfCurrent(session, readyGeneration, info, normalizedProjectRoot, null, reason);
							return readyResult;

						case CodeServiceWorkspaceState.Faulted:
							LogWorkspaceFaulted(info, reason, status.State, status.FaultKind, status.SourceFileCount, status.ProjectFileCount, status.SolutionFileCount);
							return CodeServiceWorkspaceEnsureResult.FaultedFromStatus(status);

						default:
							string unexpectedStateDetail =
								$"Workspace readiness observation reached unexpected state '{status.State}'.";
							LogWorkspaceProtocolFailure(info, reason, "MalformedResponse", unexpectedStateDetail);
							return CodeServiceWorkspaceEnsureResult.MalformedResponse(unexpectedStateDetail);
					}
				}

				switch (status.Outcome)
				{
					case CodeServiceWorkspaceStatusOutcome.Unavailable:
						LogWorkspaceUnavailable(info, reason, "Unavailable", status.Detail);
						return CodeServiceWorkspaceEnsureResult.Unavailable(status.Detail);
					case CodeServiceWorkspaceStatusOutcome.InvalidRequest:
						LogWorkspaceProtocolFailure(info, reason, "InvalidRequest", status.Detail);
						return CodeServiceWorkspaceEnsureResult.InvalidRequest(status.Detail);
					case CodeServiceWorkspaceStatusOutcome.VersionMismatch:
						LogWorkspaceProtocolFailure(info, reason, "VersionMismatch", status.Detail);
						return CodeServiceWorkspaceEnsureResult.VersionMismatch(status.Detail);
					case CodeServiceWorkspaceStatusOutcome.AuthenticationFailed:
						LogWorkspaceUnavailable(info, reason, "AuthenticationFailed", status.Detail);
						return CodeServiceWorkspaceEnsureResult.AuthenticationFailed(status.Detail);
					case CodeServiceWorkspaceStatusOutcome.ControlPlaneUnavailable:
						LogWorkspaceUnavailable(info, reason, "ControlPlaneUnavailable", status.Detail);
						return CodeServiceWorkspaceEnsureResult.ControlPlaneUnavailable(status.Detail);
					case CodeServiceWorkspaceStatusOutcome.TransportUnavailable:
						LogWorkspaceUnavailable(info, reason, "TransportUnavailable", status.Detail);
						return CodeServiceWorkspaceEnsureResult.TransportUnavailable(status.Detail);
					case CodeServiceWorkspaceStatusOutcome.MalformedResponse:
						LogWorkspaceProtocolFailure(info, reason, "MalformedResponse", status.Detail);
						return CodeServiceWorkspaceEnsureResult.MalformedResponse(status.Detail);
					default:
						return CodeServiceWorkspaceEnsureResult.MalformedResponse("Unknown workspace status outcome.");
				}
			}
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (OperationCanceledException) when (observationCancellation.IsCancellationRequested)
		{
			if (!IsCurrentReadySession(session, readyGeneration))
				return CodeServiceWorkspaceEnsureResult.StaleSession("Ready session changed at workspace readiness observation deadline.");

			SafeLog(
				"CodeService Workspace Unavailable",
				$"Reason='{reason}', Outcome='ObservationTimeout', SessionId='{info.SessionId}', ServicePid='{info.ServiceProcessIdentity.ProcessId}', DeadlineSeconds='{WorkspaceReadyObservationDeadline.TotalSeconds:0}'"
			);
			return CodeServiceWorkspaceEnsureResult.ObservationTimeout(
				$"workspace did not reach Ready within the {WorkspaceReadyObservationDeadline.TotalSeconds:0} second observation deadline."
			);
		}
	}

	private void LogWorkspaceReady(
		CodeServiceClientSessionInfo info,
		string reason,
		bool? reusedExistingWorkspace,
		int sourceFileCount,
		int projectFileCount,
		int solutionFileCount
	)
	{
		string reused = reusedExistingWorkspace.HasValue
			? reusedExistingWorkspace.Value.ToString()
			: "NotAvailableFromStatus";
		SafeLog(
			"CodeService Workspace Ready",
			$"Reason='{reason}', SessionId='{info.SessionId}', ServicePid='{info.ServiceProcessIdentity.ProcessId}', ReusedExistingWorkspace='{reused}', SourceFileCount='{sourceFileCount}', ProjectFileCount='{projectFileCount}', SolutionFileCount='{solutionFileCount}'"
		);
	}

	private void LogWorkspaceFaulted(
		CodeServiceClientSessionInfo info,
		string reason,
		CodeServiceWorkspaceState state,
		string faultKind,
		int sourceFileCount,
		int projectFileCount,
		int solutionFileCount
	)
	{
		SafeLog(
			"CodeService Workspace Faulted",
			$"Reason='{reason}', SessionId='{info.SessionId}', ServicePid='{info.ServiceProcessIdentity.ProcessId}', State='{state}', FaultKind='{ToSingleLine(faultKind)}', SourceFileCount='{sourceFileCount}', ProjectFileCount='{projectFileCount}', SolutionFileCount='{solutionFileCount}'"
		);
	}

	private void LogWorkspaceUnavailable(
		CodeServiceClientSessionInfo info,
		string reason,
		string outcome,
		string detail
	)
	{
		SafeLog(
			"CodeService Workspace Unavailable",
			$"Reason='{reason}', Outcome='{outcome}', SessionId='{info.SessionId}', ServicePid='{info.ServiceProcessIdentity.ProcessId}', Detail='{ToSingleLine(detail)}'"
		);
	}

	private void LogWorkspaceProtocolFailure(
		CodeServiceClientSessionInfo info,
		string reason,
		string outcome,
		string detail
	)
	{
		SafeLog(
			"CodeService Workspace Protocol Failure",
			$"Reason='{reason}', Outcome='{outcome}', SessionId='{info.SessionId}', ServicePid='{info.ServiceProcessIdentity.ProcessId}', Detail='{ToSingleLine(detail)}'"
		);
	}

	private async Task RetireCurrentReadySessionForRecoveryAsync(
		CodeServiceClientSession expectedSession,
		CancellationToken cancellationToken
	)
	{
		CodeServiceClientSession session = null;
		CancellationTokenSource workspaceCancellation = null;
		Task<CodeServiceWorkspaceEnsureResult> workspaceTask = null;

		lock (_gate)
		{
			if (_retirementStarted)
				throw new OperationCanceledException(cancellationToken);

			if (expectedSession != null && ReferenceEquals(_currentSession, expectedSession))
			{
				session = _currentSession;
				_currentSession = null;
				if (ReferenceEquals(_workspaceFlightSession, session))
				{
					workspaceCancellation = _workspaceFlightCancellation;
					workspaceTask = _workspaceFlight;
				}
				_readyGeneration++;
				_state = CodeServiceClientCoordinatorState.Recovering;
			}
		}

		if (session == null)
			return;

		// Remove this exact managed generation's Process.Exited handler synchronously
		// before touching finite workspace async work or disposing the session.
		session.ProcessObservation.StopExitObservation();

		try
		{
			workspaceCancellation?.Cancel();
		}
		catch (ObjectDisposedException)
		{
		}

		// Cancellation is only a request; observe actual finite workspace retirement
		// before the shared HttpClient/credentials/process handle are disposed.
		await ObserveTaskAsync(workspaceTask).ConfigureAwait(false);
		session.Dispose();
	}

	private async Task CompleteRetirementAndSignalAsync(
		string reason,
		CodeServiceClientSession session,
		Task<CodeServiceWorkspaceEnsureResult> workspaceFlight,
		Task<CodeServiceClientEnsureResult> activeFlight,
		TaskCompletionSource<bool> retirementCompletion
	)
	{
		try
		{
			// SignalRetirement already synchronously detached Process.Exited and requested
			// lifetime cancellation. Observe finite workspace retirement before session disposal.
			await ObserveTaskAsync(workspaceFlight).ConfigureAwait(false);

			try
			{
				session?.Dispose();
			}
			catch
			{
			}

			await ObserveTaskAsync(activeFlight).ConfigureAwait(false);
		}
		finally
		{
			try
			{
				_lifetimeCancellation.Dispose();
			}
			catch
			{
			}

			retirementCompletion.TrySetResult(true);
		}
	}

	private static async Task<CodeServiceWorkspaceEnsureResult> WaitForWorkspaceCallerAsync(
		Task<CodeServiceWorkspaceEnsureResult> sharedFlight,
		CancellationToken callerCancellationToken
	)
	{
		if (!callerCancellationToken.CanBeCanceled)
			return await sharedFlight.ConfigureAwait(false);

		return await sharedFlight.WaitAsync(callerCancellationToken).ConfigureAwait(false);
	}

	private static bool IsExactSessionInfo(
		CodeServiceClientSessionInfo left,
		CodeServiceClientSessionInfo right
	)
	{
		return string.Equals(left.SessionId, right.SessionId, StringComparison.Ordinal)
			&& left.GodotOwnerIdentity.ProcessId == right.GodotOwnerIdentity.ProcessId
			&& left.GodotOwnerIdentity.StartTimeUtcTicks == right.GodotOwnerIdentity.StartTimeUtcTicks
			&& left.ServiceProcessIdentity.ProcessId == right.ServiceProcessIdentity.ProcessId
			&& left.ServiceProcessIdentity.StartTimeUtcTicks == right.ServiceProcessIdentity.StartTimeUtcTicks;
	}

	private static async Task<CodeServiceClientEnsureResult> WaitForCallerAsync(
		Task<CodeServiceClientEnsureResult> sharedFlight,
		CancellationToken callerCancellationToken
	)
	{
		if (!callerCancellationToken.CanBeCanceled)
			return await sharedFlight.ConfigureAwait(false);

		return await sharedFlight.WaitAsync(callerCancellationToken).ConfigureAwait(false);
	}

	private static async Task ObserveTaskAsync(Task task)
	{
		if (task == null)
			return;
		try
		{
			await task.ConfigureAwait(false);
		}
		catch
		{
		}
	}

	private static async Task ObserveTaskAsync<T>(Task<T> task)
	{
		if (task == null)
			return;
		try
		{
			await task.ConfigureAwait(false);
		}
		catch
		{
		}
	}

	private void SafeLog(string operation, string detail)
	{
		lock (_gate)
		{
			if (_retirementStarted || _logOperation == null)
				return;

			try
			{
				// Keep best-effort diagnostics inside the same callback-admission boundary.
				_logOperation(operation, detail ?? "");
			}
			catch
			{
			}
		}
	}

	private static string ToSingleLine(string message)
	{
		return (message ?? "").Replace('\r', ' ').Replace('\n', ' ');
	}

	private enum CodeServiceDiscoveryStatus
	{
		None,
		Ready,
		Missing,
		InvalidDescriptor,
		StaleDead,
		DescriptorUnavailable,
		AmbiguousProcess,
		LiveHandshakeFailure,
		Incompatible,
	}

	private readonly struct CodeServiceDiscoveryResult
	{
		private CodeServiceDiscoveryResult(
			CodeServiceDiscoveryStatus status,
			CodeServiceClientSession session,
			CodeServiceHandshakeOutcome handshakeOutcome,
			string detail
		)
		{
			Status = status;
			Session = session;
			HandshakeOutcome = handshakeOutcome;
			Detail = detail ?? "";
		}

		internal CodeServiceDiscoveryStatus Status { get; }
		internal CodeServiceClientSession Session { get; }
		internal CodeServiceHandshakeOutcome HandshakeOutcome { get; }
		internal string Detail { get; }
		internal bool CanConsiderLaunch =>
			Status == CodeServiceDiscoveryStatus.Missing
			|| Status == CodeServiceDiscoveryStatus.InvalidDescriptor
			|| Status == CodeServiceDiscoveryStatus.StaleDead;

		internal static CodeServiceDiscoveryResult Ready(CodeServiceClientSession session)
			=> new(CodeServiceDiscoveryStatus.Ready, session, default, "");
		internal static CodeServiceDiscoveryResult Missing(string detail)
			=> new(CodeServiceDiscoveryStatus.Missing, null, default, detail);
		internal static CodeServiceDiscoveryResult InvalidDescriptor(string detail)
			=> new(CodeServiceDiscoveryStatus.InvalidDescriptor, null, default, detail);
		internal static CodeServiceDiscoveryResult StaleDead(string detail)
			=> new(CodeServiceDiscoveryStatus.StaleDead, null, default, detail);
		internal static CodeServiceDiscoveryResult DescriptorUnavailable(string detail)
			=> new(CodeServiceDiscoveryStatus.DescriptorUnavailable, null, default, detail);
		internal static CodeServiceDiscoveryResult AmbiguousProcess(string detail)
			=> new(CodeServiceDiscoveryStatus.AmbiguousProcess, null, default, detail);
		internal static CodeServiceDiscoveryResult LiveHandshakeFailure(
			CodeServiceHandshakeOutcome outcome,
			string detail
		) => new(CodeServiceDiscoveryStatus.LiveHandshakeFailure, null, outcome, detail);
		internal static CodeServiceDiscoveryResult Incompatible(string detail)
			=> new(CodeServiceDiscoveryStatus.Incompatible, null, default, detail);
	}
}

internal enum CodeServiceClientCoordinatorState
{
	Disconnected,
	DiscoveringExisting,
	Handshaking,
	Starting,
	WaitingForReadiness,
	Ready,
	Recovering,
	Unavailable,
	Disposed,
}

internal enum CodeServiceClientEnsureStatus
{
	Ready,
	Incompatible,
	Unavailable,
	Disposed,
}

internal readonly struct CodeServiceClientEnsureResult
{
	private CodeServiceClientEnsureResult(
		CodeServiceClientEnsureStatus status,
		CodeServiceClientSession session,
		string detail
	)
	{
		Status = status;
		Session = session;
		Detail = detail ?? "";
	}

	internal CodeServiceClientEnsureStatus Status { get; }
	internal CodeServiceClientSession Session { get; }
	internal string Detail { get; }
	internal bool IsReady => Status == CodeServiceClientEnsureStatus.Ready && Session != null;
	internal bool RequiresGodotRestart => Status == CodeServiceClientEnsureStatus.Incompatible;

	internal static CodeServiceClientEnsureResult Ready(CodeServiceClientSession session)
		=> new(CodeServiceClientEnsureStatus.Ready, session, "");
	internal static CodeServiceClientEnsureResult Incompatible(string detail)
		=> new(CodeServiceClientEnsureStatus.Incompatible, null, detail);
	internal static CodeServiceClientEnsureResult Unavailable(string detail)
		=> new(CodeServiceClientEnsureStatus.Unavailable, null, detail);
	internal static CodeServiceClientEnsureResult Disposed(string detail)
		=> new(CodeServiceClientEnsureStatus.Disposed, null, detail);
}

internal enum CodeServiceWorkspaceEnsureOutcome
{
	Ready,
	Faulted,
	WorkspaceMismatch,
	InvalidRequest,
	VersionMismatch,
	Unavailable,
	AuthenticationFailed,
	ControlPlaneUnavailable,
	TransportUnavailable,
	MalformedResponse,
	ObservationTimeout,
	StaleSession,
	Disposed,
}

internal readonly struct CodeServiceWorkspaceEnsureResult
{
	private CodeServiceWorkspaceEnsureResult(
		CodeServiceWorkspaceEnsureOutcome outcome,
		CodeServiceWorkspaceState state,
		string projectRoot,
		bool? reusedExistingWorkspace,
		int sourceFileCount,
		int projectFileCount,
		int solutionFileCount,
		string faultKind,
		string detail
	)
	{
		Outcome = outcome;
		State = state;
		ProjectRoot = projectRoot ?? "";
		ReusedExistingWorkspace = reusedExistingWorkspace;
		SourceFileCount = sourceFileCount;
		ProjectFileCount = projectFileCount;
		SolutionFileCount = solutionFileCount;
		FaultKind = faultKind ?? "";
		Detail = detail ?? "";
	}

	internal CodeServiceWorkspaceEnsureOutcome Outcome { get; }
	internal CodeServiceWorkspaceState State { get; }
	internal string ProjectRoot { get; }
	internal bool? ReusedExistingWorkspace { get; }
	internal int SourceFileCount { get; }
	internal int ProjectFileCount { get; }
	internal int SolutionFileCount { get; }
	internal string FaultKind { get; }
	internal string Detail { get; }
	internal bool IsReady => Outcome == CodeServiceWorkspaceEnsureOutcome.Ready;

	internal static CodeServiceWorkspaceEnsureResult Ready(CodeServiceWorkspaceInitializeResult result)
		=> new(CodeServiceWorkspaceEnsureOutcome.Ready, result.State, result.ProjectRoot, result.ReusedExistingWorkspace, result.SourceFileCount, result.ProjectFileCount, result.SolutionFileCount, result.FaultKind, result.Detail);
	internal static CodeServiceWorkspaceEnsureResult ReadyFromStatus(CodeServiceWorkspaceStatusResult result)
		=> new(CodeServiceWorkspaceEnsureOutcome.Ready, result.State, result.ProjectRoot, null, result.SourceFileCount, result.ProjectFileCount, result.SolutionFileCount, result.FaultKind, result.Detail);
	internal static CodeServiceWorkspaceEnsureResult Faulted(CodeServiceWorkspaceInitializeResult result)
		=> new(CodeServiceWorkspaceEnsureOutcome.Faulted, result.State, result.ProjectRoot, result.ReusedExistingWorkspace, result.SourceFileCount, result.ProjectFileCount, result.SolutionFileCount, result.FaultKind, result.Detail);
	internal static CodeServiceWorkspaceEnsureResult FaultedFromStatus(CodeServiceWorkspaceStatusResult result)
		=> new(CodeServiceWorkspaceEnsureOutcome.Faulted, result.State, result.ProjectRoot, null, result.SourceFileCount, result.ProjectFileCount, result.SolutionFileCount, result.FaultKind, result.Detail);
	internal static CodeServiceWorkspaceEnsureResult WorkspaceMismatch(CodeServiceWorkspaceInitializeResult result)
		=> new(CodeServiceWorkspaceEnsureOutcome.WorkspaceMismatch, result.State, result.ProjectRoot, result.ReusedExistingWorkspace, result.SourceFileCount, result.ProjectFileCount, result.SolutionFileCount, result.FaultKind, result.Detail);
	internal static CodeServiceWorkspaceEnsureResult WorkspaceMismatchFromStatus(
		CodeServiceWorkspaceStatusResult result,
		string detail
	)
		=> new(CodeServiceWorkspaceEnsureOutcome.WorkspaceMismatch, result.State, result.ProjectRoot, null, result.SourceFileCount, result.ProjectFileCount, result.SolutionFileCount, result.FaultKind, detail);
	internal static CodeServiceWorkspaceEnsureResult InvalidRequest(string detail)
		=> Simple(CodeServiceWorkspaceEnsureOutcome.InvalidRequest, detail);
	internal static CodeServiceWorkspaceEnsureResult VersionMismatch(string detail)
		=> Simple(CodeServiceWorkspaceEnsureOutcome.VersionMismatch, detail);
	internal static CodeServiceWorkspaceEnsureResult Unavailable(string detail)
		=> Simple(CodeServiceWorkspaceEnsureOutcome.Unavailable, detail);
	internal static CodeServiceWorkspaceEnsureResult AuthenticationFailed(string detail)
		=> Simple(CodeServiceWorkspaceEnsureOutcome.AuthenticationFailed, detail);
	internal static CodeServiceWorkspaceEnsureResult ControlPlaneUnavailable(string detail)
		=> Simple(CodeServiceWorkspaceEnsureOutcome.ControlPlaneUnavailable, detail);
	internal static CodeServiceWorkspaceEnsureResult TransportUnavailable(string detail)
		=> Simple(CodeServiceWorkspaceEnsureOutcome.TransportUnavailable, detail);
	internal static CodeServiceWorkspaceEnsureResult MalformedResponse(string detail)
		=> Simple(CodeServiceWorkspaceEnsureOutcome.MalformedResponse, detail);
	internal static CodeServiceWorkspaceEnsureResult ObservationTimeout(string detail)
		=> Simple(CodeServiceWorkspaceEnsureOutcome.ObservationTimeout, detail);
	internal static CodeServiceWorkspaceEnsureResult StaleSession(string detail)
		=> Simple(CodeServiceWorkspaceEnsureOutcome.StaleSession, detail);
	internal static CodeServiceWorkspaceEnsureResult Disposed(string detail)
		=> Simple(CodeServiceWorkspaceEnsureOutcome.Disposed, detail);

	private static CodeServiceWorkspaceEnsureResult Simple(
		CodeServiceWorkspaceEnsureOutcome outcome,
		string detail
	)
		=> new(outcome, CodeServiceWorkspaceState.Uninitialized, "", null, 0, 0, 0, "", detail);
}

internal readonly struct CodeServiceClientLaunchPreparation
{
	private CodeServiceClientLaunchPreparation(
		bool canLaunch,
		string executable,
		string workingDirectory,
		string projectRoot,
		bool diagnosticLoggingRequested,
		string detail
	)
	{
		CanLaunch = canLaunch;
		Executable = executable ?? "";
		WorkingDirectory = workingDirectory ?? "";
		ProjectRoot = projectRoot ?? "";
		DiagnosticLoggingRequested = diagnosticLoggingRequested;
		Detail = detail ?? "";
	}

	internal bool CanLaunch { get; }
	internal string Executable { get; }
	internal string WorkingDirectory { get; }
	internal string ProjectRoot { get; }
	internal bool DiagnosticLoggingRequested { get; }
	internal string Detail { get; }

	internal static CodeServiceClientLaunchPreparation Success(
		string executable,
		string workingDirectory,
		string projectRoot,
		bool diagnosticLoggingRequested
	)
	{
		if (string.IsNullOrWhiteSpace(executable))
			throw new ArgumentException("CodeService executable is required for launch preparation.", nameof(executable));
		if (string.IsNullOrWhiteSpace(workingDirectory))
			throw new ArgumentException("CodeService workingDirectory is required for launch preparation.", nameof(workingDirectory));
		if (string.IsNullOrWhiteSpace(projectRoot))
			throw new ArgumentException("CodeService projectRoot is required for launch preparation.", nameof(projectRoot));
		if (
			!CodeServiceWorkspacePath.TryNormalize(
				projectRoot,
				out string normalizedProjectRoot,
				out string normalizationDetail
			)
		)
		{
			throw new ArgumentException(
				"CodeService projectRoot launch preparation requires an absolute normalized path: "
					+ normalizationDetail,
				nameof(projectRoot)
			);
		}
		if (!CodeServiceWorkspacePath.EqualsNormalized(projectRoot, normalizedProjectRoot))
		{
			throw new ArgumentException(
				"CodeService projectRoot launch preparation requires an already-normalized absolute path.",
				nameof(projectRoot)
			);
		}

		return new CodeServiceClientLaunchPreparation(
			true,
			executable,
			workingDirectory,
			projectRoot,
			diagnosticLoggingRequested,
			""
		);
	}

	internal static CodeServiceClientLaunchPreparation Unavailable(string detail)
	{
		return new CodeServiceClientLaunchPreparation(false, "", "", "", false, detail);
	}
}

internal readonly struct CodeServiceClientLaunchHint
{
	private CodeServiceClientLaunchHint(
		bool hasVerifiedLiveService,
		CodeServiceProcessIdentity serviceIdentity,
		bool launchBlocked,
		string detail
	)
	{
		HasVerifiedLiveService = hasVerifiedLiveService;
		ServiceIdentity = serviceIdentity;
		LaunchBlocked = launchBlocked;
		Detail = detail ?? "";
	}

	internal bool HasVerifiedLiveService { get; }
	internal CodeServiceProcessIdentity ServiceIdentity { get; }
	internal bool LaunchBlocked { get; }
	internal string Detail { get; }

	internal static CodeServiceClientLaunchHint VerifiedLive(
		CodeServiceProcessIdentity serviceIdentity
	)
	{
		return new CodeServiceClientLaunchHint(true, serviceIdentity, false, "");
	}

	internal static CodeServiceClientLaunchHint Blocked(string detail)
	{
		return new CodeServiceClientLaunchHint(false, default, true, detail);
	}
}
#endif
