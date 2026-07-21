#if TOOLS
using Godot;
using System;

namespace SystemExplorer.QuickActions.RefactorNamespace;

internal sealed class NamespaceRefactorApplyCoordinator
{
    private readonly NamespaceRefactorPendingWriteApplyService _pendingWriteApplyService;
    private readonly NamespaceRefactorPostApplyCoordinator _postApplyCoordinator;
    private readonly Func<EditorInterface> _editorInterfaceProvider;
    private readonly Action<string> _debugLog;
    private readonly Action<string> _showWarning;
    private readonly Action<string, string> _logOperation;

    internal NamespaceRefactorApplyCoordinator(
        NamespaceRefactorPendingWriteApplyService pendingWriteApplyService,
        NamespaceRefactorPostApplyCoordinator postApplyCoordinator,
        Func<EditorInterface> editorInterfaceProvider,
        Action<string> debugLog,
        Action<string> showWarning,
        Action<string, string> logOperation
    )
    {
        _pendingWriteApplyService =
            pendingWriteApplyService
            ?? throw new ArgumentNullException(nameof(pendingWriteApplyService));
        _postApplyCoordinator =
            postApplyCoordinator ?? throw new ArgumentNullException(nameof(postApplyCoordinator));
        _editorInterfaceProvider =
            editorInterfaceProvider
            ?? throw new ArgumentNullException(nameof(editorInterfaceProvider));
        _debugLog = debugLog ?? throw new ArgumentNullException(nameof(debugLog));
        _showWarning = showWarning ?? throw new ArgumentNullException(nameof(showWarning));
        _logOperation = logOperation ?? throw new ArgumentNullException(nameof(logOperation));
    }

    internal bool ApplySingleReplacement(
        EditorInterface editorInterface,
        ScriptEditor scriptEditor,
        NamespaceRefactorPendingWriteSet initialWriteSet,
        Func<NamespaceRefactorPendingWriteBuildResult> rebuildAfterAutosave
    )
    {
        NamespaceRefactorPendingWriteApplyResult applyResult =
            _pendingWriteApplyService.TryApplyPendingWrites(
                editorInterface,
                scriptEditor,
                initialWriteSet,
                matchMode: NamespaceRefactorAffectedOpenBufferMatchMode.NonActivatingWithActivationFallback,
                rebuildAfterAutosave: rebuildAfterAutosave,
                debugLog: _debugLog
            );

        if (!applyResult.Success)
        {
            string failureMessage = NamespaceRefactorPendingWriteApplyFailureMessageBuilder.Build(
                applyResult,
                "Refactor Namespace",
                useAfterAutosaveRematchFallback: true
            );

            if (!string.IsNullOrWhiteSpace(failureMessage))
                _showWarning(failureMessage);

            return false;
        }

        NamespaceRefactorPendingWriteSet finalWriteSet = applyResult.WriteSet;
        _postApplyCoordinator.CompleteSingleReplacement(
            _editorInterfaceProvider(),
            finalWriteSet,
            _debugLog
        );

        _logOperation(
            "Refactor Namespace Completed",
            $"Updated {finalWriteSet.PendingWrites.Count} file(s)."
        );
        return true;
    }

    internal bool ApplyPendingWriteOperation(
        NamespaceRefactorPendingWriteSet writeSet,
        string operationName,
        string explicitRestorePath,
        bool syncSelectionAfterOperation,
        Func<NamespaceRefactorPendingWriteBuildResult> rebuildAfterAutosave
    )
    {
        if (writeSet == null || writeSet.PendingWrites == null || writeSet.PendingWrites.Count == 0)
        {
            _debugLog($"{operationName} cancelled: no file changes were produced.");
            return false;
        }

        NamespaceRefactorAffectedOpenBufferMatchMode matchMode = syncSelectionAfterOperation
            ? NamespaceRefactorAffectedOpenBufferMatchMode.ActivatingOnly
            : NamespaceRefactorAffectedOpenBufferMatchMode.NonActivatingOnly;

        NamespaceRefactorPendingWriteApplyResult applyResult =
            _pendingWriteApplyService.TryApplyPendingWrites(
                _editorInterfaceProvider(),
                _editorInterfaceProvider()?.GetScriptEditor(),
                writeSet,
                matchMode: matchMode,
                rebuildAfterAutosave: rebuildAfterAutosave,
                debugLog: _debugLog
            );

        if (
            applyResult.DidAutosave
            && applyResult.Failure != NamespaceRefactorPendingWriteApplyFailure.AutosaveFailed
        )
        {
            _debugLog($"{operationName} autosaved affected open script buffer(s) before writing.");
        }

        if (!applyResult.Success)
        {
            string failureMessage = NamespaceRefactorPendingWriteApplyFailureMessageBuilder.Build(
                applyResult,
                operationName,
                useAfterAutosaveRematchFallback: rebuildAfterAutosave != null
            );

            if (!string.IsNullOrWhiteSpace(failureMessage))
                _showWarning(failureMessage);

            return false;
        }

        NamespaceRefactorPendingWriteSet finalWriteSet = applyResult.WriteSet;
        _postApplyCoordinator.CompletePendingWriteOperation(
            _editorInterfaceProvider(),
            finalWriteSet,
            explicitRestorePath,
            syncSelectionAfterOperation,
            _debugLog
        );

        _logOperation(
            $"{operationName} Completed",
            $"Updated {finalWriteSet.PendingWrites.Count} file(s)."
        );
        return true;
    }
}
#endif
