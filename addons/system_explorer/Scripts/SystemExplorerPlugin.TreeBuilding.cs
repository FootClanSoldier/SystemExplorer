#if TOOLS
using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

public partial class SystemExplorerPlugin
{
	#region Tree Building and Expansion State
	private void BuildTree(bool keepCurrentExpansionState = false)
	{
		if (IsScriptFilterActive())
		{
			BuildFilteredItemTree(_scriptFilterInput.Text);
			return;
		}

		DebugLogOperation("Build Tree", $"{_systems.Count} systems");

		if (!keepCurrentExpansionState)
			SaveExpansionState();
		MergeForcedExpansionState();
		NormalizeAllSystemEntries();

		_tree.Clear();

		TreeItem root = _tree.CreateItem();

		foreach (KeyValuePair<string, List<string>> system in _systems)
		{
			TreeItem systemItem = _tree.CreateItem(root);
			systemItem.SetText(
				0,
				GetLockableItemDisplayName(
					$"system::{system.Key}",
					system.Key,
					IsSystemLocked(system.Key)
				)
			);
			systemItem.SetIcon(0, _systemIcon);
			systemItem.SetIconModulate(0, _systemColor);
			systemItem.SetMetadata(0, $"system::{system.Key}");
			systemItem.Collapsed = true;

			Dictionary<string, TreeItem> folders = new();
			Dictionary<string, bool> lockedFolders = GetLockedFoldersForSystem(system.Value);

			foreach (string entry in system.Value.Where(entry => entry.StartsWith("folder::")))
			{
				CreateFolderPath(
					systemItem,
					folders,
					system.Key,
					GetFolderPathFromFolderEntry(entry),
					lockedFolders
				);
			}

			foreach (string entry in system.Value.Where(IsScriptOrSceneEntry))
			{
				string folderPath = GetFolderPathFromEntry(entry);
				TreeItem parent = string.IsNullOrWhiteSpace(folderPath)
					? systemItem
					: CreateFolderPath(systemItem, folders, system.Key, folderPath, lockedFolders);

				if (IsSceneEntry(entry))
				{
					TreeItem sceneItem = _tree.CreateItem(parent);
					string scenePath = GetScenePathFromEntry(entry);

					sceneItem.SetText(
						0,
						GetLockableItemDisplayName(
							$"sceneLink::{entry}",
							scenePath.GetFile(),
							IsEntryLocked(entry)
						)
					);
					sceneItem.SetIcon(0, _sceneIcon);
					sceneItem.SetTooltipText(0, scenePath);
					sceneItem.SetMetadata(0, $"sceneLink::{entry}");
					continue;
				}

				TreeItem scriptItem = _tree.CreateItem(parent);

				string linkedScenePath = GetLinkedScenePathFromEntry(entry);
				string scriptText = GetScriptPathFromEntry(entry).GetFile();

				scriptItem.SetTooltipText(0, GetScriptTooltipText(entry));

				scriptItem.SetText(
					0,
					GetLockableItemDisplayName($"script::{entry}", scriptText, IsEntryLocked(entry))
				);
				scriptItem.SetIcon(
					0,
					string.IsNullOrWhiteSpace(linkedScenePath) ? _scriptIcon : _sceneIcon
				);
				scriptItem.SetMetadata(0, $"script::{entry}");
			}
		}

		RestoreExpansionState(root);
	}

	private string GetLockableItemDisplayName(string metadata, string displayName, string entry)
	{
		return GetLockableItemDisplayName(metadata, displayName, IsEntryLocked(entry));
	}

	private string GetLockableItemDisplayName(string metadata, string displayName, bool isLocked)
	{
		return isLocked && ShouldShowLockIconForMetadata(metadata)
			? $"{displayName}  \U0001F512" //Lock Icon
			: displayName;
	}

	private bool ShouldShowLockIconForMetadata(string metadata)
	{
		if (string.IsNullOrWhiteSpace(metadata))
			return false;

		if (_hoveredTreeItemMetadata == metadata)
			return true;

		return _tree?.GetSelected()?.GetMetadata(0).AsString() == metadata;
	}

	private void UpdateTreeLockIconVisibility()
	{
		TreeItem root = _tree?.GetRoot();

		if (root == null)
			return;

		UpdateTreeLockIconVisibilityRecursive(root);
	}

	private void UpdateTreeLockIconVisibilityRecursive(TreeItem item)
	{
		TreeItem current = item;

		while (current != null)
		{
			UpdateTreeItemLockIconVisibility(current);

			TreeItem child = current.GetFirstChild();

			if (child != null)
				UpdateTreeLockIconVisibilityRecursive(child);

			current = current.GetNext();
		}
	}

