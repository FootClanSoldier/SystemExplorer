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

        if (string.IsNullOrWhiteSpace(draggedMetadata) || string.IsNullOrWhiteSpace(targetMetadata))
        {
            DebugLogger.Log("Drag Move cancelled: missing metadata.");
            return;
        }

        if (!EnsureSystemsLoadedForTreeOperation("Drag Move"))
            return;

        string sourceEntry = GetEntryFromMetadata(draggedMetadata);
        string sourceSystemName = _draggedSourceSystemName;
        string sourceFolderPath = _draggedSourceFolderPath;
        List<string> sourceEntries = null;
        int sourceIndex = -1;

        if (
            IsScriptOrSceneMetadata(draggedMetadata)
            && !TryResolveDraggedSourceContext(
                draggedMetadata,
                out sourceEntry,
                out sourceSystemName,
                out sourceFolderPath,
                out sourceEntries,
                out sourceIndex
            )
        )
        {
            DebugLogger.LogOperation(
                "Drag Move cancelled: invalid captured source context",
                $"Dragged='{draggedMetadata}', "
                    + $"CapturedSourceSystem='{_draggedSourceSystemName}', "
                    + $"CapturedSourceFolder='{_draggedSourceFolderPath}', "
                    + $"SourceEntry='{GetEntryFromMetadata(draggedMetadata)}'."
            );
            return;
        }

        if (
            IsDragSourceLockedBySelfOrParentFolder(
                draggedMetadata,
                sourceSystemName,
                sourceFolderPath
            )
        )
        {
            DebugLogger.LogOperation(
                "Drag Move cancelled: dragged item or parent folder is locked",
                draggedMetadata
            );
            return;
        }

        if (IsDropTargetSortingLocked(targetItem))
        {
            DebugLogger.LogOperation(
                "Drag Move cancelled: target folder contents are locked",
                targetMetadata
            );
            return;
        }

        if (
            !IsScriptOrSceneDropAllowedByFolderBinding(
                draggedMetadata,
                targetItem,
                sourceSystemName,
                sourceFolderPath,
                sourceEntry,
                out string boundFolderPath,
                out string physicalFilePath,
                out string targetSystemName,
                out string targetFolderPath
            )
        )
        {
            DebugLogger.LogOperation(
                "Drag Move cancelled: directly bound file can only reorder within its folder",
                $"SourceSystem='{sourceSystemName}', SourceFolder='{sourceFolderPath}', "
                    + $"BoundFolder='{boundFolderPath}', File='{physicalFilePath}', "
                    + $"TargetSystem='{targetSystemName}', TargetFolder='{targetFolderPath}'. "
                    + "Directly bound files cannot leave their exact system and virtual folder."
            );
            return;
        }

        if (
            IsScriptOrSceneMetadata(draggedMetadata)
            && IsValidScriptOrSceneDropTargetMetadata(targetMetadata)
            && TryGetScriptOrSceneDropFileNameCollision(
                draggedMetadata,
                targetItem,
                sourceSystemName,
                sourceEntry,
                sourceIndex,
                sourceFolderPath,
                sourceEntries,
                out string collisionTargetSystemName,
                out string collisionTargetFolderPath,
                out string collisionDraggedPhysicalPath,
                out string collisionDraggedFileName,
                out string conflictingDestinationEntry,
                out string conflictingDestinationPhysicalPath
            )
        )
        {
            DebugLogger.LogOperation(
                "Drag Move cancelled: destination already contains the same file name",
                $"SourceSystem='{sourceSystemName}', "
                    + $"SourceFolder='{sourceFolderPath}', "
                    + $"SourceIndex={sourceIndex}, "
                    + $"SourceEntry='{sourceEntry}', "
                    + $"TargetSystem='{collisionTargetSystemName}', "
                    + $"TargetFolder='{collisionTargetFolderPath}', "
                    + $"File='{collisionDraggedPhysicalPath}', "
                    + $"FileName='{collisionDraggedFileName}', "
                    + $"ConflictingEntry='{conflictingDestinationEntry}', "
                    + $"ExistingFile='{conflictingDestinationPhysicalPath}'."
            );
            return;
        }

        DebugLogger.LogOperation(
            "Drag Move Requested",
            $"Dragged='{draggedMetadata}', Target='{targetMetadata}'"
        );

        bool moved = false;

        if (draggedMetadata.StartsWith("system::") && targetMetadata.StartsWith("system::"))
        {
            if (draggedMetadata == targetMetadata)
            {
                DebugLogger.Log("Drag Move cancelled: dragged and target metadata are identical.");
                return;
            }

            moved = MoveSystem(draggedMetadata, targetMetadata);
        }
        else if (
            IsScriptOrSceneMetadata(draggedMetadata)
            && IsValidScriptOrSceneDropTargetMetadata(targetMetadata)
        )
        {
            moved = MoveScriptOrSceneToDropTarget(
                draggedMetadata,
                targetItem,
                sourceSystemName,
                sourceFolderPath,
                sourceEntry,
                sourceEntries,
                sourceIndex
            );
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
            DebugLogger.LogOperation(
                "Drag Move cancelled: mutation failed",
                $"Dragged='{draggedMetadata}', Target='{targetMetadata}'"
            );
            return;
        }

        if (SaveSystems())
            BuildTree();
    }

    private bool TryResolveDraggedSourceContext(
        string draggedMetadata,
        out string sourceEntry,
        out string sourceSystemName,
        out string sourceFolderPath,
        out List<string> sourceEntries,
        out int sourceIndex
    )
    {
        sourceEntry = GetEntryFromMetadata(draggedMetadata);
        sourceSystemName = _draggedSourceSystemName;
        sourceFolderPath = _draggedSourceFolderPath;
        sourceEntries = null;
        sourceIndex = -1;

        if (
            !IsScriptOrSceneMetadata(draggedMetadata)
            || !string.Equals(draggedMetadata, _draggedMetadata, System.StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(sourceEntry)
            || string.IsNullOrWhiteSpace(sourceSystemName)
            || !_systems.TryGetValue(sourceSystemName, out sourceEntries)
            || sourceEntries == null
        )
            return false;

        if (
            !string.Equals(
                GetFolderPathFromEntry(sourceEntry),
                sourceFolderPath,
                System.StringComparison.Ordinal
            )
        )
            return false;

        sourceIndex = sourceEntries.IndexOf(sourceEntry);
        return sourceIndex >= 0;
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

        DebugLogger.LogOperation(
            "Move System Mutated",
            $"{draggedSystemName} -> {targetSystemName}"
        );

        return true;
    }

    private bool MoveScriptOrSceneToDropTarget(
        string draggedMetadata,
        TreeItem targetItem,
        string sourceSystemName,
        string sourceFolderPath,
        string draggedEntry,
        List<string> sourceEntries,
        int sourceIndex
    )
    {
        if (!EnsureSystemsLoadedForTreeOperation("Move Script/Scene"))
            return false;

        if (targetItem == null)
            return false;

        string targetMetadata = targetItem.GetMetadata(0).AsString();

        if (!IsValidScriptOrSceneDropTargetMetadata(targetMetadata))
            return false;

        string draggedPath = GetPathFromEntry(draggedEntry);

        if (
            string.IsNullOrWhiteSpace(sourceSystemName)
            || string.IsNullOrWhiteSpace(draggedEntry)
            || string.IsNullOrWhiteSpace(draggedPath)
            || sourceEntries == null
            || sourceIndex < 0
            || sourceIndex >= sourceEntries.Count
            || !string.Equals(
                sourceEntries[sourceIndex],
                draggedEntry,
                System.StringComparison.Ordinal
            )
            || !string.Equals(
                GetFolderPathFromEntry(draggedEntry),
                sourceFolderPath,
                System.StringComparison.Ordinal
            )
            || !_systems.TryGetValue(sourceSystemName, out List<string> currentSourceEntries)
            || !object.ReferenceEquals(sourceEntries, currentSourceEntries)
        )
            return false;

        string targetSystemName = GetSystemNameFromTreeItem(targetItem);
        string targetFolderPath = GetDropFolderPathFromTargetItem(targetItem);
        string targetEntry = IsScriptOrSceneMetadata(targetMetadata)
            ? GetEntryFromMetadata(targetMetadata)
            : "";

        if (
            string.IsNullOrWhiteSpace(targetSystemName)
            || !_systems.TryGetValue(targetSystemName, out List<string> targetEntries)
            || targetEntries == null
        )
            return false;

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

        DebugLogger.LogOperation(
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

        DebugLogger.LogOperation("Move Entry Mutated", $"{draggedEntry} -> {targetEntry}");

        return true;
    }

    private static int FindEntryIndex(List<string> entries, string entryToFind)
    {
        int index = entries.IndexOf(entryToFind);

        if (index >= 0 || !entryToFind.StartsWith("folder::"))
            return index;

        string folderPath = GetFolderPathFromFolderEntry(entryToFind);
        return entries.FindIndex(entry =>
            entry.StartsWith("folder::") && GetFolderPathFromFolderEntry(entry) == folderPath
        );
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
                .FirstOrDefault(entry =>
                    entry.StartsWith("folder::")
                    && GetFolderPathFromFolderEntry(entry) == folderPath
                ) ?? BuildFolderEntry(folderPath);
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

    private bool IsScriptOrSceneDropAllowedByFolderBinding(
        string draggedMetadata,
        TreeItem targetItem,
        string sourceSystemName,
        string sourceFolderPath,
        string sourceEntry,
        out string boundFolderPath,
        out string physicalFilePath,
        out string targetSystemName,
        out string targetFolderPath
    )
    {
        boundFolderPath = "";
        physicalFilePath = "";
        targetSystemName = "";
        targetFolderPath = "";

        if (!IsScriptOrSceneMetadata(draggedMetadata))
            return true;

        if (
            string.IsNullOrWhiteSpace(sourceSystemName)
            || string.IsNullOrWhiteSpace(sourceEntry)
            || !string.Equals(
                GetFolderPathFromEntry(sourceEntry),
                sourceFolderPath,
                System.StringComparison.Ordinal
            )
        )
            return false;

        if (
            !TryGetDirectlyBoundScriptOrSceneEntry(
                sourceSystemName,
                sourceEntry,
                out string resolvedSourceFolderPath,
                out boundFolderPath,
                out physicalFilePath
            )
        )
            return true;

        if (
            !string.Equals(
                resolvedSourceFolderPath,
                sourceFolderPath,
                System.StringComparison.Ordinal
            )
        )
            return false;

        targetSystemName = GetSystemNameFromTreeItem(targetItem);
        targetFolderPath = GetDropFolderPathFromTargetItem(targetItem);

        string targetMetadata = targetItem?.GetMetadata(0).AsString() ?? "";

        return IsScriptOrSceneMetadata(targetMetadata)
            && string.Equals(sourceSystemName, targetSystemName, System.StringComparison.Ordinal)
            && string.Equals(sourceFolderPath, targetFolderPath, System.StringComparison.Ordinal);
    }

    private bool TryGetScriptOrSceneDropFileNameCollision(
        string draggedMetadata,
        TreeItem targetItem,
        string sourceSystemName,
        string sourceEntry,
        int sourceIndex,
        string sourceFolderPath,
        List<string> sourceEntries,
        out string targetSystemName,
        out string targetFolderPath,
        out string draggedPhysicalPath,
        out string draggedFileName,
        out string conflictingDestinationEntry,
        out string conflictingDestinationPhysicalPath
    )
    {
        targetSystemName = "";
        targetFolderPath = "";
        draggedPhysicalPath = "";
        draggedFileName = "";
        conflictingDestinationEntry = "";
        conflictingDestinationPhysicalPath = "";

        if (!IsScriptOrSceneMetadata(draggedMetadata) || targetItem == null)
            return false;

        draggedPhysicalPath = GetPathFromEntry(sourceEntry);
        draggedFileName = draggedPhysicalPath.GetFile();

        if (
            string.IsNullOrWhiteSpace(sourceSystemName)
            || string.IsNullOrWhiteSpace(sourceEntry)
            || string.IsNullOrWhiteSpace(draggedPhysicalPath)
            || string.IsNullOrWhiteSpace(draggedFileName)
            || sourceEntries == null
            || sourceIndex < 0
            || sourceIndex >= sourceEntries.Count
            || !string.Equals(
                sourceEntries[sourceIndex],
                sourceEntry,
                System.StringComparison.Ordinal
            )
            || !string.Equals(
                GetFolderPathFromEntry(sourceEntry),
                sourceFolderPath,
                System.StringComparison.Ordinal
            )
        )
            return false;

        targetSystemName = GetSystemNameFromTreeItem(targetItem);
        targetFolderPath = GetDropFolderPathFromTargetItem(targetItem);

        if (
            string.IsNullOrWhiteSpace(targetSystemName)
            || !_systems.TryGetValue(targetSystemName, out List<string> targetEntries)
            || targetEntries == null
        )
            return false;

        bool sameSystem = string.Equals(
            sourceSystemName,
            targetSystemName,
            System.StringComparison.Ordinal
        );

        for (int destinationIndex = 0; destinationIndex < targetEntries.Count; destinationIndex++)
        {
            string destinationEntry = targetEntries[destinationIndex];

            if (!IsScriptOrSceneEntry(destinationEntry))
                continue;

            if (sameSystem && destinationIndex == sourceIndex)
                continue;

            if (
                !string.Equals(
                    GetFolderPathFromEntry(destinationEntry),
                    targetFolderPath,
                    System.StringComparison.Ordinal
                )
            )
                continue;

            string destinationPhysicalPath = GetPathFromEntry(destinationEntry);
            string destinationFileName = destinationPhysicalPath.GetFile();

            if (
                string.IsNullOrWhiteSpace(destinationPhysicalPath)
                || string.IsNullOrWhiteSpace(destinationFileName)
                || !string.Equals(
                    draggedFileName,
                    destinationFileName,
                    System.StringComparison.OrdinalIgnoreCase
                )
            )
                continue;

            conflictingDestinationEntry = destinationEntry;
            conflictingDestinationPhysicalPath = destinationPhysicalPath;
            return true;
        }

        return false;
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

        string sourceEntry = GetEntryFromMetadata(draggedMetadata);
        string sourceSystemName = _draggedSourceSystemName;
        string sourceFolderPath = _draggedSourceFolderPath;
        List<string> sourceEntries = null;
        int sourceIndex = -1;

        if (
            IsScriptOrSceneMetadata(draggedMetadata)
            && !TryResolveDraggedSourceContext(
                draggedMetadata,
                out sourceEntry,
                out sourceSystemName,
                out sourceFolderPath,
                out sourceEntries,
                out sourceIndex
            )
        )
            return false;

        if (
            IsDragSourceLockedBySelfOrParentFolder(
                draggedMetadata,
                sourceSystemName,
                sourceFolderPath
            )
        )
            return false;

        if (IsDropTargetSortingLocked(targetItem))
            return false;

        if (draggedMetadata.StartsWith("system::"))
            return targetMetadata.StartsWith("system::");

        if (IsScriptOrSceneMetadata(draggedMetadata))
        {
            if (
                !IsScriptOrSceneDropAllowedByFolderBinding(
                    draggedMetadata,
                    targetItem,
                    sourceSystemName,
                    sourceFolderPath,
                    sourceEntry,
                    out _,
                    out _,
                    out _,
                    out _
                )
            )
                return false;

            if (!IsValidScriptOrSceneDropTargetMetadata(targetMetadata))
                return false;

            return !TryGetScriptOrSceneDropFileNameCollision(
                draggedMetadata,
                targetItem,
                sourceSystemName,
                sourceEntry,
                sourceIndex,
                sourceFolderPath,
                sourceEntries,
                out _,
                out _,
                out _,
                out _,
                out _,
                out _
            );
        }

        return IsSystemEntryMetadata(draggedMetadata)
            && IsSystemEntryMetadata(targetMetadata)
            && GetSystemNameFromEntryMetadata(draggedMetadata)
                == GetSystemNameFromEntryMetadata(targetMetadata)
            && GetParentFolderPathFromEntryMetadata(draggedMetadata)
                == GetParentFolderPathFromEntryMetadata(targetMetadata);
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
