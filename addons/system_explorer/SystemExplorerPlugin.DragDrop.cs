#if TOOLS
using Godot;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

public partial class SystemExplorerPlugin
{
	#region Drag and Drop Reordering
	private void MoveDraggedItem(string draggedMetadata, TreeItem targetItem)
	{
		string targetMetadata = targetItem?.GetMetadata(0).AsString() ?? "";

		DebugLogOperation(
			"Drag Move Requested",
			$"Dragged='{draggedMetadata}', Target='{targetMetadata}'"
		);

		if (string.IsNullOrWhiteSpace(draggedMetadata) || string.IsNullOrWhiteSpace(targetMetadata))
		{
			DebugLog("Drag Move cancelled: missing metadata.");
			return;
		}

		if (!EnsureSystemsLoadedForTreeOperation("Drag Move"))
			return;

		if (IsDragSourceLockedBySelfOrParentFolder(draggedMetadata))
		{
			DebugLogOperation("Drag Move cancelled: dragged item or parent folder is locked", draggedMetadata);
			return;
		}

		if (IsDropTargetSortingLocked(targetMetadata))
		{
			DebugLogOperation("Drag Move cancelled: target folder contents are locked", targetMetadata);
			return;
		}

		bool moved = false;

		if (draggedMetadata.StartsWith("system::") && targetMetadata.StartsWith("system::"))
		{
			if (draggedMetadata == targetMetadata)
			{
				DebugLog("Drag Move cancelled: dragged and target metadata are identical.");
				return;
			}

			moved = MoveSystem(draggedMetadata, targetMetadata);
		}
		else if (
			IsScriptOrSceneMetadata(draggedMetadata)
			&& IsValidScriptOrSceneDropTargetMetadata(targetMetadata)
		)
		{
			moved = MoveScriptOrSceneToDropTarget(draggedMetadata, targetItem);
		}
		else if (
			draggedMetadata != targetMetadata
			&& IsSystemEntryMetadata(draggedMetadata)
			&& IsSystemEntryMetadata(targetMetadata)
		)
		{
			moved = MoveSystemEntry(draggedMetadata, targetMetadata);
		}

		if (!moved)
		{
			DebugLogOperation(
				"Drag Move cancelled: mutation failed",
				$"Dragged='{draggedMetadata}', Target='{targetMetadata}'"
			);
			return;
		}

		if (SaveSystems())
			BuildTree();
	}

	private bool MoveSystem(string draggedMetadata, string targetMetadata)
	{
		if (!EnsureSystemsLoadedForTreeOperation("Move System"))
			return false;

		string draggedSystemName = draggedMetadata.Replace("system::", "");
		string targetSystemName = targetMetadata.Replace("system::", "");

		if (!EnsureSystemsAvailable(new[] { draggedSystemName, targetSystemName }, "Move System"))
			return false;

		List<KeyValuePair<string, List<string>>> orderedSystems = _systems.ToList();

		int draggedIndex = orderedSystems.FindIndex(system => system.Key == draggedSystemName);
		int targetIndex = orderedSystems.FindIndex(system => system.Key == targetSystemName);

		if (draggedIndex < 0 || targetIndex < 0 || draggedIndex == targetIndex)
			return false;

		bool moveDown = draggedIndex < targetIndex;
		KeyValuePair<string, List<string>> draggedSystem = orderedSystems[draggedIndex];

		orderedSystems.RemoveAt(draggedIndex);

		targetIndex = orderedSystems.FindIndex(system => system.Key == targetSystemName);

		if (targetIndex < 0)
			return false;

		int insertIndex = moveDown ? targetIndex + 1 : targetIndex;

		orderedSystems.Insert(insertIndex, draggedSystem);

		_systems.Clear();

		foreach (KeyValuePair<string, List<string>> system in orderedSystems)
			_systems[system.Key] = system.Value;

		DebugLogOperation("Move System Mutated", $"{draggedSystemName} -> {targetSystemName}");

		return true;
	}

