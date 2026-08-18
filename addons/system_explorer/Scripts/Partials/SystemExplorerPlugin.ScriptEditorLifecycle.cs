#if TOOLS
using Godot;
using System;
using System.Runtime.Loader;
using SystemExplorer.EditorIntegration.ScriptEditing;

public partial class SystemExplorerPlugin
{
	#region ScriptEditor Lifecycle Coordinator

	private ScriptEditorLifecycleCoordinator _scriptEditorLifecycleCoordinator;
	private AssemblyLoadContext _scriptEditorLifecycleAssemblyLoadContext;

	private ScriptEditorLifecycleCoordinator ScriptEditorLifecycleCoordinator
	{
		get
		{
			if (
				_scriptEditorLifecycleCoordinator == null
				|| !string.Equals(
					_scriptEditorLifecycleCoordinator.ManagedAssemblyGeneration,
					ManagedAssemblyGeneration,
					StringComparison.Ordinal
				)
			)
			{
				_scriptEditorLifecycleCoordinator = new ScriptEditorLifecycleCoordinator(
					ManagedAssemblyGeneration
				);
			}

			EnsureScriptEditorLifecycleAssemblyUnloadRegistration();

			return _scriptEditorLifecycleCoordinator;
		}
	}

	private void EnsureScriptEditorLifecycleAssemblyUnloadRegistration()
	{
		AssemblyLoadContext loadContext = AssemblyLoadContext.GetLoadContext(
			typeof(SystemExplorerPlugin).Assembly
		);
		if (loadContext == null)
			return;

		if (object.ReferenceEquals(_scriptEditorLifecycleAssemblyLoadContext, loadContext))
			return;

		if (_scriptEditorLifecycleAssemblyLoadContext != null)
		{
			_scriptEditorLifecycleAssemblyLoadContext.Unloading -=
				OnScriptEditorLifecycleAssemblyUnloading;
		}

		loadContext.Unloading += OnScriptEditorLifecycleAssemblyUnloading;
		_scriptEditorLifecycleAssemblyLoadContext = loadContext;
	}

	private void OnScriptEditorLifecycleAssemblyUnloading(AssemblyLoadContext context)
	{
		_scriptEditorLifecycleCoordinator?.Invalidate();
	}

	private void ShutdownScriptEditorLifecycleAssemblyUnloadRegistration()
	{
		if (_scriptEditorLifecycleAssemblyLoadContext != null)
		{
			_scriptEditorLifecycleAssemblyLoadContext.Unloading -=
				OnScriptEditorLifecycleAssemblyUnloading;
		}

		_scriptEditorLifecycleAssemblyLoadContext = null;
	}

	private ScriptEditorTransition BeginSystemExplorerScriptEditorTransition(
		string expectedScriptPath
	)
	{
		ScriptEditorTransitionUpdate update = ScriptEditorLifecycleCoordinator.BeginTransition(
			ScriptEditorTransitionOrigin.SystemExplorerNavigation,
			expectedScriptPath
		);
		return update.Transition;
	}

	private void QueueDeferredSystemExplorerSameScriptTransitionObservation(
		ScriptEditorTransition transition,
		string expectedScriptPath
	)
	{
		string normalizedExpectedPath = ScriptPathUtility.Normalize(expectedScriptPath);
		if (
			transition.TransitionId <= 0
			|| string.IsNullOrWhiteSpace(transition.ManagedAssemblyGeneration)
			|| string.IsNullOrWhiteSpace(normalizedExpectedPath)
		)
		{
			return;
		}

		CallDeferred(
			nameof(ApplyDeferredSystemExplorerSameScriptTransitionObservation),
			transition.ManagedAssemblyGeneration,
			transition.TransitionId,
			normalizedExpectedPath
		);
	}

