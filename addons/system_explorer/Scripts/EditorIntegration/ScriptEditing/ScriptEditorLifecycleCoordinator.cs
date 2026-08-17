#if TOOLS
using System;

namespace SystemExplorer.EditorIntegration.ScriptEditing;

internal enum ScriptEditorLifecycleState
{
	Detached,
	ScriptTransitionPending,
	BindingPending,
	Stable,
}

internal enum ScriptEditorTransitionOrigin
{
	SystemExplorerNavigation,
	ObservedExternalScriptChange,
	LifecycleRecovery,
}

internal enum ScriptEditorTransitionObservationDisposition
{
	None,
	ExpectedTransitionObserved,
	BindingPendingSameTargetRetained,
	ObservedTargetBeganTransition,
	SupersededByObservedTarget,
}

internal readonly record struct ScriptEditorTransition(
	string ManagedAssemblyGeneration,
	long TransitionId,
	ScriptEditorTransitionOrigin Origin,
	string ExpectedScriptPath
);

internal readonly record struct ScriptEditorBindingIdentity(
	ulong ScriptEditorInstanceId,
	ulong ScriptEditorBaseInstanceId,
	ulong CodeEditInstanceId,
	string ScriptResourcePath
);

internal readonly record struct EditorBindingReservation(
	string ManagedAssemblyGeneration,
	long HostInstanceToken,
	long ScriptTransitionId,
	long ReloadReadyEpoch,
	long BindingEpoch,
	ulong ScriptEditorInstanceId,
	ulong ScriptEditorBaseInstanceId,
	ulong CodeEditInstanceId,
	string ScriptResourcePath
);

internal readonly record struct EditorBindingLease(
	string ManagedAssemblyGeneration,
	long HostInstanceToken,
	long ScriptTransitionId,
	long ReloadReadyEpoch,
	long BindingEpoch,
	ulong ScriptEditorInstanceId,
	ulong ScriptEditorBaseInstanceId,
	ulong CodeEditInstanceId,
	string ScriptResourcePath
);

internal readonly record struct ScriptEditorLifecycleSnapshot(
	string ManagedAssemblyGeneration,
	ScriptEditorLifecycleState State,
	long ScriptTransitionId,
	ScriptEditorTransitionOrigin? TransitionOrigin,
	string ExpectedScriptPath,
	string ObservedScriptPath,
	long BindingEpoch,
	long ReloadReadyEpoch,
	long HostInstanceToken,
	ulong ScriptEditorInstanceId,
	ulong ScriptEditorBaseInstanceId,
	ulong CodeEditInstanceId,
	string BoundScriptResourcePath
);

internal readonly record struct ScriptEditorTransitionUpdate(
	ScriptEditorTransition Transition,
	long SupersededTransitionId,
	long SupersededBindingEpoch,
	bool BeganNewTransition,
	ScriptEditorTransitionObservationDisposition ObservationDisposition
);

internal sealed class ScriptEditorLifecycleCoordinator
{
	private readonly string _managedAssemblyGeneration;
	private long _nextTransitionId;
	private long _nextBindingEpoch;
	private ScriptEditorLifecycleState _state = ScriptEditorLifecycleState.Detached;
	private ScriptEditorTransition? _currentTransition;
	private string _observedScriptPath = "";
	private EditorBindingLease? _currentBindingLease;
	private EditorBindingReservation? _pendingBindingReservation;

	internal ScriptEditorLifecycleCoordinator(string managedAssemblyGeneration)
	{
		_managedAssemblyGeneration = !string.IsNullOrWhiteSpace(managedAssemblyGeneration)
			? managedAssemblyGeneration
			: throw new ArgumentException(
				"Managed assembly generation is required.",
				nameof(managedAssemblyGeneration)
			);
	}

	internal string ManagedAssemblyGeneration => _managedAssemblyGeneration;

	internal ScriptEditorLifecycleSnapshot Snapshot => CreateSnapshot();

