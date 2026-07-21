#if TOOLS
using Godot;
using System;
using System.Collections.Generic;
using SystemExplorer.EditorIntegration.ScriptEditing;

namespace SystemExplorer.QuickActions.RefactorNamespace;

internal sealed class NamespaceRefactorFeature
{
    private static readonly NamespaceRefactorScopeResolver RefactorNamespaceScopeResolver = new(
        ScriptPathUtility.Normalize,
        path => ProjectSettings.GlobalizePath(path),
        path => ProjectSettings.LocalizePath(path)
    );
    private static readonly NamespaceRefactorSnapshotLoader RefactorNamespaceSnapshotLoader = new(
        ScriptPathUtility.Normalize,
        FileAccess.FileExists,
        ScriptTextFileService.ReadText
    );
    private static readonly NamespaceRefactorPreparationService RefactorNamespacePreparationService =
        new(RefactorNamespaceScopeResolver, RefactorNamespaceSnapshotLoader);
    private static readonly NamespaceOpenBufferActivationService RefactorNamespaceOpenBufferActivation =
        new(new ScriptEditorBufferActivationService(ScriptPathUtility.Normalize));

    private readonly NamespaceRefactorDialogView _dialogView;
    private readonly ScriptEditorBufferLocator _bufferLocator;
    private readonly ScriptEditorBufferAutosaveCoordinator _bufferAutosaveCoordinator;
    private readonly ScriptEditorBufferBatchService _bufferBatchService;
    private readonly Func<IReadOnlyDictionary<string, List<string>>> _systemsProvider;
    private readonly Func<string, string> _getSystemNameFromMetadata;
    private readonly Func<string, string> _getFolderPathFromMetadata;
    private readonly Func<string, string> _getEntryFromMetadata;
    private readonly Func<string, string> _getScriptPathFromEntry;
    private readonly Func<string, string> _getFolderPathFromEntry;
    private readonly string _sceneEntryMarker;
    private readonly Func<string, bool> _ensureSystemsLoadedForTreeOperation;
    private readonly Func<EditorInterface> _editorInterfaceProvider;
    private readonly Action<string, string> _showMissingScriptDialog;
    private readonly Action<string> _debugLog;
    private readonly Action<string> _showWarning;
    private readonly Action<string, string> _logOperation;
    private readonly Action _beginBatchScriptEditorContextPreservation;
    private readonly Action _endBatchScriptEditorContextPreservation;
    private readonly Func<string, string> _readNamespaceFromScript;
    private readonly Action<bool> _showConfiguredDialog;
    private readonly Action<string> _scheduleDeferredBufferRefresh;
    private readonly Action _syncSelectionAfterOperation;
    private readonly Action<string> _scheduleDeferredTargetScriptRestoration;
    private readonly Action _scheduleDeferredSelectionSync;
    private readonly Action _scheduleDeferredTreeFocusRelease;

    private NamespaceRefactorProjectScopeCoordinator _projectScopeCoordinator;
    private NamespaceRefactorBatchDialogPreparationCoordinator _batchDialogPreparationCoordinator;
    private NamespaceRefactorPlanBuildCoordinator _planBuildCoordinator;
    private NamespaceRefactorOpenBufferPreflightService _openBufferPreflightService;
    private NamespaceRefactorPreflightCoordinator _preflightCoordinator;
    private NamespaceRefactorPendingWriteApplyService _pendingWriteApplyService;
    private NamespaceRefactorPostApplyEditorService _postApplyEditorService;
    private NamespaceRefactorPostApplyCoordinator _postApplyCoordinator;
    private NamespaceRefactorApplyCoordinator _applyCoordinator;
    private NamespaceRefactorOperationCoordinator _operationCoordinator;
    private NamespaceRefactorOperationRequestCoordinator _operationRequestCoordinator;
    private NamespaceRefactorDialogSessionState _dialogSessionState;
    private NamespaceRefactorDialogOpeningCoordinator _dialogOpeningCoordinator;
    private NamespaceRefactorDialogRequestCoordinator _dialogRequestCoordinator;
    private NamespaceRefactorDialogConfirmationCoordinator _dialogConfirmationCoordinator;

