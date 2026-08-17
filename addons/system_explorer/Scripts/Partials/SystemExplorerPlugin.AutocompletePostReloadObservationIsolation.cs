#if TOOLS
using System;
using SystemExplorer.Autocomplete;
using SystemExplorer.EditorIntegration.ScriptEditing;

public partial class SystemExplorerPlugin
{
	#region Autocomplete Post-Reload CodeEdit Candidate Observation Isolation

	// Diagnostic A/B policy only. The permanent architecture remains exact native
	// candidate stabilization followed by BindingEpoch-owned CodeEdit activation.
	private const bool AutocompletePostReloadCodeEditCandidateObservationEnabled = false;

	private AutocompletePostReloadObservationIsolationCoordinator
		_autocompletePostReloadObservationIsolationCoordinator;

	private AutocompletePostReloadObservationIsolationCoordinator
		AutocompletePostReloadObservationIsolationCoordinator
	{
		get
		{
			if (
				_autocompletePostReloadObservationIsolationCoordinator == null
				|| !string.Equals(
					_autocompletePostReloadObservationIsolationCoordinator.ManagedAssemblyGeneration,
					ManagedAssemblyGeneration,
					StringComparison.Ordinal
				)
			)
			{
				_autocompletePostReloadObservationIsolationCoordinator =
					new AutocompletePostReloadObservationIsolationCoordinator(
						ManagedAssemblyGeneration
					);
			}

			return _autocompletePostReloadObservationIsolationCoordinator;
		}
	}

	private void InvalidateAutocompletePostReloadObservationIsolationAuthority()
	{
		_autocompletePostReloadObservationIsolationCoordinator?.Invalidate();
	}

	private bool HasPendingAutocompletePostReloadObservationIsolationProcessWork()
	{
		return _autocompletePostReloadObservationIsolationCoordinator?.HasPendingProcessWork
			== true;
	}

	private static bool IsAutocompleteReloadNonReadyState(
		AutocompleteReloadStabilizationState state
	)
	{
		return state != AutocompleteReloadStabilizationState.Ready;
	}

	private bool ShouldSuppressAutocompleteReloadCodeEditCandidateObservation(
		AutocompleteReloadStabilizationSnapshot reload
	)
	{
		return !AutocompletePostReloadCodeEditCandidateObservationEnabled
			&& IsAutocompleteReloadNonReadyState(reload.State);
	}

	private bool ShouldSuppressAutocompleteScriptTransitionCodeEditCandidateObservation(
		AutocompleteReloadStabilizationSnapshot reload
	)
	{
		return !AutocompletePostReloadCodeEditCandidateObservationEnabled
			&& reload.State == AutocompleteReloadStabilizationState.Ready
			&& reload.ReloadReadyEpoch > 1;
	}

	private bool TryGetAutocompletePostReloadObservationIsolationContext(
		ScriptEditorLifecycleSnapshot lifecycle,
		AutocompleteReloadStabilizationSnapshot reload,
		out AutocompletePostReloadObservationIsolationKind kind,
		out string authoritativeTargetPath
	)
	{
		kind = AutocompletePostReloadObservationIsolationKind.None;
		authoritativeTargetPath = "";

		if (
			AutocompletePostReloadCodeEditCandidateObservationEnabled
			|| lifecycle.State != ScriptEditorLifecycleState.BindingPending
			|| lifecycle.ScriptTransitionId <= 0
			|| !string.Equals(
				lifecycle.ManagedAssemblyGeneration,
				ManagedAssemblyGeneration,
				StringComparison.Ordinal
			)
		)
		{
			return false;
		}

		authoritativeTargetPath =
			GetAuthoritativeAutocompleteScriptTransitionTargetPath(lifecycle);
		if (
			string.IsNullOrWhiteSpace(authoritativeTargetPath)
			|| !authoritativeTargetPath.EndsWith(
				".cs",
				StringComparison.OrdinalIgnoreCase
			)
		)
		{
			authoritativeTargetPath = "";
			return false;
		}

		if (IsAutocompleteReloadNonReadyState(reload.State))
		{
			kind = AutocompletePostReloadObservationIsolationKind.Reload;
			return true;
		}

		if (
			reload.State == AutocompleteReloadStabilizationState.Ready
			&& reload.ReloadReadyEpoch > 1
		)
		{
			kind = AutocompletePostReloadObservationIsolationKind.ScriptTransition;
			return true;
		}

		authoritativeTargetPath = "";
		return false;
	}

