#if TOOLS
using System;
using SystemExplorer.Autocomplete;
using SystemExplorer.EditorIntegration.ScriptEditing;

public partial class SystemExplorerPlugin
{
	#region Autocomplete Reload Stabilization

	private AutocompleteReloadStabilizationCoordinator _autocompleteReloadStabilizationCoordinator;

	private AutocompleteReloadStabilizationCoordinator AutocompleteReloadStabilizationCoordinator
	{
		get
		{
			if (
				_autocompleteReloadStabilizationCoordinator == null
				|| !string.Equals(
					_autocompleteReloadStabilizationCoordinator.ManagedAssemblyGeneration,
					ManagedAssemblyGeneration,
					StringComparison.Ordinal
				)
			)
			{
				_autocompleteReloadStabilizationCoordinator =
					new AutocompleteReloadStabilizationCoordinator(
						ManagedAssemblyGeneration
					);
			}

			return _autocompleteReloadStabilizationCoordinator;
		}
	}

	private long CurrentAutocompleteReloadReadyEpoch =>
		AutocompleteReloadStabilizationCoordinator.CurrentReloadReadyEpoch;

	private bool IsAutocompleteReloadStabilizationReady()
	{
		return AutocompleteReloadStabilizationCoordinator.IsReady;
	}

	private void BeginAutocompleteReloadStabilization()
	{
		AutocompleteReloadStabilizationSnapshot snapshot =
			AutocompleteReloadStabilizationCoordinator.BeginReloadStabilization();
		LogAutocompleteReloadStabilizationTransition(
			"Autocomplete reload stabilization begun",
			snapshot
		);
	}

	private void InvalidateAutocompleteReloadStabilizationAuthority(
		bool parkObservation
	)
	{
		if (_autocompleteReloadStabilizationCoordinator == null)
			return;

		_autocompleteReloadStabilizationCoordinator.InvalidatePendingAuthority(
			parkObservation
		);
	}

	private void ArmAutocompleteReloadStabilizationObservation()
	{
		AutocompleteReloadStabilizationCoordinator.ArmObservation();
	}

	private bool HasPendingAutocompleteReloadStabilizationProcessWork()
	{
		return _autocompleteReloadStabilizationCoordinator?.HasPendingProcessWork == true;
	}

