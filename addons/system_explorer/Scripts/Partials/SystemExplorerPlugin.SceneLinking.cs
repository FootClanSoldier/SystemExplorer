#if TOOLS
using Godot;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

public partial class SystemExplorerPlugin
{
	#region Scene Linking
	private ScriptTreeOccurrence? _pendingSceneLinkSourceOccurrence;
	private ScriptTreeOccurrence? _pendingMissingSceneScriptOccurrence;

	private void OpenLinkSceneDialog()
	{
		if (string.IsNullOrWhiteSpace(_pendingRenameMetadata))
			return;

		if (!_pendingRenameMetadata.StartsWith("script::"))
			return;

		string entry = GetEntryFromMetadata(_pendingRenameMetadata);
		if (!IsScriptEntryValidOrOpenMissingDialog(entry))
			return;

		_pendingSceneLinkSourceOccurrence = CaptureSelectedScriptOccurrence(entry);
		_pendingSceneLinkEntry = entry;
		_linkSceneDialog.PopupCenteredRatio(0.8f);
	}

	private void OnLinkSceneFileSelected(string scenePath)
	{
		if (string.IsNullOrWhiteSpace(_pendingSceneLinkEntry))
			return;

		using TreeOperationDialogScope operationScope = BeginTreeOperationDialogScope(
			"Link Scene Failed",
			CloseLinkSceneUiAfterFailure
		);

		string entry = _pendingSceneLinkEntry;
		ScriptTreeOccurrence? sourceOccurrence = _pendingSceneLinkSourceOccurrence;

		if (!UpdateLinkedScenePath(entry, scenePath, sourceOccurrence))
			return;

		HideTreeOperationOriginWindow(_linkSceneDialog);
		_pendingSceneLinkEntry = "";
		_pendingSceneLinkSourceOccurrence = null;
	}

	private void UnlinkSceneFromPendingScript()
	{
		if (string.IsNullOrWhiteSpace(_pendingRenameMetadata))
			return;

		if (!_pendingRenameMetadata.StartsWith("script::"))
			return;

		string entry = GetEntryFromMetadata(_pendingRenameMetadata);
		ScriptTreeOccurrence? sourceOccurrence = CaptureSelectedScriptOccurrence(entry);

		using TreeOperationDialogScope operationScope =
			BeginTreeOperationDialogScope("Unlink Scene Failed");

		UpdateLinkedScenePath(entry, "", sourceOccurrence);
		_pendingSceneLinkSourceOccurrence = null;
	}

	private void OpenLinkedSceneFromTreeItem(TreeItem item)
	{
		if (item == null)
			return;

		string metadata = item.GetMetadata(0).AsString();

		if (!metadata.StartsWith("script::"))
			return;

		string entry = GetEntryFromMetadata(metadata);

		if (!IsScriptEntryValidOrOpenMissingDialog(entry))
			return;

		string scriptPath = GetScriptPathFromEntry(entry);
		string linkedScenePath = GetLinkedScenePathFromEntry(entry);
		ScriptTreeOccurrence? sourceOccurrence = TryGetScriptTreeOccurrenceFromTreeItem(
			item,
			out ScriptTreeOccurrence occurrence
		)
			? occurrence
			: null;

		if (string.IsNullOrWhiteSpace(linkedScenePath))
		{
			OpenScriptOrMissingDialog(entry, scriptPath, sourceOccurrence);
			return;
		}

		if (!FileAccess.FileExists(linkedScenePath))
		{
			OpenMissingSceneDialog(entry, linkedScenePath, sourceOccurrence);
			return;
		}

		EditorInterface.Singleton.OpenSceneFromPath(linkedScenePath);
		CallDeferred(nameof(ReleaseTreeFocusAfterNavigation));
	}

	private bool IsScriptEntryValidOrOpenMissingDialog(string entry)
	{
		string scriptPath = GetScriptPathFromEntry(entry);

		if (!FileAccess.FileExists(scriptPath))
		{
			OpenMissingScriptDialog(entry, scriptPath);
			return false;
		}

		Script script = ResourceLoader.Load<Script>(scriptPath);

		if (script == null)
		{
			OpenMissingScriptDialog(entry, scriptPath);
			return false;
		}

		return true;
	}