	private void UpdateTreeItemLockIconVisibility(TreeItem item)
	{
		string metadata = item.GetMetadata(0).AsString();

		if (string.IsNullOrWhiteSpace(metadata))
			return;

		if (!TryGetLockableItemDisplayState(metadata, out string displayName, out bool isLocked))
			return;

		item.SetText(0, GetLockableItemDisplayName(metadata, displayName, isLocked));
	}

	private bool TryGetLockableItemDisplayState(
		string metadata,
		out string displayName,
		out bool isLocked
	)
	{
		displayName = "";
		isLocked = false;

		if (metadata.StartsWith("system::"))
		{
			string systemName = GetSystemNameFromMetadata(metadata);
			displayName = systemName;
			isLocked = IsSystemLocked(systemName);
			return !string.IsNullOrWhiteSpace(displayName);
		}

		if (metadata.StartsWith("folder::"))
		{
			string folderPath = GetFolderPathFromMetadata(metadata);
			displayName = folderPath.GetFile();
			isLocked = IsFolderLocked(metadata);
			return !string.IsNullOrWhiteSpace(displayName);
		}

		if (metadata.StartsWith("sceneLink::"))
		{
			string entry = GetEntryFromMetadata(metadata);
			displayName = GetScenePathFromEntry(entry).GetFile();
			isLocked = IsEntryLocked(entry);
			return !string.IsNullOrWhiteSpace(displayName);
		}

		if (metadata.StartsWith("script::"))
		{
			string entry = GetEntryFromMetadata(metadata);
			displayName = GetScriptPathFromEntry(entry).GetFile();
			isLocked = IsEntryLocked(entry);
			return !string.IsNullOrWhiteSpace(displayName);
		}

		return false;
	}

	private static Dictionary<string, bool> GetLockedFoldersForSystem(List<string> entries)
	{
		Dictionary<string, bool> lockedFolders = new();

		foreach (string entry in entries.Where(entry => entry.StartsWith("folder::")))
		{
			string folderPath = GetFolderPathFromFolderEntry(entry);

			if (!string.IsNullOrWhiteSpace(folderPath))
				lockedFolders[folderPath] = IsEntryLocked(entry);
		}

		return lockedFolders;
	}

	private string GetScriptTooltipText(string entry)
	{
		string linkedScenePath = GetLinkedScenePathFromEntry(entry);
		string scriptPath = GetScriptPathFromEntry(entry);
		string tooltipText = $"{scriptPath}";

		if (!string.IsNullOrWhiteSpace(linkedScenePath))
		{
			tooltipText += $"\n{linkedScenePath}";
		}

		return tooltipText;
	}

	private void MergeForcedExpansionState()
	{
		foreach (string metadata in _forcedExpandedItems)
		{
			if (!string.IsNullOrWhiteSpace(metadata))
				_expandedItems.Add(metadata);
		}

		_forcedExpandedItems.Clear();
	}

	private void ForceExpandSystem(string systemName)
	{
		if (string.IsNullOrWhiteSpace(systemName))
			return;

		_forcedExpandedItems.Add($"system::{systemName}");
	}

	private void ForceExpandFolderPath(string systemName, string folderPath)
	{
		ForceExpandSystem(systemName);

		if (string.IsNullOrWhiteSpace(systemName) || string.IsNullOrWhiteSpace(folderPath))
			return;

		string[] parts = folderPath.Split("/", System.StringSplitOptions.RemoveEmptyEntries);
		string currentPath = "";

		foreach (string part in parts)
		{
			currentPath = string.IsNullOrWhiteSpace(currentPath) ? part : $"{currentPath}/{part}";
			_forcedExpandedItems.Add($"folder::{systemName}::{currentPath}");
		}
	}

	private void ForceExpandForSelectedTreeLocation()
	{
		string systemName = GetSelectedSystemName();
		string folderPath = GetSelectedFolderPath();

		if (string.IsNullOrWhiteSpace(systemName))
			return;

		if (string.IsNullOrWhiteSpace(folderPath))
			ForceExpandSystem(systemName);
		else
			ForceExpandFolderPath(systemName, folderPath);
	}

	private void SaveExpansionState()
	{
		_expandedItems.Clear();

		if (_tree == null)
			return;

		TreeItem root = _tree.GetRoot();

		if (root == null)
			return;

		SaveExpandedRecursive(root);
	}

