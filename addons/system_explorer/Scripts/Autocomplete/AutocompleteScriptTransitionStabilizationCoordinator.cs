#if TOOLS
using System;

namespace SystemExplorer.Autocomplete;

internal enum AutocompleteScriptTransitionStabilizationState
{
	Idle,
	Observing,
	CandidateObserved,
	ActivationPending,
}

internal enum AutocompleteScriptTransitionCandidateUpdateKind
{
	None,
	Observed,
	Changed,
	ActivationAuthorized,
}

internal readonly record struct AutocompleteScriptTransitionStabilizationSnapshot(
	string ManagedAssemblyGeneration,
	AutocompleteScriptTransitionStabilizationState State,
	long StabilizationToken,
	long HostInstanceToken,
	long ScriptTransitionId,
	AutocompleteEditorBindingCandidate? Candidate
);

internal sealed class AutocompleteScriptTransitionStabilizationCoordinator
{
	private readonly string _managedAssemblyGeneration;
	private long _stabilizationToken;
	private AutocompleteScriptTransitionStabilizationState _state =
		AutocompleteScriptTransitionStabilizationState.Idle;
	private long _hostInstanceToken;
	private long _scriptTransitionId;
	private AutocompleteEditorBindingCandidate? _candidate;

	internal AutocompleteScriptTransitionStabilizationCoordinator(
		string managedAssemblyGeneration
	)
	{
		_managedAssemblyGeneration = !string.IsNullOrWhiteSpace(managedAssemblyGeneration)
			? managedAssemblyGeneration
			: throw new ArgumentException(
				"Managed assembly generation is required.",
				nameof(managedAssemblyGeneration)
			);
	}

	internal string ManagedAssemblyGeneration => _managedAssemblyGeneration;

	internal AutocompleteScriptTransitionStabilizationSnapshot Snapshot =>
		new(
			_managedAssemblyGeneration,
			_state,
			_stabilizationToken,
			_hostInstanceToken,
			_scriptTransitionId,
			_candidate
		);

	internal bool HasPendingProcessWork =>
		_state is AutocompleteScriptTransitionStabilizationState.Observing
			or AutocompleteScriptTransitionStabilizationState.CandidateObserved;

	internal bool ArmForTransition(long hostInstanceToken, long scriptTransitionId)
	{
		if (hostInstanceToken <= 0 || scriptTransitionId <= 0)
			return false;

		if (
			_hostInstanceToken == hostInstanceToken
			&& _scriptTransitionId == scriptTransitionId
			&& (
				_state is AutocompleteScriptTransitionStabilizationState.Observing
					or AutocompleteScriptTransitionStabilizationState.CandidateObserved
					or AutocompleteScriptTransitionStabilizationState.ActivationPending
			)
		)
		{
			return true;
		}

		AdvanceStabilizationToken();
		_hostInstanceToken = hostInstanceToken;
		_scriptTransitionId = scriptTransitionId;
		_candidate = null;
		_state = AutocompleteScriptTransitionStabilizationState.Observing;
		return true;
	}

	internal AutocompleteScriptTransitionCandidateUpdateKind ObserveCandidate(
		AutocompleteEditorBindingCandidate candidate
	)
	{
		candidate = candidate.Normalized();
		if (!IsCandidateForCurrentContext(candidate))
			return AutocompleteScriptTransitionCandidateUpdateKind.None;

		if (_state == AutocompleteScriptTransitionStabilizationState.Observing)
		{
			_candidate = candidate;
			_state = AutocompleteScriptTransitionStabilizationState.CandidateObserved;
			return AutocompleteScriptTransitionCandidateUpdateKind.Observed;
		}

		if (_state != AutocompleteScriptTransitionStabilizationState.CandidateObserved)
			return AutocompleteScriptTransitionCandidateUpdateKind.None;

		if (!_candidate.HasValue || !_candidate.Value.AuthorityEquals(candidate))
		{
			_candidate = candidate;
			return AutocompleteScriptTransitionCandidateUpdateKind.Changed;
		}

		_candidate = candidate;
		_state = AutocompleteScriptTransitionStabilizationState.ActivationPending;
		return AutocompleteScriptTransitionCandidateUpdateKind.ActivationAuthorized;
	}

	internal bool TryGetActivationAuthority(
		string managedAssemblyGeneration,
		long hostInstanceToken,
		long scriptTransitionId,
		out AutocompleteEditorBindingCandidate candidate
	)
	{
		candidate = default;
		if (
			_state != AutocompleteScriptTransitionStabilizationState.ActivationPending
			|| !_candidate.HasValue
			|| !string.Equals(
				managedAssemblyGeneration,
				_managedAssemblyGeneration,
				StringComparison.Ordinal
			)
			|| hostInstanceToken != _hostInstanceToken
			|| scriptTransitionId != _scriptTransitionId
		)
		{
			return false;
		}

		AutocompleteEditorBindingCandidate currentCandidate = _candidate.Value.Normalized();
		if (
			!currentCandidate.IsValid
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

		candidate = currentCandidate;
		return true;
	}

	internal bool CompleteActivation(
		string managedAssemblyGeneration,
		long hostInstanceToken,
		long scriptTransitionId,
		AutocompleteEditorBindingCandidate candidate
	)
	{
		if (
			!TryGetActivationAuthority(
				managedAssemblyGeneration,
				hostInstanceToken,
				scriptTransitionId,
				out AutocompleteEditorBindingCandidate currentCandidate
			)
			|| !currentCandidate.AuthorityEquals(candidate)
		)
		{
			return false;
		}

		_candidate = null;
		_state = AutocompleteScriptTransitionStabilizationState.Idle;
		return true;
	}

	internal void RejectActivationAndRestart()
	{
		AdvanceStabilizationToken();
		_candidate = null;
		_state = _hostInstanceToken > 0 && _scriptTransitionId > 0
			? AutocompleteScriptTransitionStabilizationState.Observing
			: AutocompleteScriptTransitionStabilizationState.Idle;
	}

	internal void Invalidate()
	{
		AdvanceStabilizationToken();
		_state = AutocompleteScriptTransitionStabilizationState.Idle;
		_hostInstanceToken = 0;
		_scriptTransitionId = 0;
		_candidate = null;
	}

	private bool IsCandidateForCurrentContext(
		AutocompleteEditorBindingCandidate candidate
	)
	{
		return candidate.IsValid
			&& string.Equals(
				candidate.ManagedAssemblyGeneration,
				_managedAssemblyGeneration,
				StringComparison.Ordinal
			)
			&& candidate.HostInstanceToken == _hostInstanceToken
			&& candidate.ScriptTransitionId == _scriptTransitionId;
	}

	private void AdvanceStabilizationToken()
	{
		unchecked
		{
			_stabilizationToken++;
			if (_stabilizationToken <= 0)
				_stabilizationToken = 1;
		}
	}
}
#endif