	private void OpenMissingSceneDialog(
		string entry,
		string scenePath,
		ScriptTreeOccurrence? sourceOccurrence = null
	)
	{
		_pendingMissingSceneEntry = entry;
		_pendingMissingScenePath = scenePath;
		_pendingMissingSceneScriptOccurrence = sourceOccurrence;

		_missingSceneDialog.DialogText = $"Linked scene could not be found.\n\n{scenePath}";

		_missingSceneDialog.PopupCentered();
		CallDeferred(nameof(ReleaseMissingSceneDialogFocus));
	}

	private void ReleaseMissingSceneDialogFocus()
	{
		ReleaseDialogOkButtonFocus(_missingSceneDialog);
	}

	private void OnMissingSceneRelinkPressed()
	{
		if (string.IsNullOrWhiteSpace(_pendingMissingSceneEntry))
			return;

		_relinkSceneDialog.PopupCenteredRatio(0.8f);
	}

	private void OnMissingSceneCustomAction(StringName action)
	{
		if (action != "remove_scene_link")
			return;

		if (string.IsNullOrWhiteSpace(_pendingMissingSceneEntry))
			return;

		using TreeOperationDialogScope operationScope = BeginTreeOperationDialogScope(
			"Remove Scene Link Failed",
			CloseMissingSceneRecoveryUiAfterFailure
		);

		string entry = _pendingMissingSceneEntry;
		ScriptTreeOccurrence? sourceOccurrence = _pendingMissingSceneScriptOccurrence;

		if (IsSceneEntry(entry))
		{
			if (!EnsureSystemsLoadedForTreeOperation("Remove Missing Scene"))
				return;

			if (
				!EnsureEntryAvailableForReversibleMutation(
					entry,
					"Remove Missing Scene"
				)
			)
			{
				ReportTreeOperationFailure(
					"System Explorer could not remove the missing scene reference.",
					entry
				);
				return;
			}

			string selectedScriptEntryFromFilterBeforeMutation =
				_selectedScriptEntryFromFilter;
			SystemsAndFolderBindingsSnapshot snapshot =
				CaptureSystemsAndFolderBindingsSnapshot();

			if (!RemoveEntry(entry))
			{
				if (!HasActiveTreeOperationFailure)
				{
					ReportTreeOperationFailure(
						"System Explorer could not remove the missing scene reference.",
						entry
					);
				}

				return;
			}

			if (
				!TryPersistReversibleSystemsAndFolderBindingsMutation(
					snapshot,
					systemsChanged: true,
					folderBindingsChanged: false,
					operationName: "Remove Missing Scene"
				)
			)
			{
				_selectedScriptEntryFromFilter =
					selectedScriptEntryFromFilterBeforeMutation;
				return;
			}

			HideTreeOperationOriginWindow(_missingSceneDialog);
			HideTreeOperationOriginWindow(_relinkSceneDialog);
			ClearMissingSceneState();
			BuildTree();
			return;
		}

		if (!UpdateLinkedScenePath(entry, "", sourceOccurrence))
			return;

		HideTreeOperationOriginWindow(_missingSceneDialog);
		HideTreeOperationOriginWindow(_relinkSceneDialog);
		ClearMissingSceneState();
	}

	private void OnRelinkSceneFileSelected(string newScenePath)
	{
		if (string.IsNullOrWhiteSpace(_pendingMissingSceneEntry))
			return;

		using TreeOperationDialogScope operationScope = BeginTreeOperationDialogScope(
			"Relink Scene Failed",
			CloseMissingSceneRecoveryUiAfterFailure
		);

		string entry = _pendingMissingSceneEntry;
		ScriptTreeOccurrence? sourceOccurrence = _pendingMissingSceneScriptOccurrence;
		bool updated = IsSceneEntry(entry)
			? UpdateScenePath(entry, newScenePath)
			: UpdateLinkedScenePath(entry, newScenePath, sourceOccurrence);

		if (!updated)
			return;

		HideTreeOperationOriginWindow(_missingSceneDialog);
		HideTreeOperationOriginWindow(_relinkSceneDialog);
		ClearMissingSceneState();
	}

	private void ClearMissingSceneState()
	{
		_pendingMissingSceneEntry = "";
		_pendingMissingScenePath = "";
		_pendingMissingSceneScriptOccurrence = null;
	}

