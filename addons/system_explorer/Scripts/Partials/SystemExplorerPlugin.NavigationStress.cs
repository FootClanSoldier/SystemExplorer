#if TOOLS
using Godot;
using System;
using System.Collections.Generic;

public partial class SystemExplorerPlugin
{
	#region Navigation Stress Diagnostics
	private const double NavigationStressIntervalSeconds = 0.075;
	private const int NavigationStressProgressInterval = 100;

	private bool _navigationStressRunning;
	private double _navigationStressAccumulatorSeconds;
	private long _navigationStressRunToken;
	private string _navigationStressRunManagedAssemblyGeneration = "";
	private int _navigationStressNextTargetIndex;
	private long _navigationStressSuccessfulActivationCount;
	private string _navigationStressLastPauseReason = "";

	private bool HasPendingNavigationStressProcessWork()
	{
		return _navigationStressRunning;
	}

	private void SynchronizeNavigationStressAfterTreeReady(string reason)
	{
		if (!DebugState || !NavigationStressEnabled)
			return;

		StartNavigationStress(reason);
	}

	private void StartNavigationStress(string reason)
	{
		if (!DebugState || !NavigationStressEnabled)
			return;

		AdvanceNavigationStressRunToken();
		_navigationStressRunning = true;
		_navigationStressAccumulatorSeconds = 0.0;
		_navigationStressRunManagedAssemblyGeneration = ManagedAssemblyGeneration;
		_navigationStressNextTargetIndex = 0;
		_navigationStressSuccessfulActivationCount = 0;
		_navigationStressLastPauseReason = "";

		int visibleScriptTargets = TryCaptureCurrentNavigationStressTargets(
			out List<ScriptTreeOccurrence> targets
		)
			? targets.Count
			: 0;

		DebugLogger.LogOperation(
			"Navigation stress started",
			$"Reason='{reason ?? ""}', RunToken='{_navigationStressRunToken}', ManagedAssemblyGeneration='{_navigationStressRunManagedAssemblyGeneration}', IntervalMs='{NavigationStressIntervalSeconds * 1000.0:0}', VisibleScriptTargets='{visibleScriptTargets}', Filtering='{_isFilteringScripts}'"
		);

		RefreshEditorPluginProcessingState();
	}

	private void StopNavigationStress(string reason, bool refreshProcessingState = true)
	{
		long stoppedRunToken = _navigationStressRunToken;
		string stoppedGeneration = _navigationStressRunManagedAssemblyGeneration;
		long stoppedActivations = _navigationStressSuccessfulActivationCount;
		bool wasRunning = _navigationStressRunning;

		AdvanceNavigationStressRunToken();
		_navigationStressRunning = false;
		_navigationStressAccumulatorSeconds = 0.0;
		_navigationStressRunManagedAssemblyGeneration = "";
		_navigationStressNextTargetIndex = 0;
		_navigationStressSuccessfulActivationCount = 0;
		_navigationStressLastPauseReason = "";

		if (wasRunning)
		{
			DebugLogger.LogOperation(
				"Navigation stress stopped",
				$"Reason='{reason ?? ""}', RunToken='{stoppedRunToken}', ManagedAssemblyGeneration='{stoppedGeneration}', Activations='{stoppedActivations}'"
			);
		}

		if (refreshProcessingState)
			RefreshEditorPluginProcessingState();
	}

	private void ResetNavigationStressTransientStateAfterManagedAssemblyReload()
	{
		StopNavigationStress(
			"ManagedAssemblyReload",
			refreshProcessingState: false
		);
	}

	private void ShutdownNavigationStress()
	{
		StopNavigationStress("ExitTree", refreshProcessingState: false);
	}

	private void FailNavigationStress(Exception exception)
	{
		long failedRunToken = _navigationStressRunToken;
		string failedGeneration = _navigationStressRunManagedAssemblyGeneration;
		long failedActivations = _navigationStressSuccessfulActivationCount;

		AdvanceNavigationStressRunToken();
		_navigationStressRunning = false;
		_navigationStressAccumulatorSeconds = 0.0;
		_navigationStressRunManagedAssemblyGeneration = "";
		_navigationStressNextTargetIndex = 0;
		_navigationStressSuccessfulActivationCount = 0;
		_navigationStressLastPauseReason = "";

		DebugLogger.LogOperation(
			"Navigation stress failed",
			$"RunToken='{failedRunToken}', ManagedAssemblyGeneration='{failedGeneration}', Activations='{failedActivations}', Exception='{exception}'"
		);
	}

	private void ProcessNavigationStress(double delta)
	{
		if (!_navigationStressRunning)
			return;

		if (!DebugState || !NavigationStressEnabled)
		{
			StopNavigationStress("Disabled");
			return;
		}

		if (
			!string.Equals(
				_navigationStressRunManagedAssemblyGeneration,
				ManagedAssemblyGeneration,
				StringComparison.Ordinal
			)
		)
		{
			StopNavigationStress("StaleManagedAssemblyGeneration");
			return;
		}

		if (delta > 0.0)
			_navigationStressAccumulatorSeconds += delta;

		if (_navigationStressAccumulatorSeconds < NavigationStressIntervalSeconds)
			return;

		// Never catch up after a long frame. One frame can execute at most one selection,
		// and the next interval starts from this frame.
		_navigationStressAccumulatorSeconds = 0.0;
		TryExecuteNavigationStressStep();
	}