	private bool TryHandleAutocompletePostReloadObservationIsolationAdmission(
		AutocompletePluginHost host,
		long scheduledHostInstanceToken,
		long targetTransitionId
	)
	{
		ScriptEditorLifecycleSnapshot lifecycle = ScriptEditorLifecycleCoordinator.Snapshot;
		AutocompleteReloadStabilizationSnapshot reload =
			AutocompleteReloadStabilizationCoordinator.Snapshot;
		if (
			!TryGetAutocompletePostReloadObservationIsolationContext(
				lifecycle,
				reload,
				out AutocompletePostReloadObservationIsolationKind kind,
				out string authoritativeTargetPath
			)
		)
		{
			return false;
		}

		// Once this diagnostic branch applies, it owns the rebind fail-closed. It must
		// never fall through to either native candidate observation or CodeEdit binding.
		AutocompletePostReloadObservationIsolationCoordinator coordinator =
			AutocompletePostReloadObservationIsolationCoordinator;
		if (
			scheduledHostInstanceToken <= 0
			|| scheduledHostInstanceToken != _autocompleteHostInstanceToken
			|| targetTransitionId != lifecycle.ScriptTransitionId
			|| !coordinator.ArmForTransition(
				scheduledHostInstanceToken,
				targetTransitionId,
				authoritativeTargetPath,
				kind
			)
		)
		{
			coordinator.Invalidate();
			return true;
		}

		if (
			!coordinator.TryGetActivationAuthority(
				ManagedAssemblyGeneration,
				scheduledHostInstanceToken,
				targetTransitionId,
				authoritativeTargetPath,
				kind
			)
		)
		{
			return true;
		}

		try
		{
			TryCompleteAutocompletePostReloadObservationIsolation(
				host,
				scheduledHostInstanceToken,
				targetTransitionId,
				authoritativeTargetPath,
				kind
			);
		}
		catch (Exception exception)
		{
			RestartAutocompletePostReloadObservationIsolation(
				scheduledHostInstanceToken,
				targetTransitionId,
				authoritativeTargetPath,
				kind
			);
			DebugLogger.LogOperation(
				"C# autocomplete post-reload observation isolation completion failed",
				exception.ToString()
			);
		}

		return true;
	}