	private void ApplyDeferredSystemExplorerSameScriptTransitionObservation(
		string scheduledManagedAssemblyGeneration,
		long scheduledScriptTransitionId,
		string scheduledExpectedScriptPath
	)
	{
		string normalizedExpectedPath = ScriptPathUtility.Normalize(
			scheduledExpectedScriptPath
		);
		if (
			!string.Equals(
				scheduledManagedAssemblyGeneration,
				ManagedAssemblyGeneration,
				StringComparison.Ordinal
			)
			|| scheduledScriptTransitionId <= 0
			|| string.IsNullOrWhiteSpace(normalizedExpectedPath)
			|| !IsAutocompletePluginBoundaryAvailable()
		)
		{
			return;
		}

		ScriptEditorLifecycleCoordinator coordinator = ScriptEditorLifecycleCoordinator;
		ScriptEditorLifecycleSnapshot snapshot = coordinator.Snapshot;
		if (
			!IsExactSystemExplorerSameScriptTransitionObservationTarget(
				snapshot,
				scheduledManagedAssemblyGeneration,
				scheduledScriptTransitionId,
				normalizedExpectedPath
			)
		)
		{
			return;
		}

		if (!TryGetActiveScriptPath(out string activeScriptPath))
			return;

		string normalizedActiveScriptPath = ScriptPathUtility.Normalize(activeScriptPath);
		if (
			!string.Equals(
				normalizedActiveScriptPath,
				normalizedExpectedPath,
				StringComparison.OrdinalIgnoreCase
			)
		)
		{
			return;
		}

		if (
			!string.Equals(
				scheduledManagedAssemblyGeneration,
				ManagedAssemblyGeneration,
				StringComparison.Ordinal
			)
		)
		{
			return;
		}

		ScriptEditorLifecycleSnapshot revalidatedSnapshot = coordinator.Snapshot;
		if (
			!IsExactSystemExplorerSameScriptTransitionObservationTarget(
				revalidatedSnapshot,
				scheduledManagedAssemblyGeneration,
				scheduledScriptTransitionId,
				normalizedExpectedPath
			)
		)
		{
			return;
		}

		if (revalidatedSnapshot.State == ScriptEditorLifecycleState.ScriptTransitionPending)
		{
			if (
				!coordinator.TryObserveExpectedSystemExplorerTransition(
					scheduledScriptTransitionId,
					normalizedActiveScriptPath,
					out _
				)
			)
			{
				return;
			}
		}

		ScriptEditorLifecycleSnapshot bindingPendingSnapshot = coordinator.Snapshot;
		if (
			!IsExactSystemExplorerSameScriptTransitionObservationTarget(
				bindingPendingSnapshot,
				scheduledManagedAssemblyGeneration,
				scheduledScriptTransitionId,
				normalizedExpectedPath
			)
			|| bindingPendingSnapshot.State != ScriptEditorLifecycleState.BindingPending
		)
		{
			return;
		}

		QueueDeferredAutocompleteScriptChangeRebind(
			"SystemExplorerSameScriptPostEditObservation"
		);
	}

	private static bool IsExactSystemExplorerSameScriptTransitionObservationTarget(
		ScriptEditorLifecycleSnapshot snapshot,
		string scheduledManagedAssemblyGeneration,
		long scheduledScriptTransitionId,
		string normalizedExpectedPath
	)
	{
		if (
			!string.Equals(
				snapshot.ManagedAssemblyGeneration,
				scheduledManagedAssemblyGeneration,
				StringComparison.Ordinal
			)
			|| snapshot.ScriptTransitionId != scheduledScriptTransitionId
			|| snapshot.TransitionOrigin
				!= ScriptEditorTransitionOrigin.SystemExplorerNavigation
			|| !string.Equals(
				ScriptPathUtility.Normalize(snapshot.ExpectedScriptPath),
				normalizedExpectedPath,
				StringComparison.OrdinalIgnoreCase
			)
		)
		{
			return false;
		}

		if (
			snapshot.State != ScriptEditorLifecycleState.ScriptTransitionPending
			&& snapshot.State != ScriptEditorLifecycleState.BindingPending
		)
		{
			return false;
		}

		if (snapshot.State == ScriptEditorLifecycleState.BindingPending)
		{
			string authoritativePath = !string.IsNullOrWhiteSpace(snapshot.ObservedScriptPath)
				? snapshot.ObservedScriptPath
				: snapshot.ExpectedScriptPath;
			return string.Equals(
				ScriptPathUtility.Normalize(authoritativePath),
				normalizedExpectedPath,
				StringComparison.OrdinalIgnoreCase
			);
		}

		return true;
	}

	private ScriptEditorTransition ObserveScriptEditorLifecycleScriptChange(
		Script script,
		string callbackName
	)
	{
		string observedPath = "";
		try
		{
			if (script != null && GodotObject.IsInstanceValid(script))
				observedPath = ScriptPathUtility.Normalize(script.ResourcePath);
		}
		catch
		{
			observedPath = "";
		}

		return ObserveScriptEditorLifecycleScriptPath(observedPath, callbackName);
	}

