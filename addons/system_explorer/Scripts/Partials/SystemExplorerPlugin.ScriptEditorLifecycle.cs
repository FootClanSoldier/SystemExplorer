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
	private bool _scriptEditorLifecycleAssemblyUnloadRegistered;

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
		if (_scriptEditorLifecycleAssemblyUnloadRegistered)
			return;

		AssemblyLoadContext loadContext = AssemblyLoadContext.GetLoadContext(
			typeof(SystemExplorerPlugin).Assembly
		);
		if (loadContext == null)
			return;

		_scriptEditorLifecycleAssemblyLoadContext = loadContext;
		loadContext.Unloading += OnScriptEditorLifecycleAssemblyUnloading;
		_scriptEditorLifecycleAssemblyUnloadRegistered = true;
	}

	private void OnScriptEditorLifecycleAssemblyUnloading(AssemblyLoadContext context)
	{
		_scriptEditorLifecycleCoordinator?.Invalidate();
	}

	private void ShutdownScriptEditorLifecycleAssemblyUnloadRegistration()
	{
		if (
			_scriptEditorLifecycleAssemblyUnloadRegistered
			&& _scriptEditorLifecycleAssemblyLoadContext != null
		)
		{
			_scriptEditorLifecycleAssemblyLoadContext.Unloading -=
				OnScriptEditorLifecycleAssemblyUnloading;
		}

		_scriptEditorLifecycleAssemblyUnloadRegistered = false;
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
		LogScriptEditorLifecycleSupersededIfNeeded(update, "SystemExplorerNavigation");
		LogScriptEditorLifecycle(
			"ScriptEditor lifecycle transition begun",
			$"Reason='SystemExplorerNavigation', {DescribeScriptEditorTransition(update.Transition)}"
		);
		return update.Transition;
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
		LogScriptEditorLifecycleSupersededIfNeeded(update, "ObservedScriptChange");
		LogScriptEditorLifecycle(
			"ScriptEditor lifecycle script change observed",
			$"Callback='{callbackName ?? ""}', Disposition='{update.ObservationDisposition}', ObservedScriptPath='{observedPath}', BeganNewTransition='{update.BeganNewTransition}', {DescribeScriptEditorTransition(update.Transition)}"
		);
		LogScriptEditorLifecycle(
			"ScriptEditor lifecycle binding pending",
			DescribeScriptEditorLifecycleSnapshot(ScriptEditorLifecycleCoordinator.Snapshot)
		);
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
			LogScriptEditorLifecycleSupersededIfNeeded(update, origin);
			LogScriptEditorLifecycle(
				"ScriptEditor lifecycle transition begun",
				$"Reason='{origin ?? ""}', {DescribeScriptEditorTransition(update.Transition)}"
			);
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
			LogScriptEditorLifecycleSupersededIfNeeded(update, origin);
			LogScriptEditorLifecycle(
				"ScriptEditor lifecycle transition begun",
				$"Reason='{origin ?? ""}', {DescribeScriptEditorTransition(update.Transition)}"
			);
		}
		else if (snapshot.State == ScriptEditorLifecycleState.Detached)
		{
			ScriptEditorTransitionUpdate update = coordinator.BeginTransition(
				ScriptEditorTransitionOrigin.LifecycleRecovery,
				""
			);
			coordinator.MarkBindingPending(update.Transition.TransitionId);
			LogScriptEditorLifecycleSupersededIfNeeded(update, origin);
			LogScriptEditorLifecycle(
				"ScriptEditor lifecycle transition begun",
				$"Reason='{origin ?? ""}', {DescribeScriptEditorTransition(update.Transition)}"
			);
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

		return ScriptEditorLifecycleCoordinator.TryGetCurrentBindingLease(
			out EditorBindingLease lease
		)
			&& lease.HostInstanceToken == _autocompleteHostInstanceToken
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

	private void LogScriptEditorLifecycleSupersededIfNeeded(
		ScriptEditorTransitionUpdate update,
		string reason
	)
	{
		if (update.SupersededTransitionId <= 0 && update.SupersededBindingEpoch <= 0)
			return;

		LogScriptEditorLifecycle(
			"ScriptEditor lifecycle transition superseded",
			$"Reason='{reason ?? ""}', Disposition='{update.ObservationDisposition}', SupersededScriptTransitionId='{update.SupersededTransitionId}', SupersededBindingEpoch='{update.SupersededBindingEpoch}', CurrentScriptTransitionId='{update.Transition.TransitionId}', ManagedAssemblyGeneration='{update.Transition.ManagedAssemblyGeneration}'"
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

	private string DescribeScriptEditorTransition(ScriptEditorTransition transition)
	{
		ScriptEditorLifecycleState state = ScriptEditorLifecycleCoordinator.Snapshot.State;
		return
			$"ManagedAssemblyGeneration='{transition.ManagedAssemblyGeneration}', LifecycleState='{state}', ScriptTransitionId='{transition.TransitionId}', TransitionOrigin='{transition.Origin}', ExpectedScriptPath='{transition.ExpectedScriptPath}'";
	}

	private string DescribeScriptEditorLifecycleForDiagnostics()
	{
		if (_scriptEditorLifecycleCoordinator == null)
		{
			return
				$"LifecycleState='{ScriptEditorLifecycleState.Detached}', ScriptTransitionId='0', TransitionOrigin='<none>', ExpectedScriptPath='', ObservedScriptPath='', BindingEpoch='0', BindingHostInstanceToken='0', BindingScriptEditorInstanceId='0', BindingScriptEditorBaseInstanceId='0', BindingCodeEditInstanceId='0', BindingScriptResourcePath=''";
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
			$"ManagedAssemblyGeneration='{snapshot.ManagedAssemblyGeneration}', LifecycleState='{snapshot.State}', ScriptTransitionId='{snapshot.ScriptTransitionId}', TransitionOrigin='{snapshot.TransitionOrigin?.ToString() ?? "<none>"}', ExpectedScriptPath='{snapshot.ExpectedScriptPath}', ObservedScriptPath='{snapshot.ObservedScriptPath}', BindingEpoch='{snapshot.BindingEpoch}', BindingHostInstanceToken='{snapshot.HostInstanceToken}', BindingScriptEditorInstanceId='{snapshot.ScriptEditorInstanceId}', BindingScriptEditorBaseInstanceId='{snapshot.ScriptEditorBaseInstanceId}', BindingCodeEditInstanceId='{snapshot.CodeEditInstanceId}', BindingScriptResourcePath='{snapshot.BoundScriptResourcePath}'";
	}

	#endregion
}
#endif