	private bool TryCompleteAutocompletePostReloadObservationIsolation(
		AutocompletePluginHost host,
		long scheduledHostInstanceToken,
		long targetTransitionId,
		string capturedAuthoritativeTargetPath,
		AutocompletePostReloadObservationIsolationKind kind
	)
	{
		AutocompletePostReloadObservationIsolationCoordinator coordinator =
			AutocompletePostReloadObservationIsolationCoordinator;
		ScriptEditorLifecycleSnapshot lifecycle = ScriptEditorLifecycleCoordinator.Snapshot;
		AutocompleteReloadStabilizationSnapshot reload =
			AutocompleteReloadStabilizationCoordinator.Snapshot;
		string authoritativeTargetPath =
			GetAuthoritativeAutocompleteScriptTransitionTargetPath(lifecycle);

		bool reloadKindCurrent = kind switch
		{
			AutocompletePostReloadObservationIsolationKind.Reload =>
				IsAutocompleteReloadNonReadyState(reload.State),
			AutocompletePostReloadObservationIsolationKind.ScriptTransition =>
				reload.State == AutocompleteReloadStabilizationState.Ready
				&& reload.ReloadReadyEpoch > 1,
			_ => false,
		};

		if (
			!reloadKindCurrent
			|| !string.Equals(
				lifecycle.ManagedAssemblyGeneration,
				ManagedAssemblyGeneration,
				StringComparison.Ordinal
			)
			|| lifecycle.State != ScriptEditorLifecycleState.BindingPending
			|| lifecycle.ScriptTransitionId != targetTransitionId
			|| scheduledHostInstanceToken <= 0
			|| scheduledHostInstanceToken != _autocompleteHostInstanceToken
			|| host != _autocompleteHost
			|| !string.Equals(
				_autocompleteHostManagedAssemblyGeneration,
				ManagedAssemblyGeneration,
				StringComparison.Ordinal
			)
			|| !string.Equals(
				authoritativeTargetPath,
				capturedAuthoritativeTargetPath,
				StringComparison.OrdinalIgnoreCase
			)
			|| !coordinator.TryGetActivationAuthority(
				ManagedAssemblyGeneration,
				scheduledHostInstanceToken,
				targetTransitionId,
				capturedAuthoritativeTargetPath,
				kind
			)
		)
		{
			RestartAutocompletePostReloadObservationIsolation(
				scheduledHostInstanceToken,
				targetTransitionId,
				capturedAuthoritativeTargetPath,
				kind
			);
			return false;
		}

		host.HandleScriptChangedWithoutCodeEditBinding();

		if (
			!ScriptEditorLifecycleCoordinator.TryCompleteWithoutBinding(
				ManagedAssemblyGeneration,
				targetTransitionId,
				capturedAuthoritativeTargetPath
			)
		)
		{
			RestartAutocompletePostReloadObservationIsolation(
				scheduledHostInstanceToken,
				targetTransitionId,
				capturedAuthoritativeTargetPath,
				kind
			);
			return false;
		}

		lifecycle = ScriptEditorLifecycleCoordinator.Snapshot;
		if (
			!DoesNoBindingStableLifecycleProvePureManagedTarget(
				lifecycle,
				targetTransitionId,
				capturedAuthoritativeTargetPath
			)
		)
		{
			RestartAutocompletePostReloadObservationIsolation(
				scheduledHostInstanceToken,
				targetTransitionId,
				capturedAuthoritativeTargetPath,
				kind
			);
			return false;
		}

		if (
			!coordinator.CompleteActivation(
				ManagedAssemblyGeneration,
				scheduledHostInstanceToken,
				targetTransitionId,
				capturedAuthoritativeTargetPath,
				kind
			)
		)
		{
			RestartAutocompletePostReloadObservationIsolation(
				scheduledHostInstanceToken,
				targetTransitionId,
				capturedAuthoritativeTargetPath,
				kind
			);
			return false;
		}

		if (kind == AutocompletePostReloadObservationIsolationKind.Reload)
		{
			AutocompleteReloadStabilizationCoordinator reloadCoordinator =
				AutocompleteReloadStabilizationCoordinator;
			if (
				!reloadCoordinator.TryCompleteDiagnosticIsolationWithoutCandidate(
					ManagedAssemblyGeneration,
					out long reloadReadyEpoch
				)
				|| reloadReadyEpoch <= 1
			)
			{
				RestartAutocompletePostReloadObservationIsolation(
					scheduledHostInstanceToken,
					targetTransitionId,
					capturedAuthoritativeTargetPath,
					kind
				);
				return false;
			}

			AutocompleteReloadStabilizationSnapshot completedReload =
				reloadCoordinator.Snapshot;
			DebugLogger.LogPersistentFileOnlyOperation(
				"Autocomplete reload stabilization completed",
				$"ManagedAssemblyGeneration='{completedReload.ManagedAssemblyGeneration}', "
					+ $"State='{completedReload.State}', "
					+ $"StabilizationToken='{completedReload.StabilizationToken}', "
					+ $"ReloadReadyEpoch='{completedReload.ReloadReadyEpoch}', "
					+ "PostReloadCodeEditCandidateObservation='DisabledDiagnosticIsolation', "
					+ "PostReloadCodeEditBindingActivation='DisabledDiagnosticIsolation', "
					+ "PostReloadStabilizationAuthority='PureManagedLifecycleTarget', "
					+ "SubsequentCSharpTransitionStabilization='TwoProcessTurns', "
					+ "ScriptEditorTracking='Retained', ScriptEditorSync='OutsideIsolation'"
			);
		}

		return true;
	}

	private void RestartAutocompletePostReloadObservationIsolation(
		long scheduledHostInstanceToken,
		long targetTransitionId,
		string capturedAuthoritativeTargetPath,
		AutocompletePostReloadObservationIsolationKind kind
	)
	{
		ScriptEditorLifecycleSnapshot lifecycle = ScriptEditorLifecycleCoordinator.Snapshot;
		if (
			lifecycle.State == ScriptEditorLifecycleState.Stable
			&& lifecycle.ScriptTransitionId == targetTransitionId
		)
		{
			ScriptEditorLifecycleCoordinator.MarkBindingPending(targetTransitionId);
			lifecycle = ScriptEditorLifecycleCoordinator.Snapshot;
		}

		AutocompleteReloadStabilizationSnapshot reload =
			AutocompleteReloadStabilizationCoordinator.Snapshot;
		if (
			lifecycle.State != ScriptEditorLifecycleState.BindingPending
			|| lifecycle.ScriptTransitionId != targetTransitionId
			|| scheduledHostInstanceToken <= 0
			|| scheduledHostInstanceToken != _autocompleteHostInstanceToken
			|| !TryGetAutocompletePostReloadObservationIsolationContext(
				lifecycle,
				reload,
				out AutocompletePostReloadObservationIsolationKind currentKind,
				out string currentTargetPath
			)
		)
		{
			AutocompletePostReloadObservationIsolationCoordinator.Invalidate();
			return;
		}

		AutocompletePostReloadObservationIsolationCoordinator coordinator =
			AutocompletePostReloadObservationIsolationCoordinator;
		bool exactFailedAuthority = currentKind == kind
			&& string.Equals(
				currentTargetPath,
				capturedAuthoritativeTargetPath,
				StringComparison.OrdinalIgnoreCase
			);

		coordinator.ArmForTransition(
			scheduledHostInstanceToken,
			targetTransitionId,
			currentTargetPath,
			currentKind
		);

		if (
			exactFailedAuthority
			&& coordinator.TryGetActivationAuthority(
				ManagedAssemblyGeneration,
				scheduledHostInstanceToken,
				targetTransitionId,
				currentTargetPath,
				currentKind
			)
		)
		{
			coordinator.RejectActivationAndRestart();
		}
	}

