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
		string newNamespace,
		NamespaceRefactorDiagnosticContext diagnosticContext = null
	)
	{
		NamespaceRefactorPreparationResult preparationResult = _preparationService.PrepareReplace(
			new[] { targetScriptPath },
			referenceCandidatePaths,
			declarationCandidatePaths,
			oldNamespace,
			newNamespace
		);
		LogPreparation(diagnosticContext, preparationResult, "SingleReplacement");

		if (!preparationResult.SnapshotLoadResult.SnapshotsByPath.ContainsKey(targetScriptPath))
		{
			diagnosticContext?.Log(
				"Plan",
				() => $"Cancelled before plan application; Target snapshot missing; Path='{targetScriptPath}'"
			);
			_showMissingScriptDialog(selectedEntry, targetScriptPath);
			return NamespaceRefactorPendingWriteBuildResult.Failed();
		}

		NamespaceRefactorPlanResult result = preparationResult.PlanResult;

		if (!preparationResult.Success)
		{
			diagnosticContext?.Log(
				"Plan",
				() =>
					$"Plan failed; FirstTargetNamespace='{result.FirstTargetNamespace}'; NamespaceRewriteFailed={diagnosticContext.FormatPaths(result.NamespaceRewriteFailedPaths)}"
			);

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

		return CreateReplaceBuildResult(result, "Refactor Namespace", diagnosticContext);
	}

	internal NamespaceRefactorPendingWriteBuildResult BuildAddNamespace(
		IEnumerable<string> targetScriptPaths,
		string newNamespace,
		NamespaceRefactorDiagnosticContext diagnosticContext = null
	)
	{
		NamespaceRefactorPreparationResult preparationResult = _preparationService.PrepareAdd(
			targetScriptPaths,
			newNamespace
		);
		LogPreparation(diagnosticContext, preparationResult, "AddNamespace");

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
			diagnosticContext?.Log(
				"Plan",
				() =>
					$"Plan failed; AlreadyNamespaced={diagnosticContext.FormatPaths(result.AlreadyNamespacedPaths)}; NamespaceAddFailed={diagnosticContext.FormatPaths(result.NamespaceAddFailedPaths)}; PendingWriteCount={result.Plan?.PendingWrites.Count ?? 0}"
			);
			_debugLog(
				"Refactor Namespace add cancelled: no scripts without namespace could be updated."
			);
			return NamespaceRefactorPendingWriteBuildResult.Failed();
		}

		diagnosticContext?.Log(
			"Plan",
			() =>
				$"Plan produced; PendingWriteCount={result.Plan?.PendingWrites.Count ?? 0}; PendingPaths={diagnosticContext.FormatPaths(result.Plan?.PendingWrites.Keys)}"
		);
		return NamespaceRefactorPendingWriteBuildResult.Succeeded(
			NamespaceRefactorPendingWriteSet.FromPlan(result.Plan)
		);
	}

	internal NamespaceRefactorPendingWriteBuildResult BuildBatchReplacement(
		IEnumerable<string> targetScriptPaths,
		IEnumerable<string> referenceCandidatePaths,
		IEnumerable<string> declarationCandidatePaths,
		string oldNamespace,
		string newNamespace,
		NamespaceRefactorDiagnosticContext diagnosticContext = null
	)
	{
		NamespaceRefactorPreparationResult preparationResult = _preparationService.PrepareReplace(
			targetScriptPaths,
			referenceCandidatePaths,
			declarationCandidatePaths,
			oldNamespace,
			newNamespace
		);
		LogPreparation(diagnosticContext, preparationResult, "BatchReplacement");

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
			diagnosticContext?.Log(
				"Plan",
				() =>
					$"Plan failed; NamespaceRewriteFailed={diagnosticContext.FormatPaths(result.NamespaceRewriteFailedPaths)}; PendingWriteCount={result.Plan?.PendingWrites.Count ?? 0}"
			);
			_debugLog(
				$"Refactor Namespace batch cancelled: no scripts with namespace '{oldNamespace}' could be updated."
			);
			return NamespaceRefactorPendingWriteBuildResult.Failed();
		}

		return CreateReplaceBuildResult(
			result,
			"Refactor Namespace batch",
			diagnosticContext
		);
	}

	private NamespaceRefactorPendingWriteBuildResult CreateReplaceBuildResult(
		NamespaceRefactorPlanResult result,
		string operationName,
		NamespaceRefactorDiagnosticContext diagnosticContext
	)
	{
		if (result?.Plan?.ReplaceWritePlan == null)
		{
			diagnosticContext?.Log(
				"Plan",
				"Plan failed; staged replace metadata was not produced."
			);
			_debugLog(
				$"{operationName} cancelled: staged replace metadata was not produced."
			);
			return NamespaceRefactorPendingWriteBuildResult.Failed();
		}

		diagnosticContext?.Log(
			"Plan",
			() =>
				$"Plan produced; PendingWriteCount={result.Plan.PendingWrites.Count}; PendingPaths={diagnosticContext.FormatPaths(result.Plan.PendingWrites.Keys)}; DeclarationTargetCount={result.Plan.ReplaceWritePlan.DeclarationPathsInOrder.Count}; ReferenceSourceCount={result.Plan.ReplaceWritePlan.ReferenceOriginalTextsByPath.Count}; InitiallyIncompleteDeclarations={diagnosticContext.FormatPaths(result.Plan.ReplaceWritePlan.InitiallyIncompleteDeclarationPaths)}"
		);
		return NamespaceRefactorPendingWriteBuildResult.Succeeded(
			NamespaceRefactorPendingWriteSet.FromPlan(result.Plan)
		);
	}

	private static void LogPreparation(
		NamespaceRefactorDiagnosticContext diagnosticContext,
		NamespaceRefactorPreparationResult preparationResult,
		string preparationKind
	)
	{
		diagnosticContext?.Log(
			"Preparation",
			() =>
				$"Kind={preparationKind}; TargetCount={preparationResult.TargetPaths.Count}; ReferenceCount={preparationResult.ReferenceCandidatePaths.Count}; DeclarationCount={preparationResult.DeclarationCandidatePaths.Count}; SnapshotCount={preparationResult.SnapshotLoadResult.SnapshotsByPath.Count}; MissingCount={preparationResult.SnapshotLoadResult.MissingPaths.Count}; FailedReadCount={preparationResult.SnapshotLoadResult.FailedPaths.Count}; Targets={diagnosticContext.FormatPaths(preparationResult.TargetPaths)}; References={diagnosticContext.FormatPaths(preparationResult.ReferenceCandidatePaths)}; Declarations={diagnosticContext.FormatPaths(preparationResult.DeclarationCandidatePaths)}; Missing={diagnosticContext.FormatPaths(preparationResult.SnapshotLoadResult.MissingPaths)}; FailedReads={diagnosticContext.FormatPaths(preparationResult.SnapshotLoadResult.FailedPaths)}; MissingTargets={diagnosticContext.FormatPaths(preparationResult.MissingTargetPaths)}; FailedTargets={diagnosticContext.FormatPaths(preparationResult.FailedTargetPaths)}"
		);
	}
}
#endif
