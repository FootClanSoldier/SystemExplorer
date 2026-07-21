#if TOOLS
using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

public partial class SystemExplorerPlugin
{
    #region Folder Binding State and Persistence
    private readonly Dictionary<string, Dictionary<string, string>> _folderBindings = new(
        StringComparer.Ordinal
    );
    private string _pendingFolderBindingMetadata = "";
    private EditorFileSystem _folderBindingResourceFilesystem;
    private bool _folderBindingFilesystemSignalConnected;
    private bool _boundFolderSyncQueued;
    private bool _boundFolderSyncRunning;

    private void LoadFolderBindings()
    {
        _folderBindings.Clear();
        DebugLogger.LogOperation("Load Folder Bindings Requested", FolderBindingsPath);

        if (!FileAccess.FileExists(FolderBindingsPath))
        {
            DebugLogger.Log("Load Folder Bindings skipped: bindings file does not exist.");
            return;
        }

        using FileAccess file = FileAccess.Open(FolderBindingsPath, FileAccess.ModeFlags.Read);

        if (file == null)
        {
            DebugLogger.LogOperation(
                "Load Folder Bindings skipped: could not open file",
                FolderBindingsPath
            );
            return;
        }

        string json = file.GetAsText();

        if (string.IsNullOrWhiteSpace(json))
        {
            DebugLogger.Log("Load Folder Bindings skipped: bindings file is empty.");
            return;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                DebugLogger.Log("Load Folder Bindings skipped: root JSON value is not an object.");
                return;
            }

            foreach (JsonProperty systemProperty in document.RootElement.EnumerateObject())
            {
                string systemName = NormalizeFolderBindingSystemName(systemProperty.Name);

                if (
                    string.IsNullOrWhiteSpace(systemName)
                    || systemProperty.Value.ValueKind != JsonValueKind.Object
                    || !_systems.ContainsKey(systemName)
                )
                {
                    DebugLogger.LogOperation(
                        "Load Folder Bindings ignored system",
                        systemProperty.Name
                    );
                    continue;
                }

                Dictionary<string, string> systemBindings = new(StringComparer.Ordinal);

                foreach (JsonProperty folderProperty in systemProperty.Value.EnumerateObject())
                {
                    if (folderProperty.Value.ValueKind != JsonValueKind.String)
                        continue;

                    string virtualFolderPath = NormalizeVirtualFolderPath(folderProperty.Name);
                    string physicalFolderPath = NormalizeBoundFolderPath(
                        folderProperty.Value.GetString()
                    );

                    if (
                        string.IsNullOrWhiteSpace(virtualFolderPath)
                        || string.IsNullOrWhiteSpace(physicalFolderPath)
                        || !DoesVirtualFolderExist(systemName, virtualFolderPath)
                    )
                    {
                        DebugLogger.LogOperation(
                            "Load Folder Bindings ignored folder",
                            $"System='{systemName}', Folder='{folderProperty.Name}'"
                        );
                        continue;
                    }

                    systemBindings[virtualFolderPath] = physicalFolderPath;
                }

                if (systemBindings.Count > 0)
                    _folderBindings[systemName] = systemBindings;
            }
        }
        catch (Exception exception)
        {
            _folderBindings.Clear();
            DebugLogger.LogOperation(
                "Load Folder Bindings failed: JSON parse error",
                exception.Message
            );
            return;
        }

