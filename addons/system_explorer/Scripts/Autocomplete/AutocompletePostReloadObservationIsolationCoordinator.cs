#if TOOLS
using System;
using SystemExplorer.EditorIntegration.ScriptEditing;

namespace SystemExplorer.Autocomplete;

internal enum AutocompletePostReloadObservationIsolationState
{
	Idle,
	Observing,
	TargetObserved,
	ActivationPending,
}

internal enum AutocompletePostReloadObservationIsolationKind
{
	None,
	Reload,
	ScriptTransition,
}

internal enum AutocompletePostReloadObservationIsolationUpdateKind
{
	None,
	Observed,
	ActivationAuthorized,
}

internal readonly record struct AutocompletePostReloadObservationIsolationSnapshot(
	string ManagedAssemblyGeneration,
	AutocompletePostReloadObservationIsolationState State,
	long StabilizationToken,
	long HostInstanceToken,
	long ScriptTransitionId,
	string ScriptResourcePath,
	AutocompletePostReloadObservationIsolationKind Kind
);

internal sealed class AutocompletePostReloadObservationIsolationCoordinator
{
	private readonly string _managedAssemblyGeneration;
	private long _stabilizationToken;
	private AutocompletePostReloadObservationIsolationState _state =
		AutocompletePostReloadObservationIsolationState.Idle;
	private long _hostInstanceToken;
	private long _scriptTransitionId;
	private string _scriptResourcePath = "";
	private AutocompletePostReloadObservationIsolationKind _kind =
		AutocompletePostReloadObservationIsolationKind.None;

