#if TOOLS
using Godot;
using System.Collections.Generic;

public partial class SystemExplorerPlugin
{
    #region Debug Diagnostics

    private void DebugLogStateSnapshot(string label)
    {
        if (!DebugState)
            return;

        DebugLogger.Log($"--- {label} ---");
        DebugLogger.Log($"Systems Count: {_systems.Count}");
        DebugLogger.Log($"Pending Add Folder Metadata: '{_pendingAddFolderMetadata}'");
        DebugLogger.Log($"Pending Remove Metadata: '{_pendingRemoveMetadata}'");
        DebugLogger.Log($"Pending Rename Metadata: '{_pendingRenameMetadata}'");

        TreeItem selectedItem = _tree?.GetSelected();

        if (selectedItem == null)
        {
            DebugLogger.Log("Selected Tree Item: <null>");
        }
        else
        {
            DebugLogger.Log($"Selected Tree Text: '{selectedItem.GetText(0)}'");
            DebugLogger.Log($"Selected Tree Metadata: '{selectedItem.GetMetadata(0).AsString()}'");
        }

        DebugLogSystems("Systems Snapshot");
        DebugLogger.Log($"--- End {label} ---");
    }

    private void DebugLogSystems(string label)
    {
        if (!DebugState)
            return;

        DebugLogger.Log(label);
        if (_systems.Count == 0)
        {
            DebugLogger.Log(" - <no systems>");
            return;
        }

        foreach (KeyValuePair<string, List<string>> system in _systems)
            DebugLogger.Log($" - {system.Key}: {system.Value.Count} entries");
    }
    #endregion
}
#endif
