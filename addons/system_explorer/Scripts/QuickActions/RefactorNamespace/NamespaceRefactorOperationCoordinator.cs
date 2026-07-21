#if TOOLS
using Godot;
using System;
using System.Collections.Generic;

namespace SystemExplorer.QuickActions.RefactorNamespace;

internal sealed class NamespaceRefactorOperationCoordinator
{
    private readonly NamespaceRefactorPreflightCoordinator _preflightCoordinator;
    private readonly NamespaceRefactorPlanBuildCoordinator _planBuildCoordinator;
    private readonly NamespaceRefactorApplyCoordinator _applyCoordinator;

    internal NamespaceRefactorOperationCoordinator(
        NamespaceRefactorPreflightCoordinator preflightCoordinator,
        NamespaceRefactorPlanBuildCoordinator planBuildCoordinator,
        NamespaceRefactorApplyCoordinator applyCoordinator
    )
    {
        _preflightCoordinator =
            preflightCoordinator ?? throw new ArgumentNullException(nameof(preflightCoordinator));
        _planBuildCoordinator =
            planBuildCoordinator ?? throw new ArgumentNullException(nameof(planBuildCoordinator));
        _applyCoordinator =
            applyCoordinator ?? throw new ArgumentNullException(nameof(applyCoordinator));
    }

    internal bool ExecuteSingleReplacement(
        EditorInterface editorInterface,
        ScriptEditor scriptEditor,
        IEnumerable<string> candidatePaths,
        HashSet<string> requiredPaths,
        Func<NamespaceRefactorPendingWriteBuildResult> buildPendingWriteSet
    )
    {
        if (
            !_preflightCoordinator.PreflightSingleReplacement(
                editorInterface,
                scriptEditor,
                candidatePaths,
                requiredPaths
            )
        )
        {
            return false;
        }

        NamespaceRefactorPendingWriteBuildResult buildResult = buildPendingWriteSet();

        if (!buildResult.Success)
            return false;

        return _applyCoordinator.ApplySingleReplacement(
            editorInterface,
            scriptEditor,
            buildResult.WriteSet,
            buildPendingWriteSet
        );
    }

    internal bool ExecuteAddNamespace(
        IEnumerable<string> targetScriptPaths,
        HashSet<string> requiredPaths,
        string newNamespace,
        string operationName,
        bool activateAndSyncSelection
    )
    {
        if (
            !_preflightCoordinator.PreflightAddNamespace(
                targetScriptPaths,
                requiredPaths,
                operationName,
                activateAndSyncSelection
            )
        )
        {
            return false;
        }

        NamespaceRefactorPendingWriteBuildResult buildResult =
            _planBuildCoordinator.BuildAddNamespace(targetScriptPaths, newNamespace);

        if (!buildResult.Success)
            return false;

        return _applyCoordinator.ApplyPendingWriteOperation(
            buildResult.WriteSet,
            operationName,
            "",
            activateAndSyncSelection,
            rebuildAfterAutosave: null
        );
    }

    internal bool ExecuteBatchReplacement(
        IEnumerable<string> targetScriptPaths,
        IEnumerable<string> candidatePaths,
        HashSet<string> requiredPaths,
        string oldNamespace,
        string newNamespace,
        Func<IReadOnlyList<string>> buildReferenceCandidatePaths,
        Func<IReadOnlyList<string>> buildDeclarationCandidatePaths
    )
    {
        if (
            !_preflightCoordinator.PreflightBatchReplacement(
                candidatePaths,
                requiredPaths,
                oldNamespace
            )
        )
        {
            return false;
        }

        Func<NamespaceRefactorPendingWriteBuildResult> buildPendingWriteSet = () =>
        {
            IReadOnlyList<string> referenceCandidatePaths = buildReferenceCandidatePaths();
            IReadOnlyList<string> declarationCandidatePaths = buildDeclarationCandidatePaths();

            return _planBuildCoordinator.BuildBatchReplacement(
                targetScriptPaths,
                referenceCandidatePaths,
                declarationCandidatePaths,
                oldNamespace,
                newNamespace
            );
        };
        NamespaceRefactorPendingWriteBuildResult buildResult = buildPendingWriteSet();

        if (!buildResult.Success)
            return false;

        return _applyCoordinator.ApplyPendingWriteOperation(
            buildResult.WriteSet,
            "Refactor Namespace Batch",
            "",
            false,
            buildPendingWriteSet
        );
    }
}
#endif
