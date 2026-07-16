#if TOOLS
using Godot;
using System.Collections.Generic;

public partial class SystemExplorerPlugin
{
    #region Debug Logging

    private void DebugLog(string message)
    {
        if (!DebugState)
            return;

        GD.Print($"[SystemExplorer] {message}");
    }

    private void DebugLogOperation(string operation, string details = "")
    {
        if (!DebugState)
            return;

        if (string.IsNullOrWhiteSpace(details))
            GD.Print($"[SystemExplorer] {operation}");
        else
            GD.Print($"[SystemExplorer] {operation} -> {details}");
    }

    private void DebugLogStateSnapshot(string label)
    {
        if (!DebugState)
            return;

        DebugLog($"--- {label} ---");
        DebugLog($"Systems Count: {_systems.Count}");
        DebugLog($"Pending Add Folder Metadata: '{_pendingAddFolderMetadata}'");
        DebugLog($"Pending Remove Metadata: '{_pendingRemoveMetadata}'");
        DebugLog($"Pending Rename Metadata: '{_pendingRenameMetadata}'");

        TreeItem selectedItem = _tree?.GetSelected();

        if (selectedItem == null)
        {
            DebugLog("Selected Tree Item: <null>");
        }
        else
        {
            DebugLog($"Selected Tree Text: '{selectedItem.GetText(0)}'");
            DebugLog($"Selected Tree Metadata: '{selectedItem.GetMetadata(0).AsString()}'");
        }

        DebugLogSystems("Systems Snapshot");
        DebugLog($"--- End {label} ---");
    }

    private void DebugLogSystems(string label)
    {
        if (!DebugState)
            return;

        DebugLog(label);
        if (_systems.Count == 0)
        {
            DebugLog(" - <no systems>");
            return;
        }

        foreach (KeyValuePair<string, List<string>> system in _systems)
            DebugLog($" - {system.Key}: {system.Value.Count} entries");
    }
    #endregion
}
#endif