	private void ProcessAutocompleteReloadStabilization()
	{
		AutocompleteReloadStabilizationCoordinator coordinator =
			_autocompleteReloadStabilizationCoordinator;
		if (coordinator == null || !coordinator.HasPendingProcessWork)
			return;

		if (
			!IsAutocompletePluginBoundaryAvailable()
			|| _isRecoveringManagedAssemblyState
			|| _namespaceRefactorAutocompleteQuiescenceActive
		)
		{
			return;
		}

		AutocompleteReloadStabilizationSnapshot diagnosticReload = coordinator.Snapshot;
		if (ShouldSuppressAutocompleteReloadCodeEditCandidateObservation(diagnosticReload))
		{
			// Diagnostic A/B: keep reload readiness quiescent until the separate
			// pure-managed lifecycle-target coordinator authorizes completion.
			return;
		}

		ScriptEditorLifecycleSnapshot lifecycleSnapshot =
			ScriptEditorLifecycleCoordinator.Snapshot;
		if (
			lifecycleSnapshot.State != ScriptEditorLifecycleState.BindingPending
			|| lifecycleSnapshot.ScriptTransitionId <= 0
		)
		{
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

		AutocompleteEditorBindingCandidateObservationKind observationKind =
			host.TryObserveCodeEditBindingCandidate(
				lifecycleSnapshot.ScriptTransitionId,
				_autocompleteHostInstanceToken,
				out AutocompleteEditorBindingCandidate candidate
			);

		if (
			observationKind
			== AutocompleteEditorBindingCandidateObservationKind.NonCSharpTarget
		)
		{
			AutocompleteReloadStabilizationState previousState = coordinator.Snapshot.State;
			coordinator.ParkObservation();
			if (previousState != coordinator.Snapshot.State)
			{
				LogAutocompleteReloadStabilizationTransition(
					"Autocomplete reload stabilization parked",
					coordinator.Snapshot
				);
			}
			return;
		}

		if (
			observationKind
			!= AutocompleteEditorBindingCandidateObservationKind.Candidate
		)
		{
			return;
		}

		AutocompleteReloadStabilizationSnapshot before = coordinator.Snapshot;
		AutocompleteReloadCandidateUpdateKind update = coordinator.ObserveCandidate(
			candidate
		);
		AutocompleteReloadStabilizationSnapshot after = coordinator.Snapshot;

		switch (update)
		{
			case AutocompleteReloadCandidateUpdateKind.Observed:
				LogAutocompleteReloadStabilizationTransition(
					"Autocomplete reload candidate observed",
					after
				);
				break;
			case AutocompleteReloadCandidateUpdateKind.Changed:
				LogAutocompleteReloadStabilizationTransition(
					"Autocomplete reload candidate changed",
					after,
					before.Candidate
				);
				break;
			case AutocompleteReloadCandidateUpdateKind.ActivationAuthorized:
				LogAutocompleteReloadStabilizationTransition(
					"Autocomplete reload activation authorized",
					after
				);
				QueueDeferredAutocompleteScriptChangeRebind(
					"AutocompleteReloadStabilization"
				);
				break;
		}
	}

	private bool TryGetAutocompleteReloadRebindAdmission(
		long hostInstanceToken,
		long scriptTransitionId,
		out long reloadReadyEpoch,
		out AutocompleteEditorBindingCandidate? requiredActivationCandidate
	)
	{
		reloadReadyEpoch = 0;
		requiredActivationCandidate = null;
		AutocompleteReloadStabilizationCoordinator coordinator =
			AutocompleteReloadStabilizationCoordinator;
		AutocompleteReloadStabilizationSnapshot snapshot = coordinator.Snapshot;

		if (snapshot.State == AutocompleteReloadStabilizationState.Ready)
		{
			reloadReadyEpoch = snapshot.ReloadReadyEpoch;
			return reloadReadyEpoch > 0;
		}

		if (snapshot.State == AutocompleteReloadStabilizationState.ActivationPending)
		{
			if (
				coordinator.TryGetActivationAuthority(
					ManagedAssemblyGeneration,
					hostInstanceToken,
					scriptTransitionId,
					out reloadReadyEpoch,
					out AutocompleteEditorBindingCandidate candidate
				)
			)
			{
				requiredActivationCandidate = candidate;
				return true;
			}

			RestartAutocompleteReloadActivation(
				"ActivationAuthorityNoLongerMatchesCurrentTransition"
			);
			return false;
		}

		coordinator.ArmObservation();
		return false;
	}

	private bool TryCompleteAutocompleteReloadActivation(
		long reloadReadyEpoch,
		AutocompleteEditorBindingCandidate candidate
	)
	{
		if (
			!ScriptEditorLifecycleCoordinator.TryGetCurrentBindingLease(
				out EditorBindingLease lease
			)
			|| !string.Equals(
				lease.ManagedAssemblyGeneration,
				candidate.ManagedAssemblyGeneration,
				StringComparison.Ordinal
			)
			|| !string.Equals(
				lease.ManagedAssemblyGeneration,
				ManagedAssemblyGeneration,
				StringComparison.Ordinal
			)
			|| lease.ReloadReadyEpoch != reloadReadyEpoch
			|| lease.HostInstanceToken != candidate.HostInstanceToken
			|| lease.ScriptTransitionId != candidate.ScriptTransitionId
			|| lease.ScriptEditorInstanceId != candidate.ScriptEditorInstanceId
			|| lease.ScriptEditorBaseInstanceId != candidate.ScriptEditorBaseInstanceId
			|| lease.CodeEditInstanceId != candidate.CodeEditInstanceId
			|| !string.Equals(
				ScriptPathUtility.Normalize(lease.ScriptResourcePath),
				ScriptPathUtility.Normalize(candidate.ScriptResourcePath),
				StringComparison.OrdinalIgnoreCase
			)
		)
		{
			RestartAutocompleteReloadActivation(
				"BindingLeaseDidNotProveStabilizedCandidate"
			);
			return false;
		}

		AutocompleteReloadStabilizationCoordinator coordinator =
			AutocompleteReloadStabilizationCoordinator;
		if (
			!coordinator.CompleteActivation(
				ManagedAssemblyGeneration,
				candidate.HostInstanceToken,
				candidate.ScriptTransitionId,
				reloadReadyEpoch,
				candidate
			)
		)
		{
			ScriptEditorLifecycleCoordinator.MarkBindingPending(
				candidate.ScriptTransitionId
			);
			RestartAutocompleteReloadActivation(
				"ReloadCoordinatorRejectedCommittedBinding"
			);
			return false;
		}

		LogAutocompleteReloadStabilizationTransition(
			"Autocomplete reload stabilization completed",
			coordinator.Snapshot,
			additionalDetails: "SubsequentCSharpTransitionStabilization='TwoProcessTurns'"
		);
		return true;
	}

	private void RestartAutocompleteReloadActivation(string reason)
	{
		AutocompleteReloadStabilizationCoordinator coordinator =
			AutocompleteReloadStabilizationCoordinator;
		AutocompleteReloadStabilizationSnapshot previous = coordinator.Snapshot;
		if (
			previous.State == AutocompleteReloadStabilizationState.ActivationPending
			&& previous.Candidate.HasValue
		)
		{
			ScriptEditorLifecycleSnapshot lifecycle =
				ScriptEditorLifecycleCoordinator.Snapshot;
			if (
				lifecycle.State == ScriptEditorLifecycleState.Stable
				&& lifecycle.ScriptTransitionId
					== previous.Candidate.Value.ScriptTransitionId
			)
			{
				ScriptEditorLifecycleCoordinator.MarkBindingPending(
					lifecycle.ScriptTransitionId
				);
			}
		}

		AutocompleteReloadStabilizationSnapshot restarted =
			coordinator.RejectActivationAndRestart();
		LogAutocompleteReloadStabilizationTransition(
			"Autocomplete reload activation rejected/restarted",
			restarted,
			previous.Candidate,
			reason
		);
	}

	private void LogAutocompleteReloadStabilizationTransition(
		string operation,
		AutocompleteReloadStabilizationSnapshot snapshot,
		AutocompleteEditorBindingCandidate? previousCandidate = null,
		string reason = "",
		string additionalDetails = ""
	)
	{
		try
		{
			AutocompleteEditorBindingCandidate? candidate = snapshot.Candidate;
			string details =
				$"ManagedAssemblyGeneration='{snapshot.ManagedAssemblyGeneration}', "
				+ $"State='{snapshot.State}', "
				+ $"StabilizationToken='{snapshot.StabilizationToken}', "
				+ $"ReloadReadyEpoch='{snapshot.ReloadReadyEpoch}', "
				+ DescribeAutocompleteReloadCandidate(candidate);

			if (previousCandidate.HasValue)
				details += $", Previous{DescribeAutocompleteReloadCandidate(previousCandidate, includePrefix: false)}";
			if (!string.IsNullOrWhiteSpace(reason))
				details += $", Reason='{reason}'";
			if (!string.IsNullOrWhiteSpace(additionalDetails))
				details += $", {additionalDetails}";

			DebugLogger.LogPersistentFileOnlyOperation(operation, details);
		}
		catch
		{
		}
	}

	private static string DescribeAutocompleteReloadCandidate(
		AutocompleteEditorBindingCandidate? candidate,
		bool includePrefix = true
	)
	{
		AutocompleteEditorBindingCandidate value = candidate ?? default;
		string prefix = includePrefix ? "" : "Candidate";
		return
			$"{prefix}HostInstanceToken='{value.HostInstanceToken}', "
			+ $"{prefix}ScriptTransitionId='{value.ScriptTransitionId}', "
			+ $"{prefix}ScriptEditorInstanceId='{value.ScriptEditorInstanceId}', "
			+ $"{prefix}ScriptEditorBaseInstanceId='{value.ScriptEditorBaseInstanceId}', "
			+ $"{prefix}CodeEditInstanceId='{value.CodeEditInstanceId}', "
			+ $"{prefix}ScriptPath='{value.ScriptResourcePath ?? ""}'";
	}

	#endregion
}
#endif
