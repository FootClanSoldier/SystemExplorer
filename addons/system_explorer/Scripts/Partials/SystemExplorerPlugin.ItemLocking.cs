#if TOOLS
using Godot;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

public partial class SystemExplorerPlugin
{
    #region Item Locking
    private void ToggleSelectedItemLock()
    {
        ToggleItemLock(_tree.GetSelected());
    }

    private void ToggleItemLock(TreeItem item, bool selectToggledItemAfterBuild = true)
    {
        if (item == null)
            return;

        string metadata = item.GetMetadata(0).AsString();
        TreeItem selectedItemBeforeToggle = _tree.GetSelected();
        string selectedMetadataBeforeToggle =
            selectedItemBeforeToggle?.GetMetadata(0).AsString() ?? "";
        ScriptTreeOccurrence? selectedScriptOccurrenceBeforeToggle = null;

        if (
            !selectToggledItemAfterBuild
            && (metadata.StartsWith("system::") || metadata.StartsWith("folder::"))
            && TryGetScriptTreeOccurrenceFromTreeItem(
                selectedItemBeforeToggle,
                out ScriptTreeOccurrence selectedScriptOccurrence
            )
        )
        {
            selectedScriptOccurrenceBeforeToggle = selectedScriptOccurrence;
        }

        if (!IsLockableMetadata(metadata))
            return;

        if (!EnsureSystemsLoadedForTreeOperation("Toggle Item Lock"))
            return;

        if (metadata.StartsWith("system::"))
        {
            ToggleSystemLock(
                metadata,
                selectedMetadataBeforeToggle,
                selectedScriptOccurrenceBeforeToggle,
                selectToggledItemAfterBuild
            );
            return;
        }

        if (IsInsideLockedSystem(metadata))
        {
            DebugLogger.LogOperation(
                "Toggle Item Lock cancelled: parent system is locked",
                metadata
            );
            return;
        }

        if (IsInsideLockedFolder(metadata, includeSelf: false))
        {
            DebugLogger.LogOperation(
                "Toggle Item Lock cancelled: parent folder is locked",
                metadata
            );
            return;
        }

        string oldEntry = GetLockableEntryFromMetadata(metadata);

        if (string.IsNullOrWhiteSpace(oldEntry))
            return;

        string newEntry = SetEntryLocked(oldEntry, !IsEntryLocked(oldEntry));
        bool replacedEntry = metadata.StartsWith("folder::")
            ? ReplaceEntryInSystem(
                GetSystemNameFromMetadata(metadata),
                oldEntry,
                newEntry,
                "Toggle Folder Lock"
            )
            : ReplaceEntry(oldEntry, newEntry);

        if (!replacedEntry)
        {
            DebugLogger.LogOperation("Toggle Item Lock cancelled: mutation failed", oldEntry);
            return;
        }

        if (SaveSystems())
        {
            SaveExpansionState();
            ClearDragState();
            BuildTree(keepCurrentExpansionState: true);

            string toggledMetadataAfterBuild = GetMetadataAfterLockToggle(metadata, newEntry);

            if (!selectToggledItemAfterBuild)
                _hoveredTreeItemMetadata = toggledMetadataAfterBuild;

            RestoreSelectionAfterItemLockBuild(
                toggledMetadataAfterBuild,
                selectedMetadataBeforeToggle,
                selectedScriptOccurrenceBeforeToggle,
                selectToggledItemAfterBuild
            );

            UpdateTreeLockIconVisibility();
        }
    }

    private void ToggleSystemLock(
        string metadata,
        string selectedMetadataBeforeToggle,
        ScriptTreeOccurrence? selectedScriptOccurrenceBeforeToggle,
        bool selectToggledItemAfterBuild
    )
    {
        string systemName = GetSystemNameFromMetadata(metadata);

        if (!EnsureSystemAvailable(systemName, "Toggle System Lock"))
            return;

        List<string> entries = _systems[systemName];
        bool isLocked = IsSystemLocked(systemName);

        if (isLocked)
        {
            entries.RemoveAll(IsSystemLockEntry);
            DebugLogger.LogOperation("Toggle System Lock Mutated", $"{systemName} unlocked");
        }
        else
        {
            if (!entries.Contains(SystemLockEntry))
                entries.Insert(0, SystemLockEntry);

            DebugLogger.LogOperation("Toggle System Lock Mutated", $"{systemName} locked");
        }

        if (SaveSystems())
        {
            SaveExpansionState();
            ClearDragState();
            BuildTree(keepCurrentExpansionState: true);

            if (!selectToggledItemAfterBuild)
                _hoveredTreeItemMetadata = metadata;

            RestoreSelectionAfterItemLockBuild(
                metadata,
                selectedMetadataBeforeToggle,
                selectedScriptOccurrenceBeforeToggle,
                selectToggledItemAfterBuild
            );

            UpdateTreeLockIconVisibility();
        }
    }

