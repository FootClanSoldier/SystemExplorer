#if TOOLS
using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

public partial class SystemExplorerPlugin
{
	#region Script Filter
	private void OnScriptFilterTextChanged(string filterText)
	{
		UpdateScriptFilterSearchIconVisibility(filterText);

		if (!string.IsNullOrWhiteSpace(filterText))
		{
			if (!EnsureSystemsLoadedForScriptFilter("Script Filter Started"))
				return;

			if (!_isFilteringScripts)
			{
				SaveExpansionState();
				_expandedItemsBeforeScriptFilter.Clear();

				foreach (string metadata in _expandedItems)
					_expandedItemsBeforeScriptFilter.Add(metadata);

				_selectedScriptEntryFromFilter = "";
				_isFilteringScripts = true;
			}

			BuildFilteredItemTree(filterText);
			return;
		}

		if (!_isFilteringScripts)
			return;

		ExitScriptFilterMode();
	}

	private void UpdateScriptFilterSearchIconVisibility(string filterText)
	{
		if (_scriptFilterInput == null)
			return;

		_scriptFilterInput.RightIcon = string.IsNullOrEmpty(filterText)
			? _scriptFilterSearchIcon
			: _scriptFilterCloseIcon;

		if (string.IsNullOrEmpty(filterText))
			ResetScriptFilterInputCursor();
	}

	private void OnScriptFilterInputGuiInput(InputEvent inputEvent)
	{
		if (_scriptFilterInput == null)
			return;

		if (inputEvent is InputEventMouseMotion mouseMotion)
		{
			UpdateScriptFilterInputCursor(mouseMotion.Position);
			return;
		}

		if (string.IsNullOrEmpty(_scriptFilterInput.Text))
			return;

		if (inputEvent is not InputEventMouseButton mouseButton)
			return;

		if (mouseButton.ButtonIndex != MouseButton.Left || !mouseButton.Pressed)
			return;

		if (!IsLineEditRightIconClick(_scriptFilterInput, mouseButton.Position))
			return;

		ClearScriptFilterInput();
		_scriptFilterInput.AcceptEvent();
	}

	private void OnScriptFilterInputMouseExited()
	{
		ResetScriptFilterInputCursor();
	}

	private void UpdateScriptFilterInputCursor(Vector2 localMousePosition)
	{
		if (_scriptFilterInput == null)
			return;

		if (IsEditorOperationBusyCursorActive)
		{
			_scriptFilterInput.MouseDefaultCursorShape = Control.CursorShape.Busy;
			return;
		}

		bool isHoveringCloseIcon =
			!string.IsNullOrEmpty(_scriptFilterInput.Text)
			&& _scriptFilterInput.RightIcon == _scriptFilterCloseIcon
			&& IsLineEditRightIconClick(_scriptFilterInput, localMousePosition);

		_scriptFilterInput.MouseDefaultCursorShape = isHoveringCloseIcon
			? Control.CursorShape.Arrow
			: Control.CursorShape.Ibeam;
	}

	private void ResetScriptFilterInputCursor()
	{
		if (_scriptFilterInput == null)
			return;

		_scriptFilterInput.MouseDefaultCursorShape = IsEditorOperationBusyCursorActive
			? Control.CursorShape.Busy
			: Control.CursorShape.Ibeam;
	}

	private static bool IsLineEditRightIconClick(LineEdit lineEdit, Vector2 localMousePosition)
	{
		Texture2D rightIcon = lineEdit.RightIcon;

		if (rightIcon == null)
			return false;

		float clickableWidth = rightIcon.GetWidth() + RightIconClickablePadding;
		float controlWidth = lineEdit.Size.X;

		return localMousePosition.X >= controlWidth - clickableWidth
			&& localMousePosition.X <= controlWidth
			&& localMousePosition.Y >= 0.0f
			&& localMousePosition.Y <= lineEdit.Size.Y;
	}

	private void ClearScriptFilterInput()
	{
		if (_scriptFilterInput == null)
			return;

		if (string.IsNullOrEmpty(_scriptFilterInput.Text))
			return;

		// Setting LineEdit.Text from code does not reliably run the same path
		// as user input in the editor, so clear must explicitly update the icon
		// and exit item filter mode.
		_scriptFilterInput.Text = "";
		UpdateScriptFilterSearchIconVisibility("");

		if (_isFilteringScripts)
			ExitScriptFilterMode();
	}