    internal NamespaceRefactorFeature(
        NamespaceRefactorDialogView dialogView,
        ScriptEditorBufferLocator bufferLocator,
        ScriptEditorBufferAutosaveCoordinator bufferAutosaveCoordinator,
        ScriptEditorBufferBatchService bufferBatchService,
        Func<IReadOnlyDictionary<string, List<string>>> systemsProvider,
        Func<string, string> getSystemNameFromMetadata,
        Func<string, string> getFolderPathFromMetadata,
        Func<string, string> getEntryFromMetadata,
        Func<string, string> getScriptPathFromEntry,
        Func<string, string> getFolderPathFromEntry,
        string sceneEntryMarker,
        Func<string, bool> ensureSystemsLoadedForTreeOperation,
        Func<EditorInterface> editorInterfaceProvider,
        Action<string, string> showMissingScriptDialog,
        Action<string> debugLog,
        Action<string> showWarning,
        Action<string, string> logOperation,
        Action beginBatchScriptEditorContextPreservation,
        Action endBatchScriptEditorContextPreservation,
        Func<string, string> readNamespaceFromScript,
        Action<bool> showConfiguredDialog,
        Action<string> scheduleDeferredBufferRefresh,
        Action syncSelectionAfterOperation,
        Action<string> scheduleDeferredTargetScriptRestoration,
        Action scheduleDeferredSelectionSync,
        Action scheduleDeferredTreeFocusRelease
    )
    {
        _dialogView = dialogView ?? throw new ArgumentNullException(nameof(dialogView));
        _bufferLocator = bufferLocator ?? throw new ArgumentNullException(nameof(bufferLocator));
        _bufferAutosaveCoordinator =
            bufferAutosaveCoordinator
            ?? throw new ArgumentNullException(nameof(bufferAutosaveCoordinator));
        _bufferBatchService =
            bufferBatchService ?? throw new ArgumentNullException(nameof(bufferBatchService));
        _systemsProvider =
            systemsProvider ?? throw new ArgumentNullException(nameof(systemsProvider));
        _getSystemNameFromMetadata =
            getSystemNameFromMetadata
            ?? throw new ArgumentNullException(nameof(getSystemNameFromMetadata));
        _getFolderPathFromMetadata =
            getFolderPathFromMetadata
            ?? throw new ArgumentNullException(nameof(getFolderPathFromMetadata));
        _getEntryFromMetadata =
            getEntryFromMetadata ?? throw new ArgumentNullException(nameof(getEntryFromMetadata));
        _getScriptPathFromEntry =
            getScriptPathFromEntry
            ?? throw new ArgumentNullException(nameof(getScriptPathFromEntry));
        _getFolderPathFromEntry =
            getFolderPathFromEntry
            ?? throw new ArgumentNullException(nameof(getFolderPathFromEntry));
        _sceneEntryMarker =
            sceneEntryMarker ?? throw new ArgumentNullException(nameof(sceneEntryMarker));
        _ensureSystemsLoadedForTreeOperation =
            ensureSystemsLoadedForTreeOperation
            ?? throw new ArgumentNullException(nameof(ensureSystemsLoadedForTreeOperation));
        _editorInterfaceProvider =
            editorInterfaceProvider
            ?? throw new ArgumentNullException(nameof(editorInterfaceProvider));
        _showMissingScriptDialog =
            showMissingScriptDialog
            ?? throw new ArgumentNullException(nameof(showMissingScriptDialog));
        _debugLog = debugLog ?? throw new ArgumentNullException(nameof(debugLog));
        _showWarning = showWarning ?? throw new ArgumentNullException(nameof(showWarning));
        _logOperation = logOperation ?? throw new ArgumentNullException(nameof(logOperation));
        _beginBatchScriptEditorContextPreservation =
            beginBatchScriptEditorContextPreservation
            ?? throw new ArgumentNullException(nameof(beginBatchScriptEditorContextPreservation));
        _endBatchScriptEditorContextPreservation =
            endBatchScriptEditorContextPreservation
            ?? throw new ArgumentNullException(nameof(endBatchScriptEditorContextPreservation));
        _readNamespaceFromScript =
            readNamespaceFromScript
            ?? throw new ArgumentNullException(nameof(readNamespaceFromScript));
        _showConfiguredDialog =
            showConfiguredDialog ?? throw new ArgumentNullException(nameof(showConfiguredDialog));
        _scheduleDeferredBufferRefresh =
            scheduleDeferredBufferRefresh
            ?? throw new ArgumentNullException(nameof(scheduleDeferredBufferRefresh));
        _syncSelectionAfterOperation =
            syncSelectionAfterOperation
            ?? throw new ArgumentNullException(nameof(syncSelectionAfterOperation));
        _scheduleDeferredTargetScriptRestoration =
            scheduleDeferredTargetScriptRestoration
            ?? throw new ArgumentNullException(nameof(scheduleDeferredTargetScriptRestoration));
        _scheduleDeferredSelectionSync =
            scheduleDeferredSelectionSync
            ?? throw new ArgumentNullException(nameof(scheduleDeferredSelectionSync));
        _scheduleDeferredTreeFocusRelease =
            scheduleDeferredTreeFocusRelease
            ?? throw new ArgumentNullException(nameof(scheduleDeferredTreeFocusRelease));
    }

