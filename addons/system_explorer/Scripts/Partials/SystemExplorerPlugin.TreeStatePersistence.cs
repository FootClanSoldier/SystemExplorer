#if TOOLS
using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

public partial class SystemExplorerPlugin
{
	#region Persistent Tree View State
	private const string TreeStateDirectoryPath = "res://.godot/system_explorer";
	private const string TreeStatePath = TreeStateDirectoryPath + "/tree_state.json";
	private const string TreeStateTemporaryPath = TreeStatePath + ".tmp";
	private const int TreeStateFormatVersion = 1;

	private readonly record struct PersistentTreeSelection(
		string SystemName,
		string Metadata
	);

	private PersistentTreeSelection? _persistentTreeSelection;
	private bool _isRestoringOrRebuildingPersistentTreeState;
	private bool _treeStateSaveDirty;
	private bool _treeStateSaveQueued;
	private bool _treeStateSaveWaitingForNextProcessFrame;
	private bool _treeStatePersistenceShutdown;
	private string _lastTreeStateLoadFailure = "";
	private string _lastTreeStateSaveFailure = "";

	private void LoadPersistentTreeStateBestEffort(string reason)
	{
		_expandedItems.Clear();
		_persistentTreeSelection = null;

		try
		{
			if (!FileAccess.FileExists(TreeStatePath))
			{
				_lastTreeStateLoadFailure = "";
				return;
			}

			using FileAccess file = FileAccess.Open(TreeStatePath, FileAccess.ModeFlags.Read);
			if (file == null)
			{
				LogTreeStateLoadFailureOnce(reason, $"Open failed with '{FileAccess.GetOpenError()}'.");
				return;
			}

			using JsonDocument document = JsonDocument.Parse(file.GetAsText());
			JsonElement root = document.RootElement;

			if (root.ValueKind != JsonValueKind.Object)
				throw new JsonException("Tree-state root must be a JSON object.");

			if (
				!root.TryGetProperty("format_version", out JsonElement versionElement)
				|| versionElement.ValueKind != JsonValueKind.Number
				|| !versionElement.TryGetInt32(out int formatVersion)
			)
			{
				throw new JsonException("Tree-state format_version must be an integer.");
			}

			if (formatVersion != TreeStateFormatVersion)
			{
				LogTreeStateLoadFailureOnce(
					reason,
					$"Unsupported or missing format_version '{formatVersion}'."
				);
				return;
			}

			if (
				!root.TryGetProperty("expanded_items", out JsonElement expandedItemsElement)
				|| expandedItemsElement.ValueKind != JsonValueKind.Array
			)
			{
				throw new JsonException("Tree-state expanded_items must be an array.");
			}

			var expandedItems = new List<string>();
			foreach (JsonElement itemElement in expandedItemsElement.EnumerateArray())
			{
				if (itemElement.ValueKind != JsonValueKind.String)
					throw new JsonException("Tree-state expanded_items may only contain strings.");

				expandedItems.Add(itemElement.GetString() ?? "");
			}

			HashSet<string> normalizedExpandedItems =
				NormalizePersistentExpansionMetadata(expandedItems);

			foreach (string metadata in normalizedExpandedItems)
				_expandedItems.Add(metadata);

			if (
				TryReadPersistentTreeSelection(
					root,
					out PersistentTreeSelection? loadedSelection,
					out string selectionFailureDetail
				)
			)
			{
				_persistentTreeSelection = loadedSelection;
			}
			else
			{
				_persistentTreeSelection = null;
				LogPersistentTreeSelectionRestoreIgnored(
					reason,
					$"Stage='Load', Detail='{selectionFailureDetail}'"
				);
			}

			_lastTreeStateLoadFailure = "";
			DebugLogger.LogOperation(
				"Persistent tree view state loaded",
				BuildPersistentTreeStateLogDetail(reason, _expandedItems.Count, _persistentTreeSelection)
			);
		}
		catch (Exception exception)
		{
			_expandedItems.Clear();
			_persistentTreeSelection = null;
			LogTreeStateLoadFailureOnce(reason, exception.Message);
		}
	}

