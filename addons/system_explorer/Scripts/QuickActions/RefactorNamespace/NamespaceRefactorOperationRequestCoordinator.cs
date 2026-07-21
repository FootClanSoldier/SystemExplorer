#if TOOLS
using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SystemExplorer.QuickActions.RefactorNamespace;

internal sealed class NamespaceRefactorOperationRequestCoordinator
{
    private readonly NamespaceRefactorProjectScopeCoordinator _projectScopeCoordinator;
    private readonly NamespaceRefactorPlanBuildCoordinator _planBuildCoordinator;
    private readonly NamespaceRefactorOperationCoordinator _operationCoordinator;
    private readonly Func<string, bool> _ensureSystemsLoadedForTreeOperation;
    private readonly Func<EditorInterface> _editorInterfaceProvider;
    private readonly Func<string, string> _getEntryFromMetadata;
    private readonly Func<string, string> _getScriptPathFromEntry;
    private readonly Func<string, string> _normalizeScriptPath;
    private readonly Func<string, bool> _fileExists;
    private readonly Action<string, string> _openMissingScriptDialog;
    private readonly Action<string> _debugLog;
    private readonly Action _beginBatchScriptEditorContextPreservation;
    private readonly Action _endBatchScriptEditorContextPreservation;

    internal NamespaceRefactorOperationRequestCoordinator(
        NamespaceRefactorProjectScopeCoordinator projectScopeCoordinator,
        NamespaceRefactorPlanBuildCoordinator planBuildCoordinator,
        NamespaceRefactorOperationCoordinator operationCoordinator,
        Func<string, bool> ensureSystemsLoadedForTreeOperation,
        Func<EditorInterface> editorInterfaceProvider,
        Func<string, string> getEntryFromMetadata,
        Func<string, string> getScriptPathFromEntry,
        Func<string, string> normalizeScriptPath,
        Func<string, bool> fileExists,
        Action<string, string> openMissingScriptDialog,
        Action<string> debugLog,
        Action beginBatchScriptEditorContextPreservation,
        Action endBatchScriptEditorContextPreservation
    )
    {
        _projectScopeCoordinator =
            projectScopeCoordinator
            ?? throw new ArgumentNullException(nameof(projectScopeCoordinator));
        _planBuildCoordinator =
            planBuildCoordinator ?? throw new ArgumentNullException(nameof(planBuildCoordinator));
        _operationCoordinator =
            operationCoordinator ?? throw new ArgumentNullException(nameof(operationCoordinator));
        _ensureSystemsLoadedForTreeOperation =
            ensureSystemsLoadedForTreeOperation
            ?? throw new ArgumentNullException(nameof(ensureSystemsLoadedForTreeOperation));
        _editorInterfaceProvider =
            editorInterfaceProvider
            ?? throw new ArgumentNullException(nameof(editorInterfaceProvider));
        _getEntryFromMetadata =
            getEntryFromMetadata ?? throw new ArgumentNullException(nameof(getEntryFromMetadata));
        _getScriptPathFromEntry =
            getScriptPathFromEntry
            ?? throw new ArgumentNullException(nameof(getScriptPathFromEntry));
        _normalizeScriptPath =
            normalizeScriptPath ?? throw new ArgumentNullException(nameof(normalizeScriptPath));
        _fileExists = fileExists ?? throw new ArgumentNullException(nameof(fileExists));
        _openMissingScriptDialog =
            openMissingScriptDialog
            ?? throw new ArgumentNullException(nameof(openMissingScriptDialog));
        _debugLog = debugLog ?? throw new ArgumentNullException(nameof(debugLog));
        _beginBatchScriptEditorContextPreservation =
            beginBatchScriptEditorContextPreservation
            ?? throw new ArgumentNullException(nameof(beginBatchScriptEditorContextPreservation));
        _endBatchScriptEditorContextPreservation =
            endBatchScriptEditorContextPreservation
            ?? throw new ArgumentNullException(nameof(endBatchScriptEditorContextPreservation));
    }