	private bool DoesNoBindingStableLifecycleProvePureManagedTarget(
		ScriptEditorLifecycleSnapshot lifecycle,
		long expectedScriptTransitionId,
		string expectedAuthoritativeTargetPath
	)
	{
		string authoritativeTargetPath =
			GetAuthoritativeAutocompleteScriptTransitionTargetPath(lifecycle);
		return lifecycle.State == ScriptEditorLifecycleState.Stable
			&& string.Equals(
				lifecycle.ManagedAssemblyGeneration,
				ManagedAssemblyGeneration,
				StringComparison.Ordinal
			)
			&& lifecycle.ScriptTransitionId == expectedScriptTransitionId
			&& lifecycle.BindingEpoch == 0
			&& lifecycle.ReloadReadyEpoch == 0
			&& lifecycle.HostInstanceToken == 0
			&& lifecycle.ScriptEditorInstanceId == 0
			&& lifecycle.ScriptEditorBaseInstanceId == 0
			&& lifecycle.CodeEditInstanceId == 0
			&& string.IsNullOrWhiteSpace(lifecycle.BoundScriptResourcePath)
			&& string.Equals(
				authoritativeTargetPath,
				ScriptPathUtility.Normalize(expectedAuthoritativeTargetPath),
				StringComparison.OrdinalIgnoreCase
			);
	}

	private void ProcessAutocompletePostReloadObservationIsolation()
	{
		AutocompletePostReloadObservationIsolationCoordinator coordinator =
			_autocompletePostReloadObservationIsolationCoordinator;
		if (coordinator == null || !coordinator.HasPendingProcessWork)
			return;

		if (
			AutocompletePostReloadCodeEditCandidateObservationEnabled
			|| !string.Equals(
				coordinator.ManagedAssemblyGeneration,
				ManagedAssemblyGeneration,
				StringComparison.Ordinal
			)
		)
		{
			coordinator.Invalidate();
			return;
		}

		if (
			_autocompleteHost == null
			|| _autocompleteHostShutdownInProgress
			|| _autocompleteHostInstanceToken <= 0
			|| !string.Equals(
				_autocompleteHostManagedAssemblyGeneration,
				ManagedAssemblyGeneration,
				StringComparison.Ordinal
			)
			|| _isRecoveringManagedAssemblyState
			|| _namespaceRefactorAutocompleteQuiescenceActive
		)
		{
			return;
		}

		ScriptEditorLifecycleSnapshot lifecycle = ScriptEditorLifecycleCoordinator.Snapshot;
		AutocompleteReloadStabilizationSnapshot reload =
			AutocompleteReloadStabilizationCoordinator.Snapshot;
		if (
			!TryGetAutocompletePostReloadObservationIsolationContext(
				lifecycle,
				reload,
				out AutocompletePostReloadObservationIsolationKind kind,
				out string authoritativeTargetPath
			)
		)
		{
			coordinator.Invalidate();
			return;
		}

		if (
			!coordinator.ArmForTransition(
				_autocompleteHostInstanceToken,
				lifecycle.ScriptTransitionId,
				authoritativeTargetPath,
				kind
			)
		)
		{
			coordinator.Invalidate();
			return;
		}

		AutocompletePostReloadObservationIsolationUpdateKind update =
			coordinator.ObserveTarget(
				ManagedAssemblyGeneration,
				_autocompleteHostInstanceToken,
				lifecycle.ScriptTransitionId,
				authoritativeTargetPath,
				kind
			);
		if (
			update
			== AutocompletePostReloadObservationIsolationUpdateKind.ActivationAuthorized
		)
		{
			QueueDeferredAutocompleteScriptChangeRebind(
				"AutocompletePostReloadObservationIsolation"
			);
		}
	}

	#endregion
}
#endif
