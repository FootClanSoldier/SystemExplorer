#if TOOLS
using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

public partial class SystemExplorerPlugin
{
	#region Script Filter
	private bool _isClearingScriptFilterInputProgrammatically;
	private ScriptFilterResult[] _currentScriptFilterProjection = Array.Empty<ScriptFilterResult>();
	private string _currentScriptFilterNormalizedText = "";
	private bool _currentScriptFilterProjectionIsValid;

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

		if (!_isFilteringScripts || _isClearingScriptFilterInputProgrammatically)
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

	private void ClearScriptFilterInput(
		PersistentTreeSelection? preferredExactSelection = null
	)
	{
		if (_scriptFilterInput == null)
			return;

		PersistentTreeSelection? selectionToRestore = preferredExactSelection;

		if (
			!selectionToRestore.HasValue
			&& TryCaptureSelectedScriptFilterSelection(
				out PersistentTreeSelection selectedFilterItem
			)
		)
		{
			selectionToRestore = selectedFilterItem;
		}

		// Programmatic text changes may emit TextChanged synchronously in some
		// editor states. Keep the signal callback from exiting filter mode before
		// the exact flat-row identity captured above can be supplied explicitly.
		_isClearingScriptFilterInputProgrammatically = true;

		try
		{
			_scriptFilterInput.Text = "";
			UpdateScriptFilterSearchIconVisibility("");
		}
		finally
		{
			_isClearingScriptFilterInputProgrammatically = false;
		}

		if (_isFilteringScripts)
			ExitScriptFilterMode(selectionToRestore);
	}

	private bool TryCaptureSelectedScriptFilterSelection(
		out PersistentTreeSelection selection
	)
	{
		selection = default;

		return _isFilteringScripts
			&& IsScriptFilterActive()
			&& TryCreatePersistentTreeSelection(
				_tree?.GetSelected(),
				out selection
			);
	}