    private void RestoreSelectionAfterItemLockBuild(
        string toggledMetadataAfterBuild,
        string selectedMetadataBeforeToggle,
        ScriptTreeOccurrence? selectedScriptOccurrenceBeforeToggle,
        bool selectToggledItemAfterBuild
    )
    {
        if (selectToggledItemAfterBuild)
        {
            if (!string.IsNullOrWhiteSpace(toggledMetadataAfterBuild))
                SelectTreeItemByMetadata(toggledMetadataAfterBuild);

            return;
        }

        if (selectedScriptOccurrenceBeforeToggle.HasValue)
        {
            ScriptTreeOccurrence occurrence = selectedScriptOccurrenceBeforeToggle.Value;

            if (TrySelectScriptTreeOccurrence(occurrence))
            {
                RememberScriptTreeOccurrence(occurrence);
                return;
            }

            DebugLogger.LogOperation(
                "Toggle Item Lock selection restore warning",
                $"system='{occurrence.SystemName}', entry='{occurrence.Entry}', script='{occurrence.ScriptPath}'"
            );
            return;
        }

        if (!string.IsNullOrWhiteSpace(selectedMetadataBeforeToggle))
            SelectTreeItemByMetadata(selectedMetadataBeforeToggle);
    }

    private static string GetMetadataAfterLockToggle(string metadata, string newEntry)
    {
        if (!metadata.StartsWith("folder::"))
            return $"{GetMetadataPrefix(metadata)}{newEntry}";

        string systemName = GetSystemNameFromMetadata(metadata);
        string folderPath = GetFolderPathFromFolderEntry(newEntry);

        return string.IsNullOrWhiteSpace(systemName) || string.IsNullOrWhiteSpace(folderPath)
            ? metadata
            : $"folder::{systemName}::{folderPath}";
    }

    private static string GetMetadataPrefix(string metadata)
    {
        if (metadata.StartsWith("script::"))
            return "script::";

        if (metadata.StartsWith("sceneLink::"))
            return "sceneLink::";

        if (metadata.StartsWith("folder::"))
        {
            string systemName = GetSystemNameFromMetadata(metadata);
            return string.IsNullOrWhiteSpace(systemName) ? "" : $"folder::{systemName}::";
        }

        return "";
    }

    #endregion

    #region Lock State Helpers
    private static bool IsEntryLocked(string entry)
    {
        return !string.IsNullOrWhiteSpace(entry) && entry.EndsWith(LockedEntryMarker);
    }

    private bool IsSystemLocked(string systemName)
    {
        if (string.IsNullOrWhiteSpace(systemName))
            return false;

        if (!EnsureSystemAvailable(systemName, "Check System Lock"))
            return false;

        return _systems[systemName].Contains(SystemLockEntry);
    }

    private bool IsMetadataSystemLocked(string metadata)
    {
        string systemName = GetSystemNameFromEntryMetadata(metadata);
        return IsSystemLocked(systemName);
    }

    private bool IsInsideLockedSystem(string metadata)
    {
        if (string.IsNullOrWhiteSpace(metadata))
            return false;

        if (metadata.StartsWith("system::"))
            return false;

        return IsMetadataSystemLocked(metadata);
    }

    private bool IsDragSourceLockedBySelfOrParentFolder(
        string metadata,
        string sourceSystemName,
        string sourceFolderPath
    )
    {
        if (string.IsNullOrWhiteSpace(metadata))
            return false;

        if (
            string.IsNullOrWhiteSpace(sourceSystemName)
            || !_systems.TryGetValue(sourceSystemName, out List<string> exactSourceEntries)
            || exactSourceEntries == null
        )
            return true;

        if (metadata.StartsWith("system::"))
        {
            if (
                !string.Equals(
                    GetSystemNameFromMetadata(metadata),
                    sourceSystemName,
                    System.StringComparison.Ordinal
                )
            )
                return true;

            return exactSourceEntries.Contains(SystemLockEntry);
        }

        if (exactSourceEntries.Contains(SystemLockEntry))
            return true;

        if (IsScriptOrSceneMetadata(metadata))
        {
            string sourceEntry = GetEntryFromMetadata(metadata);

            if (
                !string.Equals(
                    GetFolderPathFromEntry(sourceEntry),
                    sourceFolderPath,
                    System.StringComparison.Ordinal
                )
            )
                return true;

            if (IsEntryLocked(sourceEntry))
                return true;

            return IsInsideLockedFolder(sourceSystemName, sourceFolderPath, includeSelf: true);
        }

        if (metadata.StartsWith("folder::"))
        {
            if (
                !string.Equals(
                    GetSystemNameFromMetadata(metadata),
                    sourceSystemName,
                    System.StringComparison.Ordinal
                )
                || !string.Equals(
                    GetFolderPathFromMetadata(metadata),
                    sourceFolderPath,
                    System.StringComparison.Ordinal
                )
            )
                return true;

            if (IsFolderEntryLocked(sourceSystemName, sourceFolderPath))
                return true;

            return IsInsideLockedFolder(sourceSystemName, sourceFolderPath, includeSelf: false);
        }

        return false;
    }