	private bool UpdateScenePath(string oldEntry, string newScenePath)
	{
		if (!IsPrimaryResourcePathRepresentable(newScenePath))
		{
			ReportTreeOperationFailure(
				"System Explorer cannot relink this scene because the selected path contains \"|\" which is reserved by the current entry format. The existing entry was not changed.",
				$"Path='{newScenePath}'"
			);
			DebugLogger.LogOperation(
				"Relink Scene cancelled: unrepresentable primary resource path",
				newScenePath
			);
			return false;
		}

		if (!EnsureSystemsLoadedForTreeOperation("Update Scene"))
			return false;

		string folderPath = GetFolderPathFromEntry(oldEntry);
		string newEntry = BuildSceneEntry(folderPath, newScenePath, IsEntryLocked(oldEntry));

		if (string.Equals(oldEntry, newEntry, System.StringComparison.Ordinal))
			return true;

		if (
			!EnsureEntryAvailableForReversibleMutation(
				oldEntry,
				"Update Scene"
			)
		)
		{
			ReportTreeOperationFailure(
				"System Explorer could not update the selected scene reference.",
				oldEntry
			);
			return false;
		}

		string selectedScriptEntryFromFilterBeforeMutation =
			_selectedScriptEntryFromFilter;
		SystemsAndFolderBindingsSnapshot snapshot =
			CaptureSystemsAndFolderBindingsSnapshot();

		if (!ReplaceEntry(oldEntry, newEntry))
		{
			if (!HasActiveTreeOperationFailure)
			{
				ReportTreeOperationFailure(
					"System Explorer could not update the selected scene reference.",
					$"{oldEntry} -> {newEntry}"
				);
			}

			DebugLogger.LogOperation(
				"Update Scene cancelled: mutation failed",
				$"{oldEntry} -> {newEntry}"
			);
			return false;
		}

		if (
			!TryPersistReversibleSystemsAndFolderBindingsMutation(
				snapshot,
				systemsChanged: true,
				folderBindingsChanged: false,
				operationName: "Update Scene"
			)
		)
		{
			_selectedScriptEntryFromFilter =
				selectedScriptEntryFromFilterBeforeMutation;
			return false;
		}

		BuildTree();
		return true;
	}

	private ScriptTreeOccurrence? CaptureSelectedScriptOccurrence(string expectedEntry)
	{
		if (
			!TryGetScriptTreeOccurrenceFromTreeItem(
				_tree?.GetSelected(),
				out ScriptTreeOccurrence occurrence
			)
			|| !string.Equals(occurrence.Entry, expectedEntry, System.StringComparison.Ordinal)
		)
		{
			return null;
		}

		return occurrence;
	}