	private bool TryReadPersistentTreeSelection(
		JsonElement root,
		out PersistentTreeSelection? selection,
		out string failureDetail
	)
	{
		selection = null;
		failureDetail = "";

		if (!root.TryGetProperty("selected_item", out JsonElement selectedItemElement))
			return true;

		if (selectedItemElement.ValueKind == JsonValueKind.Null)
			return true;

		if (selectedItemElement.ValueKind != JsonValueKind.Object)
		{
			failureDetail = "Tree-state selected_item must be an object or null.";
			return false;
		}

		if (
			!selectedItemElement.TryGetProperty("system_name", out JsonElement systemNameElement)
			|| systemNameElement.ValueKind != JsonValueKind.String
			|| !selectedItemElement.TryGetProperty("metadata", out JsonElement metadataElement)
			|| metadataElement.ValueKind != JsonValueKind.String
		)
		{
			failureDetail = "Tree-state selected_item must contain string system_name and metadata values.";
			return false;
		}

		var candidate = new PersistentTreeSelection(
			systemNameElement.GetString() ?? "",
			NormalizePersistentTreeSelectionMetadata(metadataElement.GetString() ?? "")
		);

		if (!IsPersistentTreeSelectionIdentityWellFormed(candidate, out failureDetail))
			return false;

		if (!IsPersistentTreeSelectionStillValid(candidate, out failureDetail))
			return false;

		selection = candidate;
		return true;
	}

	private static string NormalizePersistentTreeSelectionMetadata(string metadata)
	{
		string prefix;

		if (metadata.StartsWith("script::", StringComparison.Ordinal))
			prefix = "script::";
		else if (metadata.StartsWith("sceneLink::", StringComparison.Ordinal))
			prefix = "sceneLink::";
		else
			return metadata;

		string entry = metadata.Substring(prefix.Length);
		string normalizedEntry = NormalizeScriptOrSceneResourcePaths(entry);

		return string.Equals(entry, normalizedEntry, StringComparison.Ordinal)
			? metadata
			: $"{prefix}{normalizedEntry}";
	}

	private void QueuePersistentTreeStateSave()
	{
		if (_treeStatePersistenceShutdown)
			return;

		_treeStateSaveDirty = true;
		if (_isRestoringOrRebuildingPersistentTreeState)
			return;

		if (!_treeStateSaveQueued)
		{
			_treeStateSaveQueued = true;
			_treeStateSaveWaitingForNextProcessFrame = true;
		}

		RefreshEditorPluginProcessingState();
	}

	private bool HasPendingPersistentTreeStateProcessWork() =>
		!_treeStatePersistenceShutdown && _treeStateSaveQueued;

	private void ProcessPendingPersistentTreeStateSave()
	{
		if (!_treeStateSaveQueued)
			return;

		if (_treeStatePersistenceShutdown)
		{
			ClearPendingPersistentTreeStateSave();
			return;
		}

		if (_treeStateSaveWaitingForNextProcessFrame)
		{
			// The first process pass only arms the save for a later editor frame.
			_treeStateSaveWaitingForNextProcessFrame = false;
			return;
		}

		if (_isRestoringOrRebuildingPersistentTreeState)
			return;

		if (!IsValidGodotObject(this))
		{
			ClearPendingPersistentTreeStateSave();
			return;
		}

		try
		{
			if (!IsInsideTree())
			{
				ClearPendingPersistentTreeStateSave();
				return;
			}
		}
		catch
		{
			ClearPendingPersistentTreeStateSave();
			return;
		}

		if (!IsValidGodotObject(_tree))
			return;

		bool shouldSave = _treeStateSaveDirty;
		_treeStateSaveQueued = false;
		_treeStateSaveDirty = false;
		_treeStateSaveWaitingForNextProcessFrame = false;

		if (shouldSave)
			SavePersistentTreeStateBestEffort("Frame-pumped tree view state change");
	}

	private void ClearPendingPersistentTreeStateSave()
	{
		_treeStateSaveDirty = false;
		_treeStateSaveQueued = false;
		_treeStateSaveWaitingForNextProcessFrame = false;
	}

