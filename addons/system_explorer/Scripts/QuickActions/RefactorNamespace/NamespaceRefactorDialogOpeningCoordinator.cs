#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;

namespace SystemExplorer.QuickActions.RefactorNamespace;

internal sealed class NamespaceRefactorDialogOpeningCoordinator
{
    private readonly NamespaceRefactorDialogView _dialogView;
    private readonly NamespaceRefactorDialogSessionState _sessionState;
    private readonly NamespaceRefactorBatchDialogPreparationCoordinator _batchDialogPreparationCoordinator;
    private readonly Func<string, string> _normalizeScriptPath;
    private readonly Func<string, string> _readNamespaceFromScript;
    private readonly Action<bool> _showConfiguredDialog;
    private readonly Action<string> _debugLog;

    internal NamespaceRefactorDialogOpeningCoordinator(
        NamespaceRefactorDialogView dialogView,
        NamespaceRefactorDialogSessionState sessionState,
        NamespaceRefactorBatchDialogPreparationCoordinator batchDialogPreparationCoordinator,
        Func<string, string> normalizeScriptPath,
        Func<string, string> readNamespaceFromScript,
        Action<bool> showConfiguredDialog,
        Action<string> debugLog
    )
    {
        _dialogView = dialogView ?? throw new ArgumentNullException(nameof(dialogView));
        _sessionState = sessionState ?? throw new ArgumentNullException(nameof(sessionState));
        _batchDialogPreparationCoordinator =
            batchDialogPreparationCoordinator
            ?? throw new ArgumentNullException(nameof(batchDialogPreparationCoordinator));
        _normalizeScriptPath =
            normalizeScriptPath ?? throw new ArgumentNullException(nameof(normalizeScriptPath));
        _readNamespaceFromScript =
            readNamespaceFromScript
            ?? throw new ArgumentNullException(nameof(readNamespaceFromScript));
        _showConfiguredDialog =
            showConfiguredDialog ?? throw new ArgumentNullException(nameof(showConfiguredDialog));
        _debugLog = debugLog ?? throw new ArgumentNullException(nameof(debugLog));
    }

    internal void OpenSingle(string metadata, string scriptPath)
    {
        _sessionState.BeginSingleReplacement(metadata);

        string currentNamespace = _readNamespaceFromScript(scriptPath);

        if (string.IsNullOrWhiteSpace(currentNamespace))
        {
            _debugLog(
                $"Refactor Namespace found no namespace in '{scriptPath}'. Opening add-namespace dialog."
            );

            string normalizedScriptPath = _normalizeScriptPath(scriptPath);
            _sessionState.TransitionToSingleAdd(normalizedScriptPath);

            _dialogView.ConfigureSingleAddNamespace();
            _showConfiguredDialog(false);
            return;
        }

        _dialogView.ConfigureSingleExistingNamespace(currentNamespace);
        _showConfiguredDialog(true);
    }

    internal void OpenBatch(string metadata, IReadOnlyList<string> scriptPaths)
    {
        NamespaceRefactorBatchDialogPreparationResult preparationResult =
            _batchDialogPreparationCoordinator.PrepareBatchDialog(scriptPaths, metadata);

        if (!preparationResult.Success)
            return;

        List<string> namespaces = preparationResult.Namespaces.ToList();

        _sessionState.BeginBatch(metadata, scriptPaths, namespaces);

        _dialogView.ConfigureBatch(namespaces, preparationResult.HasScriptsWithoutNamespace);
        _showConfiguredDialog(true);
    }
}
#endif
