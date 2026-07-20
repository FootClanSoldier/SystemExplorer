#if TOOLS
using System;
using System.Collections.Generic;

namespace SystemExplorer.QuickActions.RefactorNamespace;

internal sealed class NamespaceRefactorPlanBuildCoordinator
{
	private readonly NamespaceRefactorPreparationService _preparationService;
	private readonly Action<string, string> _showMissingScriptDialog;
	private readonly Action<string> _debugLog;
	private readonly Action<string> _showWarning;

	internal NamespaceRefactorPlanBuildCoordinator(
		NamespaceRefactorPreparationService preparationService,
		Action<string, string> showMissingScriptDialog,
		Action<string> debugLog,
		Action<string> showWarning
	)
	{
		_preparationService =
			preparationService ?? throw new ArgumentNullException(nameof(preparationService));
		_showMissingScriptDialog =
			showMissingScriptDialog
			?? throw new ArgumentNullException(nameof(showMissingScriptDialog));
		_debugLog = debugLog ?? throw new ArgumentNullException(nameof(debugLog));
		_showWarning = showWarning ?? throw new ArgumentNullException(nameof(showWarning));
	}

	internal NamespaceRefactorPendingWriteBuildResult BuildSingleReplacement(
		string selectedEntry,
		string targetScriptPath,
		IEnumerable<string> referenceCandidatePaths,
		IEnumerable<string> declarationCandidatePaths,
		string oldNamespace,
		string newNamespace
	)
	{
		NamespaceRefactorPreparationResult preparationResult = _preparationService.PrepareReplace(
			new[] { targetScriptPath },
			referenceCandidatePaths,
			declarationCandidatePaths,
			oldNamespace,
			newNamespace
		);

		if (!preparationResult.SnapshotLoadResult.SnapshotsByPath.ContainsKey(targetScriptPath))
		{
			_showMissingScriptDialog(selectedEntry, targetScriptPath);
			return NamespaceRefactorPendingWriteBuildResult.Failed();
		}

		NamespaceRefactorPlanResult result = preparationResult.PlanResult;

		if (!preparationResult.Success)
		{
			if (string.IsNullOrWhiteSpace(result.FirstTargetNamespace))
			{
				_showWarning(
					$"Refactor Namespace cancelled: no namespace declaration was found in '{targetScriptPath}'."
				);
			}
			else if (result.FirstTargetNamespace != oldNamespace)
			{
				_showWarning(
					$"Refactor Namespace cancelled: selected script namespace is '{result.FirstTargetNamespace}', not '{oldNamespace}'."
				);
			}
			else
			{
				_showWarning(
					$"Refactor Namespace cancelled: namespace declaration could not be updated in '{targetScriptPath}'."
				);
			}

			return NamespaceRefactorPendingWriteBuildResult.Failed();
		}

		return NamespaceRefactorPendingWriteBuildResult.Succeeded(
			NamespaceRefactorPendingWriteSet.FromPlan(result.Plan)
		);
	}

	internal NamespaceRefactorPendingWriteBuildResult BuildAddNamespace(
		IEnumerable<string> targetScriptPaths,
		string newNamespace
	)
	{
		NamespaceRefactorPreparationResult preparationResult = _preparationService.PrepareAdd(
			targetScriptPaths,
			newNamespace
		);

		foreach (string scriptPath in preparationResult.SnapshotLoadResult.MissingPaths)
			_debugLog($"Refactor Namespace add skipped missing script '{scriptPath}'.");

		foreach (string scriptPath in preparationResult.SnapshotLoadResult.FailedPaths)
			_debugLog($"Refactor Namespace add skipped unreadable script '{scriptPath}'.");

		NamespaceRefactorPlanResult result = preparationResult.PlanResult;

		foreach (string scriptPath in result.AlreadyNamespacedPaths)
		{
			_debugLog(
				$"Refactor Namespace add skipped '{scriptPath}' because it already has a namespace."
			);
		}

		foreach (string scriptPath in result.NamespaceAddFailedPaths)
		{
			_debugLog(
				$"Refactor Namespace add skipped '{scriptPath}' because the namespace block could not be inserted."
			);
		}

		if (!preparationResult.Success)
		{
			_debugLog(
				"Refactor Namespace add cancelled: no scripts without namespace could be updated."
			);
			return NamespaceRefactorPendingWriteBuildResult.Failed();
		}

		return NamespaceRefactorPendingWriteBuildResult.Succeeded(
			NamespaceRefactorPendingWriteSet.FromPlan(result.Plan)
		);
	}

	internal NamespaceRefactorPendingWriteBuildResult BuildBatchReplacement(
		IEnumerable<string> targetScriptPaths,
		IEnumerable<string> referenceCandidatePaths,
		IEnumerable<string> declarationCandidatePaths,
		string oldNamespace,
		string newNamespace
	)
	{
		NamespaceRefactorPreparationResult preparationResult = _preparationService.PrepareReplace(
			targetScriptPaths,
			referenceCandidatePaths,
			declarationCandidatePaths,
			oldNamespace,
			newNamespace
		);

		foreach (string scriptPath in preparationResult.MissingTargetPaths)
			_debugLog($"Refactor Namespace batch skipped missing script '{scriptPath}'.");

		foreach (string scriptPath in preparationResult.FailedTargetPaths)
			_debugLog($"Refactor Namespace batch skipped unreadable script '{scriptPath}'.");

		NamespaceRefactorPlanResult result = preparationResult.PlanResult;

		foreach (string scriptPath in result.NamespaceRewriteFailedPaths)
		{
			_debugLog(
				$"Refactor Namespace batch skipped '{scriptPath}' because its namespace declaration could not be updated."
			);
		}

		if (!preparationResult.Success)
		{
			_debugLog(
				$"Refactor Namespace batch cancelled: no scripts with namespace '{oldNamespace}' could be updated."
			);
			return NamespaceRefactorPendingWriteBuildResult.Failed();
		}

		return NamespaceRefactorPendingWriteBuildResult.Succeeded(
			NamespaceRefactorPendingWriteSet.FromPlan(result.Plan)
		);
	}
}
#endif
