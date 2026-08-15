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

internal readonly record struct EditorBindingLease(
	string ManagedAssemblyGeneration,
	long HostInstanceToken,
	long ScriptTransitionId,
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
			&& _currentTransition.Value.Origin
				== ScriptEditorTransitionOrigin.SystemExplorerNavigation
			&& _state == ScriptEditorLifecycleState.ScriptTransitionPending
			&& !string.IsNullOrWhiteSpace(_currentTransition.Value.ExpectedScriptPath)
			&& !string.IsNullOrWhiteSpace(normalizedObservedPath)
			&& PathsEqual(
				_currentTransition.Value.ExpectedScriptPath,
				normalizedObservedPath
			)
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
				ObservationDisposition: ScriptEditorTransitionObservationDisposition.ExpectedTransitionObserved
			);
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

	internal bool MarkBindingPending(long transitionId)
	{
		if (!IsCurrentTransition(transitionId))
			return false;

		if (_state == ScriptEditorLifecycleState.Detached)
			return false;

		_currentBindingLease = null;
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

	internal bool TryCommitBinding(
		string managedAssemblyGeneration,
		long hostInstanceToken,
		long transitionId,
		ScriptEditorBindingIdentity identity,
		out EditorBindingLease lease
	)
	{
		lease = default;
		if (!CanResolveBinding(managedAssemblyGeneration, transitionId))
			return false;
		if (hostInstanceToken <= 0)
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

		lease = new EditorBindingLease(
			_managedAssemblyGeneration,
			hostInstanceToken,
			transitionId,
			NextPositive(ref _nextBindingEpoch),
			identity.ScriptEditorInstanceId,
			identity.ScriptEditorBaseInstanceId,
			identity.CodeEditInstanceId,
			normalizedScriptPath
		);
		_currentBindingLease = lease;
		_state = ScriptEditorLifecycleState.Stable;
		return true;
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
