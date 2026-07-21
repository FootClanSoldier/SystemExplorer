#if TOOLS
using Godot;
using System;
using System.Collections.Generic;
using SystemExplorer.EditorIntegration.ScriptEditing;

public partial class SystemExplorerPlugin
{
    #region Script Editor Sync
    private const string ScriptEditorChangedSignalName = "editor_script_changed";
    private const double ScriptEditorSyncPollIntervalSeconds = 0.5;

    private readonly record struct ScriptTreeOccurrence(
        string SystemName,
        string Entry,
        string ScriptPath
    );

    private readonly record struct PendingSystemExplorerScriptActivation(
        ScriptTreeOccurrence SourceOccurrence,
        string ExpectedScriptPath,
        long Token
    );

    private readonly Dictionary<string, ScriptTreeOccurrence> _lastScriptTreeOccurrencesByPath =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _followActiveScript = true;
    private bool _followActiveScriptHasObservedInitialPath;
    private bool _isSyncingTreeSelectionToActiveScript;
    private bool _isScriptEditorSyncSignalConnected;
    private bool _isScriptEditorSyncDeferredQueued;
    private bool _suppressNextTreeNavigationFromScriptEditorSync;
    private int _scriptEditorSyncSuppressionDepth;
    private int _postOperationActiveScriptSyncSuppressionDepth;
    private long _systemExplorerScriptActivationSequence;
    private string _lastObservedActiveScriptPath = "";
    private string _lastSyncedActiveScriptPath = "";
    private PendingSystemExplorerScriptActivation? _pendingSystemExplorerScriptActivation;
    private ScriptEditor _scriptEditorSyncScriptEditor;
    private Timer _scriptEditorSyncPollTimer;

    private void InitializeScriptEditorSync()
    {
        ShutdownScriptEditorSync();

        ScriptEditor scriptEditor = EditorInterface.Singleton?.GetScriptEditor();

        if (scriptEditor == null)
        {
            StartScriptEditorSyncPollingFallback();
            return;
        }

        _scriptEditorSyncScriptEditor = scriptEditor;
        PrimeScriptEditorSyncActiveScriptPath();

        if (!TryConnectScriptEditorSyncSignal(scriptEditor))
            StartScriptEditorSyncPollingFallback();
    }

    private void ShutdownScriptEditorSync()
    {
        DisconnectScriptEditorSyncSignal();
        StopScriptEditorSyncPollingFallback();

        _scriptEditorSyncScriptEditor = null;
        _lastObservedActiveScriptPath = "";
        _lastSyncedActiveScriptPath = "";
        _followActiveScriptHasObservedInitialPath = false;
        _isSyncingTreeSelectionToActiveScript = false;
        _isScriptEditorSyncDeferredQueued = false;
        _suppressNextTreeNavigationFromScriptEditorSync = false;
        _scriptEditorSyncSuppressionDepth = 0;
        _postOperationActiveScriptSyncSuppressionDepth = 0;
        ClearPendingSystemExplorerScriptActivation();
        _lastScriptTreeOccurrencesByPath.Clear();
    }

    private bool TryConnectScriptEditorSyncSignal(ScriptEditor scriptEditor)
    {
        if (scriptEditor == null || !scriptEditor.HasSignal(ScriptEditorChangedSignalName))
            return false;

        Callable callable = new(this, nameof(OnScriptEditorScriptChanged));

        if (scriptEditor.IsConnected(ScriptEditorChangedSignalName, callable))
        {
            _isScriptEditorSyncSignalConnected = true;
            return true;
        }

        Error error = scriptEditor.Connect(ScriptEditorChangedSignalName, callable);

        if (error != Error.Ok)
        {
            DebugLogger.Log($"Script Editor Sync signal connect skipped: {error}.");
            return false;
        }

        _isScriptEditorSyncSignalConnected = true;
        DebugLogger.Log("Script Editor Sync connected to editor_script_changed.");
        return true;
    }