    internal bool ExecuteSingleReplacement(
        string metadata,
        string oldNamespace,
        string newNamespace
    )
    {
        if (!_ensureSystemsLoadedForTreeOperation("Refactor Namespace"))
            return false;

        if (string.IsNullOrWhiteSpace(metadata) || !metadata.StartsWith("script::"))
            return false;

        EditorInterface editorInterface = _editorInterfaceProvider();
        ScriptEditor scriptEditor = editorInterface?.GetScriptEditor();
        HashSet<string> candidatePaths =
            scriptEditor == null
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : _projectScopeCoordinator.BuildSingleCandidateScriptPaths(metadata);
        string selectedCandidateScriptPath = "";

        if (scriptEditor != null)
        {
            string selectedEntry = _getEntryFromMetadata(metadata);
            selectedCandidateScriptPath = _normalizeScriptPath(
                _getScriptPathFromEntry(selectedEntry)
            );
        }

        HashSet<string> requiredPaths = new(StringComparer.OrdinalIgnoreCase)
        {
            selectedCandidateScriptPath,
        };
        Func<NamespaceRefactorPendingWriteBuildResult> buildPendingWriteSet = () =>
            BuildSingleReplacementPendingWriteSet(metadata, oldNamespace, newNamespace);

        return _operationCoordinator.ExecuteSingleReplacement(
            editorInterface,
            scriptEditor,
            candidatePaths,
            requiredPaths,
            buildPendingWriteSet
        );
    }

    internal bool ExecuteAddNamespace(
        IEnumerable<string> scriptPaths,
        string newNamespace,
        string operationName
    )
    {
        if (!_ensureSystemsLoadedForTreeOperation(operationName))
            return false;

        List<string> targetScriptPaths =
            scriptPaths
                ?.Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(_normalizeScriptPath)
                .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
            ?? new List<string>();

        if (targetScriptPaths.Count == 0)
        {
            _debugLog($"{operationName} cancelled: no C# scripts were selected.");
            return false;
        }

        bool preserveBatchUiState = operationName.Contains(
            "Batch",
            StringComparison.OrdinalIgnoreCase
        );

        if (preserveBatchUiState)
            _beginBatchScriptEditorContextPreservation();

        try
        {
            HashSet<string> requiredPaths = targetScriptPaths.ToHashSet(
                StringComparer.OrdinalIgnoreCase
            );
            return _operationCoordinator.ExecuteAddNamespace(
                targetScriptPaths,
                requiredPaths,
                newNamespace,
                operationName,
                !preserveBatchUiState
            );
        }
        finally
        {
            if (preserveBatchUiState)
                _endBatchScriptEditorContextPreservation();
        }
    }

    internal bool ExecuteBatchReplacement(
        IEnumerable<string> scriptPaths,
        string oldNamespace,
        string newNamespace
    )
    {
        if (!_ensureSystemsLoadedForTreeOperation("Refactor Namespace"))
            return false;

        List<string> targetScriptPaths =
            scriptPaths
                ?.Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(_normalizeScriptPath)
                .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
            ?? new List<string>();

        if (targetScriptPaths.Count == 0)
        {
            _debugLog("Refactor Namespace batch cancelled: no C# scripts were selected.");
            return false;
        }

        _beginBatchScriptEditorContextPreservation();

        try
        {
            HashSet<string> requiredPaths = targetScriptPaths.ToHashSet(
                StringComparer.OrdinalIgnoreCase
            );
            HashSet<string> candidatePaths = targetScriptPaths
                .Concat(_projectScopeCoordinator.BuildProjectCSharpFilePaths())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return _operationCoordinator.ExecuteBatchReplacement(
                targetScriptPaths,
                candidatePaths,
                requiredPaths,
                oldNamespace,
                newNamespace,
                () => _projectScopeCoordinator.GetLinkedCSharpFilePaths(),
                () => _projectScopeCoordinator.BuildProjectCSharpFilePaths()
            );
        }
        finally
        {
            _endBatchScriptEditorContextPreservation();
        }
    }

    private NamespaceRefactorPendingWriteBuildResult BuildSingleReplacementPendingWriteSet(
        string metadata,
        string oldNamespace,
        string newNamespace
    )
    {
        if (string.IsNullOrWhiteSpace(metadata) || !metadata.StartsWith("script::"))
            return NamespaceRefactorPendingWriteBuildResult.Failed();

        string selectedEntry = _getEntryFromMetadata(metadata);
        string targetScriptPath = _normalizeScriptPath(_getScriptPathFromEntry(selectedEntry));

        if (!_fileExists(targetScriptPath))
        {
            _openMissingScriptDialog(selectedEntry, targetScriptPath);
            return NamespaceRefactorPendingWriteBuildResult.Failed();
        }

        IReadOnlyList<string> linkedCSharpFilePaths =
            _projectScopeCoordinator.GetLinkedCSharpFilePaths();
        IReadOnlyList<string> projectCSharpFilePaths =
            _projectScopeCoordinator.BuildProjectCSharpFilePaths();

        return _planBuildCoordinator.BuildSingleReplacement(
            selectedEntry,
            targetScriptPath,
            linkedCSharpFilePaths,
            projectCSharpFilePaths,
            oldNamespace,
            newNamespace
        );
    }
}
#endif