	private bool IsScriptFilterActive()
	{
		return _scriptFilterInput != null && !string.IsNullOrWhiteSpace(_scriptFilterInput.Text);
	}

	private bool EnsureSystemsLoadedForScriptFilter(string reason)
	{
		if (_systems.Count > 0)
			return true;

		if (!FileAccess.FileExists(SavePath))
			return false;

		DebugLogger.LogOperation(
			"Script Filter Recovery Guard",
			$"Reason='{reason}', In-memory systems were empty while filtering."
		);

		bool recovered = TryRecoverSystemsFromDisk(reason);

		if (!recovered)
		{
			GD.PushWarning(
                "System Explorer could not filter items because the in-memory system list was empty and recovery from disk failed."
			);
		}

		return recovered;
	}

	private void BuildFilteredItemTree(string filterText)
	{
		if (!EnsureSystemsLoadedForScriptFilter("Build Filtered Item Tree"))
			return;

		NormalizeAllSystemEntries();

		_tree.Clear();

		TreeItem root = _tree.CreateItem();
		string normalizedFilter = filterText.Trim().ToLowerInvariant();

		if (string.IsNullOrWhiteSpace(normalizedFilter))
			return;

		foreach (ScriptFilterResult result in GetFilteredScriptResults(normalizedFilter))
		{
			TreeItem item = _tree.CreateItem(root);
			bool isSceneEntry = IsSceneEntry(result.Entry);
			string metadata = isSceneEntry
				? $"sceneLink::{result.Entry}"
				: $"script::{result.Entry}";

			item.SetText(0, GetLockableItemDisplayName(metadata, result.ItemName, result.Entry));
			item.SetTooltipText(
				0,
				isSceneEntry
					? GetScenePathFromEntry(result.Entry)
					: GetScriptTooltipText(result.Entry)
			);
			item.SetIcon(0, GetFilterResultIcon(result.Entry));
			item.SetMetadata(0, metadata);
		}
	}

	private List<ScriptFilterResult> GetFilteredScriptResults(string normalizedFilter)
	{
		List<ScriptFilterResult> results = new();

		foreach (KeyValuePair<string, List<string>> system in _systems)
		{
			foreach (string entry in system.Value.Where(IsScriptOrSceneEntry))
			{
				string itemPath = GetPathFromEntry(entry);
				string itemName = itemPath.GetFile();
				string normalizedItemName = itemName.ToLowerInvariant();

				if (!normalizedItemName.Contains(normalizedFilter))
					continue;

				results.Add(
					new ScriptFilterResult(
						system.Key,
						GetFolderPathFromEntry(entry),
						entry,
						itemName
					)
				);
			}
		}

		return results
			.OrderBy(result =>
				result.ItemName.ToLowerInvariant().StartsWith(normalizedFilter) ? 0 : 1
			)
			.ThenBy(result => result.ItemName)
			.ThenBy(result => result.SystemName)
			.ThenBy(result => result.FolderPath)
			.ToList();
	}

	private List<ScriptFilterResult> GetCurrentScriptFilterResults()
	{
		return GetFilteredScriptResults(
			(_scriptFilterInput?.Text ?? "").Trim().ToLowerInvariant()
		);
	}

	private bool TryGetScriptFilterResultForTreeItem(
		TreeItem item,
		out ScriptFilterResult matchingResult
	)
	{
		matchingResult = default;

		if (!IsScriptFilterActive() || _tree == null || item == null)
			return false;

		TreeItem current = _tree.GetRoot()?.GetFirstChild();
		List<ScriptFilterResult> results = GetCurrentScriptFilterResults();

		for (int index = 0; current != null && index < results.Count; index++)
		{
			if (current == item)
			{
				matchingResult = results[index];
				return true;
			}

			current = current.GetNext();
		}

		return false;
	}