    private void DisconnectScriptEditorSyncSignal()
    {
        ScriptEditor scriptEditor = _scriptEditorSyncScriptEditor;

        if (!_isScriptEditorSyncSignalConnected || !GodotObject.IsInstanceValid(scriptEditor))
        {
            _isScriptEditorSyncSignalConnected = false;
            return;
        }

        Callable callable = new(this, nameof(OnScriptEditorScriptChanged));

        if (scriptEditor.IsConnected(ScriptEditorChangedSignalName, callable))
            scriptEditor.Disconnect(ScriptEditorChangedSignalName, callable);

        _isScriptEditorSyncSignalConnected = false;
    }

    private void StartScriptEditorSyncPollingFallback()
    {
        if (_scriptEditorSyncPollTimer != null)
            return;

        _scriptEditorSyncPollTimer = new Timer
        {
            WaitTime = ScriptEditorSyncPollIntervalSeconds,
            OneShot = false,
            Autostart = false,
        };
        _scriptEditorSyncPollTimer.Timeout += OnScriptEditorSyncPollTimerTimeout;
        AddChild(_scriptEditorSyncPollTimer);
        _scriptEditorSyncPollTimer.Start();

        DebugLogger.Log("Script Editor Sync using polling fallback.");
    }

    private void StopScriptEditorSyncPollingFallback()
    {
        if (_scriptEditorSyncPollTimer == null)
            return;

        _scriptEditorSyncPollTimer.Timeout -= OnScriptEditorSyncPollTimerTimeout;
        _scriptEditorSyncPollTimer.Stop();
        _scriptEditorSyncPollTimer.QueueFree();
        _scriptEditorSyncPollTimer = null;
    }

    private void OnScriptEditorScriptChanged(Script script)
    {
        if (!_followActiveScript || IsScriptEditorSyncSuppressed())
            return;

        QueueScriptEditorSyncToActiveScript();
    }

    private void OnScriptEditorSyncPollTimerTimeout()
    {
        if (!_followActiveScript || IsScriptEditorSyncSuppressed())
            return;

        QueueScriptEditorSyncToActiveScript();
    }

    private void QueueScriptEditorSyncToActiveScript()
    {
        if (_isScriptEditorSyncDeferredQueued || IsScriptEditorSyncSuppressed())
            return;

        _isScriptEditorSyncDeferredQueued = true;
        CallDeferred(nameof(TrySyncTreeSelectionToActiveScriptDeferred));
    }

    private void TrySyncTreeSelectionToActiveScriptDeferred()
    {
        _isScriptEditorSyncDeferredQueued = false;

        if (IsScriptEditorSyncSuppressed())
            return;

        TrySyncTreeSelectionToActiveScript();
    }

    private void TrySyncTreeSelectionToActiveScript()
    {
        TrySyncTreeSelectionToActiveScriptCore(force: false);
    }

    private void SyncSelectionToActiveScriptAfterOperation()
    {
        if (IsPostOperationActiveScriptSyncSuppressed())
        {
            DebugLogger.Log(
                "Script Editor Sync post-operation sync skipped because batch context preservation is active."
            );
            return;
        }

        TrySyncTreeSelectionToActiveScriptCore(force: true);
    }

    private bool TrySyncTreeSelectionToActiveScriptCore(bool force)
    {
        if (_isSyncingTreeSelectionToActiveScript || IsScriptEditorSyncSuppressed())
            return false;

        if (!TryGetActiveScriptPath(out string scriptPath))
            return false;

        if (
            TryHandlePendingSystemExplorerScriptActivation(
                scriptPath,
                out bool pendingSelectionResult
            )
        )
        {
            return pendingSelectionResult;
        }

        if (!force && !_followActiveScript)
            return false;

        if (IsSelectedScriptPathInTree(scriptPath))
        {
            if (
                TryGetScriptTreeOccurrenceFromTreeItem(
                    _tree?.GetSelected(),
                    out ScriptTreeOccurrence selectedOccurrence
                )
            )
            {
                RememberScriptTreeOccurrence(selectedOccurrence);
            }

            UpdateScriptEditorSyncPathTracking(scriptPath);
            return true;
        }

        if (!force && !ShouldFollowSyncForActiveScriptPath(scriptPath))
            return false;

        RecordObservedActiveScriptPath(scriptPath);

        if (TrySelectLastUsedScriptTreeOccurrence(scriptPath))
        {
            _lastSyncedActiveScriptPath = scriptPath;
            return true;
        }

        bool selected = TrySelectScriptPathInTree(scriptPath);

        if (selected)
            _lastSyncedActiveScriptPath = scriptPath;

        return selected;
    }

