#if TOOLS
using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using SystemExplorer.CodeService.Client;
using SystemExplorer.CodeService.Installation;
using SystemExplorer.CodeService.Runtime;
using SystemExplorer.EditorIntegration.Operations;

public partial class SystemExplorerPlugin
{
	#region CodeService Installation and Runtime
	private const string CodeServiceLaunchMarkerMetadataKey =
		"_system_explorer_code_service_launch_v1";
	private const string CodeServiceLaunchMarkerVersion = "v1";

	private bool _isInstallingCodeService;
	private CodeServiceToolService _codeServiceToolService;
	private CodeServiceProcessRunner _codeServiceProcessRunner;
	private CodeServiceProcessLauncher _codeServiceProcessLauncher;
	private CodeServiceClientCoordinator _codeServiceClientCoordinator;
	private CodeServiceProcessIdentity _codeServiceClientOwnerIdentity;
	private bool _codeServiceClientOwnerIdentityAvailable;
	private Task _codeServiceClientEnsureObservationTask;
	private Task _codeServiceWorkspaceEnsureObservationTask;
	private Task _codeServiceClientRetirementObservationTask;
	private readonly object _codeServiceClientCallbackGate = new();
	private long _codeServiceClientCallbackGenerationCounter;
	private long _codeServiceClientActiveCallbackGeneration;
	private long _codeServiceClientCoordinatorCallbackGeneration;
	private readonly object _codeServiceLaunchSnapshotGate = new();
	private string _codeServiceLaunchWorkingDirectory = "";
	private bool _codeServiceLaunchDiagnosticLoggingRequested;
	private CodeServiceToolService _codeServiceLaunchToolService;

	private CodeServiceProcessRunner CodeServiceProcesses =>
		_codeServiceProcessRunner ??= new CodeServiceProcessRunner();

	private CodeServiceToolService CodeServiceTools =>
		_codeServiceToolService ??= new CodeServiceToolService(
			GetCodeServiceWorkingDirectory,
			(operation, details) => TryLogEditorOperation(operation, details),
			CodeServiceProcesses
		);

	private CodeServiceProcessLauncher CodeServiceLauncher =>
		_codeServiceProcessLauncher ??= new CodeServiceProcessLauncher();

	private bool ShouldShowCodeServiceInstallQuickAction()
	{
		try
		{
			return !CodeServiceTools.IsGlobalToolPresentForMenu();
		}
		catch (Exception exception)
		{
			TryLogEditorOperation(
				"CodeService Presence Check Failed",
				exception.ToString()
			);
			return true;
		}
	}

	private void StartCodeServiceSessionEnsureAtStartup()
	{
		StartCodeServiceSessionEnsure("Plugin Startup");
	}

	private void StartCodeServiceSessionEnsure(string reason)
	{
		try
		{
			RefreshCodeServiceLaunchSnapshot();

			if (
				!TryGetOrCreateCodeServiceClientCoordinator(
					reason,
					out CodeServiceClientCoordinator coordinator,
					out CodeServiceProcessIdentity ownerIdentity,
					out long callbackGeneration,
					out string coordinatorDetail
				)
			)
			{
				TryLogEditorOperation(
					"CodeService Session Unavailable",
					$"Reason='{reason}', Detail='{coordinatorDetail}'"
				);
				return;
			}

			CodeServiceClientLaunchHint launchHint = GetCodeServiceClientLaunchHint(
				ownerIdentity,
				reason
			);
			_codeServiceClientEnsureObservationTask = ObserveCodeServiceSessionEnsureAsync(
				coordinator,
				callbackGeneration,
				reason,
				launchHint
			);
		}
		catch (Exception exception)
		{
			TryLogEditorOperation(
				"CodeService Session Unavailable",
				$"Reason='{reason}', Detail='{exception}'"
			);
		}
	}

	private async Task ObserveCodeServiceSessionEnsureAsync(
		CodeServiceClientCoordinator coordinator,
		long callbackGeneration,
		string reason,
		CodeServiceClientLaunchHint launchHint
	)
	{
		try
		{
			await coordinator
				.EnsureReadyAsync(reason, launchHint)
				.ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			// Managed-generation retirement intentionally cancels only client-side work.
		}
		catch (Exception exception)
		{
			QueueCodeServiceClientDiagnostic(
				callbackGeneration,
				"CodeService Session Unavailable",
				$"Reason='{reason}', Detail='{exception}'"
			);
		}
	}

