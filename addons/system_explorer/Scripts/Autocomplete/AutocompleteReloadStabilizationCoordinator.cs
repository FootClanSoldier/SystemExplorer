#if TOOLS
using System;

namespace SystemExplorer.Autocomplete;

internal enum AutocompleteReloadStabilizationState
{
	Ready,
	ReloadQuiescent,
	ReloadQuiescentParked,
	CandidateObserved,
	ActivationPending,
}

internal enum AutocompleteReloadCandidateUpdateKind
{
	None,
	Observed,
	Changed,
	ActivationAuthorized,
}

internal readonly record struct AutocompleteReloadStabilizationSnapshot(
	string ManagedAssemblyGeneration,
	AutocompleteReloadStabilizationState State,
	long StabilizationToken,
	long ReloadReadyEpoch,
	AutocompleteEditorBindingCandidate? Candidate
);

internal sealed class AutocompleteReloadStabilizationCoordinator
{
	private readonly string _managedAssemblyGeneration;
	private long _stabilizationToken;
	private long _nextReloadReadyEpoch = 1;
	private long _reloadReadyEpoch = 1;
	private AutocompleteReloadStabilizationState _state =
		AutocompleteReloadStabilizationState.Ready;
	private AutocompleteEditorBindingCandidate? _candidate;

	internal AutocompleteReloadStabilizationCoordinator(string managedAssemblyGeneration)
	{
		_managedAssemblyGeneration = !string.IsNullOrWhiteSpace(managedAssemblyGeneration)
			? managedAssemblyGeneration
			: throw new ArgumentException(
				"Managed assembly generation is required.",
				nameof(managedAssemblyGeneration)
			);
	}

	internal string ManagedAssemblyGeneration => _managedAssemblyGeneration;

	internal AutocompleteReloadStabilizationSnapshot Snapshot =>
		new(
			_managedAssemblyGeneration,
			_state,
			_stabilizationToken,
			CurrentReloadReadyEpoch,
			_candidate
		);

	internal long CurrentReloadReadyEpoch =>
		(_state is AutocompleteReloadStabilizationState.Ready
			or AutocompleteReloadStabilizationState.ActivationPending)
			? _reloadReadyEpoch
			: 0;

	internal bool IsReady =>
		_state == AutocompleteReloadStabilizationState.Ready
		&& _reloadReadyEpoch > 0;

	internal bool HasPendingProcessWork =>
		_state is AutocompleteReloadStabilizationState.ReloadQuiescent
			or AutocompleteReloadStabilizationState.CandidateObserved;

	internal AutocompleteReloadStabilizationSnapshot BeginReloadStabilization()
	{
		AdvanceStabilizationToken();
		_reloadReadyEpoch = 0;
		_candidate = null;
		_state = AutocompleteReloadStabilizationState.ReloadQuiescent;
		return Snapshot;
	}

	internal AutocompleteReloadStabilizationSnapshot InvalidatePendingAuthority(
		bool parkObservation
	)
	{
		AdvanceStabilizationToken();
		_reloadReadyEpoch = 0;
		_candidate = null;
		_state = parkObservation
			? AutocompleteReloadStabilizationState.ReloadQuiescentParked
			: AutocompleteReloadStabilizationState.ReloadQuiescent;
		return Snapshot;
	}

	internal void ArmObservation()
	{
		if (_state == AutocompleteReloadStabilizationState.ReloadQuiescentParked)
			_state = AutocompleteReloadStabilizationState.ReloadQuiescent;
	}

	internal void ParkObservation()
	{
		if (
			_state is AutocompleteReloadStabilizationState.ReloadQuiescent
				or AutocompleteReloadStabilizationState.CandidateObserved
		)
		{
			_candidate = null;
			_reloadReadyEpoch = 0;
			_state = AutocompleteReloadStabilizationState.ReloadQuiescentParked;
		}
	}