	private void SavePersistentTreeStateBestEffort(string reason)
	{
		try
		{
			HashSet<string> expansionSource = CaptureNormalTreeExpansionForPersistence();
			List<string> orderedExpandedItems = expansionSource
				.OrderBy(metadata => metadata, StringComparer.OrdinalIgnoreCase)
				.ToList();
			PersistentTreeSelection? selectedItem = _persistentTreeSelection;

			if (
				selectedItem.HasValue
				&& !IsPersistentTreeSelectionStillValid(
					selectedItem.Value,
					out string selectionFailureDetail
				)
			)
			{
				_persistentTreeSelection = null;
				selectedItem = null;
				LogPersistentTreeSelectionRestoreIgnored(
					reason,
					$"Stage='Save', Detail='Selected item is no longer valid: {selectionFailureDetail}'"
				);
			}

			using var stream = new System.IO.MemoryStream();
			using (
				var writer = new Utf8JsonWriter(
					stream,
					new JsonWriterOptions
					{
						Indented = true,
					}
				)
			)
			{
				writer.WriteStartObject();
				writer.WriteNumber("format_version", TreeStateFormatVersion);
				writer.WritePropertyName("expanded_items");
				writer.WriteStartArray();

				foreach (string metadata in orderedExpandedItems)
					writer.WriteStringValue(metadata);

				writer.WriteEndArray();
				writer.WritePropertyName("selected_item");

				if (selectedItem.HasValue)
				{
					writer.WriteStartObject();
					writer.WriteString("system_name", selectedItem.Value.SystemName);
					writer.WriteString("metadata", selectedItem.Value.Metadata);
					writer.WriteEndObject();
				}
				else
				{
					writer.WriteNullValue();
				}

				writer.WriteEndObject();
				writer.Flush();
			}

			string json = Encoding.UTF8.GetString(stream.ToArray());

			Error directoryError = DirAccess.MakeDirRecursiveAbsolute(
				ProjectSettings.GlobalizePath(TreeStateDirectoryPath)
			);
			if (directoryError != Error.Ok && directoryError != Error.AlreadyExists)
				throw new InvalidOperationException($"Directory creation failed with '{directoryError}'.");

			using (FileAccess temporaryFile = FileAccess.Open(TreeStateTemporaryPath, FileAccess.ModeFlags.Write))
			{
				if (temporaryFile == null)
					throw new InvalidOperationException($"Temporary open failed with '{FileAccess.GetOpenError()}'.");

				if (!temporaryFile.StoreString(json))
					throw new InvalidOperationException("Writing the temporary tree-state file failed.");
			}

			string targetGlobalPath = ProjectSettings.GlobalizePath(TreeStatePath);
			string temporaryGlobalPath = ProjectSettings.GlobalizePath(TreeStateTemporaryPath);

			if (FileAccess.FileExists(TreeStatePath))
			{
				Error removeError = DirAccess.RemoveAbsolute(targetGlobalPath);
				if (removeError != Error.Ok)
					throw new InvalidOperationException($"Replacing the existing tree-state file failed with '{removeError}'.");
			}

			Error renameError = DirAccess.RenameAbsolute(temporaryGlobalPath, targetGlobalPath);
			if (renameError != Error.Ok)
				throw new InvalidOperationException($"Promoting the temporary tree-state file failed with '{renameError}'.");

			_lastTreeStateSaveFailure = "";
			DebugLogger.LogOperation(
				"Persistent tree view state saved",
				BuildPersistentTreeStateLogDetail(reason, orderedExpandedItems.Count, selectedItem)
			);
		}
		catch (Exception exception)
		{
			LogTreeStateSaveFailureOnce(reason, exception.Message);
		}
	}

	private void UpdatePersistentTreeSelectionFromTreeItem(TreeItem item)
	{
		if (!TryCreatePersistentTreeSelection(item, out PersistentTreeSelection selection))
			return;

		if (_persistentTreeSelection.HasValue && IsSamePersistentTreeSelection(
			_persistentTreeSelection.Value,
			selection
		))
		{
			return;
		}

		_persistentTreeSelection = selection;
		QueuePersistentTreeStateSave();
	}

	private void ClearPersistentTreeSelectionForKeyboardNavigation()
	{
		if (!_persistentTreeSelection.HasValue)
			return;

		_persistentTreeSelection = null;
		QueuePersistentTreeStateSave();
	}

