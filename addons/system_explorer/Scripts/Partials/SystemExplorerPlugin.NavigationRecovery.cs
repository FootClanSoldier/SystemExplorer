#if TOOLS
using Godot;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

public partial class SystemExplorerPlugin
{
	#region Tree Item Navigation and Missing Script Recovery
	private void OnItemSelected()
	{
		TreeItem selectedItem = _tree.GetSelected();

		if (_isFilteringScripts && IsScriptOrSceneItem(selectedItem))
			_selectedScriptEntryFromFilter = GetEntryFromMetadata(
				selectedItem.GetMetadata(0).AsString()
			);

		UpdateTreeLockIconVisibility();

		if (_isRestoringOrRebuildingPersistentTreeState)
			return;

		UpdatePersistentTreeSelectionFromTreeItem(selectedItem);

		if (
			_isSyncingTreeSelectionToActiveScript || _suppressNextTreeNavigationFromScriptEditorSync
		)
		{
			_suppressNextTreeNavigationFromScriptEditorSync = false;
			return;
		}

		OpenScriptFromTreeItem(selectedItem);
		OpenSceneFromTreeItem(selectedItem);
	}

	private void OpenScriptFromTreeItem(TreeItem item)
	{
		if (item == null)
			return;

		string metadata = item.GetMetadata(0).AsString();

		if (!metadata.StartsWith("script::"))
			return;

		string entry = GetEntryFromMetadata(metadata);
		string scriptPath = GetScriptPathFromEntry(entry);
		ScriptTreeOccurrence? sourceOccurrence = TryGetScriptTreeOccurrenceFromTreeItem(
			item,
			out ScriptTreeOccurrence occurrence
		)
			? occurrence
			: null;

		OpenScriptOrMissingDialog(entry, scriptPath, sourceOccurrence);
	}

	private void OpenSceneFromTreeItem(TreeItem item)
	{
		if (item == null)
			return;

		string metadata = item.GetMetadata(0).AsString();

		if (!metadata.StartsWith("sceneLink::"))
			return;

		string entry = metadata.Substring("sceneLink::".Length);
		string scenePath = GetScenePathFromEntry(entry);

		if (!FileAccess.FileExists(scenePath))
		{
			OpenMissingSceneDialog(entry, scenePath);
			return;
		}

		EditorInterface.Singleton.OpenSceneFromPath(scenePath);
		CallDeferred(nameof(ReleaseTreeFocusAfterNavigation));
	}

	private void OpenScriptOrMissingDialog(
		string entry,
		string scriptPath,
		ScriptTreeOccurrence? sourceOccurrence = null
	)
	{
		ClearPendingSystemExplorerScriptActivation();

		if (!FileAccess.FileExists(scriptPath))
		{
			OpenMissingScriptDialog(entry, scriptPath);
			return;
		}

		Script script = ResourceLoader.Load<Script>(scriptPath);

		if (script == null)
		{
			OpenMissingScriptDialog(entry, scriptPath);
			return;
		}

		OpenScriptFromSystemExplorer(
			script,
			scriptPath,
			sourceOccurrence: sourceOccurrence
		);
	}

	private void ReleaseTreeFocusAfterNavigation()
	{
		if (
			_focusReleaseTarget == null
			|| !GodotObject.IsInstanceValid(_focusReleaseTarget)
			|| !_focusReleaseTarget.IsInsideTree()
		)
		{
			return;
		}

		// Preserve System Explorer's shortcut and keyboard-navigation context
		// without giving the tree a visual focus frame.
		_focusReleaseTarget.GrabFocus();
	}

	private void OpenMissingScriptDialog(string entry, string scriptPath)
	{
		_pendingMissingScriptEntry = entry;
		_pendingMissingScriptPath = scriptPath;

		_missingScriptDialog.DialogText =
			$"The selected script could not be found:\n{scriptPath}\n\nIt may have been moved, renamed, or deleted outside System Explorer.";

		_missingScriptDialog.PopupCentered();
		CallDeferred(nameof(ReleaseMissingDialogFocus));
	}

	private void ReleaseMissingDialogFocus()
	{
		ReleaseDialogOkButtonFocus(_missingScriptDialog);
	}

	private void OnMissingScriptRelinkPressed()
	{
		if (string.IsNullOrWhiteSpace(_pendingMissingScriptEntry))
			return;

		_relinkScriptDialog.PopupCenteredRatio(0.8f);
	}

