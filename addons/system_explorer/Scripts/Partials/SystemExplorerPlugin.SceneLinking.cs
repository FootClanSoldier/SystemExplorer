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

        UpdateLinkedScenePath(_pendingSceneLinkEntry, scenePath, _pendingSceneLinkSourceOccurrence);
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

        string entry = _pendingMissingSceneEntry;
        ScriptTreeOccurrence? sourceOccurrence = _pendingMissingSceneScriptOccurrence;
        _missingSceneDialog.Hide();
        ClearMissingSceneState();

        if (IsSceneEntry(entry))
        {
            if (RemoveEntry(entry) && SaveSystems())
                BuildTree();

            return;
        }

        UpdateLinkedScenePath(entry, "", sourceOccurrence);
    }

    private void OnRelinkSceneFileSelected(string newScenePath)
    {
        if (string.IsNullOrWhiteSpace(_pendingMissingSceneEntry))
            return;

        string entry = _pendingMissingSceneEntry;
        ScriptTreeOccurrence? sourceOccurrence = _pendingMissingSceneScriptOccurrence;
        ClearMissingSceneState();

        if (IsSceneEntry(entry))
        {
            UpdateScenePath(entry, newScenePath);
            return;
        }

        UpdateLinkedScenePath(entry, newScenePath, sourceOccurrence);
    }

    private void ClearMissingSceneState()
    {
        _pendingMissingSceneEntry = "";
        _pendingMissingScenePath = "";
        _pendingMissingSceneScriptOccurrence = null;
    }

    private bool UpdateScenePath(string oldEntry, string newScenePath)
    {
        if (!EnsureSystemsLoadedForTreeOperation("Update Scene"))
            return false;

        string folderPath = GetFolderPathFromEntry(oldEntry);
        string newEntry = BuildSceneEntry(folderPath, newScenePath, IsEntryLocked(oldEntry));

        if (!ReplaceEntry(oldEntry, newEntry))
        {
            DebugLogger.LogOperation(
                "Update Scene cancelled: mutation failed",
                $"{oldEntry} -> {newEntry}"
            );
            return false;
        }

        if (SaveSystems())
            BuildTree();

        return true;
    }

    private ScriptTreeOccurrence? CaptureSelectedScriptOccurrence(string expectedEntry)
    {
        if (
            !TryGetScriptTreeOccurrenceFromTreeItem(
                _tree?.GetSelected(),
                out ScriptTreeOccurrence occurrence
            ) || !string.Equals(occurrence.Entry, expectedEntry, System.StringComparison.Ordinal)
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
            DebugLogger.LogOperation(
                "Update Linked Scene cancelled: script path unavailable",
                oldEntry
            );
            return false;
        }

        int matchedEntryCount = 0;
        int changedEntryCount = 0;
        ScriptTreeOccurrence? updatedSourceOccurrence = null;

        foreach (string systemName in _systems.Keys.ToList())
        {
            List<string> updatedEntries = new();
            HashSet<string> updatedTargetEntries = new(System.StringComparer.Ordinal);
            bool systemMatched = false;

            foreach (string entry in _systems[systemName])
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
                    UpdateSelectedScriptEntryFromFilter(entry, newEntry);
                    changedEntryCount++;
                }

                if (updatedTargetEntries.Add(newEntry))
                    updatedEntries.Add(newEntry);
            }

            if (systemMatched)
                _systems[systemName] = updatedEntries;
        }

        if (matchedEntryCount == 0)
        {
            DebugLogger.LogOperation(
                "Update Linked Scene cancelled: no matching script references",
                scriptPath
            );
            return false;
        }

        DebugLogger.LogOperation(
            "Update Linked Scene references mutated",
            $"Path='{scriptPath}', Matched={matchedEntryCount}, Changed={changedEntryCount}"
        );

        if (changedEntryCount > 0 && SaveSystems())
        {
            BuildTree();

            if (updatedSourceOccurrence.HasValue)
                RestoreLinkedSceneSourceOccurrence(updatedSourceOccurrence.Value);
        }
        else if (changedEntryCount == 0 && updatedSourceOccurrence.HasValue)
        {
            RestoreLinkedSceneSourceOccurrence(updatedSourceOccurrence.Value);
        }

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
