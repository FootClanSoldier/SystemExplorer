#if TOOLS
using Godot;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

public partial class SystemExplorerPlugin
{
    #region Scene Linking
    private void OpenLinkSceneDialog()
    {
        if (string.IsNullOrWhiteSpace(_pendingRenameMetadata))
            return;

        if (!_pendingRenameMetadata.StartsWith("script::"))
            return;

        string entry = GetEntryFromMetadata(_pendingRenameMetadata);
        if (!IsScriptEntryValidOrOpenMissingDialog(entry))
            return;

        _pendingSceneLinkEntry = entry;
        _linkSceneDialog.PopupCenteredRatio(0.8f);
    }

    private void OnLinkSceneFileSelected(string scenePath)
    {
        if (string.IsNullOrWhiteSpace(_pendingSceneLinkEntry))
            return;

        UpdateLinkedScenePath(_pendingSceneLinkEntry, scenePath);
        _pendingSceneLinkEntry = "";
    }

    private void UnlinkSceneFromPendingScript()
    {
        if (string.IsNullOrWhiteSpace(_pendingRenameMetadata))
            return;

        if (!_pendingRenameMetadata.StartsWith("script::"))
            return;

        string entry = GetEntryFromMetadata(_pendingRenameMetadata);
        UpdateLinkedScenePath(entry, "");
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

        if (string.IsNullOrWhiteSpace(linkedScenePath))
        {
            OpenScriptOrMissingDialog(entry, scriptPath);
            return;
        }

        if (!FileAccess.FileExists(linkedScenePath))
        {
            OpenMissingSceneDialog(entry, linkedScenePath);
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

    private void OpenMissingSceneDialog(string entry, string scenePath)
    {
        _pendingMissingSceneEntry = entry;
        _pendingMissingScenePath = scenePath;

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
        _missingSceneDialog.Hide();
        ClearMissingSceneState();

        if (IsSceneEntry(entry))
        {
            if (RemoveEntry(entry) && SaveSystems())
                BuildTree();

            return;
        }

        UpdateLinkedScenePath(entry, "");
    }

    private void OnRelinkSceneFileSelected(string newScenePath)
    {
        if (string.IsNullOrWhiteSpace(_pendingMissingSceneEntry))
            return;

        string entry = _pendingMissingSceneEntry;
        ClearMissingSceneState();

        if (IsSceneEntry(entry))
        {
            UpdateScenePath(entry, newScenePath);
            return;
        }

        UpdateLinkedScenePath(entry, newScenePath);
    }

    private void ClearMissingSceneState()
    {
        _pendingMissingSceneEntry = "";
        _pendingMissingScenePath = "";
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

    private bool UpdateLinkedScenePath(string oldEntry, string linkedScenePath)
    {
        if (!EnsureSystemsLoadedForTreeOperation("Update Linked Scene"))
            return false;

        string folderPath = GetFolderPathFromEntry(oldEntry);
        string scriptPath = GetScriptPathFromEntry(oldEntry);
        string newEntry = BuildScriptEntry(
            folderPath,
            scriptPath,
            linkedScenePath,
            IsEntryLocked(oldEntry)
        );

        if (!ReplaceEntry(oldEntry, newEntry))
        {
            DebugLogger.LogOperation(
                "Update Linked Scene cancelled: mutation failed",
                $"{oldEntry} -> {newEntry}"
            );
            return false;
        }

        if (SaveSystems())
            BuildTree();

        return true;
    }

    #endregion
}
#endif
