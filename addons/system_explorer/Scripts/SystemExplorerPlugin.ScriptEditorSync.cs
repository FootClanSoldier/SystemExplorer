#if TOOLS
using Godot;
using System;
using SystemExplorer.EditorIntegration.ScriptEditing;

public partial class SystemExplorerPlugin
{
    #region Script Editor Sync
    private const string ScriptEditorChangedSignalName = "editor_script_changed";
    private const double ScriptEditorSyncPollIntervalSeconds = 0.5;

    private bool _followActiveScript = true;
    private bool _followActiveScriptHasObservedInitialPath;
    private bool _isSyncingTreeSelectionToActiveScript;
    private bool _isScriptEditorSyncSignalConnected;
    private bool _isScriptEditorSyncDeferredQueued;
    private bool _suppressNextTreeNavigationFromScriptEditorSync;
    private int _scriptEditorSyncSuppressionDepth;
    private int _postOperationActiveScriptSyncSuppressionDepth;
    private string _lastObservedActiveScriptPath = "";
    private string _lastSyncedActiveScriptPath = "";
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
            DebugLog($"Script Editor Sync signal connect skipped: {error}.");
            return false;
        }

        _isScriptEditorSyncSignalConnected = true;
        DebugLog("Script Editor Sync connected to editor_script_changed.");
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

        DebugLog("Script Editor Sync using polling fallback.");
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
            DebugLog(
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

        if (!force && !_followActiveScript)
            return false;

        if (!TryGetActiveScriptPath(out string scriptPath))
            return false;

        if (!force && !ShouldFollowSyncForActiveScriptPath(scriptPath))
            return false;

        RecordObservedActiveScriptPath(scriptPath);

        if (
            string.Equals(
                _lastSyncedActiveScriptPath,
                scriptPath,
                StringComparison.OrdinalIgnoreCase
            ) && IsSelectedScriptPathInTree(scriptPath)
        )
        {
            return true;
        }

        bool selected = TrySelectScriptPathInTree(scriptPath);

        if (selected)
            _lastSyncedActiveScriptPath = scriptPath;

        return selected;
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

    private void BeginScriptEditorSyncSuppression()
    {
        _scriptEditorSyncSuppressionDepth++;
        _isScriptEditorSyncDeferredQueued = false;
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

    private bool TrySelectScriptPathInTree(string scriptPath)
    {
        string normalizedPath = NormalizeScriptPathForSync(scriptPath);

        if (string.IsNullOrWhiteSpace(normalizedPath) || _tree == null)
            return false;

        if (!TryFindScriptTreeItemByPath(normalizedPath, out TreeItem item))
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

        if (string.IsNullOrWhiteSpace(metadata) || !metadata.StartsWith("script::"))
            return false;

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