	private void OnMissingScriptCustomAction(StringName action)
	{
		if (action != "remove_from_plugin")
			return;

		RemoveMissingScriptFromPlugin();
	}

	private void RemoveMissingScriptFromPlugin()
	{
		if (string.IsNullOrWhiteSpace(_pendingMissingScriptEntry))
			return;

		using TreeOperationDialogScope operationScope = BeginTreeOperationDialogScope(
			"Remove Failed",
			CloseMissingScriptRecoveryUiAfterFailure
		);

		string missingEntry = _pendingMissingScriptEntry;

		if (!EnsureSystemsLoadedForTreeOperation("Remove Missing Script"))
			return;

		if (
			!EnsureEntryAvailableForReversibleMutation(
				missingEntry,
				"Remove Missing Script"
			)
		)
		{
			ReportTreeOperationFailure(
				"System Explorer could not remove the missing script reference from the tree.",
				missingEntry
			);
			return;
		}

		string selectedScriptEntryFromFilterBeforeMutation =
			_selectedScriptEntryFromFilter;
		SystemsAndFolderBindingsSnapshot snapshot =
			CaptureSystemsAndFolderBindingsSnapshot();

		if (!RemoveEntry(missingEntry))
		{
			if (!HasActiveTreeOperationFailure)
			{
				ReportTreeOperationFailure(
					"System Explorer could not remove the missing script reference from the tree.",
					missingEntry
				);
			}

			DebugLogger.LogOperation(
				"Remove Missing Script cancelled: mutation failed",
				missingEntry
			);
			return;
		}

		if (
			!TryPersistReversibleSystemsAndFolderBindingsMutation(
				snapshot,
				systemsChanged: true,
				folderBindingsChanged: false,
				operationName: "Remove Missing Script"
			)
		)
		{
			_selectedScriptEntryFromFilter =
				selectedScriptEntryFromFilterBeforeMutation;
			return;
		}

		HideTreeOperationOriginWindow(_missingScriptDialog);
		HideTreeOperationOriginWindow(_relinkScriptDialog);
		ClearMissingScriptState();
		BuildTree();
	}

	private void OnRelinkScriptFileSelected(string newScriptPath)
	{
		if (string.IsNullOrWhiteSpace(_pendingMissingScriptEntry))
			return;

		using TreeOperationDialogScope operationScope = BeginTreeOperationDialogScope(
			"Relink Script Failed",
			CloseMissingScriptRecoveryUiAfterFailure
		);

		string oldEntry = _pendingMissingScriptEntry;

		if (!EnsureSystemsLoadedForTreeOperation("Relink Missing Script"))
			return;

		if (
			!EnsureEntryAvailableForReversibleMutation(
				oldEntry,
				"Relink Missing Script"
			)
		)
		{
			ReportTreeOperationFailure(
				"System Explorer could not update the missing script reference.",
				oldEntry
			);
			return;
		}

		string folderPath = GetFolderPathFromEntry(oldEntry);
		string linkedScenePath = GetLinkedScenePathFromEntry(oldEntry);
		string newEntry = BuildScriptEntry(
			folderPath,
			newScriptPath,
			linkedScenePath,
			IsEntryLocked(oldEntry)
		);
		string selectedScriptEntryFromFilterBeforeMutation =
			_selectedScriptEntryFromFilter;
		SystemsAndFolderBindingsSnapshot snapshot =
			CaptureSystemsAndFolderBindingsSnapshot();

		if (!ReplaceEntry(oldEntry, newEntry))
		{
			if (!HasActiveTreeOperationFailure)
			{
				ReportTreeOperationFailure(
					"System Explorer could not update the missing script reference.",
					$"{oldEntry} -> {newEntry}"
				);
			}

			DebugLogger.LogOperation(
				"Relink Script cancelled: mutation failed",
				$"{oldEntry} -> {newEntry}"
			);
			return;
		}

		if (
			!TryPersistReversibleSystemsAndFolderBindingsMutation(
				snapshot,
				systemsChanged: true,
				folderBindingsChanged: false,
				operationName: "Relink Missing Script"
			)
		)
		{
			_selectedScriptEntryFromFilter =
				selectedScriptEntryFromFilterBeforeMutation;
			return;
		}

		HideTreeOperationOriginWindow(_missingScriptDialog);
		HideTreeOperationOriginWindow(_relinkScriptDialog);
		ClearMissingScriptState();
		BuildTree();
		OpenScriptOrMissingDialog(newEntry, newScriptPath);
	}