    private NamespaceRefactorProjectScopeCoordinator ProjectScopeCoordinator =>
        _projectScopeCoordinator ??= new NamespaceRefactorProjectScopeCoordinator(
            RefactorNamespaceScopeResolver,
            _systemsProvider,
            _getSystemNameFromMetadata,
            _getFolderPathFromMetadata,
            _getEntryFromMetadata,
            _getScriptPathFromEntry,
            _getFolderPathFromEntry,
            _sceneEntryMarker,
            _debugLog
        );

    private NamespaceRefactorBatchDialogPreparationCoordinator BatchDialogPreparationCoordinator =>
        _batchDialogPreparationCoordinator ??=
            new NamespaceRefactorBatchDialogPreparationCoordinator(
                RefactorNamespacePreparationService,
                _debugLog
            );

    private NamespaceRefactorPlanBuildCoordinator PlanBuildCoordinator =>
        _planBuildCoordinator ??= new NamespaceRefactorPlanBuildCoordinator(
            RefactorNamespacePreparationService,
            _showMissingScriptDialog,
            _debugLog,
            _showWarning
        );

    private NamespaceRefactorOpenBufferPreflightService OpenBufferPreflightService =>
        _openBufferPreflightService ??= new NamespaceRefactorOpenBufferPreflightService(
            RefactorNamespaceOpenBufferActivation,
            new NamespaceOpenBufferLookupService(_bufferLocator),
            new NamespaceOpenBufferReferenceGuard(),
            _bufferAutosaveCoordinator
        );

    private NamespaceRefactorPreflightCoordinator PreflightCoordinator =>
        _preflightCoordinator ??= new NamespaceRefactorPreflightCoordinator(
            OpenBufferPreflightService,
            _editorInterfaceProvider,
            _debugLog,
            _showWarning
        );

    private NamespaceRefactorPendingWriteApplyService PendingWriteApplyService =>
        _pendingWriteApplyService ??= new NamespaceRefactorPendingWriteApplyService(
            RefactorNamespaceOpenBufferActivation,
            _bufferLocator,
            _bufferAutosaveCoordinator,
            _bufferBatchService,
            ScriptTextFileService.WriteText,
            ScriptResourceRefreshService.RefreshChangedScripts
        );

    private NamespaceRefactorPostApplyEditorService PostApplyEditorService =>
        _postApplyEditorService ??= new NamespaceRefactorPostApplyEditorService(
            _bufferLocator,
            _bufferBatchService,
            FileAccess.FileExists,
            ScriptTextFileService.ReadText,
            path => ResourceLoader.Load<Script>(path)
        );

    private NamespaceRefactorPostApplyCoordinator PostApplyCoordinator =>
        _postApplyCoordinator ??= new NamespaceRefactorPostApplyCoordinator(
            PostApplyEditorService,
            _scheduleDeferredBufferRefresh,
            _syncSelectionAfterOperation,
            _scheduleDeferredTargetScriptRestoration,
            _scheduleDeferredSelectionSync,
            _scheduleDeferredTreeFocusRelease
        );