	private bool UpdateLinkedScenePath(
		string oldEntry,
		string linkedScenePath,
		ScriptTreeOccurrence? sourceOccurrence = null
	)
	{
		if (!EnsureSystemsLoadedForTreeOperation("Update Linked Scene"))
			return false;

		string scriptPath = NormalizeScriptPathForSync(GetScriptPathFromEntry(oldEntry));

		if (string.IsNullOrWhiteSpace(scriptPath))
		{
			ReportTreeOperationFailure(
				"System Explorer could not verify the script before updating its scene link.",
				oldEntry
			);
			DebugLogger.LogOperation(
				"Update Linked Scene cancelled: script path unavailable",
				oldEntry
			);
			return false;
		}

		int matchedEntryCount = 0;
		int changedEntryCount = 0;
		bool systemsChanged = false;
		ScriptTreeOccurrence? updatedSourceOccurrence = null;
		Dictionary<string, List<string>> updatedSystems = new(_systems.Comparer);
		List<KeyValuePair<string, string>> changedEntries = new();

		foreach (string systemName in _systems.Keys.ToList())
		{
			List<string> currentEntries = _systems[systemName];
			List<string> updatedEntries = new();
			HashSet<string> updatedTargetEntries = new(System.StringComparer.Ordinal);
			bool systemMatched = false;

			foreach (string entry in currentEntries)
			{
				if (entry.StartsWith("folder::") || IsSceneEntry(entry))
				{
					updatedEntries.Add(entry);
					continue;
				}

				string currentScriptPath = NormalizeScriptPathForSync(
					GetScriptPathFromEntry(entry)
				);

				if (
					!string.Equals(
						currentScriptPath,
						scriptPath,
						System.StringComparison.OrdinalIgnoreCase
					)
				)
				{
					updatedEntries.Add(entry);
					continue;
				}

				matchedEntryCount++;
				systemMatched = true;

				string newEntry = BuildScriptEntry(
					GetFolderPathFromEntry(entry),
					scriptPath,
					linkedScenePath,
					IsEntryLocked(entry)
				);

				bool isSourceOccurrence =
					sourceOccurrence.HasValue
					&& string.Equals(
						sourceOccurrence.Value.SystemName,
						systemName,
						System.StringComparison.Ordinal
					)
					&& string.Equals(
						sourceOccurrence.Value.Entry,
						entry,
						System.StringComparison.Ordinal
					)
					&& string.Equals(
						sourceOccurrence.Value.ScriptPath,
						scriptPath,
						System.StringComparison.OrdinalIgnoreCase
					);

				if (isSourceOccurrence)
				{
					updatedSourceOccurrence = new ScriptTreeOccurrence(
						systemName,
						newEntry,
						scriptPath
					);
				}

				if (!string.Equals(entry, newEntry, System.StringComparison.Ordinal))
				{
					changedEntries.Add(new KeyValuePair<string, string>(entry, newEntry));
					changedEntryCount++;
				}

				if (updatedTargetEntries.Add(newEntry))
					updatedEntries.Add(newEntry);
			}

			if (!systemMatched)
				continue;

			updatedSystems[systemName] = updatedEntries;

			if (!currentEntries.SequenceEqual(updatedEntries, System.StringComparer.Ordinal))
				systemsChanged = true;
		}

		if (matchedEntryCount == 0)
		{
			ReportTreeOperationFailure(
				"System Explorer could not verify the selected script reference before updating its scene link.",
				scriptPath
			);
			DebugLogger.LogOperation(
				"Update Linked Scene cancelled: no matching script references",
				scriptPath
			);
			return false;
		}

		DebugLogger.LogOperation(
			"Update Linked Scene references mutated",
			$"Path='{scriptPath}', Matched={matchedEntryCount}, Changed={changedEntryCount}, SystemsChanged={systemsChanged}"
		);

		if (!systemsChanged)
		{
			if (updatedSourceOccurrence.HasValue)
				RestoreLinkedSceneSourceOccurrence(updatedSourceOccurrence.Value);

			return true;
		}

		string selectedScriptEntryFromFilterBeforeMutation =
			_selectedScriptEntryFromFilter;
		SystemsAndFolderBindingsSnapshot snapshot =
			CaptureSystemsAndFolderBindingsSnapshot();

		foreach (KeyValuePair<string, List<string>> updatedSystem in updatedSystems)
			_systems[updatedSystem.Key] = updatedSystem.Value;

		foreach (KeyValuePair<string, string> changedEntry in changedEntries)
			UpdateSelectedScriptEntryFromFilter(changedEntry.Key, changedEntry.Value);

		if (
			!TryPersistReversibleSystemsAndFolderBindingsMutation(
				snapshot,
				systemsChanged: true,
				folderBindingsChanged: false,
				operationName: "Update Scene Link"
			)
		)
		{
			_selectedScriptEntryFromFilter =
				selectedScriptEntryFromFilterBeforeMutation;
			return false;
		}

		BuildTree();

		if (updatedSourceOccurrence.HasValue)
			RestoreLinkedSceneSourceOccurrence(updatedSourceOccurrence.Value);

		return true;
	}

	private void RestoreLinkedSceneSourceOccurrence(ScriptTreeOccurrence occurrence)
	{
		if (!TrySelectScriptTreeOccurrence(occurrence))
		{
			DebugLogger.LogOperation(
				"Update Linked Scene selection restore warning",
				$"system='{occurrence.SystemName}', entry='{occurrence.Entry}'"
			);
			return;
		}

		RememberScriptTreeOccurrence(occurrence);
		CallDeferred(nameof(ReleaseTreeFocusAfterNavigation));
	}

	private string GetExistingLinkedScenePathForScript(string scriptPath)
	{
		string normalizedScriptPath = NormalizeScriptPathForSync(scriptPath);

		if (string.IsNullOrWhiteSpace(normalizedScriptPath))
			return "";

		foreach (List<string> entries in _systems.Values)
		{
			if (entries == null)
				continue;

			foreach (string entry in entries)
			{
				if (entry.StartsWith("folder::") || IsSceneEntry(entry))
					continue;

				string currentScriptPath = NormalizeScriptPathForSync(
					GetScriptPathFromEntry(entry)
				);

				if (
					!string.Equals(
						currentScriptPath,
						normalizedScriptPath,
						System.StringComparison.OrdinalIgnoreCase
					)
				)
				{
					continue;
				}

				string linkedScenePath = GetLinkedScenePathFromEntry(entry);

				if (!string.IsNullOrWhiteSpace(linkedScenePath))
					return linkedScenePath;
			}
		}

		return "";
	}

	#endregion
}
#endif
