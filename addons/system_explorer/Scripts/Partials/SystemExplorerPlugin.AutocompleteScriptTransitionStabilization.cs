#if TOOLS
using System;
using SystemExplorer.Autocomplete;
using SystemExplorer.EditorIntegration.ScriptEditing;

public partial class SystemExplorerPlugin
{
	#region Autocomplete Script Transition Stabilization

	private enum AutocompleteBindingActivationStabilizationKind
	{
		None,
		Reload,
		ScriptTransition,
	}

	private AutocompleteScriptTransitionStabilizationCoordinator
		_autocompleteScriptTransitionStabilizationCoordinator;

	private AutocompleteScriptTransitionStabilizationCoordinator
		AutocompleteScriptTransitionStabilizationCoordinator
	{
		get
		{
			if (
				_autocompleteScriptTransitionStabilizationCoordinator == null
				|| !string.Equals(
					_autocompleteScriptTransitionStabilizationCoordinator.ManagedAssemblyGeneration,
					ManagedAssemblyGeneration,
					StringComparison.Ordinal
				)
			)
			{
				_autocompleteScriptTransitionStabilizationCoordinator =
					new AutocompleteScriptTransitionStabilizationCoordinator(
						ManagedAssemblyGeneration
					);
			}

			return _autocompleteScriptTransitionStabilizationCoordinator;
		}
	}

	private void InvalidateAutocompleteScriptTransitionStabilizationAuthority()
	{
		_autocompleteScriptTransitionStabilizationCoordinator?.Invalidate();
	}

	private bool HasPendingAutocompleteScriptTransitionStabilizationProcessWork()
	{
		return _autocompleteScriptTransitionStabilizationCoordinator?.HasPendingProcessWork
			== true;
	}

	private static string GetAuthoritativeAutocompleteScriptTransitionTargetPath(
		ScriptEditorLifecycleSnapshot lifecycle
	)
	{
		string path = !string.IsNullOrWhiteSpace(lifecycle.ObservedScriptPath)
			? lifecycle.ObservedScriptPath
			: lifecycle.ExpectedScriptPath;
		return ScriptPathUtility.Normalize(path);
	}

	private static bool IsKnownCSharpAutocompleteScriptTransitionTarget(
		ScriptEditorLifecycleSnapshot lifecycle
	)
	{
		string path = GetAuthoritativeAutocompleteScriptTransitionTargetPath(lifecycle);
		return !string.IsNullOrWhiteSpace(path)
			&& path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
	}

	private bool ShouldRequireAutocompleteScriptTransitionStabilization(
		long reloadReadyEpoch,
		ScriptEditorLifecycleSnapshot lifecycle
	)
	{
		return reloadReadyEpoch > 1
			&& AutocompleteReloadStabilizationCoordinator.Snapshot.State
				== AutocompleteReloadStabilizationState.Ready
			&& lifecycle.State == ScriptEditorLifecycleState.BindingPending
			&& lifecycle.ScriptTransitionId > 0
			&& IsKnownCSharpAutocompleteScriptTransitionTarget(lifecycle);
	}

	private bool TryGetAutocompleteScriptTransitionRebindAdmission(
		long hostInstanceToken,
		long scriptTransitionId,
		out AutocompleteEditorBindingCandidate requiredActivationCandidate
	)
	{
		requiredActivationCandidate = default;
		AutocompleteScriptTransitionStabilizationCoordinator coordinator =
			AutocompleteScriptTransitionStabilizationCoordinator;

		if (
			coordinator.TryGetActivationAuthority(
				ManagedAssemblyGeneration,
				hostInstanceToken,
				scriptTransitionId,
				out requiredActivationCandidate
			)
		)
		{
			return true;
		}

		AutocompleteScriptTransitionStabilizationSnapshot before = coordinator.Snapshot;
		if (
			before.State == AutocompleteScriptTransitionStabilizationState.ActivationPending
			&& before.HostInstanceToken == hostInstanceToken
			&& before.ScriptTransitionId == scriptTransitionId
		)
		{
			coordinator.RejectActivationAndRestart();
			DebugLogger.LogOperation(
				"C# autocomplete ScriptTransition stabilization authority lost",
				$"ManagedAssemblyGeneration='{ManagedAssemblyGeneration}', HostInstanceToken='{hostInstanceToken}', ScriptTransitionId='{scriptTransitionId}'"
			);
			return false;
		}

		coordinator.ArmForTransition(hostInstanceToken, scriptTransitionId);
		return false;
	}