    private bool IsDropTargetSortingLocked(TreeItem targetItem)
    {
        if (targetItem == null)
            return true;

        string metadata = targetItem.GetMetadata(0).AsString();

        if (string.IsNullOrWhiteSpace(metadata))
            return true;

        string targetSystemName = GetSystemNameFromTreeItem(targetItem);
        string targetFolderPath = GetFolderPathFromTreeItem(targetItem);

        if (
            string.IsNullOrWhiteSpace(targetSystemName)
            || !_systems.TryGetValue(targetSystemName, out List<string> exactTargetEntries)
            || exactTargetEntries == null
        )
            return true;

        // Dropping directly on a locked system is allowed so files can be moved into it.
        // Dropping on anything inside that system is treated as sorting and is blocked.
        if (metadata.StartsWith("system::"))
        {
            return !string.Equals(
                GetSystemNameFromMetadata(metadata),
                targetSystemName,
                System.StringComparison.Ordinal
            );
        }

        if (exactTargetEntries.Contains(SystemLockEntry))
            return true;

        if (IsScriptOrSceneMetadata(metadata))
        {
            string targetEntry = GetEntryFromMetadata(metadata);

            if (
                !string.Equals(
                    GetFolderPathFromEntry(targetEntry),
                    targetFolderPath,
                    System.StringComparison.Ordinal
                )
            )
                return true;

            if (IsEntryLocked(targetEntry))
                return true;
        }

        // Dropping directly on a locked folder is still allowed so files can be added to it.
        // Dropping on items inside that folder is treated as sorting and is blocked.
        if (metadata.StartsWith("folder::"))
        {
            if (
                !string.Equals(
                    GetSystemNameFromMetadata(metadata),
                    targetSystemName,
                    System.StringComparison.Ordinal
                )
                || !string.Equals(
                    GetFolderPathFromMetadata(metadata),
                    targetFolderPath,
                    System.StringComparison.Ordinal
                )
            )
                return true;

            if (IsFolderEntryLocked(targetSystemName, targetFolderPath))
                return false;
        }

        return IsInsideLockedFolder(targetSystemName, targetFolderPath, includeSelf: true);
    }

    private bool IsInsideLockedFolder(string metadata, bool includeSelf)
    {
        string systemName = GetSystemNameFromEntryMetadata(metadata);
        string folderPath = metadata.StartsWith("folder::")
            ? GetFolderPathFromMetadata(metadata)
            : GetFolderPathFromEntry(GetEntryFromMetadata(metadata));

        bool includeExactFolder = includeSelf || !metadata.StartsWith("folder::");
        return IsInsideLockedFolder(systemName, folderPath, includeExactFolder);
    }

    private bool IsInsideLockedFolder(
        string exactSystemName,
        string exactFolderPath,
        bool includeSelf
    )
    {
        if (
            string.IsNullOrWhiteSpace(exactSystemName) || string.IsNullOrWhiteSpace(exactFolderPath)
        )
            return false;

        string folderPath = exactFolderPath;

        if (!includeSelf)
        {
            if (!folderPath.Contains("/"))
                return false;

            folderPath = folderPath.Substring(0, folderPath.LastIndexOf('/'));
        }

        foreach (string ancestorPath in GetFolderPathAndAncestors(folderPath))
        {
            if (IsFolderEntryLocked(exactSystemName, ancestorPath))
                return true;
        }

        return false;
    }

    private bool IsFolderLocked(string metadata)
    {
        string systemName = GetSystemNameFromMetadata(metadata);
        string folderPath = GetFolderPathFromMetadata(metadata);

        return IsFolderEntryLocked(systemName, folderPath);
    }

    private bool IsFolderEntryLocked(string systemName, string folderPath)
    {
        if (string.IsNullOrWhiteSpace(systemName) || string.IsNullOrWhiteSpace(folderPath))
            return false;

        if (!EnsureSystemAvailable(systemName, "Check Folder Lock"))
            return false;

        string folderEntry = _systems[systemName]
            .FirstOrDefault(entry =>
                entry.StartsWith("folder::") && GetFolderPathFromFolderEntry(entry) == folderPath
            );

        return IsEntryLocked(folderEntry);
    }

    private static IEnumerable<string> GetFolderPathAndAncestors(string folderPath)
    {
        string currentPath = folderPath;

        while (!string.IsNullOrWhiteSpace(currentPath))
        {
            yield return currentPath;

            if (!currentPath.Contains("/"))
                yield break;

            currentPath = currentPath.Substring(0, currentPath.LastIndexOf('/'));
        }
    }

    private static string SetEntryLocked(string entry, bool locked)
    {
        string entryWithoutLock = RemoveLockMarker(entry);
        return locked ? AddLockMarker(entryWithoutLock) : entryWithoutLock;
    }

    private static string AddLockMarker(string entry)
    {
        if (string.IsNullOrWhiteSpace(entry) || IsEntryLocked(entry))
            return entry;

        return $"{entry}{LockedEntryMarker}";
    }

    private static string RemoveLockMarker(string entry)
    {
        if (string.IsNullOrWhiteSpace(entry) || !entry.EndsWith(LockedEntryMarker))
            return entry;

        return entry.Substring(0, entry.Length - LockedEntryMarker.Length);
    }

    #endregion
}
#endif