	private bool MoveScriptOrSceneToDropTarget(string draggedMetadata, TreeItem targetItem)
	{
		if (!EnsureSystemsLoadedForTreeOperation("Move Script/Scene"))
			return false;

		if (targetItem == null)
			return false;

		string targetMetadata = targetItem.GetMetadata(0).AsString();

		if (!IsValidScriptOrSceneDropTargetMetadata(targetMetadata))
			return false;

		string draggedEntry = GetEntryFromMetadata(draggedMetadata);
		string draggedPath = GetPathFromEntry(draggedEntry);

		if (string.IsNullOrWhiteSpace(draggedEntry) || string.IsNullOrWhiteSpace(draggedPath))
			return false;

		string sourceSystemName = _draggedSourceSystemName;

		if (string.IsNullOrWhiteSpace(sourceSystemName))
			sourceSystemName = FindSystemNameForEntry(draggedEntry);

		string targetSystemName = GetSystemNameFromTreeItem(targetItem);
		string targetFolderPath = GetDropFolderPathFromTargetItem(targetItem);
		string targetEntry = IsScriptOrSceneMetadata(targetMetadata)
			? GetEntryFromMetadata(targetMetadata)
			: "";

		if (
			string.IsNullOrWhiteSpace(sourceSystemName)
			|| string.IsNullOrWhiteSpace(targetSystemName)
		)
		{
			TryRecoverSystemsFromDisk("Move Script/Scene Resolve Systems");

			if (string.IsNullOrWhiteSpace(sourceSystemName))
				sourceSystemName = FindSystemNameForEntry(draggedEntry);

			if (string.IsNullOrWhiteSpace(targetSystemName))
				targetSystemName = GetSystemNameFromTreeItem(targetItem);
		}

		if (!EnsureSystemsAvailable(new[] { sourceSystemName, targetSystemName }, "Move Script/Scene"))
			return false;

		List<string> sourceEntries = _systems[sourceSystemName];
		List<string> targetEntries = _systems[targetSystemName];

		int sourceIndex = sourceEntries.IndexOf(draggedEntry);

		if (sourceIndex < 0)
		{
			if (!TryRecoverSystemsFromDisk("Move Script/Scene Source Entry"))
				return false;

			if (!_systems.ContainsKey(sourceSystemName) || !_systems.ContainsKey(targetSystemName))
				return false;

			sourceEntries = _systems[sourceSystemName];
			targetEntries = _systems[targetSystemName];
			sourceIndex = sourceEntries.IndexOf(draggedEntry);

			if (sourceIndex < 0)
				return false;
		}

		string newEntry = IsSceneEntry(draggedEntry)
			? BuildSceneEntry(targetFolderPath, draggedPath, IsEntryLocked(draggedEntry))
			: BuildScriptEntry(
				targetFolderPath,
				draggedPath,
				GetLinkedScenePathFromEntry(draggedEntry),
				IsEntryLocked(draggedEntry)
			);

		bool sameSystem = sourceSystemName == targetSystemName;
		bool sameLocation = sameSystem && draggedEntry == newEntry;

		if (sameLocation && !IsScriptOrSceneMetadata(targetMetadata))
			return false;

		if (sameLocation && IsScriptOrSceneMetadata(targetMetadata) && draggedEntry == targetEntry)
			return false;

		int targetIndexBeforeRemove = IsScriptOrSceneMetadata(targetMetadata)
			? targetEntries.IndexOf(targetEntry)
			: -1;

		if (IsScriptOrSceneMetadata(targetMetadata) && targetIndexBeforeRemove < 0)
			return false;

		sourceEntries.RemoveAt(sourceIndex);

		if (IsScriptOrSceneMetadata(targetMetadata))
		{
			if (sameSystem)
				targetEntries = sourceEntries;

			int targetIndexAfterRemove = targetEntries.IndexOf(targetEntry);

			if (targetIndexAfterRemove < 0)
				return false;

			if (!targetEntries.Contains(newEntry))
			{
				bool moveDownInSameList = sameSystem && sourceIndex < targetIndexBeforeRemove;
				int insertIndex = moveDownInSameList
					? targetIndexAfterRemove + 1
					: targetIndexAfterRemove;

				targetEntries.Insert(insertIndex, newEntry);
			}
		}
		else if (!targetEntries.Contains(newEntry))
		{
			int insertIndex = GetAppendIndexForScriptDrop(targetEntries, targetFolderPath);
			targetEntries.Insert(insertIndex, newEntry);
		}

		if (string.IsNullOrWhiteSpace(targetFolderPath))
			ForceExpandSystem(targetSystemName);
		else
			ForceExpandFolderPath(targetSystemName, targetFolderPath);

		DebugLogOperation(
			"Move Script/Scene Mutated",
			$"{sourceSystemName}:{draggedEntry} -> {targetSystemName}:{newEntry}"
		);

		return true;
	}