	private bool TryFindScriptFilterTreeItemByIdentity(
		string systemName,
		string entry,
		bool isSceneEntry,
		out TreeItem item
	)
	{
		item = null;

		if (
			!IsScriptFilterActive()
			|| _tree == null
			|| string.IsNullOrWhiteSpace(systemName)
			|| string.IsNullOrWhiteSpace(entry)
		)
		{
			return false;
		}

		string expectedMetadata = isSceneEntry
			? $"sceneLink::{entry}"
			: $"script::{entry}";
		TreeItem current = _tree.GetRoot()?.GetFirstChild();
		List<ScriptFilterResult> results = GetCurrentScriptFilterResults();

		for (int index = 0; current != null && index < results.Count; index++)
		{
			ScriptFilterResult result = results[index];

			if (
				string.Equals(result.SystemName, systemName, StringComparison.Ordinal)
				&& string.Equals(result.Entry, entry, StringComparison.Ordinal)
				&& IsSceneEntry(result.Entry) == isSceneEntry
				&& string.Equals(
					current.GetMetadata(0).AsString(),
					expectedMetadata,
					StringComparison.Ordinal
				)
			)
			{
				item = current;
				return true;
			}

			current = current.GetNext();
		}

		return false;
	}

	private Texture2D GetFilterResultIcon(string entry)
	{
		if (IsSceneEntry(entry))
			return _sceneIcon;

		return string.IsNullOrWhiteSpace(GetLinkedScenePathFromEntry(entry))
			? _scriptIcon
			: _sceneIcon;
	}

	private void ExitScriptFilterMode()
	{
		PersistentTreeSelection? selectionToRestore = _persistentTreeSelection;

		if (
			TryCreatePersistentTreeSelection(
				_tree?.GetSelected(),
				out PersistentTreeSelection selectedFilterItem
			)
		)
		{
			selectionToRestore = selectedFilterItem;

			if (
				!_persistentTreeSelection.HasValue
				|| !IsSamePersistentTreeSelection(
					_persistentTreeSelection.Value,
					selectedFilterItem
				)
			)
			{
				_persistentTreeSelection = selectedFilterItem;
				QueuePersistentTreeStateSave();
			}
		}

		_isFilteringScripts = false;
		EnsureSystemsLoadedForScriptFilter("Script Filter Exited");
		_expandedItems.Clear();

		foreach (string metadata in _expandedItemsBeforeScriptFilter)
			_expandedItems.Add(metadata);

		RevealPersistentSelectionAfterFilter(selectionToRestore);
		BuildTree(true);

		if (selectionToRestore.HasValue)
			RestorePersistentTreeSelectionBestEffort("Script Filter Exit");
	}

	private void RevealPersistentSelectionAfterFilter(
		PersistentTreeSelection? exactSelection
	)
	{
		if (
			!exactSelection.HasValue
			|| !IsScriptOrScenePersistentTreeSelection(exactSelection.Value)
		)
		{
			return;
		}

		string entry = GetEntryFromMetadata(exactSelection.Value.Metadata);
		string systemName = exactSelection.Value.SystemName;
		string folderPath = GetFolderPathFromEntry(entry);

		if (string.IsNullOrWhiteSpace(entry) || string.IsNullOrWhiteSpace(systemName))
			return;

		if (string.IsNullOrWhiteSpace(folderPath))
			ForceExpandSystem(systemName);
		else
			ForceExpandFolderPath(systemName, folderPath);
	}

	private bool SelectTreeItemByMetadata(string metadata)
	{
		TreeItem root = _tree.GetRoot();

		if (root == null)
			return false;

		return SelectTreeItemByMetadataRecursive(root, metadata);
	}

	private bool SelectTreeItemByMetadataRecursive(TreeItem item, string metadata)
	{
		TreeItem current = item;

		while (current != null)
		{
			if (current.GetMetadata(0).AsString() == metadata)
			{
				current.Select(0);
				_tree.ScrollToItem(current);
				UpdateTreeLockIconVisibility();
				return true;
			}

			TreeItem child = current.GetFirstChild();

			if (child != null && SelectTreeItemByMetadataRecursive(child, metadata))
				return true;

			current = current.GetNext();
		}

		return false;
	}

	private readonly record struct ScriptFilterResult(
		string SystemName,
		string FolderPath,
		string Entry,
		string ItemName
	);

	#endregion
}
#endif
