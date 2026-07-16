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
        string selectedMetadataBeforeToggle = _tree.GetSelected()?.GetMetadata(0).AsString() ?? "";

        if (!IsLockableMetadata(metadata))
            return;

        if (!EnsureSystemsLoadedForTreeOperation("Toggle Item Lock"))
            return;

        if (metadata.StartsWith("system::"))
        {
            ToggleSystemLock(metadata, selectedMetadataBeforeToggle, selectToggledItemAfterBuild);
            return;
        }

        if (IsInsideLockedSystem(metadata))
        {
            DebugLogOperation("Toggle Item Lock cancelled: parent system is locked", metadata);
            return;
        }

        if (IsInsideLockedFolder(metadata, includeSelf: false))
        {
            DebugLogOperation("Toggle Item Lock cancelled: parent folder is locked", metadata);
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
            DebugLogOperation("Toggle Item Lock cancelled: mutation failed", oldEntry);
            return;
        }

        if (SaveSystems())
        {
            SaveExpansionState();
            BuildTree(keepCurrentExpansionState: true);

            string toggledMetadataAfterBuild = GetMetadataAfterLockToggle(metadata, newEntry);
            string metadataToSelect = selectToggledItemAfterBuild
                ? toggledMetadataAfterBuild
                : selectedMetadataBeforeToggle;

            if (!selectToggledItemAfterBuild)
                _hoveredTreeItemMetadata = toggledMetadataAfterBuild;

            if (!string.IsNullOrWhiteSpace(metadataToSelect))
                SelectTreeItemByMetadata(metadataToSelect);

            UpdateTreeLockIconVisibility();
        }
    }

    private void ToggleSystemLock(
        string metadata,
        string selectedMetadataBeforeToggle,
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
            DebugLogOperation("Toggle System Lock Mutated", $"{systemName} unlocked");
        }
        else
        {
            if (!entries.Contains(SystemLockEntry))
                entries.Insert(0, SystemLockEntry);

            DebugLogOperation("Toggle System Lock Mutated", $"{systemName} locked");
        }

        if (SaveSystems())
        {
            SaveExpansionState();
            BuildTree(keepCurrentExpansionState: true);

            string metadataToSelect = selectToggledItemAfterBuild
                ? metadata
                : selectedMetadataBeforeToggle;

            if (!selectToggledItemAfterBuild)
                _hoveredTreeItemMetadata = metadata;

            if (!string.IsNullOrWhiteSpace(metadataToSelect))
                SelectTreeItemByMetadata(metadataToSelect);

            UpdateTreeLockIconVisibility();
        }
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

    private bool IsDragSourceLockedBySelfOrParentFolder(string metadata)
    {
        if (string.IsNullOrWhiteSpace(metadata))
            return false;

        if (metadata.StartsWith("system::") && IsMetadataSystemLocked(metadata))
            return true;

        if (IsInsideLockedSystem(metadata))
            return true;

        if (IsScriptOrSceneMetadata(metadata) && IsEntryLocked(GetEntryFromMetadata(metadata)))
            return true;

        if (metadata.StartsWith("folder::") && IsFolderLocked(metadata))
            return true;

        return IsInsideLockedFolder(metadata, includeSelf: false);
    }

    private bool IsDropTargetSortingLocked(string metadata)
    {
        if (string.IsNullOrWhiteSpace(metadata))
            return false;

        // Dropping directly on a locked system is allowed so files can be moved into it.
        // Dropping on anything inside that system is treated as sorting and is blocked.
        if (metadata.StartsWith("system::") && IsMetadataSystemLocked(metadata))
            return false;

        if (IsInsideLockedSystem(metadata))
            return true;

        if (IsScriptOrSceneMetadata(metadata) && IsEntryLocked(GetEntryFromMetadata(metadata)))
            return true;

        // Dropping directly on a locked folder is still allowed so files can be added to it.
        // Dropping on items inside that folder is treated as sorting and is blocked.
        if (metadata.StartsWith("folder::") && IsFolderLocked(metadata))
            return false;

        return IsInsideLockedFolder(metadata, includeSelf: true);
    }

    private bool IsInsideLockedFolder(string metadata, bool includeSelf)
    {
        string systemName = GetSystemNameFromEntryMetadata(metadata);
        string folderPath = metadata.StartsWith("folder::")
            ? GetFolderPathFromMetadata(metadata)
            : GetFolderPathFromEntry(GetEntryFromMetadata(metadata));

        if (string.IsNullOrWhiteSpace(systemName) || string.IsNullOrWhiteSpace(folderPath))
            return false;

        if (!includeSelf && metadata.StartsWith("folder::"))
        {
            if (!folderPath.Contains("/"))
                return false;

            folderPath = folderPath.Substring(0, folderPath.LastIndexOf('/'));
        }

        foreach (string ancestorPath in GetFolderPathAndAncestors(folderPath))
        {
            if (IsFolderEntryLocked(systemName, ancestorPath))
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