	private bool MoveSystemEntry(string draggedMetadata, string targetMetadata)
	{
		if (!EnsureSystemsLoadedForTreeOperation("Move Entry"))
			return false;

		string draggedSystemName = GetSystemNameFromEntryMetadata(draggedMetadata);
		string targetSystemName = GetSystemNameFromEntryMetadata(targetMetadata);

		if (
			string.IsNullOrWhiteSpace(draggedSystemName)
			|| string.IsNullOrWhiteSpace(targetSystemName)
		)
		{
			TryRecoverSystemsFromDisk("Move Entry Resolve System");

			draggedSystemName = GetSystemNameFromEntryMetadata(draggedMetadata);
			targetSystemName = GetSystemNameFromEntryMetadata(targetMetadata);
		}

		if (string.IsNullOrWhiteSpace(draggedSystemName) || draggedSystemName != targetSystemName)
			return false;

		if (!EnsureSystemAvailable(draggedSystemName, "Move Entry"))
			return false;

		string draggedEntry = GetEntryFromMetadata(draggedMetadata);
		string targetEntry = GetEntryFromMetadata(targetMetadata);

		if (string.IsNullOrWhiteSpace(draggedEntry) || string.IsNullOrWhiteSpace(targetEntry))
			return false;

		List<string> entries = _systems[draggedSystemName];

		int draggedIndex = FindEntryIndex(entries, draggedEntry);
		int targetIndex = FindEntryIndex(entries, targetEntry);

		if (draggedIndex < 0 || targetIndex < 0 || draggedIndex == targetIndex)
			return false;

		string draggedParentPath = GetParentFolderPathFromEntryMetadata(draggedMetadata);
		string targetParentPath = GetParentFolderPathFromEntryMetadata(targetMetadata);

		if (draggedParentPath != targetParentPath)
			return false;

		bool moveDown = draggedIndex < targetIndex;

		entries.RemoveAt(draggedIndex);

		targetIndex = FindEntryIndex(entries, targetEntry);

		if (targetIndex < 0)
			return false;

		int insertIndex = moveDown ? targetIndex + 1 : targetIndex;

		entries.Insert(insertIndex, draggedEntry);

		DebugLogOperation("Move Entry Mutated", $"{draggedEntry} -> {targetEntry}");

		return true;
	}

	private static int FindEntryIndex(List<string> entries, string entryToFind)
	{
		int index = entries.IndexOf(entryToFind);

		if (index >= 0 || !entryToFind.StartsWith("folder::"))
			return index;

		string folderPath = GetFolderPathFromFolderEntry(entryToFind);
		return entries.FindIndex(entry => entry.StartsWith("folder::") && GetFolderPathFromFolderEntry(entry) == folderPath);
	}

	private static bool IsSystemEntryMetadata(string metadata)
	{
		return metadata.StartsWith("folder::") || IsScriptOrSceneMetadata(metadata);
	}

	private static bool IsSystemLockEntry(string entry)
	{
		return entry == SystemLockEntry;
	}

	private static bool IsTreeContentEntry(string entry)
	{
		return !IsSystemLockEntry(entry);
	}

	private static bool IsScriptOrSceneEntry(string entry)
	{
		return IsTreeContentEntry(entry) && !entry.StartsWith("folder::");
	}