	private bool TryCreatePersistentTreeSelection(
		TreeItem item,
		out PersistentTreeSelection selection
	)
	{
		selection = default;

		if (item == null || !GodotObject.IsInstanceValid(item))
			return false;

		string metadata = item.GetMetadata(0).AsString();
		if (!IsSupportedPersistentTreeSelectionMetadata(metadata))
			return false;

		string systemName;

		if (
			metadata.StartsWith("system::", StringComparison.Ordinal)
			|| metadata.StartsWith("folder::", StringComparison.Ordinal)
		)
		{
			systemName = GetSystemNameFromMetadata(metadata);
		}
		else if (IsScriptFilterActive())
		{
			if (!TryGetScriptFilterResultForTreeItem(item, out ScriptFilterResult result))
				return false;

			bool metadataRepresentsScene = metadata.StartsWith(
				"sceneLink::",
				StringComparison.Ordinal
			);

			if (
				metadataRepresentsScene != IsSceneEntry(result.Entry)
				|| !string.Equals(
					metadata,
					metadataRepresentsScene
						? $"sceneLink::{result.Entry}"
						: $"script::{result.Entry}",
					StringComparison.Ordinal
				)
			)
			{
				return false;
			}

			systemName = result.SystemName;
			metadata = metadataRepresentsScene
				? $"sceneLink::{result.Entry}"
				: $"script::{result.Entry}";
		}
		else if (!TryGetSystemNameFromTreeItemParentChain(item, out systemName))
		{
			return false;
		}

		var candidate = new PersistentTreeSelection(systemName, metadata);
		if (!IsPersistentTreeSelectionIdentityWellFormed(candidate, out _))
			return false;

		if (!IsPersistentTreeSelectionStillValid(candidate, out _))
			return false;

		selection = candidate;
		return true;
	}

	private bool TryRestoreTreeSelectionByIdentity(
		PersistentTreeSelection selection,
		string reason
	)
	{
		try
		{
			if (!IsPersistentTreeSelectionStillValid(selection, out _))
				return false;

			if (!TryFindTreeItemByPersistentSelectionIdentity(selection, out TreeItem targetItem))
				return false;

			if (!TryApplyPersistentTreeSelection(targetItem, selection))
				return false;

			DebugLogger.LogOperation(
				"Tree selection restored by exact identity",
				BuildPersistentTreeSelectionLogDetail(reason, selection)
			);
			return true;
		}
		catch (Exception exception)
		{
			LogPersistentTreeSelectionRestoreIgnored(
				reason,
				$"Detail='Exact selection restore failed: {exception.Message}'"
			);
			return false;
		}
	}

	private bool TryRestoreFirstVisibleTreeSelection(string reason)
	{
		TreeItem firstVisibleItem = _tree?.GetRoot()?.GetFirstChild();

		if (
			firstVisibleItem == null
			|| !TryCreatePersistentTreeSelection(
				firstVisibleItem,
				out PersistentTreeSelection selection
			)
		)
		{
			return false;
		}

		return TryRestoreTreeSelectionByIdentity(selection, reason);
	}

	private bool TryFindTreeItemByPersistentSelectionIdentity(
		PersistentTreeSelection selection,
		out TreeItem targetItem
	)
	{
		targetItem = null;

		if (!IsValidGodotObject(_tree))
			return false;

		TreeItem root = _tree.GetRoot();
		if (root == null)
			return false;

		if (IsScriptFilterActive())
		{
			if (!IsScriptOrScenePersistentTreeSelection(selection))
				return false;

			string entry = GetEntryFromMetadata(selection.Metadata);
			bool isSceneEntry = selection.Metadata.StartsWith(
				"sceneLink::",
				StringComparison.Ordinal
			);

			return TryFindScriptFilterTreeItemByIdentity(
				selection.SystemName,
				entry,
				isSceneEntry,
				out targetItem
			);
		}

		TreeItem systemItem = FindDirectSystemTreeItem(root, selection.SystemName);
		targetItem = systemItem == null
			? null
			: FindTreeItemByMetadataWithinSubtree(systemItem, selection.Metadata);
		return targetItem != null;
	}

	private bool TryApplyPersistentTreeSelection(
		TreeItem targetItem,
		PersistentTreeSelection selection
	)
	{
		if (
			targetItem == null
			|| !GodotObject.IsInstanceValid(targetItem)
			|| !IsValidGodotObject(_tree)
		)
		{
			return false;
		}

		bool previousSuppression = _isRestoringOrRebuildingPersistentTreeState;
		_isRestoringOrRebuildingPersistentTreeState = true;
		bool restored = false;

		try
		{
			if (!IsScriptFilterActive())
				ExpandParentsForTreeItem(targetItem);

			if (_tree.GetSelected() != targetItem)
				targetItem.Select(0);

			_tree.ScrollToItem(targetItem);
			UpdateTreeLockIconVisibility();
			restored = _tree.GetSelected() == targetItem;
		}
		finally
		{
			_isRestoringOrRebuildingPersistentTreeState = previousSuppression;
			if (!previousSuppression && _treeStateSaveDirty)
				QueuePersistentTreeStateSave();
		}

		if (!restored)
			return false;

		if (
			!_persistentTreeSelection.HasValue
			|| !IsSamePersistentTreeSelection(
				_persistentTreeSelection.Value,
				selection
			)
		)
		{
			_persistentTreeSelection = selection;
			QueuePersistentTreeStateSave();
		}

		return true;
	}