	internal AutocompletePostReloadObservationIsolationCoordinator(
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

	internal AutocompletePostReloadObservationIsolationSnapshot Snapshot =>
		new(
			_managedAssemblyGeneration,
			_state,
			_stabilizationToken,
			_hostInstanceToken,
			_scriptTransitionId,
			_scriptResourcePath,
			_kind
		);

	internal bool HasPendingProcessWork =>
		_state is AutocompletePostReloadObservationIsolationState.Observing
			or AutocompletePostReloadObservationIsolationState.TargetObserved;

	internal bool ArmForTransition(
		long hostInstanceToken,
		long scriptTransitionId,
		string scriptResourcePath,
		AutocompletePostReloadObservationIsolationKind kind
	)
	{
		string normalizedPath = ScriptPathUtility.Normalize(scriptResourcePath);
		if (!IsValidTarget(hostInstanceToken, scriptTransitionId, normalizedPath, kind))
			return false;

		if (AuthorityEquals(hostInstanceToken, scriptTransitionId, normalizedPath, kind))
			return true;

		AdvanceStabilizationToken();
		_hostInstanceToken = hostInstanceToken;
		_scriptTransitionId = scriptTransitionId;
		_scriptResourcePath = normalizedPath;
		_kind = kind;
		_state = AutocompletePostReloadObservationIsolationState.Observing;
		return true;
	}

	internal AutocompletePostReloadObservationIsolationUpdateKind ObserveTarget(
		string managedAssemblyGeneration,
		long hostInstanceToken,
		long scriptTransitionId,
		string scriptResourcePath,
		AutocompletePostReloadObservationIsolationKind kind
	)
	{
		if (
			!string.Equals(
				managedAssemblyGeneration,
				_managedAssemblyGeneration,
				StringComparison.Ordinal
			)
		)
		{
			return AutocompletePostReloadObservationIsolationUpdateKind.None;
		}

		string normalizedPath = ScriptPathUtility.Normalize(scriptResourcePath);
		if (!IsValidTarget(hostInstanceToken, scriptTransitionId, normalizedPath, kind))
			return AutocompletePostReloadObservationIsolationUpdateKind.None;

		if (!AuthorityEquals(hostInstanceToken, scriptTransitionId, normalizedPath, kind))
		{
			ArmForTransition(hostInstanceToken, scriptTransitionId, normalizedPath, kind);
		}

		switch (_state)
		{
			case AutocompletePostReloadObservationIsolationState.Observing:
				_state = AutocompletePostReloadObservationIsolationState.TargetObserved;
				return AutocompletePostReloadObservationIsolationUpdateKind.Observed;

			case AutocompletePostReloadObservationIsolationState.TargetObserved:
				_state = AutocompletePostReloadObservationIsolationState.ActivationPending;
				return AutocompletePostReloadObservationIsolationUpdateKind.ActivationAuthorized;

			default:
				return AutocompletePostReloadObservationIsolationUpdateKind.None;
		}
	}

	internal bool TryGetActivationAuthority(
		string managedAssemblyGeneration,
		long hostInstanceToken,
		long scriptTransitionId,
		string scriptResourcePath,
		AutocompletePostReloadObservationIsolationKind kind
	)
	{
		string normalizedPath = ScriptPathUtility.Normalize(scriptResourcePath);
		return _state == AutocompletePostReloadObservationIsolationState.ActivationPending
			&& string.Equals(
				managedAssemblyGeneration,
				_managedAssemblyGeneration,
				StringComparison.Ordinal
			)
			&& AuthorityEquals(
				hostInstanceToken,
				scriptTransitionId,
				normalizedPath,
				kind
			);
	}

	internal bool CompleteActivation(
		string managedAssemblyGeneration,
		long hostInstanceToken,
		long scriptTransitionId,
		string scriptResourcePath,
		AutocompletePostReloadObservationIsolationKind kind
	)
	{
		if (
			!TryGetActivationAuthority(
				managedAssemblyGeneration,
				hostInstanceToken,
				scriptTransitionId,
				scriptResourcePath,
				kind
			)
		)
		{
			return false;
		}

		ClearAuthority(advanceToken: false);
		return true;
	}

	internal bool RejectActivationAndRestart()
	{
		if (
			_state != AutocompletePostReloadObservationIsolationState.ActivationPending
			|| !HasValidCurrentAuthority()
		)
		{
			return false;
		}

		AdvanceStabilizationToken();
		_state = AutocompletePostReloadObservationIsolationState.Observing;
		return true;
	}

	internal void Invalidate()
	{
		ClearAuthority(advanceToken: true);
	}

	private bool AuthorityEquals(
		long hostInstanceToken,
		long scriptTransitionId,
		string normalizedScriptResourcePath,
		AutocompletePostReloadObservationIsolationKind kind
	)
	{
		return _state != AutocompletePostReloadObservationIsolationState.Idle
			&& _hostInstanceToken == hostInstanceToken
			&& _scriptTransitionId == scriptTransitionId
			&& _kind == kind
			&& string.Equals(
				_scriptResourcePath,
				normalizedScriptResourcePath,
				StringComparison.OrdinalIgnoreCase
			);
	}

	private bool HasValidCurrentAuthority()
	{
		return IsValidTarget(
			_hostInstanceToken,
			_scriptTransitionId,
			_scriptResourcePath,
			_kind
		);
	}

	private static bool IsValidTarget(
		long hostInstanceToken,
		long scriptTransitionId,
		string normalizedScriptResourcePath,
		AutocompletePostReloadObservationIsolationKind kind
	)
	{
		return hostInstanceToken > 0
			&& scriptTransitionId > 0
			&& kind
				is AutocompletePostReloadObservationIsolationKind.Reload
					or AutocompletePostReloadObservationIsolationKind.ScriptTransition
			&& !string.IsNullOrWhiteSpace(normalizedScriptResourcePath)
			&& normalizedScriptResourcePath.EndsWith(
				".cs",
				StringComparison.OrdinalIgnoreCase
			);
	}

	private void ClearAuthority(bool advanceToken)
	{
		if (advanceToken)
			AdvanceStabilizationToken();

		_state = AutocompletePostReloadObservationIsolationState.Idle;
		_hostInstanceToken = 0;
		_scriptTransitionId = 0;
		_scriptResourcePath = "";
		_kind = AutocompletePostReloadObservationIsolationKind.None;
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