	private ScriptEditorTransition ObserveScriptEditorLifecycleScriptPath(
		string observedScriptPath,
		string callbackName
	)
	{
		string observedPath = ScriptPathUtility.Normalize(observedScriptPath);
		ScriptEditorTransitionUpdate update =
			ScriptEditorLifecycleCoordinator.ObserveScriptChange(observedPath);
		return update.Transition;
	}

	private void ObservePolledScriptEditorLifecyclePathIfChanged(
		string observedScriptPath
	)
	{
		ScriptEditorLifecycleCoordinator coordinator = ScriptEditorLifecycleCoordinator;
		ScriptEditorLifecycleSnapshot snapshot = coordinator.Snapshot;
		string normalizedObservedPath = ScriptPathUtility.Normalize(observedScriptPath);
		string currentLifecyclePath = !string.IsNullOrWhiteSpace(snapshot.ObservedScriptPath)
			? snapshot.ObservedScriptPath
			: !string.IsNullOrWhiteSpace(snapshot.BoundScriptResourcePath)
				? snapshot.BoundScriptResourcePath
				: snapshot.ExpectedScriptPath;

		bool systemExplorerTransitionAwaitingObservation =
			snapshot.State == ScriptEditorLifecycleState.ScriptTransitionPending
			&& snapshot.TransitionOrigin
				== ScriptEditorTransitionOrigin.SystemExplorerNavigation;
		bool lifecyclePathChanged = !string.Equals(
			ScriptPathUtility.Normalize(currentLifecyclePath),
			normalizedObservedPath,
			StringComparison.OrdinalIgnoreCase
		);

		if (
			!systemExplorerTransitionAwaitingObservation
			&& snapshot.State != ScriptEditorLifecycleState.Detached
			&& !lifecyclePathChanged
		)
		{
			return;
		}

		ObserveScriptEditorLifecycleScriptPath(
			normalizedObservedPath,
			"ScriptEditorSyncPoll"
		);
		QueueDeferredAutocompleteScriptChangeRebind("ScriptEditorSyncPoll");
	}

	private void EnsureScriptEditorLifecycleRecoveryQueued(string origin)
	{
		ScriptEditorLifecycleCoordinator coordinator = ScriptEditorLifecycleCoordinator;
		ScriptEditorLifecycleSnapshot snapshot = coordinator.Snapshot;

		if (snapshot.State == ScriptEditorLifecycleState.Stable)
			return;

		if (snapshot.State == ScriptEditorLifecycleState.Detached)
		{
			ScriptEditorTransitionUpdate update = coordinator.BeginTransition(
				ScriptEditorTransitionOrigin.LifecycleRecovery,
				""
			);
			coordinator.MarkBindingPending(update.Transition.TransitionId);
			snapshot = coordinator.Snapshot;
		}
		else if (
			snapshot.State == ScriptEditorLifecycleState.ScriptTransitionPending
			&& snapshot.TransitionOrigin == ScriptEditorTransitionOrigin.LifecycleRecovery
		)
		{
			coordinator.MarkBindingPending(snapshot.ScriptTransitionId);
			snapshot = coordinator.Snapshot;
		}

		if (snapshot.State == ScriptEditorLifecycleState.BindingPending)
			QueueDeferredAutocompleteScriptChangeRebind(origin ?? "LifecycleRecovery");
	}

	private void RequestScriptEditorLifecycleRebind(string origin)
	{
		ScriptEditorLifecycleCoordinator coordinator = ScriptEditorLifecycleCoordinator;
		ScriptEditorLifecycleSnapshot snapshot = coordinator.Snapshot;

		if (
			snapshot.State == ScriptEditorLifecycleState.ScriptTransitionPending
			&& snapshot.TransitionOrigin == ScriptEditorTransitionOrigin.SystemExplorerNavigation
		)
		{
			return;
		}

		if (snapshot.State == ScriptEditorLifecycleState.Stable)
		{
			ScriptEditorTransitionUpdate update = coordinator.BeginTransition(
				ScriptEditorTransitionOrigin.LifecycleRecovery,
				""
			);
			coordinator.MarkBindingPending(update.Transition.TransitionId);
		}
		else if (snapshot.State == ScriptEditorLifecycleState.Detached)
		{
			ScriptEditorTransitionUpdate update = coordinator.BeginTransition(
				ScriptEditorTransitionOrigin.LifecycleRecovery,
				""
			);
			coordinator.MarkBindingPending(update.Transition.TransitionId);
		}
		else if (snapshot.State == ScriptEditorLifecycleState.ScriptTransitionPending)
		{
			coordinator.MarkBindingPending(snapshot.ScriptTransitionId);
		}

		if (coordinator.Snapshot.State == ScriptEditorLifecycleState.BindingPending)
			QueueDeferredAutocompleteScriptChangeRebind(origin ?? "LifecycleRebindIntent");
	}