	private void ClearPersistentTreeSelectionAndTreeSelection()
	{
		bool previousSuppression = _isRestoringOrRebuildingPersistentTreeState;
		_isRestoringOrRebuildingPersistentTreeState = true;

		try
		{
			if (IsValidGodotObject(_tree))
			{
				_tree.DeselectAll();
				UpdateTreeLockIconVisibility();
			}
		}
		catch (Exception exception)
		{
			LogPersistentTreeSelectionRestoreIgnored(
				"Clear Tree Selection",
				$"Detail='Tree deselection failed: {exception.Message}'"
			);
		}
		finally
		{
			_isRestoringOrRebuildingPersistentTreeState = previousSuppression;
		}

		if (IsScriptFilterActive())
			_selectedScriptEntryFromFilter = "";

		_persistentTreeSelection = null;
		QueuePersistentTreeStateSave();
	}

	private void RestorePersistentTreeSelectionBestEffort(string reason)
	{
		if (!_persistentTreeSelection.HasValue)
		{
			LogPersistentTreeSelectionRestoreIgnored(reason, "Detail='No selected item was persisted.'");
			return;
		}

		PersistentTreeSelection selection = _persistentTreeSelection.Value;

		if (!IsPersistentTreeSelectionStillValid(selection, out string validityFailureDetail))
		{
			_persistentTreeSelection = null;
			LogPersistentTreeSelectionRestoreIgnored(
				reason,
				$"Detail='Persisted selection is no longer valid: {validityFailureDetail}'"
			);
			return;
		}

		if (!IsValidGodotObject(_tree))
		{
			LogPersistentTreeSelectionRestoreIgnored(reason, "Detail='Tree is unavailable.'");
			return;
		}

		if (_tree.GetRoot() == null)
		{
			LogPersistentTreeSelectionRestoreIgnored(reason, "Detail='Tree root is unavailable.'");
			return;
		}

		if (!TryFindTreeItemByPersistentSelectionIdentity(selection, out TreeItem targetItem))
		{
			if (IsScriptFilterActive())
			{
				LogPersistentTreeSelectionRestoreIgnored(
					reason,
					IsScriptOrScenePersistentTreeSelection(selection)
						? "Detail='The selected item is valid but does not occur in the current filter result.'"
						: "Detail='The selected system or folder is temporarily unavailable in the flat filter tree.'"
				);
				return;
			}

			_persistentTreeSelection = null;
			QueuePersistentTreeStateSave();
			LogPersistentTreeSelectionRestoreIgnored(
				reason,
				"Detail='The exact selected item was not found in its owning system subtree.'"
			);
			return;
		}

		try
		{
			if (!TryApplyPersistentTreeSelection(targetItem, selection))
			{
				LogPersistentTreeSelectionRestoreIgnored(
					reason,
					"Detail='Tree selection verification did not match the intended item.'"
				);
				return;
			}
		}
		catch (Exception exception)
		{
			LogPersistentTreeSelectionRestoreIgnored(
				reason,
				$"Detail='Selection application failed: {exception.Message}'"
			);
			return;
		}

		DebugLogger.LogOperation(
			"Persistent tree selection restored",
			BuildPersistentTreeSelectionLogDetail(reason, selection)
		);
	}

	private static TreeItem FindDirectSystemTreeItem(TreeItem root, string systemName)
	{
		if (root == null || string.IsNullOrWhiteSpace(systemName))
			return null;

		string expectedMetadata = $"system::{systemName}";
		TreeItem current = root.GetFirstChild();

		while (current != null)
		{
			if (
				string.Equals(
					current.GetMetadata(0).AsString(),
					expectedMetadata,
					StringComparison.Ordinal
				)
			)
			{
				return current;
			}

			current = current.GetNext();
		}

		return null;
	}