    private NamespaceRefactorApplyCoordinator ApplyCoordinator =>
        _applyCoordinator ??= new NamespaceRefactorApplyCoordinator(
            PendingWriteApplyService,
            PostApplyCoordinator,
            _editorInterfaceProvider,
            _debugLog,
            _showWarning,
            _logOperation
        );

    private NamespaceRefactorOperationCoordinator OperationCoordinator =>
        _operationCoordinator ??= new NamespaceRefactorOperationCoordinator(
            PreflightCoordinator,
            PlanBuildCoordinator,
            ApplyCoordinator
        );

    private NamespaceRefactorOperationRequestCoordinator OperationRequestCoordinator =>
        _operationRequestCoordinator ??= new NamespaceRefactorOperationRequestCoordinator(
            ProjectScopeCoordinator,
            PlanBuildCoordinator,
            OperationCoordinator,
            _ensureSystemsLoadedForTreeOperation,
            _editorInterfaceProvider,
            _getEntryFromMetadata,
            _getScriptPathFromEntry,
            ScriptPathUtility.Normalize,
            FileAccess.FileExists,
            _showMissingScriptDialog,
            _debugLog,
            _beginBatchScriptEditorContextPreservation,
            _endBatchScriptEditorContextPreservation
        );

    private NamespaceRefactorDialogSessionState DialogSessionState =>
        _dialogSessionState ??= new NamespaceRefactorDialogSessionState();

    private NamespaceRefactorDialogOpeningCoordinator DialogOpeningCoordinator =>
        _dialogOpeningCoordinator ??= new NamespaceRefactorDialogOpeningCoordinator(
            _dialogView,
            DialogSessionState,
            BatchDialogPreparationCoordinator,
            ScriptPathUtility.Normalize,
            _readNamespaceFromScript,
            _showConfiguredDialog,
            _debugLog
        );

    private NamespaceRefactorDialogRequestCoordinator DialogRequestCoordinator =>
        _dialogRequestCoordinator ??= new NamespaceRefactorDialogRequestCoordinator(
            ProjectScopeCoordinator,
            DialogOpeningCoordinator,
            _ensureSystemsLoadedForTreeOperation,
            _getEntryFromMetadata,
            _getScriptPathFromEntry,
            _debugLog
        );

    private NamespaceRefactorDialogConfirmationCoordinator DialogConfirmationCoordinator =>
        _dialogConfirmationCoordinator ??= new NamespaceRefactorDialogConfirmationCoordinator(
            _dialogView,
            DialogSessionState,
            NamespaceTextRewriter.IsValidNamespaceName,
            _readNamespaceFromScript,
            (metadata, oldNamespace, newNamespace) =>
            {
                OperationRequestCoordinator.ExecuteSingleReplacement(
                    metadata,
                    oldNamespace,
                    newNamespace
                );
            },
            (scriptPaths, newNamespace, operationName) =>
            {
                OperationRequestCoordinator.ExecuteAddNamespace(
                    scriptPaths,
                    newNamespace,
                    operationName
                );
            },
            (scriptPaths, oldNamespace, newNamespace) =>
            {
                OperationRequestCoordinator.ExecuteBatchReplacement(
                    scriptPaths,
                    oldNamespace,
                    newNamespace
                );
            },
            _debugLog,
            _showWarning,
            _logOperation
        );

    internal void OpenDialog(string metadata)
    {
        DialogRequestCoordinator.Open(metadata);
    }

    internal void ConfirmDialog()
    {
        DialogConfirmationCoordinator.Confirm();
    }

    internal void SetBatchApplyMode(bool useExistingNamespaceMode)
    {
        _dialogView.SetBatchApplyMode(useExistingNamespaceMode);
    }

    internal void SelectExistingNamespace(long index)
    {
        _dialogView.SelectExistingNamespace(index);
    }

    internal void RestoreTargetScriptEditor(string scriptPath)
    {
        PostApplyEditorService.RestoreTargetScriptEditor(
            _editorInterfaceProvider(),
            scriptPath,
            _debugLog
        );
    }

    internal void RefreshOpenBuffersAfterDeferredResourceRefresh(string scriptPathPayload)
    {
        PostApplyEditorService.RefreshOpenBuffersAfterDeferredResourceRefresh(
            _editorInterfaceProvider()?.GetScriptEditor(),
            scriptPathPayload
        );
    }
}
#endif
