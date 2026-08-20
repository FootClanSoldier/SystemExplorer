#if TOOLS
using System;

namespace SystemExplorer.Autocomplete;

internal enum AutocompleteScriptTransitionStabilizationState
{
	Idle,
	Quiescing,
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
	bool NavigationQuietPeriodRequired,
	double QuietElapsedSeconds,
	int CoalescedNavigationTransitionCount,
	AutocompleteEditorBindingCandidate? Candidate
);

internal sealed class AutocompleteScriptTransitionStabilizationCoordinator
{
	internal const double NavigationQuietPeriodSeconds = 0.200;
	internal const int NavigationQuietPeriodMilliseconds = 200;

	private readonly string _managedAssemblyGeneration;
	private long _stabilizationToken;
	private AutocompleteScriptTransitionStabilizationState _state =
		AutocompleteScriptTransitionStabilizationState.Idle;
	private long _hostInstanceToken;
	private long _scriptTransitionId;
	private bool _navigationQuietPeriodRequired;
	private double _quietElapsedSeconds;
	private bool _discardNextEligibleDelta;
	private int _coalescedNavigationTransitionCount;
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
			_navigationQuietPeriodRequired,
			_quietElapsedSeconds,
			_coalescedNavigationTransitionCount,
			_candidate
		);

	internal bool HasPendingProcessWork =>
		_state is AutocompleteScriptTransitionStabilizationState.Quiescing
			or AutocompleteScriptTransitionStabilizationState.Observing
			or AutocompleteScriptTransitionStabilizationState.CandidateObserved;

	internal bool ArmForTransition(
		long hostInstanceToken,
		long scriptTransitionId,
		bool requireNavigationQuietPeriod
	)
	{
		if (hostInstanceToken <= 0 || scriptTransitionId <= 0)
			return false;

		if (
			_hostInstanceToken == hostInstanceToken
			&& _scriptTransitionId == scriptTransitionId
			&& (
				_state is AutocompleteScriptTransitionStabilizationState.Quiescing
					or AutocompleteScriptTransitionStabilizationState.Observing
					or AutocompleteScriptTransitionStabilizationState.CandidateObserved
					or AutocompleteScriptTransitionStabilizationState.ActivationPending
			)
		)
		{
			return _navigationQuietPeriodRequired == requireNavigationQuietPeriod;
		}

		bool coalescesNavigationTransition =
			requireNavigationQuietPeriod
			&& _navigationQuietPeriodRequired
			&& _state != AutocompleteScriptTransitionStabilizationState.Idle
			&& _hostInstanceToken == hostInstanceToken;

		if (coalescesNavigationTransition)
		{
			if (_coalescedNavigationTransitionCount < int.MaxValue)
				_coalescedNavigationTransitionCount++;
		}
		else
		{
			_coalescedNavigationTransitionCount = 0;
		}

		AdvanceStabilizationToken();
		_hostInstanceToken = hostInstanceToken;
		_scriptTransitionId = scriptTransitionId;
		_navigationQuietPeriodRequired = requireNavigationQuietPeriod;
		_quietElapsedSeconds = 0;
		_discardNextEligibleDelta = requireNavigationQuietPeriod;
		_candidate = null;
		_state = requireNavigationQuietPeriod
			? AutocompleteScriptTransitionStabilizationState.Quiescing
			: AutocompleteScriptTransitionStabilizationState.Observing;
		return true;
	}

	internal bool TryAdvanceNavigationQuietPeriod(
		double delta,
		bool admissionAllowed
	)
	{
		if (!_navigationQuietPeriodRequired)
			return _state != AutocompleteScriptTransitionStabilizationState.Idle;

		if (!admissionAllowed)
		{
			RestartQuietWindowAfterBarrier();
			return false;
		}

		if (_state != AutocompleteScriptTransitionStabilizationState.Quiescing)
			return true;

		if (_discardNextEligibleDelta)
		{
			_discardNextEligibleDelta = false;
			_quietElapsedSeconds = 0;
			return false;
		}

		if (double.IsNaN(delta) || double.IsInfinity(delta) || delta < 0)
			delta = 0;

		_quietElapsedSeconds += delta;
		if (_quietElapsedSeconds < NavigationQuietPeriodSeconds)
			return false;

		_state = AutocompleteScriptTransitionStabilizationState.Observing;
		return true;
	}

	internal void RestartQuietWindowAfterBarrier()
	{
		_candidate = null;
		_quietElapsedSeconds = 0;

		if (
			_navigationQuietPeriodRequired
			&& _hostInstanceToken > 0
			&& _scriptTransitionId > 0
		)
		{
			_state = AutocompleteScriptTransitionStabilizationState.Quiescing;
			_discardNextEligibleDelta = true;
			return;
		}

		_discardNextEligibleDelta = false;
		_state = _hostInstanceToken > 0 && _scriptTransitionId > 0
			? AutocompleteScriptTransitionStabilizationState.Observing
			: AutocompleteScriptTransitionStabilizationState.Idle;
	}

	internal AutocompleteScriptTransitionCandidateUpdateKind ObserveCandidate(
		AutocompleteEditorBindingCandidate candidate
	)
	{
		candidate = candidate.Normalized();
		if (!IsCandidateForCurrentContext(candidate))
			return AutocompleteScriptTransitionCandidateUpdateKind.None;

		if (_state == AutocompleteScriptTransitionStabilizationState.Quiescing)
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
			if (_navigationQuietPeriodRequired)
			{
				RestartNavigationQuietWindowForCandidateInstability();
			}
			else
			{
				_candidate = candidate;
			}

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
		_navigationQuietPeriodRequired = false;
		_quietElapsedSeconds = 0;
		_discardNextEligibleDelta = false;
		_coalescedNavigationTransitionCount = 0;
		_state = AutocompleteScriptTransitionStabilizationState.Idle;
		return true;
	}

	internal void RejectActivationAndRestart()
	{
		AdvanceStabilizationToken();
		_candidate = null;
		_quietElapsedSeconds = 0;

		if (
			_navigationQuietPeriodRequired
			&& _hostInstanceToken > 0
			&& _scriptTransitionId > 0
		)
		{
			_discardNextEligibleDelta = true;
			_state = AutocompleteScriptTransitionStabilizationState.Quiescing;
			return;
		}

		_discardNextEligibleDelta = false;
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
		_navigationQuietPeriodRequired = false;
		_quietElapsedSeconds = 0;
		_discardNextEligibleDelta = false;
		_coalescedNavigationTransitionCount = 0;
		_candidate = null;
	}

	private void RestartNavigationQuietWindowForCandidateInstability()
	{
		_candidate = null;
		_quietElapsedSeconds = 0;
		_discardNextEligibleDelta = true;
		_state = AutocompleteScriptTransitionStabilizationState.Quiescing;
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
