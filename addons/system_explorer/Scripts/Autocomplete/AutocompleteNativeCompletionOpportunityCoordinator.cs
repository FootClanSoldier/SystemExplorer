#if TOOLS
using System;
using SystemExplorer.EditorIntegration.ScriptEditing;

namespace SystemExplorer.Autocomplete;

internal readonly record struct AutocompleteNativeCompletionSettingsSnapshot(
	bool AutomaticCompletionEnabled,
	double DelaySeconds
);

internal sealed class AutocompleteNativeCompletionOpportunityCoordinator
{
	internal const double DefaultDelaySeconds = 0.300;

	private enum OpportunityState
	{
		None,
		Waiting,
		DeadlineReached,
		Released,
	}

	private OpportunityState _state;
	private EditorBindingLease _bindingLease;
	private long _textChangedObservationSequence;
	private AutocompleteNativeCompletionSettingsSnapshot _settings;
	private double _elapsedSeconds;
	private bool _discardNextEligibleDelta;

	internal bool HasPendingProcessWork =>
		_state == OpportunityState.Waiting || _state == OpportunityState.DeadlineReached;

	internal EditorBindingLease CurrentBindingLease => _bindingLease;
	internal long CurrentTextChangedObservationSequence => _textChangedObservationSequence;
	internal double ConfiguredDelaySeconds => _settings.DelaySeconds;

	internal void Arm(
		EditorBindingLease bindingLease,
		long textChangedObservationSequence,
		AutocompleteNativeCompletionSettingsSnapshot settings
	)
	{
		if (
			textChangedObservationSequence <= 0
			|| bindingLease.BindingEpoch <= 0
			|| bindingLease.CodeEditInstanceId == 0
		)
		{
			Clear();
			return;
		}

		double normalizedDelaySeconds = NormalizeDelaySeconds(settings.DelaySeconds);
		_settings = new AutocompleteNativeCompletionSettingsSnapshot(
			settings.AutomaticCompletionEnabled,
			normalizedDelaySeconds
		);
		_bindingLease = bindingLease;
		_textChangedObservationSequence = textChangedObservationSequence;
		_elapsedSeconds = 0;
		_discardNextEligibleDelta = settings.AutomaticCompletionEnabled;
		_state = settings.AutomaticCompletionEnabled
			? OpportunityState.Waiting
			: OpportunityState.Released;
	}

	internal bool TryAdvance(double delta, out double releasedElapsedSeconds)
	{
		releasedElapsedSeconds = 0;

		if (_state == OpportunityState.DeadlineReached)
		{
			_state = OpportunityState.Released;
			releasedElapsedSeconds = _elapsedSeconds;
			return true;
		}

		if (_state != OpportunityState.Waiting)
			return false;

		if (_discardNextEligibleDelta)
		{
			_discardNextEligibleDelta = false;
			_elapsedSeconds = 0;
			return false;
		}

		if (double.IsNaN(delta) || double.IsInfinity(delta) || delta < 0)
			delta = 0;

		_elapsedSeconds += delta;
		if (_elapsedSeconds < _settings.DelaySeconds)
			return false;

		_state = OpportunityState.DeadlineReached;
		return false;
	}

	internal bool ObserveParentlessCompletionRequest(
		EditorBindingLease bindingLease,
		long textChangedObservationSequence
	)
	{
		if (
			textChangedObservationSequence <= 0
			|| !_bindingLease.Equals(bindingLease)
			|| _textChangedObservationSequence != textChangedObservationSequence
			|| (
				_state != OpportunityState.Waiting
				&& _state != OpportunityState.DeadlineReached
			)
		)
		{
			return false;
		}

		_state = OpportunityState.Released;
		_discardNextEligibleDelta = false;
		return true;
	}

	internal bool IsForcedMemberFollowUpAllowed(
		EditorBindingLease bindingLease,
		long textChangedObservationSequence
	)
	{
		if (textChangedObservationSequence <= 0)
			return true;

		if (
			_textChangedObservationSequence != textChangedObservationSequence
			|| !_bindingLease.Equals(bindingLease)
		)
		{
			return true;
		}

		return _state != OpportunityState.Waiting
			&& _state != OpportunityState.DeadlineReached;
	}

	internal void Clear()
	{
		_state = OpportunityState.None;
		_bindingLease = default;
		_textChangedObservationSequence = 0;
		_settings = default;
		_elapsedSeconds = 0;
		_discardNextEligibleDelta = false;
	}

	private static double NormalizeDelaySeconds(double delaySeconds)
	{
		return double.IsNaN(delaySeconds)
			|| double.IsInfinity(delaySeconds)
			|| delaySeconds < 0
			? DefaultDelaySeconds
			: delaySeconds;
	}
}
#endif