	private static bool IsScriptOrSceneMetadata(string metadata)
	{
		return metadata.StartsWith("script::") || metadata.StartsWith("sceneLink::");
	}

	private static bool IsLockableMetadata(string metadata)
	{
		return metadata.StartsWith("system::")
			|| metadata.StartsWith("folder::")
			|| IsScriptOrSceneMetadata(metadata);
	}

	private static bool IsValidScriptOrSceneDropTargetMetadata(string metadata)
	{
		return metadata.StartsWith("system::")
			|| metadata.StartsWith("folder::")
			|| IsScriptOrSceneMetadata(metadata);
	}

	private static int GetAppendIndexForScriptDrop(List<string> entries, string targetFolderPath)
	{
		for (int i = entries.Count - 1; i >= 0; i--)
		{
			string entry = entries[i];

			if (entry.StartsWith("folder::"))
				continue;

			if (GetFolderPathFromEntry(entry) == targetFolderPath)
				return i + 1;
		}

		if (!string.IsNullOrWhiteSpace(targetFolderPath))
		{
			int folderIndex = entries.IndexOf($"folder::{targetFolderPath}");

			if (folderIndex >= 0)
				return folderIndex + 1;
		}

		return entries.Count;
	}

	private string GetSystemNameFromEntryMetadata(string metadata)
	{
		if (metadata.StartsWith("system::"))
			return metadata.Replace("system::", "");

		if (metadata.StartsWith("folder::"))
			return GetSystemNameFromMetadata(metadata);

		if (metadata.StartsWith("script::"))
		{
			string entry = GetEntryFromMetadata(metadata);
			return FindSystemNameForEntry(entry);
		}

		if (metadata.StartsWith("sceneLink::"))
		{
			string entry = metadata.Substring("sceneLink::".Length);
			return FindSystemNameForEntry(entry);
		}

		return "";
	}

	private string FindSystemNameForEntry(string entry)
	{
		foreach (KeyValuePair<string, List<string>> system in _systems)
		{
			if (system.Value.Contains(entry))
				return system.Key;
		}

		return "";
	}

	private static string GetEntryFromMetadata(string metadata)
	{
		if (metadata.StartsWith("script::"))
			return metadata.Substring("script::".Length);

		if (metadata.StartsWith("sceneLink::"))
			return metadata.Substring("sceneLink::".Length);

		if (metadata.StartsWith("folder::"))
		{
			string folderPath = GetFolderPathFromMetadata(metadata);
			return string.IsNullOrWhiteSpace(folderPath) ? "" : BuildFolderEntry(folderPath);
		}

		return "";
	}

	private string GetLockableEntryFromMetadata(string metadata)
	{
		if (!metadata.StartsWith("folder::"))
			return GetEntryFromMetadata(metadata);

		string systemName = GetSystemNameFromMetadata(metadata);
		string folderPath = GetFolderPathFromMetadata(metadata);

		if (!EnsureSystemAvailable(systemName, "Resolve Folder Lock Entry"))
			return "";

		return _systems[systemName]
			.FirstOrDefault(entry => entry.StartsWith("folder::") && GetFolderPathFromFolderEntry(entry) == folderPath)
			?? BuildFolderEntry(folderPath);
	}

	private static string GetParentFolderPathFromEntryMetadata(string metadata)
	{
		if (metadata.StartsWith("script::"))
		{
			string entry = GetEntryFromMetadata(metadata);
			return GetFolderPathFromEntry(entry);
		}

		if (metadata.StartsWith("sceneLink::"))
		{
			string entry = metadata.Substring("sceneLink::".Length);
			return GetFolderPathFromEntry(entry);
		}

		if (metadata.StartsWith("folder::"))
		{
			string folderPath = GetFolderPathFromMetadata(metadata);

			if (!folderPath.Contains("/"))
				return "";

			return folderPath.Substring(0, folderPath.LastIndexOf('/'));
		}

		return "";
	}

