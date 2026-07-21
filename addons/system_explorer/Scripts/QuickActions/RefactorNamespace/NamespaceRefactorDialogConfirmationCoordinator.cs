#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;

namespace SystemExplorer.QuickActions.RefactorNamespace;

internal sealed class NamespaceRefactorDialogConfirmationCoordinator
{
    private readonly NamespaceRefactorDialogView _dialogView;
    private readonly NamespaceRefactorDialogSessionState _sessionState;
    private readonly Func<string, bool> _isValidNamespaceName;
    private readonly Func<string, string> _readNamespaceFromScript;
    private readonly Action<string, string, string> _singleReplacement;
    private readonly Action<IReadOnlyList<string>, string, string> _addNamespace;
    private readonly Action<IReadOnlyList<string>, string, string> _batchReplacement;
    private readonly Action<string> _debugLog;
    private readonly Action<string> _showWarning;
    private readonly Action<string, string> _logOperation;

    internal NamespaceRefactorDialogConfirmationCoordinator(
        NamespaceRefactorDialogView dialogView,
        NamespaceRefactorDialogSessionState sessionState,
        Func<string, bool> isValidNamespaceName,
        Func<string, string> readNamespaceFromScript,
        Action<string, string, string> singleReplacement,
        Action<IReadOnlyList<string>, string, string> addNamespace,
        Action<IReadOnlyList<string>, string, string> batchReplacement,
        Action<string> debugLog,
        Action<string> showWarning,
        Action<string, string> logOperation
    )
    {
        _dialogView = dialogView ?? throw new ArgumentNullException(nameof(dialogView));
        _sessionState = sessionState ?? throw new ArgumentNullException(nameof(sessionState));
        _isValidNamespaceName =
            isValidNamespaceName ?? throw new ArgumentNullException(nameof(isValidNamespaceName));
        _readNamespaceFromScript =
            readNamespaceFromScript
            ?? throw new ArgumentNullException(nameof(readNamespaceFromScript));
        _singleReplacement =
            singleReplacement ?? throw new ArgumentNullException(nameof(singleReplacement));
        _addNamespace = addNamespace ?? throw new ArgumentNullException(nameof(addNamespace));
        _batchReplacement =
            batchReplacement ?? throw new ArgumentNullException(nameof(batchReplacement));
        _debugLog = debugLog ?? throw new ArgumentNullException(nameof(debugLog));
        _showWarning = showWarning ?? throw new ArgumentNullException(nameof(showWarning));
        _logOperation = logOperation ?? throw new ArgumentNullException(nameof(logOperation));
    }

    internal void Confirm()
    {
        if (string.IsNullOrWhiteSpace(_sessionState.Metadata))
            return;

        string newNamespace = _dialogView.NewNamespaceText.Trim();

        switch (_sessionState.Mode)
        {
            case NamespaceRefactorDialogMode.None:
                return;
            case NamespaceRefactorDialogMode.SingleReplacement:
                ConfirmSingleReplacement(newNamespace);
                return;
            case NamespaceRefactorDialogMode.SingleAdd:
                ConfirmSingleAdd(newNamespace);
                return;
            case NamespaceRefactorDialogMode.Batch:
                ConfirmBatch(newNamespace);
                return;
        }
    }

    private void ConfirmSingleAdd(string newNamespace)
    {
        _logOperation("Refactor Namespace Add Confirmed", newNamespace);

        if (!_isValidNamespaceName(newNamespace))
        {
            _debugLog(
                "Refactor Namespace add cancelled: new namespace must be a valid C# namespace name."
            );
            return;
        }

        _addNamespace(_sessionState.ScriptPaths, newNamespace, "Refactor Namespace Add");
        _sessionState.Clear();
    }

    private void ConfirmSingleReplacement(string newNamespace)
    {
        string oldNamespace = _dialogView.OldNamespaceText.Trim();

        _logOperation("Refactor Namespace Confirmed", $"{oldNamespace} -> {newNamespace}");

        if (!_isValidNamespaceName(oldNamespace) || !_isValidNamespaceName(newNamespace))
        {
            _showWarning(
                "Refactor Namespace cancelled: namespace values must be valid C# namespace names."
            );
            return;
        }

        if (oldNamespace == newNamespace)
        {
            _debugLog("Refactor Namespace cancelled: namespace is unchanged.");
            return;
        }

        _singleReplacement(_sessionState.Metadata, oldNamespace, newNamespace);
        _sessionState.Clear();
    }

    private void ConfirmBatch(string newNamespace)
    {
        if (!_isValidNamespaceName(newNamespace))
        {
            _debugLog(
                "Refactor Namespace batch cancelled: new namespace must be a valid C# namespace name."
            );
            return;
        }

        if (_dialogView.IsWithoutNamespaceSelected)
        {
            ConfirmBatchAdd(newNamespace);
            return;
        }

        ConfirmBatchReplacement(newNamespace);
    }

    private void ConfirmBatchAdd(string newNamespace)
    {
        _logOperation("Refactor Namespace Batch Add Confirmed", newNamespace);

        List<string> targetScriptPaths = _sessionState
            .ScriptPaths.Where(path => string.IsNullOrWhiteSpace(_readNamespaceFromScript(path)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        _addNamespace(targetScriptPaths, newNamespace, "Refactor Namespace Batch Add");
        _sessionState.Clear();
    }

    private void ConfirmBatchReplacement(string newNamespace)
    {
        string oldNamespace = _dialogView.GetSelectedExistingNamespace();

        if (!_isValidNamespaceName(oldNamespace))
        {
            _debugLog("Refactor Namespace batch cancelled: no valid old namespace was selected.");
            return;
        }

        if (oldNamespace == newNamespace)
        {
            _debugLog("Refactor Namespace batch cancelled: namespace is unchanged.");
            return;
        }

        _logOperation("Refactor Namespace Batch Confirmed", $"{oldNamespace} -> {newNamespace}");
        _batchReplacement(_sessionState.ScriptPaths, oldNamespace, newNamespace);
        _sessionState.Clear();
    }
}
#endif