        DebugLogger.LogOperation(
            "Load Folder Bindings Completed",
            $"{_folderBindings.Sum(binding => binding.Value.Count)} bindings"
        );
    }

    private bool SaveFolderBindings()
    {
        if (!EnsureResourcesFolderExists())
        {
            GD.PushWarning(
                $"System Explorer could not save folder bindings because '{ResourcesFolderPath}' is unavailable."
            );
            return false;
        }

        try
        {
            SortedDictionary<string, SortedDictionary<string, string>> serializedBindings = new(
                StringComparer.Ordinal
            );

            foreach (
                KeyValuePair<string, Dictionary<string, string>> systemBinding in _folderBindings
            )
            {
                if (string.IsNullOrWhiteSpace(systemBinding.Key) || systemBinding.Value == null)
                    continue;

                SortedDictionary<string, string> folderBindings = new(StringComparer.Ordinal);

                foreach (KeyValuePair<string, string> folderBinding in systemBinding.Value)
                {
                    string virtualFolderPath = NormalizeVirtualFolderPath(folderBinding.Key);
                    string physicalFolderPath = NormalizeBoundFolderPath(folderBinding.Value);

                    if (
                        string.IsNullOrWhiteSpace(virtualFolderPath)
                        || string.IsNullOrWhiteSpace(physicalFolderPath)
                    )
                        continue;

                    folderBindings[virtualFolderPath] = physicalFolderPath;
                }

                if (folderBindings.Count > 0)
                    serializedBindings[systemBinding.Key.Trim()] = folderBindings;
            }

            string json = JsonSerializer.Serialize(
                serializedBindings,
                new JsonSerializerOptions { WriteIndented = true }
            );

            using FileAccess file = FileAccess.Open(FolderBindingsPath, FileAccess.ModeFlags.Write);

            if (file == null)
            {
                GD.PushWarning(
                    $"System Explorer could not open '{FolderBindingsPath}' for writing."
                );
                return false;
            }

            file.StoreString(json);
            DebugLogger.LogOperation(
                "Save Folder Bindings Completed",
                $"{serializedBindings.Sum(binding => binding.Value.Count)} bindings"
            );
            return true;
        }
        catch (Exception exception)
        {
            GD.PushWarning($"System Explorer could not save folder bindings: {exception.Message}");
            DebugLogger.LogOperation("Save Folder Bindings failed", exception.Message);
            return false;
        }
    }

    private static string NormalizeFolderBindingSystemName(string systemName)
    {
        return systemName?.Trim() ?? "";
    }

    private static string NormalizeVirtualFolderPath(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
            return "";

        string[] segments = folderPath
            .Trim()
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(segment => segment.Trim())
            .ToArray();

        if (
            segments.Length == 0
            || segments.Any(segment =>
                string.IsNullOrWhiteSpace(segment) || segment == "." || segment == ".."
            )
        )
            return "";

        return string.Join("/", segments).Trim('/');
    }

    private static string NormalizeBoundFolderPath(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
            return "";

        string normalized = folderPath.Trim().Replace('\\', '/');

        if (!normalized.StartsWith("res://", StringComparison.OrdinalIgnoreCase))
            return "";

        string relativePath = normalized.Substring("res://".Length);
        string[] segments = relativePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(segment => segment.Trim())
            .ToArray();

        if (
            segments.Any(segment =>
                string.IsNullOrWhiteSpace(segment) || segment == "." || segment == ".."
            )
        )
            return "";

        string canonicalRelativePath = string.Join("/", segments);
        return string.IsNullOrWhiteSpace(canonicalRelativePath)
            ? "res://"
            : $"res://{canonicalRelativePath}";
    }

    private bool TryGetFolderBinding(
        string systemName,
        string virtualFolderPath,
        out string physicalFolderPath
    )
    {
        physicalFolderPath = "";
        systemName = NormalizeFolderBindingSystemName(systemName);
        virtualFolderPath = NormalizeVirtualFolderPath(virtualFolderPath);

        return !string.IsNullOrWhiteSpace(systemName)
            && !string.IsNullOrWhiteSpace(virtualFolderPath)
            && _folderBindings.TryGetValue(systemName, out Dictionary<string, string> bindings)
            && bindings != null
            && bindings.TryGetValue(virtualFolderPath, out physicalFolderPath)
            && !string.IsNullOrWhiteSpace(physicalFolderPath);
    }

    private bool TryGetDirectlyBoundScriptOrSceneEntry(
        string sourceSystemName,
        string entry,
        out string sourceVirtualFolderPath,
        out string boundPhysicalFolderPath,
        out string physicalFilePath
    )
    {
        sourceVirtualFolderPath = "";
        boundPhysicalFolderPath = "";
        physicalFilePath = "";

        if (
            string.IsNullOrWhiteSpace(sourceSystemName)
            || string.IsNullOrWhiteSpace(entry)
            || !IsScriptOrSceneEntry(entry)
        )
            return false;

        sourceVirtualFolderPath = GetFolderPathFromEntry(entry);

        if (
            string.IsNullOrWhiteSpace(sourceVirtualFolderPath)
            || !TryGetFolderBinding(
                sourceSystemName,
                sourceVirtualFolderPath,
                out boundPhysicalFolderPath
            )
        )
            return false;

        physicalFilePath = GetPathFromEntry(entry);

        if (string.IsNullOrWhiteSpace(physicalFilePath))
            return false;

        string physicalParentFolderPath = NormalizeBoundFolderPath(physicalFilePath.GetBaseDir());
        boundPhysicalFolderPath = NormalizeBoundFolderPath(boundPhysicalFolderPath);

        return !string.IsNullOrWhiteSpace(physicalParentFolderPath)
            && !string.IsNullOrWhiteSpace(boundPhysicalFolderPath)
            && string.Equals(
                physicalParentFolderPath,
                boundPhysicalFolderPath,
                StringComparison.OrdinalIgnoreCase
            );
    }

    private bool TryGetFolderBindingFromMetadata(string metadata, out string physicalFolderPath)
    {
        physicalFolderPath = "";

        if (string.IsNullOrWhiteSpace(metadata) || !metadata.StartsWith("folder::"))
            return false;

        return TryGetFolderBinding(
            GetSystemNameFromMetadata(metadata),
            GetFolderPathFromMetadata(metadata),
            out physicalFolderPath
        );
    }

    private bool DoesVirtualFolderExist(string systemName, string virtualFolderPath)
    {
        if (
            string.IsNullOrWhiteSpace(systemName)
            || string.IsNullOrWhiteSpace(virtualFolderPath)
            || !_systems.TryGetValue(systemName, out List<string> entries)
            || entries == null
        )
            return false;

        return entries.Any(entry =>
            entry.StartsWith("folder::", StringComparison.Ordinal)
            && string.Equals(
                GetFolderPathFromFolderEntry(entry),
                virtualFolderPath,
                StringComparison.Ordinal
            )
        );
    }

    private void SetFolderBinding(
        string systemName,
        string virtualFolderPath,
        string physicalFolderPath
    )
    {
        if (!_folderBindings.TryGetValue(systemName, out Dictionary<string, string> bindings))
        {
            bindings = new Dictionary<string, string>(StringComparer.Ordinal);
            _folderBindings[systemName] = bindings;
        }

        bindings[virtualFolderPath] = physicalFolderPath;
    }

    private void RestoreFolderBindingAfterFailedSave(
        string systemName,
        string virtualFolderPath,
        bool hadPreviousBinding,
        string previousPhysicalFolderPath
    )
    {
        if (hadPreviousBinding)
        {
            SetFolderBinding(systemName, virtualFolderPath, previousPhysicalFolderPath);
            return;
        }

        RemoveExactFolderBinding(systemName, virtualFolderPath);
    }

    private bool RemoveExactFolderBinding(string systemName, string virtualFolderPath)
    {
        if (
            !_folderBindings.TryGetValue(systemName, out Dictionary<string, string> bindings)
            || bindings == null
            || !bindings.Remove(virtualFolderPath)
        )
            return false;

        if (bindings.Count == 0)
            _folderBindings.Remove(systemName);

        return true;
    }
    #endregion

    #region Folder Binding User Operations
    private void OpenFolderBindingDialog()
    {
        if (!TryGetPendingFolderBindingTarget(out _, out _))
            return;

        _folderBindingDialog.PopupCenteredRatio(0.8f);
    }

    private void OnFolderBindingDirectorySelected(string selectedDirectory)
    {
        string metadata = _pendingFolderBindingMetadata;

        if (!TryGetPendingFolderBindingTarget(out string systemName, out string virtualFolderPath))
            return;

        string physicalFolderPath = NormalizeBoundFolderPath(selectedDirectory);

        if (string.IsNullOrWhiteSpace(physicalFolderPath))
        {
            GD.PushWarning("System Explorer can only bind folders under res://.");
            return;
        }

        if (!DirAccess.DirExistsAbsolute(physicalFolderPath))
        {
            GD.PushWarning($"System Explorer could not find folder: {physicalFolderPath}");
            return;
        }

        using (DirAccess directory = DirAccess.Open(physicalFolderPath))
        {
            if (directory == null)
            {
                GD.PushWarning($"System Explorer could not open folder: {physicalFolderPath}");
                return;
            }
        }

        bool hadPreviousBinding = TryGetFolderBinding(
            systemName,
            virtualFolderPath,
            out string previousPhysicalFolderPath
        );

        SetFolderBinding(systemName, virtualFolderPath, physicalFolderPath);

        if (!SaveFolderBindings())
        {
            RestoreFolderBindingAfterFailedSave(
                systemName,
                virtualFolderPath,
                hadPreviousBinding,
                previousPhysicalFolderPath
            );
            GD.PushWarning(
                $"System Explorer rolled back the folder binding for '{virtualFolderPath}' because it could not be saved."
            );
            return;
        }

        int addedEntries = SynchronizeSingleBoundFolder(
            systemName,
            virtualFolderPath,
            physicalFolderPath,
            showUserWarnings: true
        );

        if (addedEntries > 0)
            SaveSystems();

        _pendingFolderBindingMetadata = "";
        BuildTree();
        SelectTreeItemByMetadata(metadata);
    }

    private void UnbindPendingFolder()
    {
        string metadata = _pendingFolderBindingMetadata;

        if (!TryGetPendingFolderBindingTarget(out string systemName, out string virtualFolderPath))
            return;

        if (!TryGetFolderBinding(systemName, virtualFolderPath, out string physicalFolderPath))
            return;

        if (!RemoveExactFolderBinding(systemName, virtualFolderPath))
            return;

        if (!SaveFolderBindings())
        {
            SetFolderBinding(systemName, virtualFolderPath, physicalFolderPath);
            GD.PushWarning(
                $"System Explorer could not unbind '{virtualFolderPath}' because the bindings file could not be saved."
            );
            return;
        }

        _pendingFolderBindingMetadata = "";
        BuildTree();
        SelectTreeItemByMetadata(metadata);
    }

    private bool TryGetPendingFolderBindingTarget(
        out string systemName,
        out string virtualFolderPath
    )
    {
        systemName = "";
        virtualFolderPath = "";

        if (
            string.IsNullOrWhiteSpace(_pendingFolderBindingMetadata)
            || !_pendingFolderBindingMetadata.StartsWith("folder::")
        )
            return false;

        if (!EnsureSystemsLoadedForTreeOperation("Folder Binding"))
            return false;

        systemName = NormalizeFolderBindingSystemName(
            GetSystemNameFromMetadata(_pendingFolderBindingMetadata)
        );
        virtualFolderPath = NormalizeVirtualFolderPath(
            GetFolderPathFromMetadata(_pendingFolderBindingMetadata)
        );

        if (
            string.IsNullOrWhiteSpace(systemName)
            || string.IsNullOrWhiteSpace(virtualFolderPath)
            || !DoesVirtualFolderExist(systemName, virtualFolderPath)
        )
        {
            GD.PushWarning(
                "System Explorer could not find the selected folder for the folder binding operation."
            );
            return false;
        }

        return true;
    }
    #endregion

    #region Bound Folder Synchronization
    private void SynchronizeBoundFoldersAtStartup()
    {
        int addedEntries = SynchronizeAllBoundFolders();

        if (addedEntries > 0)
            SaveSystems();
    }

    private int SynchronizeAllBoundFolders()
    {
        int addedEntries = 0;

        foreach (
            KeyValuePair<string, Dictionary<string, string>> systemBinding in _folderBindings
                .OrderBy(binding => binding.Key, StringComparer.OrdinalIgnoreCase)
                .ThenBy(binding => binding.Key, StringComparer.Ordinal)
                .ToList()
        )
        {
            if (systemBinding.Value == null)
                continue;

            foreach (
                KeyValuePair<string, string> folderBinding in systemBinding
                    .Value.OrderBy(binding => binding.Key, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(binding => binding.Key, StringComparer.Ordinal)
                    .ToList()
            )
            {
                if (!DoesVirtualFolderExist(systemBinding.Key, folderBinding.Key))
                {
                    DebugLogger.LogOperation(
                        "Bound Folder Sync skipped: virtual folder missing",
                        $"System='{systemBinding.Key}', Folder='{folderBinding.Key}'"
                    );
                    continue;
                }

                addedEntries += SynchronizeSingleBoundFolder(
                    systemBinding.Key,
                    folderBinding.Key,
                    folderBinding.Value,
                    showUserWarnings: false
                );
            }
        }

        return addedEntries;
    }

    private int SynchronizeSingleBoundFolder(
        string systemName,
        string virtualFolderPath,
        string physicalFolderPath,
        bool showUserWarnings
    )
    {
        if (
            !_systems.TryGetValue(systemName, out List<string> entries)
            || entries == null
            || !DoesVirtualFolderExist(systemName, virtualFolderPath)
        )
            return 0;

        physicalFolderPath = NormalizeBoundFolderPath(physicalFolderPath);

        if (string.IsNullOrWhiteSpace(physicalFolderPath))
            return 0;

        if (!DirAccess.DirExistsAbsolute(physicalFolderPath))
        {
            ReportUnavailableBoundFolder(physicalFolderPath, showUserWarnings);
            return 0;
        }

        using DirAccess directory = DirAccess.Open(physicalFolderPath);

        if (directory == null)
        {
            ReportUnavailableBoundFolder(physicalFolderPath, showUserWarnings);
            return 0;
        }

        List<string> supportedFiles = directory
            .GetFiles()
            .Where(IsSupportedBoundFolderFile)
            .OrderBy(fileName => fileName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(fileName => fileName, StringComparer.Ordinal)
            .ToList();

        int addedEntries = 0;

        foreach (string fileName in supportedFiles)
        {
            bool isScene = fileName.EndsWith(".tscn", StringComparison.OrdinalIgnoreCase);
            string resourcePath = CombineBoundFolderFilePath(physicalFolderPath, fileName);

            if (HasMatchingBoundFolderEntry(entries, virtualFolderPath, resourcePath, isScene))
                continue;

            string linkedScenePath = isScene
                ? ""
                : GetExistingLinkedScenePathForScript(resourcePath);

            entries.Add(
                isScene
                    ? BuildSceneEntry(virtualFolderPath, resourcePath)
                    : BuildScriptEntry(virtualFolderPath, resourcePath, linkedScenePath)
            );
            addedEntries++;
        }

        if (addedEntries > 0)
        {
            DebugLogger.LogOperation(
                "Bound Folder Sync added entries",
                $"System='{systemName}', Folder='{virtualFolderPath}', Count={addedEntries}"
            );
        }

        return addedEntries;
    }

    private void ReportUnavailableBoundFolder(string physicalFolderPath, bool showUserWarnings)
    {
        string message = $"Bound folder is unavailable: {physicalFolderPath}";

        if (showUserWarnings)
            GD.PushWarning(message);
        else
            DebugLogger.LogOperation("Bound Folder Sync skipped", message);
    }

    private static bool IsSupportedBoundFolderFile(string fileName)
    {
        return !string.IsNullOrWhiteSpace(fileName)
            && (
                fileName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                || fileName.EndsWith(".tscn", StringComparison.OrdinalIgnoreCase)
            );
    }

    private static string CombineBoundFolderFilePath(string folderPath, string fileName)
    {
        return folderPath == "res://" ? $"res://{fileName}" : $"{folderPath}/{fileName}";
    }

    private static bool HasMatchingBoundFolderEntry(
        IEnumerable<string> entries,
        string virtualFolderPath,
        string physicalFilePath,
        bool isScene
    )
    {
        foreach (string entry in entries)
        {
            if (!IsScriptOrSceneEntry(entry) || IsSceneEntry(entry) != isScene)
                continue;

            if (
                !string.Equals(
                    GetFolderPathFromEntry(entry),
                    virtualFolderPath,
                    StringComparison.Ordinal
                )
            )
                continue;

            string existingPath = isScene
                ? GetScenePathFromEntry(entry)
                : GetScriptPathFromEntry(entry);

            if (string.Equals(existingPath, physicalFilePath, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
    #endregion

    #region EditorFileSystem Lifecycle
    private void InitializeFolderBindingFilesystemLifecycle()
    {
        if (_folderBindingFilesystemSignalConnected)
            return;

        _folderBindingResourceFilesystem = EditorInterface.Singleton?.GetResourceFilesystem();

        if (_folderBindingResourceFilesystem == null)
        {
            DebugLogger.Log("Folder Binding filesystem lifecycle skipped: filesystem unavailable.");
            return;
        }

        _folderBindingResourceFilesystem.FilesystemChanged += OnBoundFolderFilesystemChanged;
        _folderBindingFilesystemSignalConnected = true;
        DebugLogger.Log("Folder Binding filesystem signal connected.");
    }

    private void ShutdownFolderBindingFilesystemLifecycle()
    {
        _boundFolderSyncQueued = false;
        _boundFolderSyncRunning = false;

        if (
            _folderBindingFilesystemSignalConnected
            && GodotObject.IsInstanceValid(_folderBindingResourceFilesystem)
        )
        {
            _folderBindingResourceFilesystem.FilesystemChanged -= OnBoundFolderFilesystemChanged;
        }

        _folderBindingFilesystemSignalConnected = false;
        _folderBindingResourceFilesystem = null;
    }

    private void OnBoundFolderFilesystemChanged()
    {
        if (_boundFolderSyncQueued || _boundFolderSyncRunning || _folderBindings.Count == 0)
            return;

        _boundFolderSyncQueued = true;
        CallDeferred(nameof(RunQueuedBoundFolderSync));
    }

    private void RunQueuedBoundFolderSync()
    {
        _boundFolderSyncQueued = false;

        if (
            _boundFolderSyncRunning
            || !_folderBindingFilesystemSignalConnected
            || _tree == null
            || !GodotObject.IsInstanceValid(_tree)
        )
            return;

        _boundFolderSyncRunning = true;

        try
        {
            string selectedMetadata = _tree?.GetSelected()?.GetMetadata(0).AsString() ?? "";
            int addedEntries = SynchronizeAllBoundFolders();

            if (addedEntries == 0)
                return;

            SaveSystems();
            BuildTree();

            if (!string.IsNullOrWhiteSpace(selectedMetadata))
                SelectTreeItemByMetadata(selectedMetadata);
        }
        finally
        {
            _boundFolderSyncRunning = false;
        }
    }
    #endregion

    #region Folder Binding Rename and Remove Migration
    private void MigrateFolderBindingsForSystemRename(string oldSystemName, string newSystemName)
    {
        if (
            string.IsNullOrWhiteSpace(oldSystemName)
            || string.IsNullOrWhiteSpace(newSystemName)
            || !_folderBindings.TryGetValue(
                oldSystemName,
                out Dictionary<string, string> oldSystemBindings
            )
        )
            return;

        _folderBindings.Remove(oldSystemName);

        if (!_folderBindings.TryGetValue(newSystemName, out Dictionary<string, string> newBindings))
        {
            newBindings = new Dictionary<string, string>(StringComparer.Ordinal);
            _folderBindings[newSystemName] = newBindings;
        }

        foreach (KeyValuePair<string, string> binding in oldSystemBindings)
            newBindings[binding.Key] = binding.Value;

        SaveFolderBindings();
    }

    private void MigrateFolderBindingsForFolderRename(
        string systemName,
        string oldFolderPath,
        string newFolderPath
    )
    {
        if (
            string.IsNullOrWhiteSpace(systemName)
            || string.IsNullOrWhiteSpace(oldFolderPath)
            || string.IsNullOrWhiteSpace(newFolderPath)
            || !_folderBindings.TryGetValue(
                systemName,
                out Dictionary<string, string> systemBindings
            )
        )
            return;

        List<KeyValuePair<string, string>> affectedBindings = systemBindings
            .Where(binding =>
                binding.Key == oldFolderPath
                || binding.Key.StartsWith($"{oldFolderPath}/", StringComparison.Ordinal)
            )
            .ToList();

        if (affectedBindings.Count == 0)
            return;

        foreach (KeyValuePair<string, string> binding in affectedBindings)
            systemBindings.Remove(binding.Key);

        foreach (KeyValuePair<string, string> binding in affectedBindings)
        {
            string migratedFolderPath =
                binding.Key == oldFolderPath
                    ? newFolderPath
                    : $"{newFolderPath}{binding.Key.Substring(oldFolderPath.Length)}";
            systemBindings[migratedFolderPath] = binding.Value;
        }

        SaveFolderBindings();
    }

    private void RemoveFolderBindingsForSystem(string systemName)
    {
        if (string.IsNullOrWhiteSpace(systemName) || !_folderBindings.Remove(systemName))
            return;

        SaveFolderBindings();
    }

    private void RemoveFolderBindingsForFolderAndDescendants(string metadata)
    {
        string systemName = GetSystemNameFromMetadata(metadata);
        string folderPath = GetFolderPathFromMetadata(metadata);

        if (
            string.IsNullOrWhiteSpace(systemName)
            || string.IsNullOrWhiteSpace(folderPath)
            || !_folderBindings.TryGetValue(
                systemName,
                out Dictionary<string, string> systemBindings
            )
        )
            return;

        List<string> bindingPathsToRemove = systemBindings
            .Keys.Where(bindingPath =>
                bindingPath == folderPath
                || bindingPath.StartsWith($"{folderPath}/", StringComparison.Ordinal)
            )
            .ToList();

        if (bindingPathsToRemove.Count == 0)
            return;

        foreach (string bindingPath in bindingPathsToRemove)
            systemBindings.Remove(bindingPath);

        if (systemBindings.Count == 0)
            _folderBindings.Remove(systemName);

        SaveFolderBindings();
    }
    #endregion
}
#endif