	private HashSet<string> CaptureTreeExpansionStateSnapshot()
	{
		SaveExpansionState();

		return new HashSet<string>(_expandedItems, StringComparer.OrdinalIgnoreCase);
	}

	private void RestoreTreeExpansionStateSnapshot(HashSet<string> expandedItemsSnapshot)
	{
		if (expandedItemsSnapshot == null)
			return;

		_expandedItems.Clear();

		foreach (string metadata in expandedItemsSnapshot)
		{
			if (!string.IsNullOrWhiteSpace(metadata))
				_expandedItems.Add(metadata);
		}

		TreeItem root = _tree?.GetRoot();

		if (root == null)
			return;

		RestoreTreeExpansionStateSnapshotRecursive(root, _expandedItems);
	}

	private static void RestoreTreeExpansionStateSnapshotRecursive(
		TreeItem item,
		HashSet<string> expandedItems
	)
	{
		TreeItem current = item;

		while (current != null)
		{
			TreeItem child = current.GetFirstChild();
			string metadata = current.GetMetadata(0).AsString();

			if (!string.IsNullOrWhiteSpace(metadata) && child != null)
				current.Collapsed = !expandedItems.Contains(metadata);

			if (child != null)
				RestoreTreeExpansionStateSnapshotRecursive(child, expandedItems);

			current = current.GetNext();
		}
	}

	private void SaveExpandedRecursive(TreeItem item)
	{
		TreeItem current = item;

		while (current != null)
		{
			string metadata = current.GetMetadata(0).AsString();

			if (!string.IsNullOrWhiteSpace(metadata) && !current.Collapsed)
				_expandedItems.Add(metadata);

			TreeItem child = current.GetFirstChild();

			if (child != null)
				SaveExpandedRecursive(child);

			current = current.GetNext();
		}
	}

	private void RestoreExpansionState(TreeItem item)
	{
		TreeItem current = item;

		while (current != null)
		{
			string metadata = current.GetMetadata(0).AsString();

			if (_expandedItems.Contains(metadata))
				current.Collapsed = false;

			TreeItem child = current.GetFirstChild();

			if (child != null)
				RestoreExpansionState(child);

			current = current.GetNext();
		}
	}

	private TreeItem CreateFolderPath(
		TreeItem systemItem,
		Dictionary<string, TreeItem> folders,
		string systemName,
		string folderPath,
		Dictionary<string, bool> lockedFolders = null
	)
	{
		string[] parts = folderPath.Split("/", System.StringSplitOptions.RemoveEmptyEntries);

		TreeItem parent = systemItem;
		string currentPath = "";

		foreach (string part in parts)
		{
			currentPath = string.IsNullOrWhiteSpace(currentPath) ? part : $"{currentPath}/{part}";

			if (!folders.ContainsKey(currentPath))
			{
				TreeItem folderItem = _tree.CreateItem(parent);
				bool isLocked =
					lockedFolders != null
					&& lockedFolders.TryGetValue(currentPath, out bool locked)
					&& locked;
				folderItem.SetText(
					0,
					GetLockableItemDisplayName(
						$"folder::{systemName}::{currentPath}",
						part,
						isLocked
					)
				);
				folderItem.SetIcon(0, _folderIcon);
				folderItem.SetIconModulate(0, _folderColor);
				folderItem.SetMetadata(0, $"folder::{systemName}::{currentPath}");
				folderItem.Collapsed = true;
				folders[currentPath] = folderItem;
			}

			parent = folders[currentPath];
		}

		return parent;
	}

	#endregion

	#region Tree Collapse Helpers
	private void CollapseEntireTree()
	{
		_expandedItems.Clear();
		_forcedExpandedItems.Clear();
		_expandedItemsBeforeScriptFilter.Clear();

		if (_tree == null)
			return;

		TreeItem root = _tree.GetRoot();

		if (root == null)
			return;

		root.Collapsed = false;

		TreeItem firstVisibleItem = root.GetFirstChild();

		if (firstVisibleItem == null)
			return;

		CollapseTreeItemsRecursive(firstVisibleItem);
		_tree.DeselectAll();
	}

	private static void CollapseTreeItemsRecursive(TreeItem item)
	{
		TreeItem current = item;

		while (current != null)
		{
			current.Collapsed = true;

			TreeItem child = current.GetFirstChild();

			if (child != null)
				CollapseTreeItemsRecursive(child);

			current = current.GetNext();
		}
	}

	#endregion
}
#endif