	internal AutocompleteReloadCandidateUpdateKind ObserveCandidate(
		AutocompleteEditorBindingCandidate candidate
	)
	{
		candidate = candidate.Normalized();
		if (!IsCandidateForCurrentGeneration(candidate))
			return AutocompleteReloadCandidateUpdateKind.None;

		if (
			_state is AutocompleteReloadStabilizationState.ReloadQuiescent
				or AutocompleteReloadStabilizationState.ReloadQuiescentParked
		)
		{
			_candidate = candidate;
			_reloadReadyEpoch = 0;
			_state = AutocompleteReloadStabilizationState.CandidateObserved;
			return AutocompleteReloadCandidateUpdateKind.Observed;
		}

		if (_state != AutocompleteReloadStabilizationState.CandidateObserved)
			return AutocompleteReloadCandidateUpdateKind.None;

		if (!_candidate.HasValue || !_candidate.Value.AuthorityEquals(candidate))
		{
			_candidate = candidate;
			_reloadReadyEpoch = 0;
			return AutocompleteReloadCandidateUpdateKind.Changed;
		}

		_reloadReadyEpoch = NextPositive(ref _nextReloadReadyEpoch);
		_candidate = candidate;
		_state = AutocompleteReloadStabilizationState.ActivationPending;
		return AutocompleteReloadCandidateUpdateKind.ActivationAuthorized;
	}

	internal bool TryGetActivationAuthority(
		string managedAssemblyGeneration,
		long hostInstanceToken,
		long scriptTransitionId,
		out long reloadReadyEpoch,
		out AutocompleteEditorBindingCandidate candidate
	)
	{
		reloadReadyEpoch = 0;
		candidate = default;
		if (
			_state != AutocompleteReloadStabilizationState.ActivationPending
			|| _reloadReadyEpoch <= 0
			|| !_candidate.HasValue
		)
		{
			return false;
		}

		AutocompleteEditorBindingCandidate currentCandidate = _candidate.Value;
		if (
			!string.Equals(
				managedAssemblyGeneration,
				_managedAssemblyGeneration,
				StringComparison.Ordinal
			)
			|| !string.Equals(
				currentCandidate.ManagedAssemblyGeneration,
				managedAssemblyGeneration,
				StringComparison.Ordinal
			)
			|| currentCandidate.HostInstanceToken != hostInstanceToken
			|| currentCandidate.ScriptTransitionId != scriptTransitionId
		)
		{
			return false;
		}

		reloadReadyEpoch = _reloadReadyEpoch;
		candidate = currentCandidate;
		return true;
	}


	internal bool CompleteActivation(
		string managedAssemblyGeneration,
		long hostInstanceToken,
		long scriptTransitionId,
		long reloadReadyEpoch,
		AutocompleteEditorBindingCandidate candidate
	)
	{
		if (
			!TryGetActivationAuthority(
				managedAssemblyGeneration,
				hostInstanceToken,
				scriptTransitionId,
				out long currentEpoch,
				out AutocompleteEditorBindingCandidate currentCandidate
			)
			|| currentEpoch != reloadReadyEpoch
			|| !currentCandidate.AuthorityEquals(candidate)
		)
		{
			return false;
		}

		_state = AutocompleteReloadStabilizationState.Ready;
		_candidate = null;
		return true;
	}

	internal AutocompleteReloadStabilizationSnapshot RejectActivationAndRestart()
	{
		AdvanceStabilizationToken();
		_reloadReadyEpoch = 0;
		_candidate = null;
		_state = AutocompleteReloadStabilizationState.ReloadQuiescent;
		return Snapshot;
	}

	private bool IsCandidateForCurrentGeneration(
		AutocompleteEditorBindingCandidate candidate
	)
	{
		return candidate.IsValid
			&& string.Equals(
				candidate.ManagedAssemblyGeneration,
				_managedAssemblyGeneration,
				StringComparison.Ordinal
			);
	}

	private void AdvanceStabilizationToken()
	{
		NextPositive(ref _stabilizationToken);
	}

	private static long NextPositive(ref long value)
	{
		unchecked
		{
			value++;
			if (value <= 0)
				value = 1;
		}

		return value;
	}
}
#endif
