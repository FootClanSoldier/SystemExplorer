#if TOOLS
using System;
using System.Collections.Generic;

namespace SystemExplorer.QuickActions.RefactorNamespace;

internal sealed class NamespaceRefactorDialogRequestCoordinator
{
    private readonly NamespaceRefactorProjectScopeCoordinator _projectScopeCoordinator;
    private readonly NamespaceRefactorDialogOpeningCoordinator _dialogOpeningCoordinator;
    private readonly Func<string, bool> _ensureSystemsLoadedForTreeOperation;
    private readonly Func<string, string> _getEntryFromMetadata;
    private readonly Func<string, string> _getScriptPathFromEntry;
    private readonly Action<string> _debugLog;

    internal NamespaceRefactorDialogRequestCoordinator(
        NamespaceRefactorProjectScopeCoordinator projectScopeCoordinator,
        NamespaceRefactorDialogOpeningCoordinator dialogOpeningCoordinator,
        Func<string, bool> ensureSystemsLoadedForTreeOperation,
        Func<string, string> getEntryFromMetadata,
        Func<string, string> getScriptPathFromEntry,
        Action<string> debugLog
    )
    {
        _projectScopeCoordinator =
            projectScopeCoordinator
            ?? throw new ArgumentNullException(nameof(projectScopeCoordinator));
        _dialogOpeningCoordinator =
            dialogOpeningCoordinator
            ?? throw new ArgumentNullException(nameof(dialogOpeningCoordinator));
        _ensureSystemsLoadedForTreeOperation =
            ensureSystemsLoadedForTreeOperation
            ?? throw new ArgumentNullException(nameof(ensureSystemsLoadedForTreeOperation));
        _getEntryFromMetadata =
            getEntryFromMetadata ?? throw new ArgumentNullException(nameof(getEntryFromMetadata));
        _getScriptPathFromEntry =
            getScriptPathFromEntry
            ?? throw new ArgumentNullException(nameof(getScriptPathFromEntry));
        _debugLog = debugLog ?? throw new ArgumentNullException(nameof(debugLog));
    }

    internal void Open(string metadata)
    {
        if (string.IsNullOrWhiteSpace(metadata))
            return;

        if (metadata.StartsWith("system::") || metadata.StartsWith("folder::"))
        {
            OpenBatch(metadata);
            return;
        }

        if (!metadata.StartsWith("script::"))
            return;

        string scriptEntry = _getEntryFromMetadata(metadata);
        string scriptPath = _getScriptPathFromEntry(scriptEntry);

        _dialogOpeningCoordinator.OpenSingle(metadata, scriptPath);
    }

    private void OpenBatch(string metadata)
    {
        if (!_ensureSystemsLoadedForTreeOperation("Refactor Namespace"))
            return;

        List<string> scriptPaths = _projectScopeCoordinator.ResolveBatchTargetScriptPaths(metadata);

        if (scriptPaths.Count == 0)
        {
            _debugLog(
                $"Refactor Namespace batch cancelled: no C# scripts found for metadata '{metadata}'."
            );
            return;
        }

        _dialogOpeningCoordinator.OpenBatch(metadata, scriptPaths);
    }
}
#endif