	internal ScriptEditorTransitionUpdate BeginTransition(
		ScriptEditorTransitionOrigin origin,
		string expectedScriptPath
	)
	{
		long supersededTransitionId = _currentTransition?.TransitionId ?? 0;
		long supersededBindingEpoch = _currentBindingLease?.BindingEpoch ?? 0;
		ScriptEditorTransition transition = new(
			_managedAssemblyGeneration,
			NextPositive(ref _nextTransitionId),
			origin,
			ScriptPathUtility.Normalize(expectedScriptPath)
		);

		_currentTransition = transition;
		_observedScriptPath = "";
		_currentBindingLease = null;
		_pendingBindingReservation = null;
		_state = ScriptEditorLifecycleState.ScriptTransitionPending;

		return new ScriptEditorTransitionUpdate(
			transition,
			supersededTransitionId,
			supersededBindingEpoch,
			BeganNewTransition: true,
			ObservationDisposition: ScriptEditorTransitionObservationDisposition.None
		);
	}

	internal ScriptEditorTransitionUpdate ObserveScriptChange(string observedScriptPath)
	{
		string normalizedObservedPath = ScriptPathUtility.Normalize(observedScriptPath);

		if (
			_currentTransition.HasValue
			&& TryObserveExpectedSystemExplorerTransitionCore(
				_currentTransition.Value.TransitionId,
				normalizedObservedPath,
				out ScriptEditorTransitionUpdate expectedTransitionUpdate
			)
		)
		{
			return expectedTransitionUpdate;
		}

		if (_currentTransition.HasValue && _state == ScriptEditorLifecycleState.BindingPending)
		{
			string authoritativePath = GetCurrentAuthoritativeScriptPath();
			if (
				!string.IsNullOrWhiteSpace(authoritativePath)
				&& !string.IsNullOrWhiteSpace(normalizedObservedPath)
				&& PathsEqual(authoritativePath, normalizedObservedPath)
			)
			{
				_observedScriptPath = normalizedObservedPath;
				_currentBindingLease = null;
				_state = ScriptEditorLifecycleState.BindingPending;
				return new ScriptEditorTransitionUpdate(
					_currentTransition.Value,
					0,
					0,
					BeganNewTransition: false,
					ObservationDisposition: ScriptEditorTransitionObservationDisposition.BindingPendingSameTargetRetained
				);
			}
		}

		ScriptEditorTransitionUpdate update = BeginTransition(
			ScriptEditorTransitionOrigin.ObservedExternalScriptChange,
			normalizedObservedPath
		);
		_observedScriptPath = normalizedObservedPath;
		_state = ScriptEditorLifecycleState.BindingPending;
		ScriptEditorTransitionObservationDisposition disposition =
			update.SupersededTransitionId > 0 || update.SupersededBindingEpoch > 0
				? ScriptEditorTransitionObservationDisposition.SupersededByObservedTarget
				: ScriptEditorTransitionObservationDisposition.ObservedTargetBeganTransition;
		return new ScriptEditorTransitionUpdate(
			update.Transition,
			update.SupersededTransitionId,
			update.SupersededBindingEpoch,
			update.BeganNewTransition,
			disposition
		);
	}

	internal bool TryObserveExpectedSystemExplorerTransition(
		long transitionId,
		string observedScriptPath,
		out ScriptEditorTransitionUpdate update
	)
	{
		string normalizedObservedPath = ScriptPathUtility.Normalize(observedScriptPath);
		return TryObserveExpectedSystemExplorerTransitionCore(
			transitionId,
			normalizedObservedPath,
			out update
		);
	}

	private bool TryObserveExpectedSystemExplorerTransitionCore(
		long transitionId,
		string normalizedObservedPath,
		out ScriptEditorTransitionUpdate update
	)
	{
		update = default;
		if (
			!IsCurrentTransition(transitionId)
			|| !_currentTransition.HasValue
			|| _currentTransition.Value.Origin
				!= ScriptEditorTransitionOrigin.SystemExplorerNavigation
			|| _state != ScriptEditorLifecycleState.ScriptTransitionPending
			|| string.IsNullOrWhiteSpace(_currentTransition.Value.ExpectedScriptPath)
			|| string.IsNullOrWhiteSpace(normalizedObservedPath)
			|| !PathsEqual(
				_currentTransition.Value.ExpectedScriptPath,
				normalizedObservedPath
			)
		)
		{
			return false;
		}

		_observedScriptPath = normalizedObservedPath;
		_currentBindingLease = null;
		_pendingBindingReservation = null;
		_state = ScriptEditorLifecycleState.BindingPending;
		update = new ScriptEditorTransitionUpdate(
			_currentTransition.Value,
			0,
			0,
			BeganNewTransition: false,
			ObservationDisposition: ScriptEditorTransitionObservationDisposition.ExpectedTransitionObserved
		);
		return true;
	}