	private static string GetSystemNameFromTreeItem(TreeItem item)
	{
		TreeItem current = item;

		while (current != null)
		{
			string metadata = current.GetMetadata(0).AsString();

			if (metadata.StartsWith("system::"))
				return metadata.Replace("system::", "");

			if (metadata.StartsWith("folder::"))
				return GetSystemNameFromMetadata(metadata);

			current = current.GetParent();
		}

		return "";
	}

	private static string GetFolderPathFromTreeItem(TreeItem item)
	{
		TreeItem current = item;

		while (current != null)
		{
			string metadata = current.GetMetadata(0).AsString();

			if (metadata.StartsWith("folder::"))
				return GetFolderPathFromMetadata(metadata);

			current = current.GetParent();
		}

		return "";
	}

	private static string GetDropFolderPathFromTargetItem(TreeItem targetItem)
	{
		if (targetItem == null)
			return "";

		string targetMetadata = targetItem.GetMetadata(0).AsString();

		if (targetMetadata.StartsWith("folder::"))
			return GetFolderPathFromMetadata(targetMetadata);

		if (targetMetadata.StartsWith("script::") || targetMetadata.StartsWith("sceneLink::"))
			return GetFolderPathFromEntry(GetEntryFromMetadata(targetMetadata));

		return "";
	}

	private void UpdateDragDropTargetHighlight()
	{
		if (_tree == null || string.IsNullOrWhiteSpace(_draggedMetadata))
		{
			ClearDragDropTargetHighlight();
			return;
		}

		Vector2 mousePosition = _tree.GetLocalMousePosition();

		if (_leftMousePressPosition.DistanceTo(mousePosition) <= ClickOpenDragThreshold)
		{
			ClearDragDropTargetHighlight();
			return;
		}

		TreeItem targetItem = _tree.GetItemAtPosition(mousePosition);

		if (!CanHighlightDragDropTarget(_draggedMetadata, targetItem))
		{
			ClearDragDropTargetHighlight();
			return;
		}

		if (_dragDropHighlightedItem == targetItem)
			return;

		ClearDragDropTargetHighlight();

		_dragDropHighlightedItem = targetItem;
		_dragDropHighlightedItem.SetCustomBgColor(0, DragDropTargetHighlightColor);
	}

	private void ClearDragDropTargetHighlight()
	{
		if (_dragDropHighlightedItem == null)
			return;

		if (GodotObject.IsInstanceValid(_dragDropHighlightedItem))
			_dragDropHighlightedItem.ClearCustomBgColor(0);

		_dragDropHighlightedItem = null;
	}

	private bool CanHighlightDragDropTarget(string draggedMetadata, TreeItem targetItem)
	{
		if (targetItem == null || string.IsNullOrWhiteSpace(draggedMetadata))
			return false;

		string targetMetadata = targetItem.GetMetadata(0).AsString();

		if (string.IsNullOrWhiteSpace(targetMetadata) || draggedMetadata == targetMetadata)
			return false;

		if (IsDragSourceLockedBySelfOrParentFolder(draggedMetadata))
			return false;

		if (IsDropTargetSortingLocked(targetMetadata))
			return false;

		if (draggedMetadata.StartsWith("system::"))
			return targetMetadata.StartsWith("system::");

		if (IsScriptOrSceneMetadata(draggedMetadata))
			return IsValidScriptOrSceneDropTargetMetadata(targetMetadata);

		return IsSystemEntryMetadata(draggedMetadata)
			&& IsSystemEntryMetadata(targetMetadata)
			&& GetSystemNameFromEntryMetadata(draggedMetadata) == GetSystemNameFromEntryMetadata(targetMetadata)
			&& GetParentFolderPathFromEntryMetadata(draggedMetadata) == GetParentFolderPathFromEntryMetadata(targetMetadata);
	}

	private void ClearDragState()
	{
		ClearDragDropTargetHighlight();

		_draggedMetadata = "";
		_draggedSourceSystemName = "";
		_draggedSourceFolderPath = "";
		_leftMousePressedOnSelectedScript = false;
		_leftMousePressPosition = Vector2.Zero;
		_leftMousePressedMetadata = "";
	}

	#endregion
}
#endif