	private bool TryClearActiveScriptFilterFromTreeKeyboardContext()
	{
		if (!_isFilteringScripts || !IsScriptFilterActive())
			return false;

		PersistentTreeSelection? exactSelection = null;

		if (
			TryCaptureSelectedScriptFilterSelection(
				out PersistentTreeSelection selectedFilterItem
			)
		)
		{
			exactSelection = selectedFilterItem;
		}

		ClearScriptFilterInput(exactSelection);
		CallDeferred(nameof(ReleaseTreeFocusAfterNavigation));
		return true;
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
		InvalidateCurrentScriptFilterProjection();

		if (!EnsureSystemsLoadedForScriptFilter("Build Filtered Item Tree"))
			return;

		NormalizeAllSystemEntries();

		string normalizedFilter = NormalizeScriptFilterText(filterText);
		ScriptFilterResult[] projection = string.IsNullOrWhiteSpace(normalizedFilter)
			? Array.Empty<ScriptFilterResult>()
			: GetFilteredScriptResults(normalizedFilter).ToArray();

		_tree.Clear();

		TreeItem root = _tree.CreateItem();

		foreach (ScriptFilterResult result in projection)
		{
			TreeItem item = _tree.CreateItem(root);
			bool isSceneEntry = IsSceneEntry(result.Entry);
			string metadata = GetScriptFilterResultMetadata(result);

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

		_currentScriptFilterProjection = projection;
		_currentScriptFilterNormalizedText = normalizedFilter;
		_currentScriptFilterProjectionIsValid = true;
	}

	private List<ScriptFilterResult> GetFilteredScriptResults(string normalizedFilter)
	{
		List<ScriptFilterResult> results = new();

		foreach (KeyValuePair<string, List<string>> system in _systems)
		{
			List<string> entries = system.Value;

			if (entries == null)
				continue;

			foreach (string entry in entries)
			{
				if (string.IsNullOrWhiteSpace(entry) || !IsScriptOrSceneEntry(entry))
					continue;

				string itemPath = GetPathFromEntry(entry);

				if (string.IsNullOrWhiteSpace(itemPath))
					continue;

				string itemName = itemPath.GetFile();

				if (string.IsNullOrWhiteSpace(itemName))
					continue;

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

	private static string NormalizeScriptFilterText(string filterText)
	{
		return (filterText ?? "").Trim().ToLowerInvariant();
	}

	private void InvalidateCurrentScriptFilterProjection()
	{
		_currentScriptFilterProjection = Array.Empty<ScriptFilterResult>();
		_currentScriptFilterNormalizedText = "";
		_currentScriptFilterProjectionIsValid = false;
	}

	private bool TryGetCurrentScriptFilterProjection(
		out ScriptFilterResult[] projection
	)
	{
		projection = Array.Empty<ScriptFilterResult>();

		if (
			!_isFilteringScripts
			|| !IsScriptFilterActive()
			|| !_currentScriptFilterProjectionIsValid
		)
		{
			return false;
		}

		string currentNormalizedFilter = NormalizeScriptFilterText(_scriptFilterInput?.Text);

		if (
			!string.Equals(
				currentNormalizedFilter,
				_currentScriptFilterNormalizedText,
				StringComparison.Ordinal
			)
		)
		{
			return false;
		}

		projection = _currentScriptFilterProjection;
		return true;
	}

	private bool TryGetScriptFilterResultForTreeItem(
		TreeItem item,
		out ScriptFilterResult matchingResult
	)
	{
		matchingResult = default;

		if (
			_tree == null
			|| item == null
			|| !TryGetCurrentScriptFilterProjection(out ScriptFilterResult[] projection)
		)
		{
			return false;
		}

		TreeItem current = _tree.GetRoot()?.GetFirstChild();
		int currentIndex = 0;
		int matchingIndex = -1;

		while (current != null)
		{
			if (current == item)
				matchingIndex = currentIndex;

			currentIndex++;
			current = current.GetNext();
		}

		if (currentIndex != projection.Length || matchingIndex < 0)
			return false;

		ScriptFilterResult result = projection[matchingIndex];

		if (!DoesScriptFilterTreeItemMatchResult(item, result))
			return false;

		matchingResult = result;
		return true;
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
			_tree == null
			|| string.IsNullOrWhiteSpace(systemName)
			|| string.IsNullOrWhiteSpace(entry)
			|| !TryGetCurrentScriptFilterProjection(out ScriptFilterResult[] projection)
		)
		{
			return false;
		}

		TreeItem current = _tree.GetRoot()?.GetFirstChild();
		TreeItem matchingItem = null;
		int index = 0;

		while (current != null && index < projection.Length)
		{
			ScriptFilterResult result = projection[index];

			if (!DoesScriptFilterTreeItemMatchResult(current, result))
				return false;

			if (
				matchingItem == null
				&& string.Equals(result.SystemName, systemName, StringComparison.Ordinal)
				&& string.Equals(result.Entry, entry, StringComparison.Ordinal)
				&& IsSceneEntry(result.Entry) == isSceneEntry
			)
			{
				matchingItem = current;
			}

			index++;
			current = current.GetNext();
		}

		if (current != null || index != projection.Length || matchingItem == null)
			return false;

		item = matchingItem;
		return true;
	}

	private static string GetScriptFilterResultMetadata(ScriptFilterResult result)
	{
		return IsSceneEntry(result.Entry)
			? $"sceneLink::{result.Entry}"
			: $"script::{result.Entry}";
	}

	private static bool DoesScriptFilterTreeItemMatchResult(
		TreeItem item,
		ScriptFilterResult result
	)
	{
		if (item == null || string.IsNullOrWhiteSpace(result.Entry))
			return false;

		return string.Equals(
			item.GetMetadata(0).AsString(),
			GetScriptFilterResultMetadata(result),
			StringComparison.Ordinal
		);
	}

	private Texture2D GetFilterResultIcon(string entry)
	{
		if (IsSceneEntry(entry))
			return _sceneIcon;

		return string.IsNullOrWhiteSpace(GetLinkedScenePathFromEntry(entry))
			? _scriptIcon
			: _sceneIcon;
	}

	private void ExitScriptFilterMode(
		PersistentTreeSelection? preferredExactSelection = null
	)
	{
		PersistentTreeSelection? selectionToRestore =
			preferredExactSelection ?? _persistentTreeSelection;

		if (
			!preferredExactSelection.HasValue
			&& TryCaptureSelectedScriptFilterSelection(
				out PersistentTreeSelection selectedFilterItem
			)
		)
		{
			selectionToRestore = selectedFilterItem;
		}

		if (
			selectionToRestore.HasValue
			&& (
				!_persistentTreeSelection.HasValue
				|| !IsSamePersistentTreeSelection(
					_persistentTreeSelection.Value,
					selectionToRestore.Value
				)
			)
		)
		{
			_persistentTreeSelection = selectionToRestore.Value;
			QueuePersistentTreeStateSave();
		}

		InvalidateCurrentScriptFilterProjection();
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