	internal bool MarkBindingPending(long transitionId)
	{
		if (!IsCurrentTransition(transitionId))
			return false;

		if (_state == ScriptEditorLifecycleState.Detached)
			return false;

		_currentBindingLease = null;
		_pendingBindingReservation = null;
		_state = ScriptEditorLifecycleState.BindingPending;
		return true;
	}

	internal bool CanResolveBinding(
		string managedAssemblyGeneration,
		long transitionId
	)
	{
		return string.Equals(
				managedAssemblyGeneration,
				_managedAssemblyGeneration,
				StringComparison.Ordinal
			)
			&& IsCurrentTransition(transitionId)
			&& _state == ScriptEditorLifecycleState.BindingPending;
	}

	internal bool TryReserveBinding(
		string managedAssemblyGeneration,
		long hostInstanceToken,
		long transitionId,
		long reloadReadyEpoch,
		ScriptEditorBindingIdentity identity,
		out EditorBindingReservation reservation
	)
	{
		reservation = default;
		if (_pendingBindingReservation.HasValue)
			return false;
		if (!CanResolveBinding(managedAssemblyGeneration, transitionId))
			return false;
		if (hostInstanceToken <= 0 || reloadReadyEpoch <= 0)
			return false;
		if (
			identity.ScriptEditorInstanceId == 0
			|| identity.ScriptEditorBaseInstanceId == 0
			|| identity.CodeEditInstanceId == 0
		)
		{
			return false;
		}

		string normalizedScriptPath = ScriptPathUtility.Normalize(identity.ScriptResourcePath);
		if (string.IsNullOrWhiteSpace(normalizedScriptPath))
			return false;

		string authoritativePath = GetCurrentAuthoritativeScriptPath();
		if (
			!string.IsNullOrWhiteSpace(authoritativePath)
			&& !PathsEqual(authoritativePath, normalizedScriptPath)
		)
		{
			return false;
		}

		reservation = new EditorBindingReservation(
			_managedAssemblyGeneration,
			hostInstanceToken,
			transitionId,
			reloadReadyEpoch,
			NextPositive(ref _nextBindingEpoch),
			identity.ScriptEditorInstanceId,
			identity.ScriptEditorBaseInstanceId,
			identity.CodeEditInstanceId,
			normalizedScriptPath
		);
		_pendingBindingReservation = reservation;
		return true;
	}

	internal bool TryCommitReservedBinding(
		EditorBindingReservation reservation,
		out EditorBindingLease lease
	)
	{
		lease = default;
		if (!_pendingBindingReservation.HasValue)
			return false;
		if (!_pendingBindingReservation.Value.Equals(reservation))
			return false;
		if (!CanResolveBinding(reservation.ManagedAssemblyGeneration, reservation.ScriptTransitionId))
			return false;
		if (
			reservation.HostInstanceToken <= 0
			|| reservation.ReloadReadyEpoch <= 0
			|| reservation.BindingEpoch <= 0
			|| reservation.ScriptEditorInstanceId == 0
			|| reservation.ScriptEditorBaseInstanceId == 0
			|| reservation.CodeEditInstanceId == 0
		)
		{
			return false;
		}

		string normalizedScriptPath = ScriptPathUtility.Normalize(reservation.ScriptResourcePath);
		if (string.IsNullOrWhiteSpace(normalizedScriptPath))
			return false;
		if (!string.Equals(normalizedScriptPath, reservation.ScriptResourcePath, StringComparison.Ordinal))
			return false;

		string authoritativePath = GetCurrentAuthoritativeScriptPath();
		if (
			!string.IsNullOrWhiteSpace(authoritativePath)
			&& !PathsEqual(authoritativePath, normalizedScriptPath)
		)
		{
			return false;
		}

		lease = new EditorBindingLease(
			reservation.ManagedAssemblyGeneration,
			reservation.HostInstanceToken,
			reservation.ScriptTransitionId,
			reservation.ReloadReadyEpoch,
			reservation.BindingEpoch,
			reservation.ScriptEditorInstanceId,
			reservation.ScriptEditorBaseInstanceId,
			reservation.CodeEditInstanceId,
			normalizedScriptPath
		);
		_currentBindingLease = lease;
		_pendingBindingReservation = null;
		_state = ScriptEditorLifecycleState.Stable;
		return true;
	}