	private bool IsScriptEditorLifecycleStableForCurrentAutocompleteHost()
	{
		if (_autocompleteHost == null || _autocompleteHostInstanceToken <= 0)
			return false;

		return IsAutocompleteReloadStabilizationReady()
			&& ScriptEditorLifecycleCoordinator.TryGetCurrentBindingLease(
				out EditorBindingLease lease
			)
			&& lease.HostInstanceToken == _autocompleteHostInstanceToken
			&& lease.ReloadReadyEpoch > 0
			&& lease.ReloadReadyEpoch == CurrentAutocompleteReloadReadyEpoch
			&& string.Equals(
				lease.ManagedAssemblyGeneration,
				ManagedAssemblyGeneration,
				StringComparison.Ordinal
			);
	}

	private void InvalidateScriptEditorLifecycle(string reason)
	{
		if (_scriptEditorLifecycleCoordinator == null)
			return;

		ScriptEditorLifecycleSnapshot previous = _scriptEditorLifecycleCoordinator.Invalidate();
		if (previous.State == ScriptEditorLifecycleState.Detached)
			return;

		LogScriptEditorLifecycle(
			"ScriptEditor lifecycle invalidated",
			$"Reason='{reason ?? ""}', PreviousState='{previous.State}', PreviousScriptTransitionId='{previous.ScriptTransitionId}', PreviousBindingEpoch='{previous.BindingEpoch}', ManagedAssemblyGeneration='{previous.ManagedAssemblyGeneration}'"
		);
	}

	private void LogScriptEditorLifecycle(string operation, string details)
	{
		try
		{
			DebugLogger.LogPersistentFileOnlyOperation(operation, details ?? "");
		}
		catch
		{
		}
	}

	private string DescribeScriptEditorLifecycleForDiagnostics()
	{
		if (_scriptEditorLifecycleCoordinator == null)
		{
			return
				$"LifecycleState='{ScriptEditorLifecycleState.Detached}', ScriptTransitionId='0', TransitionOrigin='<none>', ExpectedScriptPath='', ObservedScriptPath='', BindingEpoch='0', ReloadReadyEpoch='0', BindingHostInstanceToken='0', BindingScriptEditorInstanceId='0', BindingScriptEditorBaseInstanceId='0', BindingCodeEditInstanceId='0', BindingScriptResourcePath=''";
		}

		return DescribeScriptEditorLifecycleSnapshot(
			_scriptEditorLifecycleCoordinator.Snapshot
		);
	}

	private static string DescribeScriptEditorLifecycleSnapshot(
		ScriptEditorLifecycleSnapshot snapshot
	)
	{
		return
			$"ManagedAssemblyGeneration='{snapshot.ManagedAssemblyGeneration}', LifecycleState='{snapshot.State}', ScriptTransitionId='{snapshot.ScriptTransitionId}', TransitionOrigin='{snapshot.TransitionOrigin?.ToString() ?? "<none>"}', ExpectedScriptPath='{snapshot.ExpectedScriptPath}', ObservedScriptPath='{snapshot.ObservedScriptPath}', BindingEpoch='{snapshot.BindingEpoch}', ReloadReadyEpoch='{snapshot.ReloadReadyEpoch}', BindingHostInstanceToken='{snapshot.HostInstanceToken}', BindingScriptEditorInstanceId='{snapshot.ScriptEditorInstanceId}', BindingScriptEditorBaseInstanceId='{snapshot.ScriptEditorBaseInstanceId}', BindingCodeEditInstanceId='{snapshot.CodeEditInstanceId}', BindingScriptResourcePath='{snapshot.BoundScriptResourcePath}'";
	}

	#endregion
}
#endif