	private void ProcessAutocompleteScriptTransitionStabilization()
	{
		AutocompleteScriptTransitionStabilizationCoordinator coordinator =
			_autocompleteScriptTransitionStabilizationCoordinator;
		if (coordinator == null || !coordinator.HasPendingProcessWork)
			return;

		if (
			!string.Equals(
				coordinator.ManagedAssemblyGeneration,
				ManagedAssemblyGeneration,
				StringComparison.Ordinal
			)
			|| !IsAutocompletePluginBoundaryAvailable()
			|| _isRecoveringManagedAssemblyState
			|| _namespaceRefactorAutocompleteQuiescenceActive
		)
		{
			if (
				!string.Equals(
					coordinator.ManagedAssemblyGeneration,
					ManagedAssemblyGeneration,
					StringComparison.Ordinal
				)
			)
			{
				coordinator.Invalidate();
			}
			return;
		}

		AutocompleteReloadStabilizationSnapshot reload =
			AutocompleteReloadStabilizationCoordinator.Snapshot;
		if (
			reload.State != AutocompleteReloadStabilizationState.Ready
			|| reload.ReloadReadyEpoch <= 1
		)
		{
			coordinator.Invalidate();
			return;
		}

		if (ShouldSuppressAutocompleteScriptTransitionCodeEditCandidateObservation(reload))
		{
			// The pure-managed post-reload diagnostic coordinator is the sole
			// stabilization authority in this mode. Retire any ordinary candidate
			// authority before it can observe ScriptEditorBase/CodeEdit state.
			coordinator.Invalidate();
			return;
		}

		ScriptEditorLifecycleSnapshot lifecycle = ScriptEditorLifecycleCoordinator.Snapshot;
		if (
			lifecycle.State != ScriptEditorLifecycleState.BindingPending
			|| lifecycle.ScriptTransitionId <= 0
			|| !IsKnownCSharpAutocompleteScriptTransitionTarget(lifecycle)
		)
		{
			coordinator.Invalidate();
			return;
		}

		AutocompletePluginHost host = _autocompleteHost;
		if (
			host == null
			|| _autocompleteHostInstanceToken <= 0
			|| !string.Equals(
				_autocompleteHostManagedAssemblyGeneration,
				ManagedAssemblyGeneration,
				StringComparison.Ordinal
			)
		)
		{
			return;
		}

		AutocompleteScriptTransitionStabilizationSnapshot stabilization =
			coordinator.Snapshot;
		if (
			stabilization.HostInstanceToken != _autocompleteHostInstanceToken
			|| stabilization.ScriptTransitionId != lifecycle.ScriptTransitionId
		)
		{
			coordinator.Invalidate();
			return;
		}

		AutocompleteEditorBindingCandidateObservationKind observationKind =
			host.TryObserveCodeEditBindingCandidate(
				lifecycle.ScriptTransitionId,
				_autocompleteHostInstanceToken,
				out AutocompleteEditorBindingCandidate candidate
			);

		if (
			observationKind != AutocompleteEditorBindingCandidateObservationKind.Candidate
		)
		{
			return;
		}

		string authoritativeTargetPath =
			GetAuthoritativeAutocompleteScriptTransitionTargetPath(lifecycle);
		if (
			!string.Equals(
				ScriptPathUtility.Normalize(candidate.ScriptResourcePath),
				authoritativeTargetPath,
				StringComparison.OrdinalIgnoreCase
			)
		)
		{
			return;
		}

		AutocompleteScriptTransitionCandidateUpdateKind update = coordinator.ObserveCandidate(
			candidate
		);
		if (
			update
			== AutocompleteScriptTransitionCandidateUpdateKind.ActivationAuthorized
		)
		{
			QueueDeferredAutocompleteScriptChangeRebind(
				"AutocompleteScriptTransitionStabilization"
			);
		}
	}