	private bool IsPersistentTreeSelectionStillValid(
		PersistentTreeSelection selection,
		out string failureDetail
	)
	{
		failureDetail = "";

		if (!IsPersistentTreeSelectionIdentityWellFormed(selection, out failureDetail))
			return false;

		if (!_systems.TryGetValue(selection.SystemName, out List<string> entries))
		{
			failureDetail = $"System '{selection.SystemName}' does not exist.";
			return false;
		}

		if (selection.Metadata.StartsWith("system::", StringComparison.Ordinal))
			return true;

		if (selection.Metadata.StartsWith("folder::", StringComparison.Ordinal))
		{
			string folderPath = GetFolderPathFromMetadata(selection.Metadata);
			bool folderExists = entries.Any(entry =>
				entry.StartsWith("folder::", StringComparison.Ordinal)
				&& string.Equals(
					GetFolderPathFromFolderEntry(entry),
					folderPath,
					StringComparison.Ordinal
				)
			);

			if (!folderExists)
				failureDetail = $"Folder '{folderPath}' does not exist in system '{selection.SystemName}'.";

			return folderExists;
		}

		string selectedEntry = GetEntryFromMetadata(selection.Metadata);
		bool expectsScene = selection.Metadata.StartsWith(
			"sceneLink::",
			StringComparison.Ordinal
		);

		bool entryExists = entries.Any(entry =>
			string.Equals(entry, selectedEntry, StringComparison.Ordinal)
			&& IsSceneEntry(entry) == expectsScene
		);

		if (!entryExists)
			failureDetail = $"Entry '{selectedEntry}' does not exist with the expected type in system '{selection.SystemName}'.";

		return entryExists;
	}

	private static bool IsPersistentTreeSelectionIdentityWellFormed(
		PersistentTreeSelection selection,
		out string failureDetail
	)
	{
		failureDetail = "";

		if (string.IsNullOrWhiteSpace(selection.SystemName))
		{
			failureDetail = "Selected system name is empty.";
			return false;
		}

		if (!IsSupportedPersistentTreeSelectionMetadata(selection.Metadata))
		{
			failureDetail = $"Unsupported selected metadata '{selection.Metadata}'.";
			return false;
		}

		if (
			selection.Metadata.StartsWith("system::", StringComparison.Ordinal)
			|| selection.Metadata.StartsWith("folder::", StringComparison.Ordinal)
		)
		{
			string metadataSystemName = GetSystemNameFromMetadata(selection.Metadata);
			if (!string.Equals(metadataSystemName, selection.SystemName, StringComparison.Ordinal))
			{
				failureDetail = "Selected metadata does not match its stored system name.";
				return false;
			}
		}

		if (
			selection.Metadata.StartsWith("folder::", StringComparison.Ordinal)
			&& string.IsNullOrWhiteSpace(GetFolderPathFromMetadata(selection.Metadata))
		)
		{
			failureDetail = "Selected folder metadata has no folder path.";
			return false;
		}

		if (
			IsScriptOrScenePersistentTreeSelection(selection)
			&& string.IsNullOrWhiteSpace(GetEntryFromMetadata(selection.Metadata))
		)
		{
			failureDetail = "Selected script or scene metadata has no entry.";
			return false;
		}

		return true;
	}

	private static bool IsSupportedPersistentTreeSelectionMetadata(string metadata)
	{
		if (string.IsNullOrWhiteSpace(metadata))
			return false;

		return metadata.StartsWith("system::", StringComparison.Ordinal)
			|| metadata.StartsWith("folder::", StringComparison.Ordinal)
			|| metadata.StartsWith("script::", StringComparison.Ordinal)
			|| metadata.StartsWith("sceneLink::", StringComparison.Ordinal);
	}

	private static bool IsScriptOrScenePersistentTreeSelection(
		PersistentTreeSelection selection
	)
	{
		return selection.Metadata.StartsWith("script::", StringComparison.Ordinal)
			|| selection.Metadata.StartsWith("sceneLink::", StringComparison.Ordinal);
	}

	private static bool IsSamePersistentTreeSelection(
		PersistentTreeSelection left,
		PersistentTreeSelection right
	)
	{
		return string.Equals(left.SystemName, right.SystemName, StringComparison.Ordinal)
			&& string.Equals(left.Metadata, right.Metadata, StringComparison.Ordinal);
	}