    private bool TryHandlePendingSystemExplorerScriptActivation(
        string activeScriptPath,
        out bool selectionResult
    )
    {
        selectionResult = false;

        if (!_pendingSystemExplorerScriptActivation.HasValue)
            return false;

        PendingSystemExplorerScriptActivation pendingActivation =
            _pendingSystemExplorerScriptActivation.Value;

        if (
            !string.Equals(
                pendingActivation.ExpectedScriptPath,
                activeScriptPath,
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            ClearPendingSystemExplorerScriptActivation(pendingActivation.Token);
            return false;
        }

        if (!_followActiveScript)
        {
            if (
                TryGetScriptTreeOccurrenceFromTreeItem(
                    _tree?.GetSelected(),
                    out ScriptTreeOccurrence selectedOccurrence
                )
                && string.Equals(
                    selectedOccurrence.ScriptPath,
                    activeScriptPath,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                RememberScriptTreeOccurrence(selectedOccurrence);
                selectionResult = true;
            }

            UpdateScriptEditorSyncPathTracking(activeScriptPath);
            ClearPendingSystemExplorerScriptActivation(pendingActivation.Token);
            return true;
        }

        bool selectedExactOccurrence = IsSelectedScriptTreeOccurrence(
            pendingActivation.SourceOccurrence
        );

        if (!selectedExactOccurrence)
        {
            selectedExactOccurrence = TrySelectScriptTreeOccurrence(
                pendingActivation.SourceOccurrence
            );
        }

        if (selectedExactOccurrence)
            RememberScriptTreeOccurrence(pendingActivation.SourceOccurrence);

        UpdateScriptEditorSyncPathTracking(activeScriptPath);
        ClearPendingSystemExplorerScriptActivation(pendingActivation.Token);
        selectionResult = selectedExactOccurrence;
        return true;
    }

    private void PrimeScriptEditorSyncActiveScriptPath()
    {
        if (TryGetActiveScriptPath(out string scriptPath))
            RecordObservedActiveScriptPath(scriptPath);
    }

    private bool ShouldFollowSyncForActiveScriptPath(string scriptPath)
    {
        string normalizedPath = NormalizeScriptPathForSync(scriptPath);

        if (string.IsNullOrWhiteSpace(normalizedPath))
            return false;

        if (!_followActiveScriptHasObservedInitialPath)
        {
            RecordObservedActiveScriptPath(normalizedPath);
            return false;
        }

        return !string.Equals(
            _lastObservedActiveScriptPath,
            normalizedPath,
            StringComparison.OrdinalIgnoreCase
        );
    }

    private void RecordObservedActiveScriptPath(string scriptPath)
    {
        string normalizedPath = NormalizeScriptPathForSync(scriptPath);

        if (string.IsNullOrWhiteSpace(normalizedPath))
            return;

        _lastObservedActiveScriptPath = normalizedPath;
        _followActiveScriptHasObservedInitialPath = true;
    }

    private void UpdateScriptEditorSyncPathTracking(string scriptPath)
    {
        string normalizedPath = NormalizeScriptPathForSync(scriptPath);

        if (string.IsNullOrWhiteSpace(normalizedPath))
            return;

        RecordObservedActiveScriptPath(normalizedPath);
        _lastSyncedActiveScriptPath = normalizedPath;
    }

    private void BeginScriptEditorSyncSuppression()
    {
        _scriptEditorSyncSuppressionDepth++;
        _isScriptEditorSyncDeferredQueued = false;
        ClearPendingSystemExplorerScriptActivation();
    }

    private void EndScriptEditorSyncSuppression()
    {
        if (_scriptEditorSyncSuppressionDepth <= 0)
        {
            _scriptEditorSyncSuppressionDepth = 0;
            return;
        }

        _scriptEditorSyncSuppressionDepth--;

        if (_scriptEditorSyncSuppressionDepth > 0)
            return;

        _isScriptEditorSyncDeferredQueued = false;
        PrimeScriptEditorSyncActiveScriptPath();
    }

    private bool IsScriptEditorSyncSuppressed()
    {
        return _scriptEditorSyncSuppressionDepth > 0;
    }

    private void BeginPostOperationActiveScriptSyncSuppression()
    {
        _postOperationActiveScriptSyncSuppressionDepth++;
    }

    private void EndPostOperationActiveScriptSyncSuppression()
    {
        if (_postOperationActiveScriptSyncSuppressionDepth <= 0)
        {
            _postOperationActiveScriptSyncSuppressionDepth = 0;
            return;
        }

        _postOperationActiveScriptSyncSuppressionDepth--;
    }

    private bool IsPostOperationActiveScriptSyncSuppressed()
    {
        return _postOperationActiveScriptSyncSuppressionDepth > 0;
    }

    private void BeginBatchScriptEditorContextPreservation()
    {
        BeginScriptEditorSyncSuppression();
        BeginPostOperationActiveScriptSyncSuppression();
    }

    private void EndBatchScriptEditorContextPreservation()
    {
        EndScriptEditorSyncSuppression();
        CallDeferred(nameof(EndBatchPostOperationActiveScriptSyncSuppressionDeferred));
    }

    private void EndBatchPostOperationActiveScriptSyncSuppressionDeferred()
    {
        EndPostOperationActiveScriptSyncSuppression();
    }

    private bool TryGetActiveScriptPath(out string scriptPath)
    {
        scriptPath = "";

        ScriptEditor scriptEditor = EditorInterface.Singleton?.GetScriptEditor();

        if (scriptEditor == null)
            return false;

        Script currentScript = scriptEditor.GetCurrentScript();
        scriptPath = NormalizeScriptPathForSync(currentScript?.ResourcePath);

        return !string.IsNullOrWhiteSpace(scriptPath);
    }

    private long RegisterSystemExplorerScriptActivation(
        ScriptTreeOccurrence sourceOccurrence,
        string expectedScriptPath
    )
    {
        if (IsScriptEditorSyncSuppressed())
        {
            ClearPendingSystemExplorerScriptActivation();
            return 0;
        }

        string normalizedExpectedPath = NormalizeScriptPathForSync(expectedScriptPath);

        if (
            string.IsNullOrWhiteSpace(normalizedExpectedPath)
            || !string.Equals(
                sourceOccurrence.ScriptPath,
                normalizedExpectedPath,
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            ClearPendingSystemExplorerScriptActivation();
            return 0;
        }

        _systemExplorerScriptActivationSequence++;

        if (_systemExplorerScriptActivationSequence <= 0)
            _systemExplorerScriptActivationSequence = 1;

        _pendingSystemExplorerScriptActivation = new PendingSystemExplorerScriptActivation(
            sourceOccurrence,
            normalizedExpectedPath,
            _systemExplorerScriptActivationSequence
        );

        return _systemExplorerScriptActivationSequence;
    }

    private void QueueSystemExplorerScriptActivationDeferredCheck(long token)
    {
        if (token <= 0)
            return;

        CallDeferred(nameof(ResolveSystemExplorerScriptActivationDeferred), token);
    }

    private void ResolveSystemExplorerScriptActivationDeferred(long token)
    {
        if (
            !_pendingSystemExplorerScriptActivation.HasValue
            || _pendingSystemExplorerScriptActivation.Value.Token != token
        )
        {
            return;
        }

        if (IsScriptEditorSyncSuppressed())
        {
            ClearPendingSystemExplorerScriptActivation(token);
            return;
        }

        PendingSystemExplorerScriptActivation pendingActivation =
            _pendingSystemExplorerScriptActivation.Value;

        if (
            !TryGetActiveScriptPath(out string activeScriptPath)
            || !string.Equals(
                activeScriptPath,
                pendingActivation.ExpectedScriptPath,
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            ClearPendingSystemExplorerScriptActivation(token);
            return;
        }

        TrySyncTreeSelectionToActiveScriptCore(force: false);

        if (
            _pendingSystemExplorerScriptActivation.HasValue
            && _pendingSystemExplorerScriptActivation.Value.Token == token
        )
        {
            ClearPendingSystemExplorerScriptActivation(token);
        }
    }

    private void ClearPendingSystemExplorerScriptActivation()
    {
        _pendingSystemExplorerScriptActivation = null;
    }

    private void ClearPendingSystemExplorerScriptActivation(long token)
    {
        if (
            _pendingSystemExplorerScriptActivation.HasValue
            && _pendingSystemExplorerScriptActivation.Value.Token == token
        )
        {
            _pendingSystemExplorerScriptActivation = null;
        }
    }

    private bool TryGetScriptTreeOccurrenceFromTreeItem(
        TreeItem item,
        out ScriptTreeOccurrence occurrence
    )
    {
        occurrence = default;

        if (item == null || !GodotObject.IsInstanceValid(item))
            return false;

        string metadata = item.GetMetadata(0).AsString();

        if (
            string.IsNullOrWhiteSpace(metadata)
            || !metadata.StartsWith("script::", StringComparison.Ordinal)
        )
        {
            return false;
        }

        string entry = GetEntryFromMetadata(metadata);

        if (string.IsNullOrWhiteSpace(entry))
            return false;

        string systemName;

        if (IsScriptFilterActive())
        {
            if (!TryGetScriptFilterResultForTreeItem(item, out ScriptFilterResult result))
                return false;

            if (
                !string.Equals(result.Entry, entry, StringComparison.Ordinal)
                || !string.Equals(
                    result.FolderPath,
                    GetFolderPathFromEntry(entry),
                    StringComparison.Ordinal
                )
            )
            {
                return false;
            }

            systemName = result.SystemName;
            entry = result.Entry;
        }
        else if (!TryGetSystemNameFromTreeItemParentChain(item, out systemName))
        {
            return false;
        }

        string scriptPath = NormalizeScriptPathForSync(GetScriptPathFromEntry(entry));

        if (string.IsNullOrWhiteSpace(systemName) || string.IsNullOrWhiteSpace(scriptPath))
        {
            return false;
        }

        occurrence = new ScriptTreeOccurrence(systemName, entry, scriptPath);
        return true;
    }

    private bool TryGetScriptFilterResultForTreeItem(
        TreeItem item,
        out ScriptFilterResult matchingResult
    )
    {
        matchingResult = default;

        if (!IsScriptFilterActive() || _tree == null || item == null)
            return false;

        TreeItem root = _tree.GetRoot();
        TreeItem current = root?.GetFirstChild();
        List<ScriptFilterResult> results = GetFilteredScriptResults(
            (_scriptFilterInput?.Text ?? "").Trim().ToLowerInvariant()
        );

        for (int index = 0; current != null && index < results.Count; index++)
        {
            if (current == item)
            {
                matchingResult = results[index];
                return true;
            }

            current = current.GetNext();
        }

        return false;
    }

    private static bool TryGetSystemNameFromTreeItemParentChain(
        TreeItem item,
        out string systemName
    )
    {
        systemName = "";
        TreeItem current = item?.GetParent();

        while (current != null)
        {
            string metadata = current.GetMetadata(0).AsString();

            if (metadata.StartsWith("system::", StringComparison.Ordinal))
            {
                systemName = GetSystemNameFromMetadata(metadata);
                return !string.IsNullOrWhiteSpace(systemName);
            }

            current = current.GetParent();
        }

        return false;
    }

    private bool TryFindScriptTreeItemByOccurrence(
        ScriptTreeOccurrence occurrence,
        out TreeItem item
    )
    {
        item = null;

        if (_tree == null || !IsScriptTreeOccurrenceStillValid(occurrence))
            return false;

        TreeItem root = _tree.GetRoot();

        if (root == null)
            return false;

        if (IsScriptFilterActive())
            return TryFindFilteredScriptTreeItemByOccurrence(root, occurrence, out item);

        TreeItem systemItem = root.GetFirstChild();
        string expectedSystemMetadata = $"system::{occurrence.SystemName}";

        while (systemItem != null)
        {
            if (
                string.Equals(
                    systemItem.GetMetadata(0).AsString(),
                    expectedSystemMetadata,
                    StringComparison.Ordinal
                )
            )
            {
                TreeItem matchingItem = FindTreeItemByMetadataWithinSubtree(
                    systemItem,
                    $"script::{occurrence.Entry}"
                );

                if (
                    matchingItem != null
                    && TryGetScriptPathFromTreeItem(matchingItem, out string matchingScriptPath)
                    && string.Equals(
                        matchingScriptPath,
                        occurrence.ScriptPath,
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                {
                    item = matchingItem;
                    return true;
                }

                return false;
            }

            systemItem = systemItem.GetNext();
        }

        return false;
    }

    private bool TryFindFilteredScriptTreeItemByOccurrence(
        TreeItem root,
        ScriptTreeOccurrence occurrence,
        out TreeItem item
    )
    {
        item = null;
        List<ScriptFilterResult> results = GetFilteredScriptResults(
            (_scriptFilterInput?.Text ?? "").Trim().ToLowerInvariant()
        );
        TreeItem current = root.GetFirstChild();

        for (int index = 0; current != null && index < results.Count; index++)
        {
            ScriptFilterResult result = results[index];

            if (
                string.Equals(result.SystemName, occurrence.SystemName, StringComparison.Ordinal)
                && string.Equals(result.Entry, occurrence.Entry, StringComparison.Ordinal)
                && string.Equals(
                    current.GetMetadata(0).AsString(),
                    $"script::{occurrence.Entry}",
                    StringComparison.Ordinal
                )
                && TryGetScriptPathFromTreeItem(current, out string currentScriptPath)
                && string.Equals(
                    currentScriptPath,
                    occurrence.ScriptPath,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                item = current;
                return true;
            }

            current = current.GetNext();
        }

        return false;
    }

    private bool IsScriptTreeOccurrenceStillValid(ScriptTreeOccurrence occurrence)
    {
        if (
            string.IsNullOrWhiteSpace(occurrence.SystemName)
            || string.IsNullOrWhiteSpace(occurrence.Entry)
            || string.IsNullOrWhiteSpace(occurrence.ScriptPath)
            || !_systems.TryGetValue(occurrence.SystemName, out List<string> entries)
        )
        {
            return false;
        }

        foreach (string entry in entries)
        {
            if (!string.Equals(entry, occurrence.Entry, StringComparison.Ordinal))
                continue;

            if (IsSceneEntry(entry))
                return false;

            string currentScriptPath = NormalizeScriptPathForSync(GetScriptPathFromEntry(entry));
            return string.Equals(
                currentScriptPath,
                occurrence.ScriptPath,
                StringComparison.OrdinalIgnoreCase
            );
        }

        return false;
    }

    private static bool IsSameScriptTreeOccurrence(
        ScriptTreeOccurrence left,
        ScriptTreeOccurrence right
    )
    {
        return string.Equals(left.SystemName, right.SystemName, StringComparison.Ordinal)
            && string.Equals(left.Entry, right.Entry, StringComparison.Ordinal)
            && string.Equals(left.ScriptPath, right.ScriptPath, StringComparison.OrdinalIgnoreCase);
    }

    private bool IsSelectedScriptTreeOccurrence(ScriptTreeOccurrence occurrence)
    {
        return TryGetScriptTreeOccurrenceFromTreeItem(
                _tree?.GetSelected(),
                out ScriptTreeOccurrence selectedOccurrence
            ) && IsSameScriptTreeOccurrence(selectedOccurrence, occurrence);
    }

    private void RememberScriptTreeOccurrence(ScriptTreeOccurrence occurrence)
    {
        string normalizedPath = NormalizeScriptPathForSync(occurrence.ScriptPath);

        if (string.IsNullOrWhiteSpace(normalizedPath))
            return;

        _lastScriptTreeOccurrencesByPath[normalizedPath] = occurrence;
    }

    private bool TrySelectLastUsedScriptTreeOccurrence(string scriptPath)
    {
        string normalizedPath = NormalizeScriptPathForSync(scriptPath);

        if (
            string.IsNullOrWhiteSpace(normalizedPath)
            || !_lastScriptTreeOccurrencesByPath.TryGetValue(
                normalizedPath,
                out ScriptTreeOccurrence occurrence
            )
        )
        {
            return false;
        }

        if (!IsScriptTreeOccurrenceStillValid(occurrence))
        {
            _lastScriptTreeOccurrencesByPath.Remove(normalizedPath);
            return false;
        }

        if (!TrySelectScriptTreeOccurrence(occurrence))
            return false;

        RememberScriptTreeOccurrence(occurrence);
        return true;
    }

    private bool TrySelectScriptTreeOccurrence(ScriptTreeOccurrence occurrence)
    {
        return TryFindScriptTreeItemByOccurrence(occurrence, out TreeItem item)
            && TryApplyScriptTreeSelection(item);
    }

    private bool TrySelectScriptPathInTree(string scriptPath)
    {
        string normalizedPath = NormalizeScriptPathForSync(scriptPath);

        if (string.IsNullOrWhiteSpace(normalizedPath) || _tree == null)
            return false;

        return TryFindScriptTreeItemByPath(normalizedPath, out TreeItem item)
            && TryApplyScriptTreeSelection(item);
    }

    private bool TryApplyScriptTreeSelection(TreeItem item)
    {
        if (item == null || !GodotObject.IsInstanceValid(item) || _tree == null)
            return false;

        Control focusedControl = GetViewport()?.GuiGetFocusOwner();
        bool shouldRestoreScriptEditorFocus = IsFocusedControlInsideCurrentScriptEditor(
            focusedControl
        );

        _isSyncingTreeSelectionToActiveScript = true;

        try
        {
            ExpandParentsForTreeItem(item);

            bool selectionChanged = _tree.GetSelected() != item;

            if (selectionChanged)
            {
                _suppressNextTreeNavigationFromScriptEditorSync = true;
                item.Select(0);
            }

            _tree.ScrollToItem(item);
            UpdateTreeLockIconVisibility();

            if (selectionChanged)
                CallDeferred(nameof(ClearScriptEditorSyncTreeNavigationSuppression));
        }
        finally
        {
            _isSyncingTreeSelectionToActiveScript = false;
        }

        if (shouldRestoreScriptEditorFocus)
            CallDeferred(nameof(RestoreActiveScriptEditorFocusDeferred));

        return true;
    }

    private void ClearScriptEditorSyncTreeNavigationSuppression()
    {
        _suppressNextTreeNavigationFromScriptEditorSync = false;
    }

    private bool TryFindScriptTreeItemByPath(string scriptPath, out TreeItem item)
    {
        item = null;

        string normalizedPath = NormalizeScriptPathForSync(scriptPath);

        if (string.IsNullOrWhiteSpace(normalizedPath) || _tree == null)
            return false;

        TreeItem root = _tree.GetRoot();

        if (root == null)
            return false;

        return TryFindScriptTreeItemByPathRecursive(root, normalizedPath, out item);
    }

    private bool TryFindScriptTreeItemByPathRecursive(
        TreeItem item,
        string scriptPath,
        out TreeItem matchingItem
    )
    {
        matchingItem = null;
        TreeItem current = item;

        while (current != null)
        {
            if (
                TryGetScriptPathFromTreeItem(current, out string currentScriptPath)
                && string.Equals(currentScriptPath, scriptPath, StringComparison.OrdinalIgnoreCase)
            )
            {
                matchingItem = current;
                return true;
            }

            TreeItem child = current.GetFirstChild();

            if (
                child != null
                && TryFindScriptTreeItemByPathRecursive(child, scriptPath, out matchingItem)
            )
            {
                return true;
            }

            current = current.GetNext();
        }

        return false;
    }

    private bool TryGetScriptPathFromTreeItem(TreeItem item, out string scriptPath)
    {
        scriptPath = "";

        if (item == null)
            return false;

        string metadata = item.GetMetadata(0).AsString();

        if (
            string.IsNullOrWhiteSpace(metadata)
            || !metadata.StartsWith("script::", StringComparison.Ordinal)
        )
        {
            return false;
        }

        string entry = GetEntryFromMetadata(metadata);
        scriptPath = NormalizeScriptPathForSync(GetScriptPathFromEntry(entry));

        return !string.IsNullOrWhiteSpace(scriptPath);
    }

    private bool IsSelectedScriptPathInTree(string scriptPath)
    {
        string normalizedPath = NormalizeScriptPathForSync(scriptPath);

        if (string.IsNullOrWhiteSpace(normalizedPath) || _tree == null)
            return false;

        return TryGetScriptPathFromTreeItem(_tree.GetSelected(), out string selectedScriptPath)
            && string.Equals(
                selectedScriptPath,
                normalizedPath,
                StringComparison.OrdinalIgnoreCase
            );
    }

    private void ExpandParentsForTreeItem(TreeItem item)
    {
        TreeItem parent = item?.GetParent();

        while (parent != null)
        {
            parent.Collapsed = false;
            parent = parent.GetParent();
        }
    }

    private bool IsFocusedControlInsideCurrentScriptEditor(Control focusedControl)
    {
        if (focusedControl == null)
            return false;

        ScriptEditor scriptEditor = EditorInterface.Singleton?.GetScriptEditor();

        if (scriptEditor == null)
            return false;

        ScriptEditorBase currentEditor = scriptEditor.GetCurrentEditor();
        Control baseEditor = currentEditor?.GetBaseEditor();

        return IsFocusedControlInsideScriptEditor(focusedControl, currentEditor, baseEditor);
    }

    private void RestoreActiveScriptEditorFocusDeferred()
    {
        ScriptEditor scriptEditor = EditorInterface.Singleton?.GetScriptEditor();

        if (scriptEditor == null)
            return;

        Control baseEditor = scriptEditor.GetCurrentEditor()?.GetBaseEditor();

        if (baseEditor is TextEdit textEditor)
        {
            textEditor.GrabFocus();
            return;
        }

        baseEditor?.GrabFocus();
    }

    private string NormalizeScriptPathForSync(string path)
    {
        string normalizedPath = ScriptPathUtility.Normalize(path);

        if (string.IsNullOrWhiteSpace(normalizedPath))
            return "";

        if (!normalizedPath.StartsWith("res://", StringComparison.OrdinalIgnoreCase))
            normalizedPath = ScriptPathUtility.Normalize(
                ProjectSettings.LocalizePath(normalizedPath)
            );

        if (!normalizedPath.StartsWith("res://", StringComparison.OrdinalIgnoreCase))
            return "";

        return normalizedPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
            ? normalizedPath
            : "";
    }

    #endregion
}
#endif