	private bool TryCompleteAutocompleteScriptTransitionActivation(
		long reloadReadyEpoch,
		AutocompleteEditorBindingCandidate candidate
	)
	{
		bool leaseProvesCandidate =
			ScriptEditorLifecycleCoordinator.TryGetCurrentBindingLease(
				out EditorBindingLease lease
			)
			&& string.Equals(
				lease.ManagedAssemblyGeneration,
				candidate.ManagedAssemblyGeneration,
				StringComparison.Ordinal
			)
			&& string.Equals(
				lease.ManagedAssemblyGeneration,
				ManagedAssemblyGeneration,
				StringComparison.Ordinal
			)
			&& lease.HostInstanceToken == candidate.HostInstanceToken
			&& lease.ScriptTransitionId == candidate.ScriptTransitionId
			&& lease.ReloadReadyEpoch == reloadReadyEpoch
			&& lease.ReloadReadyEpoch == CurrentAutocompleteReloadReadyEpoch
			&& lease.ScriptEditorInstanceId == candidate.ScriptEditorInstanceId
			&& lease.ScriptEditorBaseInstanceId == candidate.ScriptEditorBaseInstanceId
			&& lease.CodeEditInstanceId == candidate.CodeEditInstanceId
			&& string.Equals(
				ScriptPathUtility.Normalize(lease.ScriptResourcePath),
				ScriptPathUtility.Normalize(candidate.ScriptResourcePath),
				StringComparison.OrdinalIgnoreCase
			);

		if (!leaseProvesCandidate)
		{
			RestartAutocompleteScriptTransitionActivation(
				"BindingLeaseDidNotProveStabilizedCandidate"
			);
			return false;
		}

		AutocompleteScriptTransitionStabilizationCoordinator coordinator =
			AutocompleteScriptTransitionStabilizationCoordinator;
		if (
			!coordinator.CompleteActivation(
				ManagedAssemblyGeneration,
				candidate.HostInstanceToken,
				candidate.ScriptTransitionId,
				candidate
			)
		)
		{
			ScriptEditorLifecycleSnapshot lifecycle =
				ScriptEditorLifecycleCoordinator.Snapshot;
			if (
				lifecycle.State == ScriptEditorLifecycleState.Stable
				&& lifecycle.ScriptTransitionId == candidate.ScriptTransitionId
			)
			{
				ScriptEditorLifecycleCoordinator.MarkBindingPending(
					candidate.ScriptTransitionId
				);
			}

			RestartAutocompleteScriptTransitionActivation(
				"CoordinatorRejectedCommittedBinding"
			);
			return false;
		}

		return true;
	}

	private void RestartAutocompleteScriptTransitionActivation(string reason)
	{
		AutocompleteScriptTransitionStabilizationCoordinator coordinator =
			AutocompleteScriptTransitionStabilizationCoordinator;
		AutocompleteScriptTransitionStabilizationSnapshot stabilization =
			coordinator.Snapshot;
		ScriptEditorLifecycleSnapshot lifecycle = ScriptEditorLifecycleCoordinator.Snapshot;

		bool sameCurrentTransition =
			stabilization.HostInstanceToken > 0
			&& stabilization.ScriptTransitionId > 0
			&& stabilization.HostInstanceToken == _autocompleteHostInstanceToken
			&& lifecycle.ScriptTransitionId == stabilization.ScriptTransitionId
			&& string.Equals(
				stabilization.ManagedAssemblyGeneration,
				ManagedAssemblyGeneration,
				StringComparison.Ordinal
			);

		if (!sameCurrentTransition)
		{
			coordinator.Invalidate();
		}
		else
		{
			if (lifecycle.State == ScriptEditorLifecycleState.Stable)
			{
				ScriptEditorLifecycleCoordinator.MarkBindingPending(
					lifecycle.ScriptTransitionId
				);
				lifecycle = ScriptEditorLifecycleCoordinator.Snapshot;
			}

			if (lifecycle.State == ScriptEditorLifecycleState.BindingPending)
				coordinator.RejectActivationAndRestart();
			else
				coordinator.Invalidate();
		}

		DebugLogger.LogOperation(
			"C# autocomplete ScriptTransition stabilization activation restarted",
			$"Reason='{reason ?? ""}', ManagedAssemblyGeneration='{ManagedAssemblyGeneration}', HostInstanceToken='{stabilization.HostInstanceToken}', ScriptTransitionId='{stabilization.ScriptTransitionId}', LifecycleState='{lifecycle.State}'"
		);
	}

	#endregion
}
#endif