	private void StartCodeServiceInstallation()
	{
		if (_isInstallingCodeService)
			return;

		StartObservedEditorOperation(
			"Install C# Code Intelligence",
			InstallCodeServiceAsync
		);
	}

	private async Task InstallCodeServiceAsync(EditorOperationLease operation)
	{
		_isInstallingCodeService = true;
		try
		{
			CodeServiceInstallationResult result;
			try
			{
				result = await CodeServiceTools.InstallRequiredVersionAsync(operation);
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (Exception exception)
			{
				TryLogEditorOperation(
					"CodeService Installation Failed",
					exception.ToString()
				);
				result = new CodeServiceInstallationResult(
					false,
					"",
					"Failed to install C# Code Intelligence.\n\n"
					+ "An unexpected installation error occurred. See System Explorer diagnostics for technical details."
				);
			}

			operation.CancellationToken.ThrowIfCancellationRequested();
			if (!IsEditorOperationAccessValid(operation))
				return;

			if (result.Success)
			{
				CodeServiceClientEnsureResult sessionResult =
					await EnsureInstalledCodeServiceSessionAsync(operation);
				result = AddCodeServiceSessionStatusToInstallationResult(
					result,
					sessionResult
				);
			}

			if (!IsEditorOperationAccessValid(operation))
				return;

			ShowCodeServiceInstallationResult(result);
		}
		finally
		{
			_isInstallingCodeService = false;
		}
	}

	private async Task<CodeServiceClientEnsureResult> EnsureInstalledCodeServiceSessionAsync(
		EditorOperationLease operation
	)
	{
		try
		{
			RefreshCodeServiceLaunchSnapshot();

			if (
				!TryGetOrCreateCodeServiceClientCoordinator(
					"Post-Install",
					out CodeServiceClientCoordinator coordinator,
					out CodeServiceProcessIdentity ownerIdentity,
					out _,
					out string coordinatorDetail
				)
			)
			{
				return CodeServiceClientEnsureResult.Unavailable(coordinatorDetail);
			}

			CodeServiceClientLaunchHint launchHint = GetCodeServiceClientLaunchHint(
				ownerIdentity,
				"Post-Install"
			);
			return await coordinator
				.EnsureReadyAsync(
					"Post-Install",
					launchHint,
					operation.CancellationToken
				)
				.ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception exception)
		{
			TryLogEditorOperation(
				"CodeService Session Unavailable",
				$"Reason='Post-Install', Detail='{exception}'"
			);
			return CodeServiceClientEnsureResult.Unavailable(exception.Message);
		}
	}

	private static CodeServiceInstallationResult AddCodeServiceSessionStatusToInstallationResult(
		CodeServiceInstallationResult installationResult,
		CodeServiceClientEnsureResult sessionResult
	)
	{
		string message;
		if (sessionResult.IsReady)
		{
			message = installationResult.Message
				+ "\n\nThe CodeService is connected for this Godot editor session.";
		}
		else if (sessionResult.RequiresGodotRestart)
		{
			message = installationResult.Message
				+ "\n\nThe currently running CodeService session is incompatible with this plugin version. "
				+ "Restart Godot to use the required CodeService version.";
		}
		else
		{
			message = installationResult.Message
				+ "\n\nThe CodeService session could not be verified or connected for this Godot editor session. "
				+ "System Explorer can continue to load normally.";
		}

		return new CodeServiceInstallationResult(
			installationResult.Success,
			installationResult.InstalledVersion,
			message
		);
	}

	private bool TryGetOrCreateCodeServiceClientCoordinator(
		string reason,
		out CodeServiceClientCoordinator coordinator,
		out CodeServiceProcessIdentity ownerIdentity,
		out long callbackGeneration,
		out string detail
	)
	{
		coordinator = _codeServiceClientCoordinator;
		ownerIdentity = _codeServiceClientOwnerIdentity;
		callbackGeneration = _codeServiceClientCoordinatorCallbackGeneration;
		detail = "";

		if (
			coordinator != null
			&& _codeServiceClientOwnerIdentityAvailable
			&& IsCodeServiceClientCallbackGenerationCurrent(callbackGeneration)
		)
		{
			return true;
		}

		if (
			!CodeServiceProcessIdentity.TryGetCurrent(
				out ownerIdentity,
				out string ownerIdentityDetail
			)
		)
		{
			detail = "Current Godot process identity could not be captured: " + ownerIdentityDetail;
			return false;
		}

		callbackGeneration = OpenCodeServiceClientCallbackAdmission();
		try
		{
			long compositionGeneration = callbackGeneration;
			coordinator = new CodeServiceClientCoordinator(
				ownerIdentity,
				CodeServiceToolService.RequiredVersion,
				CodeServiceLauncher,
				() => PrepareCodeServiceLaunch(compositionGeneration),
				(operation, details) =>
					QueueCodeServiceClientDiagnostic(compositionGeneration, operation, details),
				(sessionInfo, readyReason) =>
					OnCodeServiceClientSessionReady(
						compositionGeneration,
						sessionInfo,
						readyReason
					),
				(sessionInfo, projectRoot, reusedExistingWorkspace, workspaceReason) =>
					OnCodeServiceClientWorkspaceReady(
						compositionGeneration,
						sessionInfo,
						projectRoot,
						reusedExistingWorkspace,
						workspaceReason
					)
			);
			_codeServiceClientCoordinator = coordinator;
			_codeServiceClientOwnerIdentity = ownerIdentity;
			_codeServiceClientOwnerIdentityAvailable = true;
			_codeServiceClientCoordinatorCallbackGeneration = compositionGeneration;
			return true;
		}
		catch (Exception exception)
		{
			CloseCodeServiceClientCallbackAdmission(callbackGeneration);
			callbackGeneration = 0;
			detail =
				$"CodeService client coordinator could not be created for '{reason}': {exception.Message}";
			return false;
		}
	}

	private long OpenCodeServiceClientCallbackAdmission()
	{
		lock (_codeServiceClientCallbackGate)
		{
			long generation = Interlocked.Increment(
				ref _codeServiceClientCallbackGenerationCounter
			);
			if (generation == 0)
			{
				generation = Interlocked.Increment(
					ref _codeServiceClientCallbackGenerationCounter
				);
			}

			Volatile.Write(ref _codeServiceClientActiveCallbackGeneration, generation);
			return generation;
		}
	}

	private void CloseCodeServiceClientCallbackAdmission(long generation)
	{
		if (generation == 0)
			return;

		lock (_codeServiceClientCallbackGate)
		{
			if (Volatile.Read(ref _codeServiceClientActiveCallbackGeneration) == generation)
				Volatile.Write(ref _codeServiceClientActiveCallbackGeneration, 0);
		}
	}

	private bool IsCodeServiceClientCallbackGenerationCurrent(long generation)
	{
		return generation != 0
			&& Volatile.Read(ref _codeServiceClientActiveCallbackGeneration) == generation;
	}

	private void RefreshCodeServiceLaunchSnapshot()
	{
		string workingDirectory = GetCodeServiceWorkingDirectory();
		bool diagnosticLoggingRequested = DebugState;
		CodeServiceToolService toolService = CodeServiceTools;
		lock (_codeServiceLaunchSnapshotGate)
		{
			_codeServiceLaunchWorkingDirectory = workingDirectory;
			_codeServiceLaunchDiagnosticLoggingRequested = diagnosticLoggingRequested;
			_codeServiceLaunchToolService = toolService;
		}
	}

	private CodeServiceClientLaunchPreparation PrepareCodeServiceLaunch(long callbackGeneration)
	{
		lock (_codeServiceClientCallbackGate)
		{
			if (!IsCodeServiceClientCallbackGenerationCurrent(callbackGeneration))
			{
				return CodeServiceClientLaunchPreparation.Unavailable(
					"The CodeService launch callback belongs to a retired managed generation."
				);
			}

			// This delegate is invoked by the Godot-independent coordinator and may run on a
			// BCL continuation thread. It therefore uses only the last main-thread composition
			// snapshot plus filesystem/environment executable resolution.
			string workingDirectory;
			bool diagnosticLoggingRequested;
			CodeServiceToolService toolService;
			lock (_codeServiceLaunchSnapshotGate)
			{
				workingDirectory = _codeServiceLaunchWorkingDirectory;
				diagnosticLoggingRequested = _codeServiceLaunchDiagnosticLoggingRequested;
				toolService = _codeServiceLaunchToolService;
			}

			if (toolService == null)
			{
				return CodeServiceClientLaunchPreparation.Unavailable(
					"The CodeService launch composition snapshot is no longer available."
				);
			}

			if (!toolService.TryResolveLaunchExecutable(out string executable))
			{
				return CodeServiceClientLaunchPreparation.Unavailable(
					"The CodeService executable could not be resolved from the global tool shim or PATH."
				);
			}

			if (string.IsNullOrWhiteSpace(workingDirectory))
				workingDirectory = System.Environment.CurrentDirectory;

			return CodeServiceClientLaunchPreparation.Success(
				executable,
				workingDirectory,
				diagnosticLoggingRequested
			);
		}
	}

	private void QueueCodeServiceClientDiagnostic(
		long callbackGeneration,
		string operation,
		string detail
	)
	{
		lock (_codeServiceClientCallbackGate)
		{
			if (!IsCodeServiceClientCallbackGenerationCurrent(callbackGeneration))
				return;

			try
			{
				CallDeferred(
					nameof(LogCodeServiceClientDiagnosticDeferred),
					callbackGeneration,
					operation ?? "CodeService Client",
					detail ?? ""
				);
			}
			catch
			{
				// Diagnostics are best-effort and must never fault a client continuation.
			}
		}
	}

	private void LogCodeServiceClientDiagnosticDeferred(
		long callbackGeneration,
		string operation,
		string detail
	)
	{
		if (!IsCodeServiceClientCallbackGenerationCurrent(callbackGeneration))
			return;

		TryLogEditorOperation(operation, detail);
	}

	private CodeServiceClientLaunchHint GetCodeServiceClientLaunchHint(
		CodeServiceProcessIdentity currentGodotOwnerIdentity,
		string source
	)
	{
		if (!TryGetCodeServiceLaunchMarkerHost(out Control markerHost, out string markerHostDetail))
		{
			TryLogEditorOperation(
				"CodeService Launch Marker Unavailable",
				$"Source='{source}', Detail='{markerHostDetail}'"
			);
			return default;
		}

		if (
			!TryValidateCodeServiceLaunchMarker(
				markerHost,
				currentGodotOwnerIdentity,
				source,
				out bool exactLiveHint,
				out CodeServiceProcessIdentity serviceIdentity,
				out string validationDetail
			)
		)
		{
			TryLogEditorOperation(
				"CodeService Launch Marker Validation Failed",
				$"Source='{source}', Detail='{validationDetail}'"
			);
			return CodeServiceClientLaunchHint.Blocked(validationDetail);
		}

		if (!exactLiveHint)
			return default;

		TryLogEditorOperation(
			"CodeService Launch Marker Live Hint",
			$"Source='{source}', ServicePid='{serviceIdentity.ProcessId}', ServiceStartTimeUtcTicks='{serviceIdentity.StartTimeUtcTicks}'"
		);
		return CodeServiceClientLaunchHint.VerifiedLive(serviceIdentity);
	}

	private void OnCodeServiceClientSessionReady(
		long callbackGeneration,
		CodeServiceClientSessionInfo sessionInfo,
		string reason
	)
	{
		lock (_codeServiceClientCallbackGate)
		{
			if (!IsCodeServiceClientCallbackGenerationCurrent(callbackGeneration))
				return;

			try
			{
				// Coordinator callbacks may arrive on a BCL continuation thread. Marshal the
				// Godot-facing Ready work to the editor thread using scalar-only payload. The
				// callback generation is checked again before marker or workspace work.
				CallDeferred(
					nameof(ApplyCodeServiceReadyMarkerDeferred),
					callbackGeneration,
					sessionInfo.SessionId,
					sessionInfo.GodotOwnerIdentity.ProcessId,
					sessionInfo.GodotOwnerIdentity.StartTimeUtcTicks,
					sessionInfo.ServiceProcessIdentity.ProcessId,
					sessionInfo.ServiceProcessIdentity.StartTimeUtcTicks,
					reason ?? "CodeService Ready"
				);
			}
			catch
			{
				// A retiring/disposed Godot object is contained here. Coordinator diagnostics are
				// also generation-gated and must not become a second stale callback path.
			}
		}
	}


	private void OnCodeServiceClientWorkspaceReady(
		long callbackGeneration,
		CodeServiceClientSessionInfo sessionInfo,
		string projectRoot,
		bool? reusedExistingWorkspace,
		string reason
	)
	{
		lock (_codeServiceClientCallbackGate)
		{
			if (!IsCodeServiceClientCallbackGenerationCurrent(callbackGeneration))
				return;

			try
			{
				CallDeferred(
					nameof(ApplyCodeServiceWorkspaceReadyDeferred),
					callbackGeneration,
					sessionInfo.SessionId,
					sessionInfo.GodotOwnerIdentity.ProcessId,
					sessionInfo.GodotOwnerIdentity.StartTimeUtcTicks,
					sessionInfo.ServiceProcessIdentity.ProcessId,
					sessionInfo.ServiceProcessIdentity.StartTimeUtcTicks,
					projectRoot ?? "",
					reusedExistingWorkspace.HasValue ? (reusedExistingWorkspace.Value ? 1 : 0) : -1,
					reason ?? "CodeService Workspace Ready"
				);
			}
			catch
			{
			}
		}
	}

	private void ApplyCodeServiceWorkspaceReadyDeferred(
		long callbackGeneration,
		string sessionId,
		int ownerProcessId,
		long ownerStartTimeUtcTicks,
		int serviceProcessId,
		long serviceStartTimeUtcTicks,
		string projectRoot,
		int reusedState,
		string reason
	)
	{
		if (!IsCodeServiceClientCallbackGenerationCurrent(callbackGeneration))
			return;

		CodeServiceClientCoordinator coordinator = _codeServiceClientCoordinator;
		if (
			coordinator == null
			|| _codeServiceClientCoordinatorCallbackGeneration != callbackGeneration
			|| !coordinator.TryGetReadySessionInfo(out CodeServiceClientSessionInfo currentInfo)
			|| !string.Equals(currentInfo.SessionId, sessionId, StringComparison.Ordinal)
			|| currentInfo.GodotOwnerIdentity.ProcessId != ownerProcessId
			|| currentInfo.GodotOwnerIdentity.StartTimeUtcTicks != ownerStartTimeUtcTicks
			|| currentInfo.ServiceProcessIdentity.ProcessId != serviceProcessId
			|| currentInfo.ServiceProcessIdentity.StartTimeUtcTicks != serviceStartTimeUtcTicks
		)
		{
			return;
		}

		if (!CodeServiceWorkspacePath.TryNormalize(projectRoot, out string normalizedProjectRoot, out _))
			return;

		string currentProjectRoot;
		try
		{
			currentProjectRoot = GetCodeServiceProjectRoot();
		}
		catch
		{
			return;
		}

		if (!CodeServiceWorkspacePath.TryNormalize(currentProjectRoot, out string normalizedCurrentProjectRoot, out _)
			|| !CodeServiceWorkspacePath.EqualsNormalized(normalizedProjectRoot, normalizedCurrentProjectRoot))
		{
			return;
		}

		ApplyCodeServiceDocumentWorkspaceReadyIntent(
			currentInfo,
			normalizedProjectRoot,
			reusedState,
			reason
		);
	}

	private void ApplyCodeServiceReadyMarkerDeferred(
		long callbackGeneration,
		string sessionId,
		int ownerProcessId,
		long ownerStartTimeUtcTicks,
		int serviceProcessId,
		long serviceStartTimeUtcTicks,
		string reason
	)
	{
		if (!IsCodeServiceClientCallbackGenerationCurrent(callbackGeneration))
			return;

		CodeServiceClientCoordinator coordinator = _codeServiceClientCoordinator;
		if (
			coordinator == null
			|| _codeServiceClientCoordinatorCallbackGeneration != callbackGeneration
			|| !coordinator.TryGetReadySessionInfo(out CodeServiceClientSessionInfo currentInfo)
			|| !string.Equals(currentInfo.SessionId, sessionId, StringComparison.Ordinal)
			|| currentInfo.GodotOwnerIdentity.ProcessId != ownerProcessId
			|| currentInfo.GodotOwnerIdentity.StartTimeUtcTicks != ownerStartTimeUtcTicks
			|| currentInfo.ServiceProcessIdentity.ProcessId != serviceProcessId
			|| currentInfo.ServiceProcessIdentity.StartTimeUtcTicks != serviceStartTimeUtcTicks
		)
		{
			return;
		}

		if (!TryGetCodeServiceLaunchMarkerHost(out Control markerHost, out string markerHostDetail))
		{
			TryLogEditorOperation(
				"CodeService Launch Marker Write Failed",
				$"Reason='{reason}', Detail='{markerHostDetail}'"
			);
		}
		else if (
			!TryWriteCodeServiceLaunchMarker(
				markerHost,
				currentInfo.GodotOwnerIdentity,
				currentInfo.ServiceProcessIdentity,
				out string markerWriteDetail
			)
		)
		{
			TryLogEditorOperation(
				"CodeService Launch Marker Write Failed",
				$"Reason='{reason}', SessionId='{currentInfo.SessionId}', ServicePid='{currentInfo.ServiceProcessIdentity.ProcessId}', ServiceStartTimeUtcTicks='{currentInfo.ServiceProcessIdentity.StartTimeUtcTicks}', Detail='{markerWriteDetail}'"
			);
		}

		string projectRoot;
		try
		{
			projectRoot = GetCodeServiceProjectRoot();
		}
		catch (Exception exception)
		{
			TryLogEditorOperation(
				"CodeService Workspace Protocol Failure",
				$"Reason='{reason}', Outcome='InvalidRequest', SessionId='{currentInfo.SessionId}', ServicePid='{currentInfo.ServiceProcessIdentity.ProcessId}', Detail='Godot project root capture failed: {exception.Message}'"
			);
			return;
		}

		// The coordinator owns and retires the actual workspace flight. Keep only a
		// non-authoritative observation reference in the plugin generation; do not add
		// a plugin-side async continuation/callback around the workspace lifetime.
		_codeServiceWorkspaceEnsureObservationTask = coordinator.EnsureWorkspaceAsync(
			currentInfo,
			projectRoot,
			reason
		);
	}

	private Task<CodeServiceClientEnsureResult> ReportCodeServiceSessionFailureAndEnsureReadyAsync(
		CodeServiceClientSession failedSession,
		string reason,
		CancellationToken cancellationToken = default
	)
	{
		CodeServiceClientCoordinator coordinator = _codeServiceClientCoordinator;
		if (coordinator == null)
		{
			return Task.FromResult(
				CodeServiceClientEnsureResult.Unavailable(
					"The CodeService client coordinator is not available in this managed generation."
				)
			);
		}

		return coordinator.ReportSessionFailureAndEnsureReadyAsync(
			failedSession,
			reason,
			cancellationToken
		);
	}

	private static bool TryGetCodeServiceLaunchMarkerHost(
		out Control markerHost,
		out string detail
	)
	{
		markerHost = null;
		detail = "";

		try
		{
			Control baseControl = EditorInterface.Singleton.GetBaseControl();
			if (baseControl == null || !GodotObject.IsInstanceValid(baseControl))
			{
				detail = "EditorInterface base control is unavailable.";
				return false;
			}

			markerHost = baseControl;
			return true;
		}
		catch (Exception exception)
		{
			detail = exception.Message;
			return false;
		}
	}

	private bool TryValidateCodeServiceLaunchMarker(
		Control markerHost,
		CodeServiceProcessIdentity currentGodotOwnerIdentity,
		string source,
		out bool exactLiveHint,
		out CodeServiceProcessIdentity serviceIdentity,
		out string failureDetail
	)
	{
		exactLiveHint = false;
		serviceIdentity = default;
		failureDetail = "";

		Variant rawMarker;
		try
		{
			if (!markerHost.HasMeta(CodeServiceLaunchMarkerMetadataKey))
				return true;

			rawMarker = markerHost.GetMeta(CodeServiceLaunchMarkerMetadataKey);
		}
		catch (Exception exception)
		{
			failureDetail = exception.Message;
			return false;
		}

		if (
			rawMarker.VariantType != Variant.Type.String
			|| !TryParseCodeServiceLaunchMarker(
				rawMarker.AsString(),
				out CodeServiceProcessIdentity markerOwnerIdentity,
				out CodeServiceProcessIdentity markerServiceIdentity
			)
		)
		{
			LogAndClearStaleCodeServiceLaunchMarker(markerHost, source, "Marker was malformed.");
			return true;
		}

		if (!ProcessIdentitiesMatch(markerOwnerIdentity, currentGodotOwnerIdentity))
		{
			LogAndClearStaleCodeServiceLaunchMarker(
				markerHost,
				source,
				$"Owner identity mismatch. MarkerOwnerPid='{markerOwnerIdentity.ProcessId}', MarkerOwnerStartTimeUtcTicks='{markerOwnerIdentity.StartTimeUtcTicks}', CurrentOwnerPid='{currentGodotOwnerIdentity.ProcessId}', CurrentOwnerStartTimeUtcTicks='{currentGodotOwnerIdentity.StartTimeUtcTicks}'."
			);
			return true;
		}

		if (markerServiceIdentity.ProcessId == currentGodotOwnerIdentity.ProcessId)
		{
			LogAndClearStaleCodeServiceLaunchMarker(
				markerHost,
				source,
				"Marker service identity pointed at the Godot owner process."
			);
			return true;
		}

		if (
			!CodeServiceProcessIdentity.TryGetByProcessId(
				markerServiceIdentity.ProcessId,
				out CodeServiceProcessIdentity observedServiceIdentity,
				out bool serviceDefinitelyUnavailable,
				out string processObservationDetail
			)
		)
		{
			if (serviceDefinitelyUnavailable)
			{
				LogAndClearStaleCodeServiceLaunchMarker(
					markerHost,
					source,
					$"Marked service process is unavailable. ServicePid='{markerServiceIdentity.ProcessId}', Detail='{processObservationDetail}'."
				);
				return true;
			}

			failureDetail =
				$"Marked service process identity could not be verified safely. ServicePid='{markerServiceIdentity.ProcessId}', Detail='{processObservationDetail}'.";
			return false;
		}

		if (!ProcessIdentitiesMatch(markerServiceIdentity, observedServiceIdentity))
		{
			LogAndClearStaleCodeServiceLaunchMarker(
				markerHost,
				source,
				$"Marked service process identity is stale or PID-reused. MarkerServicePid='{markerServiceIdentity.ProcessId}', MarkerServiceStartTimeUtcTicks='{markerServiceIdentity.StartTimeUtcTicks}', ObservedServicePid='{observedServiceIdentity.ProcessId}', ObservedServiceStartTimeUtcTicks='{observedServiceIdentity.StartTimeUtcTicks}'."
			);
			return true;
		}

		exactLiveHint = true;
		serviceIdentity = observedServiceIdentity;
		return true;
	}

	private void LogAndClearStaleCodeServiceLaunchMarker(
		Control markerHost,
		string source,
		string reason
	)
	{
		TryLogEditorOperation(
			"CodeService Launch Marker Stale",
			$"Source='{source}', Reason='{reason}'"
		);

		try
		{
			markerHost.RemoveMeta(CodeServiceLaunchMarkerMetadataKey);
		}
		catch (Exception exception)
		{
			TryLogEditorOperation(
				"CodeService Launch Marker Clear Failed",
				$"Source='{source}', Detail='{exception}'"
			);
		}
	}

	private static bool TryWriteCodeServiceLaunchMarker(
		Control markerHost,
		CodeServiceProcessIdentity godotOwnerIdentity,
		CodeServiceProcessIdentity serviceIdentity,
		out string detail
	)
	{
		detail = "";
		try
		{
			markerHost.SetMeta(
				CodeServiceLaunchMarkerMetadataKey,
				SerializeCodeServiceLaunchMarker(godotOwnerIdentity, serviceIdentity)
			);
			return true;
		}
		catch (Exception exception)
		{
			detail = exception.Message;
			return false;
		}
	}

	private static string SerializeCodeServiceLaunchMarker(
		CodeServiceProcessIdentity godotOwnerIdentity,
		CodeServiceProcessIdentity serviceIdentity
	)
	{
		return string.Join(
			"|",
			CodeServiceLaunchMarkerVersion,
			godotOwnerIdentity.ProcessId.ToString(CultureInfo.InvariantCulture),
			godotOwnerIdentity.StartTimeUtcTicks.ToString(CultureInfo.InvariantCulture),
			serviceIdentity.ProcessId.ToString(CultureInfo.InvariantCulture),
			serviceIdentity.StartTimeUtcTicks.ToString(CultureInfo.InvariantCulture)
		);
	}

	private static bool TryParseCodeServiceLaunchMarker(
		string marker,
		out CodeServiceProcessIdentity godotOwnerIdentity,
		out CodeServiceProcessIdentity serviceIdentity
	)
	{
		godotOwnerIdentity = default;
		serviceIdentity = default;

		if (string.IsNullOrWhiteSpace(marker))
			return false;

		string[] parts = marker.Split('|');
		if (
			parts.Length != 5
			|| !string.Equals(parts[0], CodeServiceLaunchMarkerVersion, StringComparison.Ordinal)
			|| !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int ownerProcessId)
			|| ownerProcessId <= 0
			|| !long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out long ownerStartTimeUtcTicks)
			|| ownerStartTimeUtcTicks <= 0
			|| !int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int serviceProcessId)
			|| serviceProcessId <= 0
			|| !long.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out long serviceStartTimeUtcTicks)
			|| serviceStartTimeUtcTicks <= 0
		)
		{
			return false;
		}

		godotOwnerIdentity = new CodeServiceProcessIdentity(ownerProcessId, ownerStartTimeUtcTicks);
		serviceIdentity = new CodeServiceProcessIdentity(serviceProcessId, serviceStartTimeUtcTicks);
		return true;
	}

	private static bool ProcessIdentitiesMatch(
		CodeServiceProcessIdentity left,
		CodeServiceProcessIdentity right
	)
	{
		return left.ProcessId == right.ProcessId
			&& left.StartTimeUtcTicks == right.StartTimeUtcTicks;
	}

	private void ShowCodeServiceInstallationResult(CodeServiceInstallationResult result)
	{
		if (
			_codeServiceInstallResultDialog == null
			|| !GodotObject.IsInstanceValid(_codeServiceInstallResultDialog)
		)
		{
			TryLogEditorOperation(
				"CodeService Installation Result Dialog Unavailable",
				$"Success='{result.Success}', InstalledVersion='{result.InstalledVersion}', Message='{result.Message}'"
			);
			return;
		}

		_codeServiceInstallResultDialog.Title = result.Success
			? "C# Code Intelligence Installed"
			: "C# Code Intelligence Installation Failed";
		_codeServiceInstallResultDialog.DialogText = result.Message;
		_codeServiceInstallResultDialog.PopupCentered();
	}

	private void ResetCodeServiceManagedStateForOperationLifecycleShutdown(
		string reason = "Operation Lifecycle Shutdown"
	)
	{
		_isInstallingCodeService = false;

		CodeServiceClientCoordinator coordinator = _codeServiceClientCoordinator;
		long callbackGeneration = _codeServiceClientCoordinatorCallbackGeneration;

		// Close the plugin-side managed callback boundary before touching coordinator
		// composition. Any BCL continuation from this generation must fail admission
		// before it can CallDeferred into Godot.
		CloseCodeServiceClientCallbackAdmission(callbackGeneration);

		Task retirementTask = null;
		Exception retirementStartException = null;
		if (coordinator != null)
		{
			try
			{
				// RetireAsync performs its synchronous admission/cancellation phase before
				// returning the asynchronously observed cleanup task. It never stops service.
				retirementTask = coordinator.RetireAsync(reason);
			}
			catch (Exception exception)
			{
				retirementStartException = exception;
			}
		}

		_codeServiceClientCoordinator = null;
		_codeServiceClientOwnerIdentity = default;
		_codeServiceClientOwnerIdentityAvailable = false;
		_codeServiceClientCoordinatorCallbackGeneration = 0;
		_codeServiceClientEnsureObservationTask = null;
		_codeServiceWorkspaceEnsureObservationTask = null;

		if (retirementTask != null)
		{
			_codeServiceClientRetirementObservationTask =
				ObserveCodeServiceClientRetirementAsync(retirementTask);
		}

		if (retirementStartException != null)
		{
			TryLogEditorOperation(
				"CodeService Client Retirement Failed",
				$"Reason='{reason}', Detail='{retirementStartException}'"
			);
		}

		lock (_codeServiceLaunchSnapshotGate)
		{
			_codeServiceLaunchWorkingDirectory = "";
			_codeServiceLaunchDiagnosticLoggingRequested = false;
			_codeServiceLaunchToolService = null;
		}

		_codeServiceToolService = null;
		_codeServiceProcessRunner = null;
		_codeServiceProcessLauncher = null;
	}

	private static async Task ObserveCodeServiceClientRetirementAsync(Task retirementTask)
	{
		try
		{
			await retirementTask.ConfigureAwait(false);
		}
		catch
		{
			// Callback admission is already closed. Observe/contain retirement failure
			// without queueing diagnostics into a retired Godot/plugin generation.
		}
	}

	private static string GetCodeServiceWorkingDirectory()
	{
		string path = ProjectSettings.GlobalizePath("res://");
		return string.IsNullOrWhiteSpace(path) ? System.Environment.CurrentDirectory : path;
	}

	private static string GetCodeServiceProjectRoot()
	{
		return ProjectSettings.GlobalizePath("res://");
	}
	#endregion
}
#endif