	private HashSet<string> CaptureNormalTreeExpansionForPersistence()
	{
		IEnumerable<string> source;

		if (_isFilteringScripts || IsScriptFilterActive())
		{
			source = _expandedItemsBeforeScriptFilter;
		}
		else
		{
			SaveExpansionState();
			source = _expandedItems;
		}

		return NormalizePersistentExpansionMetadata(source);
	}

	private static HashSet<string> NormalizePersistentExpansionMetadata(
		IEnumerable<string> metadataEntries
	)
	{
		var normalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		if (metadataEntries == null)
			return normalized;

		foreach (string metadata in metadataEntries)
		{
			if (string.IsNullOrWhiteSpace(metadata))
				continue;

			string trimmed = metadata.Trim();
			if (trimmed.StartsWith("system::", StringComparison.OrdinalIgnoreCase))
			{
				if (!string.IsNullOrWhiteSpace(trimmed.Substring("system::".Length)))
					normalized.Add(trimmed);
				continue;
			}

			if (trimmed.StartsWith("folder::", StringComparison.OrdinalIgnoreCase))
			{
				string remainder = trimmed.Substring("folder::".Length);
				int separatorIndex = remainder.IndexOf("::", StringComparison.Ordinal);
				if (separatorIndex > 0 && separatorIndex < remainder.Length - 2)
					normalized.Add(trimmed);
			}
		}

		return normalized;
	}

	private void PrepareTreeStatePersistenceForManagedAssemblyRecovery()
	{
		ClearPendingPersistentTreeStateSave();
		_treeStatePersistenceShutdown = false;
		_isRestoringOrRebuildingPersistentTreeState = false;
		RefreshEditorPluginProcessingState();
	}

	private void FlushAndShutdownTreeStatePersistence()
	{
		if (!_treeStatePersistenceShutdown && IsValidGodotObject(_tree))
			SavePersistentTreeStateBestEffort("Plugin Exit");

		_treeStatePersistenceShutdown = true;
		_isRestoringOrRebuildingPersistentTreeState = false;
		ClearPendingPersistentTreeStateSave();
		RefreshEditorPluginProcessingState();
	}

	private static string BuildPersistentTreeStateLogDetail(
		string reason,
		int expandedItemCount,
		PersistentTreeSelection? selection
	)
	{
		string selectionDetail = selection.HasValue
			? $"SelectedSystem='{selection.Value.SystemName}', SelectedMetadata='{selection.Value.Metadata}'"
			: "SelectedSystem='<null>', SelectedMetadata='<null>'";

		return $"Reason='{reason}', ExpandedItems={expandedItemCount}, {selectionDetail}";
	}

	private static string BuildPersistentTreeSelectionLogDetail(
		string reason,
		PersistentTreeSelection selection
	)
	{
		return $"Reason='{reason}', SelectedSystem='{selection.SystemName}', SelectedMetadata='{selection.Metadata}'";
	}

	private void LogPersistentTreeSelectionRestoreIgnored(string reason, string detail)
	{
		string selectionDetail = _persistentTreeSelection.HasValue
			? $", SelectedSystem='{_persistentTreeSelection.Value.SystemName}', SelectedMetadata='{_persistentTreeSelection.Value.Metadata}'"
			: ", SelectedSystem='<null>', SelectedMetadata='<null>'";

		DebugLogger.LogOperation(
			"Persistent tree selection restore ignored",
			$"Reason='{reason}', {detail}{selectionDetail}"
		);
	}

	private void LogTreeStateLoadFailureOnce(string reason, string detail)
	{
		string failure = $"Reason='{reason}', Detail='{detail}'";
		if (string.Equals(_lastTreeStateLoadFailure, failure, StringComparison.Ordinal))
			return;

		_lastTreeStateLoadFailure = failure;
		DebugLogger.LogOperation("Persistent tree view state load ignored", failure);
	}

	private void LogTreeStateSaveFailureOnce(string reason, string detail)
	{
		string failure = $"Reason='{reason}', Detail='{detail}'";
		if (string.Equals(_lastTreeStateSaveFailure, failure, StringComparison.Ordinal))
			return;

		_lastTreeStateSaveFailure = failure;
		DebugLogger.LogOperation("Persistent tree view state save ignored", failure);
	}
	#endregion
}
#endif