	private bool ReplaceEntryInSystem(
		string systemName,
		string oldEntry,
		string newEntry,
		string operationName
	)
	{
		if (string.IsNullOrWhiteSpace(systemName))
			return false;

		if (!EnsureSystemsLoadedForTreeOperation(operationName))
			return false;

		if (!TryReplaceEntryInSystem(systemName, oldEntry, newEntry, operationName))
		{
			if (!TryRecoverSystemsFromDisk(operationName))
				return false;

			return TryReplaceEntryInSystem(
				systemName,
				oldEntry,
				newEntry,
				$"{operationName} After Recovery"
			);
		}

		return true;
	}

	private bool TryReplaceEntryInSystem(
		string systemName,
		string oldEntry,
		string newEntry,
		string operationName
	)
	{
		if (!EnsureSystemAvailable(systemName, operationName))
			return false;

		List<string> entries = _systems[systemName];
		int index = FindEntryIndex(entries, oldEntry);

		if (index < 0)
			return false;

		entries.RemoveAt(index);

		if (!entries.Contains(newEntry))
			entries.Insert(index, newEntry);

		UpdateSelectedScriptEntryFromFilter(oldEntry, newEntry);

		DebugLogger.LogOperation(operationName, $"{systemName}: {oldEntry} -> {newEntry}");

		return true;
	}

	private bool ReplaceEntry(string oldEntry, string newEntry)
	{
		if (!EnsureSystemsLoadedForTreeOperation("Replace Entry"))
			return false;

		foreach (string systemName in _systems.Keys.ToList())
		{
			List<string> entries = _systems[systemName];
			int index = entries.IndexOf(oldEntry);

			if (index < 0 && oldEntry.StartsWith("folder::"))
			{
				string oldFolderPath = GetFolderPathFromFolderEntry(oldEntry);
				index = entries.FindIndex(entry =>
					entry.StartsWith("folder::")
					&& GetFolderPathFromFolderEntry(entry) == oldFolderPath
				);
			}

			if (index < 0)
				continue;

			entries.RemoveAt(index);

			if (!entries.Contains(newEntry))
				entries.Insert(index, newEntry);

			UpdateSelectedScriptEntryFromFilter(oldEntry, newEntry);

			DebugLogger.LogOperation("Relink Script Mutated", $"{oldEntry} -> {newEntry}");

			return true;
		}

		if (TryRecoverSystemsFromDisk("Relink Script"))
		{
			foreach (string systemName in _systems.Keys.ToList())
			{
				List<string> entries = _systems[systemName];
				int index = entries.IndexOf(oldEntry);

				if (index < 0 && oldEntry.StartsWith("folder::"))
				{
					string oldFolderPath = GetFolderPathFromFolderEntry(oldEntry);
					index = entries.FindIndex(entry =>
						entry.StartsWith("folder::")
						&& GetFolderPathFromFolderEntry(entry) == oldFolderPath
					);
				}

				if (index < 0)
					continue;

				entries.RemoveAt(index);

				if (!entries.Contains(newEntry))
					entries.Insert(index, newEntry);

				UpdateSelectedScriptEntryFromFilter(oldEntry, newEntry);

				DebugLogger.LogOperation(
					"Relink Script Mutated After Recovery",
					$"{oldEntry} -> {newEntry}"
				);

				return true;
			}
		}

		return false;
	}

	private void ClearMissingScriptState()
	{
		_pendingMissingScriptEntry = "";
		_pendingMissingScriptPath = "";
	}

	private void PrintScriptCreationDebugInfo(string path, string systemName, string folderPath)
	{
		DebugLogger.Log("=== Script Creation Debug ===");
		DebugLogger.Log($"Path: {path}");
		DebugLogger.Log($"Selected System: '{systemName}'");
		DebugLogger.Log($"Selected Folder: '{folderPath}'");
		DebugLogger.Log($"Systems Count: {_systems.Count}");

		TreeItem selectedItem = _tree.GetSelected();

		if (selectedItem != null)
		{
			DebugLogger.Log($"Selected Text: '{selectedItem.GetText(0)}'");
			DebugLogger.Log($"Selected Metadata: '{selectedItem.GetMetadata(0).AsString()}'");
		}
		else
		{
			DebugLogger.Log("Selected Item: <null>");
		}

		DebugLogSystems("Script Creation Systems Snapshot");
		DebugLogger.Log("=============================");
	}

	#endregion
}
#endif
