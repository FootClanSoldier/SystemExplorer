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

	private void RestartAutocompleteScriptTransitionStabilizationForExternalMutationBarrier()
	{
		AutocompleteScriptTransitionStabilizationCoordinator coordinator =
			_autocompleteScriptTransitionStabilizationCoordinator;
		if (
			coordinator == null
			|| !string.Equals(
				coordinator.ManagedAssemblyGeneration,
				ManagedAssemblyGeneration,
				StringComparison.Ordinal
			)
		)
		{
			return;
		}

		bool hadPendingProcessWork = coordinator.HasPendingProcessWork;
		coordinator.RestartQuietWindowAfterBarrier();

		if (!hadPendingProcessWork && coordinator.HasPendingProcessWork)
			RefreshEditorPluginProcessingState();
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

	private bool ShouldRequireSystemExplorerNavigationBindingQuiescence(
		long reloadReadyEpoch,
		ScriptEditorLifecycleSnapshot lifecycle
	)
	{
		return reloadReadyEpoch > 0
			&& AutocompleteReloadStabilizationCoordinator.Snapshot.State
				== AutocompleteReloadStabilizationState.Ready
			&& lifecycle.State == ScriptEditorLifecycleState.BindingPending
			&& lifecycle.ScriptTransitionId > 0
			&& lifecycle.TransitionOrigin
				== ScriptEditorTransitionOrigin.SystemExplorerNavigation
			&& IsKnownCSharpAutocompleteScriptTransitionTarget(lifecycle);
	}

	private bool ShouldRequirePostReloadAutocompleteScriptTransitionStabilization(
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

	private bool ShouldRequireAutocompleteScriptTransitionStabilization(
		long reloadReadyEpoch,
		ScriptEditorLifecycleSnapshot lifecycle
	)
	{
		return ShouldRequireSystemExplorerNavigationBindingQuiescence(
				reloadReadyEpoch,
				lifecycle
			)
			|| ShouldRequirePostReloadAutocompleteScriptTransitionStabilization(
				reloadReadyEpoch,
				lifecycle
			);
	}

	private bool TryInterceptSystemExplorerNavigationBindingQuiescenceAdmission()
	{
		AutocompleteReloadStabilizationSnapshot reload =
			AutocompleteReloadStabilizationCoordinator.Snapshot;
		ScriptEditorLifecycleSnapshot lifecycle = ScriptEditorLifecycleCoordinator.Snapshot;

		if (
			!ShouldRequireSystemExplorerNavigationBindingQuiescence(
				reload.ReloadReadyEpoch,
				lifecycle
			)
		)
		{
			return false;
		}

		if (
			_autocompleteHostInstanceToken <= 0
			|| !string.Equals(
				_autocompleteHostManagedAssemblyGeneration,
				ManagedAssemblyGeneration,
				StringComparison.Ordinal
			)
		)
		{
			return false;
		}

		AutocompleteScriptTransitionStabilizationCoordinator coordinator =
			AutocompleteScriptTransitionStabilizationCoordinator;
		AutocompleteScriptTransitionStabilizationSnapshot before = coordinator.Snapshot;

		bool exactActivationPendingWithoutFinalResolver =
			before.State == AutocompleteScriptTransitionStabilizationState.ActivationPending
			&& before.NavigationQuietPeriodRequired
			&& before.HostInstanceToken == _autocompleteHostInstanceToken
			&& before.ScriptTransitionId == lifecycle.ScriptTransitionId
			&& !_autocompleteDeferredScriptChangeRebindPending;
		if (exactActivationPendingWithoutFinalResolver)
		{
			coordinator.RestartQuietWindowAfterBarrier();
			before = coordinator.Snapshot;
		}

		if (
			!coordinator.ArmForTransition(
				_autocompleteHostInstanceToken,
				lifecycle.ScriptTransitionId,
				requireNavigationQuietPeriod: true
			)
		)
		{
			return false;
		}

		bool beganNewExactTransition =
			before.State == AutocompleteScriptTransitionStabilizationState.Idle
			|| before.HostInstanceToken != _autocompleteHostInstanceToken
			|| before.ScriptTransitionId != lifecycle.ScriptTransitionId;

		if (beganNewExactTransition && _autocompleteDeferredScriptChangeRebindPending)
			ResetDeferredAutocompleteScriptChangeRebindState(invalidateToken: true);

		RefreshEditorPluginProcessingState();
		return true;
	}

	private bool TryGetAutocompleteScriptTransitionRebindAdmission(
		long hostInstanceToken,
		long scriptTransitionId,
		bool requireNavigationQuietPeriod,
		out AutocompleteEditorBindingCandidate requiredActivationCandidate
	)
	{
		requiredActivationCandidate = default;
		AutocompleteScriptTransitionStabilizationCoordinator coordinator =
			AutocompleteScriptTransitionStabilizationCoordinator;
		AutocompleteScriptTransitionStabilizationSnapshot current = coordinator.Snapshot;

		if (
			current.State != AutocompleteScriptTransitionStabilizationState.Idle
			&& current.HostInstanceToken == hostInstanceToken
			&& current.ScriptTransitionId == scriptTransitionId
			&& current.NavigationQuietPeriodRequired != requireNavigationQuietPeriod
		)
		{
			coordinator.Invalidate();
			return false;
		}

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

		coordinator.ArmForTransition(
			hostInstanceToken,
			scriptTransitionId,
			requireNavigationQuietPeriod
		);
		return false;
	}

	private void ProcessAutocompleteScriptTransitionStabilization(double delta)
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
		)
		{
			coordinator.Invalidate();
			return;
		}

		ScriptEditorLifecycleSnapshot lifecycle = ScriptEditorLifecycleCoordinator.Snapshot;
		AutocompleteScriptTransitionStabilizationSnapshot stabilization = coordinator.Snapshot;
		if (
			lifecycle.State != ScriptEditorLifecycleState.BindingPending
			|| lifecycle.ScriptTransitionId <= 0
			|| stabilization.HostInstanceToken <= 0
			|| stabilization.ScriptTransitionId != lifecycle.ScriptTransitionId
			|| !IsKnownCSharpAutocompleteScriptTransitionTarget(lifecycle)
		)
		{
			coordinator.Invalidate();
			return;
		}

		AutocompleteReloadStabilizationSnapshot reload =
			AutocompleteReloadStabilizationCoordinator.Snapshot;
		bool requiresNavigationQuiet = stabilization.NavigationQuietPeriodRequired;
		bool stillRequiresNavigationQuiet =
			ShouldRequireSystemExplorerNavigationBindingQuiescence(
				reload.ReloadReadyEpoch,
				lifecycle
			);
		bool stillRequiresOrdinaryPostReloadStabilization =
			ShouldRequirePostReloadAutocompleteScriptTransitionStabilization(
				reload.ReloadReadyEpoch,
				lifecycle
			);

		if (
			requiresNavigationQuiet
				? !stillRequiresNavigationQuiet
				: !stillRequiresOrdinaryPostReloadStabilization
		)
		{
			if (
				requiresNavigationQuiet
				&& reload.State != AutocompleteReloadStabilizationState.Ready
			)
			{
				coordinator.RestartQuietWindowAfterBarrier();
				return;
			}

			coordinator.Invalidate();
			return;
		}

		bool pluginAndMutationAdmissionAllowed =
			IsAutocompletePluginBoundaryAvailable()
			&& !_isRecoveringManagedAssemblyState
			&& !IsAutocompleteExternalMutationActive;

		AutocompletePluginHost host = _autocompleteHost;
		bool hostAuthorityAvailable =
			host != null
			&& _autocompleteHostInstanceToken > 0
			&& stabilization.HostInstanceToken == _autocompleteHostInstanceToken
			&& string.Equals(
				_autocompleteHostManagedAssemblyGeneration,
				ManagedAssemblyGeneration,
				StringComparison.Ordinal
			);

		if (requiresNavigationQuiet)
		{
			bool quietAdmissionAllowed =
				pluginAndMutationAdmissionAllowed
				&& reload.State == AutocompleteReloadStabilizationState.Ready
				&& reload.ReloadReadyEpoch > 0
				&& hostAuthorityAvailable;

			AutocompleteScriptTransitionStabilizationState stateBeforeAdvance =
				stabilization.State;
			if (!coordinator.TryAdvanceNavigationQuietPeriod(delta, quietAdmissionAllowed))
				return;

			AutocompleteScriptTransitionStabilizationSnapshot afterAdvance =
				coordinator.Snapshot;
			if (
				stateBeforeAdvance == AutocompleteScriptTransitionStabilizationState.Quiescing
				&& afterAdvance.State
					== AutocompleteScriptTransitionStabilizationState.Observing
			)
			{
				LogAutocompleteNavigationBindingQuiescenceAdmission(
					afterAdvance,
					reload.ReloadReadyEpoch,
					GetAuthoritativeAutocompleteScriptTransitionTargetPath(lifecycle)
				);
			}
		}
		else if (!pluginAndMutationAdmissionAllowed || !hostAuthorityAvailable)
		{
			return;
		}

		stabilization = coordinator.Snapshot;
		if (stabilization.State == AutocompleteScriptTransitionStabilizationState.Quiescing)
			return;
		if (host == null)
			return;

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
			if (requiresNavigationQuiet)
				coordinator.RestartQuietWindowAfterBarrier();
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
			if (requiresNavigationQuiet)
				coordinator.RestartQuietWindowAfterBarrier();
			return;
		}

		AutocompleteScriptTransitionCandidateUpdateKind update = coordinator.ObserveCandidate(
			candidate
		);
		if (
			requiresNavigationQuiet
			&& update == AutocompleteScriptTransitionCandidateUpdateKind.None
		)
		{
			coordinator.RestartQuietWindowAfterBarrier();
			return;
		}

		if (
			update
			== AutocompleteScriptTransitionCandidateUpdateKind.ActivationAuthorized
		)
		{
			QueueDeferredAutocompleteScriptChangeRebind(
				"AutocompleteScriptTransitionStabilization",
				bypassSystemExplorerNavigationQuiescenceAdmission: true
			);
		}
	}

	private void LogAutocompleteNavigationBindingQuiescenceAdmission(
		AutocompleteScriptTransitionStabilizationSnapshot snapshot,
		long reloadReadyEpoch,
		string scriptPath
	)
	{
		DebugLogger.LogPersistentFileOnlyOperation(
			"C# autocomplete navigation BindingActivation quiescence admitted",
			$"QuietPeriodMs='{SystemExplorer.Autocomplete.AutocompleteScriptTransitionStabilizationCoordinator.NavigationQuietPeriodMilliseconds}', "
				+ $"QuietDurationMs='{snapshot.QuietElapsedSeconds * 1000.0:F1}', "
				+ $"CoalescedNavigationTransitionCount='{snapshot.CoalescedNavigationTransitionCount}', "
				+ $"ManagedAssemblyGeneration='{snapshot.ManagedAssemblyGeneration}', "
				+ $"HostInstanceToken='{snapshot.HostInstanceToken}', "
				+ $"ScriptTransitionId='{snapshot.ScriptTransitionId}', "
				+ $"ReloadReadyEpoch='{reloadReadyEpoch}', "
				+ $"ScriptPath='{scriptPath ?? ""}'"
		);
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