	private void TryExecuteNavigationStressStep()
	{
		long runToken = _navigationStressRunToken;
		string runGeneration = _navigationStressRunManagedAssemblyGeneration;

		if (!IsNavigationStressRunCurrent(runToken, runGeneration))
			return;

		if (!EnsureManagedAssemblyStateCurrent("Navigation Stress"))
		{
			SetNavigationStressPauseReason("ManagedStateUnavailable");
			return;
		}

		// Recovery may have reset and rearmed the harness. The process step that entered
		// the recovery boundary must never continue with its pre-recovery authority.
		if (!IsNavigationStressRunCurrent(runToken, runGeneration))
			return;

		if (!TryCaptureCurrentNavigationStressTargets(out List<ScriptTreeOccurrence> targets))
		{
			SetNavigationStressPauseReason("TreeUnavailable");
			return;
		}

		if (targets.Count < 2)
		{
			SetNavigationStressPauseReason("InsufficientVisibleScriptTargets");
			return;
		}

		ClearNavigationStressPauseReason();

		ScriptTreeOccurrence? selectedOccurrence = TryGetScriptTreeOccurrenceFromTreeItem(
			_tree.GetSelected(),
			out ScriptTreeOccurrence currentSelection
		)
			? currentSelection
			: null;

		int startIndex = NormalizeNavigationStressTargetIndex(
			_navigationStressNextTargetIndex,
			targets.Count
		);

		for (int offset = 0; offset < targets.Count; offset++)
		{
			int candidateIndex = (startIndex + offset) % targets.Count;
			ScriptTreeOccurrence candidate = targets[candidateIndex];

			if (
				selectedOccurrence.HasValue
				&& IsSameScriptTreeOccurrence(selectedOccurrence.Value, candidate)
			)
			{
				continue;
			}

			if (!TryFindScriptTreeItemByOccurrence(candidate, out TreeItem targetItem))
				continue;

			if (
				targetItem == null
				|| !GodotObject.IsInstanceValid(targetItem)
				|| _tree == null
				|| !GodotObject.IsInstanceValid(_tree)
				|| _tree.GetSelected() == targetItem
			)
			{
				continue;
			}

			_navigationStressNextTargetIndex = (candidateIndex + 1) % targets.Count;
			targetItem.Select(0);
			_navigationStressSuccessfulActivationCount++;

			if (
				_navigationStressSuccessfulActivationCount
					% NavigationStressProgressInterval
				== 0
			)
			{
				DebugLogger.LogOperation(
					"Navigation stress progress",
					$"RunToken='{runToken}', ManagedAssemblyGeneration='{runGeneration}', Activations='{_navigationStressSuccessfulActivationCount}', CurrentScriptPath='{candidate.ScriptPath}', Filtering='{_isFilteringScripts}'"
				);
			}

			return;
		}

		SetNavigationStressPauseReason("TreeUnavailable");
	}

	private bool TryCaptureCurrentNavigationStressTargets(
		out List<ScriptTreeOccurrence> targets
	)
	{
		targets = new List<ScriptTreeOccurrence>();

		if (_tree == null || !GodotObject.IsInstanceValid(_tree))
			return false;

		TreeItem root = _tree.GetRoot();
		if (root == null || !GodotObject.IsInstanceValid(root))
			return false;

		TreeItem current = root.GetFirstChild();
		while (current != null)
		{
			if (!GodotObject.IsInstanceValid(current))
				return false;

			string metadata = current.GetMetadata(0).AsString();
			if (
				metadata.StartsWith("script::", StringComparison.Ordinal)
				&& TryGetScriptTreeOccurrenceFromTreeItem(
					current,
					out ScriptTreeOccurrence occurrence
				)
			)
			{
				targets.Add(occurrence);
			}

			current = current.GetNextVisible(false);
		}

		return true;
	}

	private bool IsNavigationStressRunCurrent(long runToken, string runGeneration)
	{
		return _navigationStressRunning
			&& runToken == _navigationStressRunToken
			&& string.Equals(
				runGeneration,
				_navigationStressRunManagedAssemblyGeneration,
				StringComparison.Ordinal
			)
			&& string.Equals(
				runGeneration,
				ManagedAssemblyGeneration,
				StringComparison.Ordinal
			)
			&& DebugState
			&& NavigationStressEnabled;
	}

	private void SetNavigationStressPauseReason(string pauseReason)
	{
		pauseReason ??= "";

		if (string.Equals(_navigationStressLastPauseReason, pauseReason, StringComparison.Ordinal))
			return;

		_navigationStressLastPauseReason = pauseReason;
		DebugLogger.LogOperation(
			"Navigation stress paused",
			$"Reason='{pauseReason}', RunToken='{_navigationStressRunToken}', ManagedAssemblyGeneration='{_navigationStressRunManagedAssemblyGeneration}', Activations='{_navigationStressSuccessfulActivationCount}'"
		);
	}

	private void ClearNavigationStressPauseReason()
	{
		if (string.IsNullOrEmpty(_navigationStressLastPauseReason))
			return;

		string previousPauseReason = _navigationStressLastPauseReason;
		_navigationStressLastPauseReason = "";
		DebugLogger.LogOperation(
			"Navigation stress resumed",
			$"PreviousPauseReason='{previousPauseReason}', RunToken='{_navigationStressRunToken}', ManagedAssemblyGeneration='{_navigationStressRunManagedAssemblyGeneration}', Activations='{_navigationStressSuccessfulActivationCount}'"
		);
	}

	private long AdvanceNavigationStressRunToken()
	{
		unchecked
		{
			_navigationStressRunToken++;
			if (_navigationStressRunToken <= 0)
				_navigationStressRunToken = 1;
		}

		return _navigationStressRunToken;
	}

	private static int NormalizeNavigationStressTargetIndex(int index, int count)
	{
		if (count <= 0)
			return 0;

		if (index < 0)
			return 0;

		return index % count;
	}
	#endregion
}
#endif