	internal void AbandonBindingReservation(EditorBindingReservation reservation)
	{
		if (
			_pendingBindingReservation.HasValue
			&& _pendingBindingReservation.Value.Equals(reservation)
		)
		{
			_pendingBindingReservation = null;
		}
	}

	internal bool TryCompleteWithoutBinding(
		string managedAssemblyGeneration,
		long transitionId,
		string resolvedScriptPath
	)
	{
		if (!CanResolveBinding(managedAssemblyGeneration, transitionId))
			return false;

		string normalizedResolvedPath = ScriptPathUtility.Normalize(resolvedScriptPath);
		string authoritativePath = GetCurrentAuthoritativeScriptPath();
		if (
			!string.IsNullOrWhiteSpace(authoritativePath)
			&& !string.IsNullOrWhiteSpace(normalizedResolvedPath)
			&& !PathsEqual(authoritativePath, normalizedResolvedPath)
		)
		{
			return false;
		}

		_currentBindingLease = null;
		_pendingBindingReservation = null;
		_state = ScriptEditorLifecycleState.Stable;
		return true;
	}

	internal bool TryGetCurrentTransition(out ScriptEditorTransition transition)
	{
		if (_currentTransition.HasValue)
		{
			transition = _currentTransition.Value;
			return true;
		}

		transition = default;
		return false;
	}

	internal bool TryGetCurrentBindingLease(out EditorBindingLease lease)
	{
		if (_state == ScriptEditorLifecycleState.Stable && _currentBindingLease.HasValue)
		{
			lease = _currentBindingLease.Value;
			return true;
		}

		lease = default;
		return false;
	}

	internal bool IsCurrentStableBinding(EditorBindingLease lease)
	{
		return _state == ScriptEditorLifecycleState.Stable
			&& _currentBindingLease.HasValue
			&& _currentBindingLease.Value.Equals(lease)
			&& string.Equals(
				lease.ManagedAssemblyGeneration,
				_managedAssemblyGeneration,
				StringComparison.Ordinal
			)
			&& IsCurrentTransition(lease.ScriptTransitionId);
	}

	internal ScriptEditorLifecycleSnapshot Invalidate()
	{
		ScriptEditorLifecycleSnapshot previous = CreateSnapshot();
		_currentTransition = null;
		_observedScriptPath = "";
		_currentBindingLease = null;
		_pendingBindingReservation = null;
		_state = ScriptEditorLifecycleState.Detached;
		return previous;
	}

	private string GetCurrentAuthoritativeScriptPath()
	{
		if (!_currentTransition.HasValue)
			return "";

		string normalizedObservedPath = ScriptPathUtility.Normalize(_observedScriptPath);
		if (!string.IsNullOrWhiteSpace(normalizedObservedPath))
			return normalizedObservedPath;

		return ScriptPathUtility.Normalize(_currentTransition.Value.ExpectedScriptPath);
	}

	private bool IsCurrentTransition(long transitionId)
	{
		return transitionId > 0
			&& _currentTransition.HasValue
			&& _currentTransition.Value.TransitionId == transitionId
			&& string.Equals(
				_currentTransition.Value.ManagedAssemblyGeneration,
				_managedAssemblyGeneration,
				StringComparison.Ordinal
			);
	}

	private ScriptEditorLifecycleSnapshot CreateSnapshot()
	{
		ScriptEditorTransition? transition = _currentTransition;
		EditorBindingLease? binding = _currentBindingLease;
		return new ScriptEditorLifecycleSnapshot(
			_managedAssemblyGeneration,
			_state,
			transition?.TransitionId ?? 0,
			transition?.Origin,
			transition?.ExpectedScriptPath ?? "",
			_observedScriptPath ?? "",
			binding?.BindingEpoch ?? 0,
			binding?.ReloadReadyEpoch ?? 0,
			binding?.HostInstanceToken ?? 0,
			binding?.ScriptEditorInstanceId ?? 0,
			binding?.ScriptEditorBaseInstanceId ?? 0,
			binding?.CodeEditInstanceId ?? 0,
			binding?.ScriptResourcePath ?? ""
		);
	}

	private static bool PathsEqual(string left, string right)
	{
		return string.Equals(
			ScriptPathUtility.Normalize(left),
			ScriptPathUtility.Normalize(right),
			StringComparison.OrdinalIgnoreCase
		);
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
